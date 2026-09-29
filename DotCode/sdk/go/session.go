package dotcode

import (
	"context"
	"encoding/json"
	"fmt"
	"sync"
	"time"
)

// SessionConfig configures a session; unset fields fall back to the user's DotCode settings.
type SessionConfig struct {
	// Model is provider:model, an alias or a role (e.g. "anthropic:claude-sonnet-4-5", "ollama:qwen3-coder").
	Model         string
	FallbackModel string
	// WorkingDirectory of the session (default: ClientOptions.Cwd).
	WorkingDirectory string
	PermissionMode   PermissionMode
	ReasoningEffort  ReasoningEffort
	SystemMessage    *SystemMessageConfig
	// Tools are custom tools implemented by your application (see DefineTool).
	Tools []Tool
	// AvailableTools restricts the built-in tools.
	AvailableTools []BuiltinTool
	// AllowedTools are permission rules to pre-approve, e.g. BuiltinToolBash.Rule("npm test:*").
	AllowedTools []string
	// ExcludedTools removes tools / denies matching calls.
	ExcludedTools []string
	McpServers    map[string]McpServerConfig
	// DisableMcp skips MCP servers from settings files.
	DisableMcp bool
	// Providers are named model providers (BYOK), referenced as "<name>:<model>".
	Providers map[string]ProviderConfig
	// Settings is advanced: raw settings merged over the settings files (prefer the typed options).
	Settings map[string]any
	MaxTurns int
	// PersistSession saves the transcript for ResumeSession (default true).
	PersistSession *bool
	// Worktree runs the session in a fresh git worktree; WorktreeName picks (or reuses) a named one.
	Worktree     bool
	WorktreeName string

	// OnPermissionRequest approves tool calls. Without it the session is deny-by-default.
	OnPermissionRequest PermissionHandlerFunc
	// OnUserInputRequest answers the model's AskUserQuestion tool.
	OnUserInputRequest UserInputHandlerFunc
	// OnExitPlanMode reviews the plan when the agent leaves plan mode (default: approve).
	OnExitPlanMode ExitPlanModeHandlerFunc
	// OnEvent receives every event, including those emitted while the session is created.
	OnEvent func(SessionEvent)
}

func (o *SessionConfig) wire(defaultCwd string) map[string]any {
	m := map[string]any{}
	set := func(k, v string) {
		if v != "" {
			m[k] = v
		}
	}
	cwd := o.WorkingDirectory
	if cwd == "" {
		cwd = defaultCwd
	}
	set("cwd", cwd)
	set("model", o.Model)
	set("fallbackModel", o.FallbackModel)
	set("permissionMode", string(o.PermissionMode))
	set("effort", string(o.ReasoningEffort))
	if o.SystemMessage != nil {
		if o.SystemMessage.Mode == SystemMessageReplace {
			m["systemPrompt"] = o.SystemMessage.Content
		} else {
			m["appendSystemPrompt"] = o.SystemMessage.Content
		}
	}
	if len(o.AllowedTools) > 0 {
		m["allowedTools"] = o.AllowedTools
	}
	if len(o.ExcludedTools) > 0 {
		m["disallowedTools"] = o.ExcludedTools
	}
	if o.AvailableTools != nil {
		m["tools"] = o.AvailableTools
	}
	if len(o.McpServers) > 0 {
		servers := map[string]any{}
		for k, v := range o.McpServers {
			servers[k] = v.mcpWire()
		}
		m["mcpServers"] = servers
	}
	settings := map[string]any{}
	for k, v := range o.Settings {
		settings[k] = v
	}
	if len(o.Providers) > 0 {
		providers := map[string]any{}
		if existing, ok := settings["providers"].(map[string]any); ok {
			for k, v := range existing {
				providers[k] = v
			}
		}
		for k, v := range o.Providers {
			providers[k] = v
		}
		settings["providers"] = providers
	}
	if len(settings) > 0 {
		m["settings"] = settings
	}
	if o.MaxTurns > 0 {
		m["maxTurns"] = o.MaxTurns
	}
	if o.PersistSession != nil {
		m["persistSession"] = *o.PersistSession
	}
	if o.DisableMcp {
		m["noMcp"] = true
	}
	if o.WorktreeName != "" {
		m["worktree"] = o.WorktreeName
	} else if o.Worktree {
		m["worktree"] = true
	}
	if len(o.Tools) > 0 {
		tools := make([]map[string]any, 0, len(o.Tools))
		for _, t := range o.Tools {
			schema := t.Parameters
			if schema == nil {
				schema = map[string]any{"type": "object", "properties": map[string]any{}}
			}
			tools = append(tools, map[string]any{"name": t.Name, "description": t.Description, "inputSchema": schema, "readOnly": t.ReadOnly})
		}
		m["hostTools"] = tools
	}
	return m
}

// Session is one conversation with the agent.
type Session struct {
	client *Client
	config SessionConfig
	tools  map[string]Tool

	// SessionID identifies the session (use it with Client.ResumeSession).
	SessionID string
	Info      SessionInfo

	mu       sync.Mutex
	model    string
	nextSub  int
	handlers map[int]func(SessionEvent)
}

func newSession(c *Client, info SessionInfo, config SessionConfig) *Session {
	s := &Session{client: c, config: config, SessionID: info.SessionID, Info: info, model: info.Model,
		tools: map[string]Tool{}, handlers: map[int]func(SessionEvent){}}
	for _, t := range config.Tools {
		s.tools[t.Name] = t
	}
	return s
}

// Model is the session's current model.
func (s *Session) Model() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.model
}

// On subscribes to all events and returns an unsubscribe function. Handlers run on the SDK's reader goroutine;
// keep them short.
func (s *Session) On(handler func(SessionEvent)) func() {
	s.mu.Lock()
	id := s.nextSub
	s.nextSub++
	s.handlers[id] = handler
	s.mu.Unlock()
	return func() {
		s.mu.Lock()
		delete(s.handlers, id)
		s.mu.Unlock()
	}
}

func (s *Session) dispatch(e SessionEvent) {
	s.mu.Lock()
	if d, ok := e.Data.(*ModelChangedData); ok {
		s.model = d.Model
	}
	handlers := make([]func(SessionEvent), 0, len(s.handlers))
	for _, h := range s.handlers {
		handlers = append(handlers, h)
	}
	s.mu.Unlock()
	if s.config.OnEvent != nil {
		s.config.OnEvent(e)
	}
	for _, h := range handlers {
		h(e)
	}
}

func (s *Session) handleServerRequest(method string, params json.RawMessage) (any, error) {
	inv := Invocation{SessionID: s.SessionID}
	switch method {
	case "permission.request":
		var p struct {
			Request PermissionRequest `json:"request"`
		}
		if err := json.Unmarshal(params, &p); err != nil {
			return nil, err
		}
		if s.config.OnPermissionRequest == nil {
			return (&PermissionDecisionReject{Feedback: "No permission handler registered in the SDK host (deny by default)."}).permissionWire(), nil
		}
		d, err := s.config.OnPermissionRequest(p.Request, inv)
		if err != nil {
			return (&PermissionDecisionReject{Feedback: "Permission handler error: " + err.Error()}).permissionWire(), nil
		}
		if d == nil {
			d = &PermissionDecisionReject{}
		}
		return d.permissionWire(), nil
	case "user.question":
		var p struct {
			Questions []UserQuestion `json:"questions"`
		}
		_ = json.Unmarshal(params, &p)
		answers := []UserQuestionAnswer{}
		if s.config.OnUserInputRequest != nil {
			a, err := s.config.OnUserInputRequest(UserInputRequest{Questions: p.Questions}, inv)
			if err != nil {
				return nil, err
			}
			if a != nil {
				answers = a
			}
		}
		return map[string]any{"answers": answers}, nil
	case "plan.review":
		var p struct {
			Plan string `json:"plan"`
		}
		_ = json.Unmarshal(params, &p)
		if s.config.OnExitPlanMode == nil {
			return map[string]any{"approval": "approve"}, nil
		}
		r, err := s.config.OnExitPlanMode(ExitPlanModeRequest{Plan: p.Plan}, inv)
		if err != nil {
			return nil, err
		}
		switch {
		case r.Approved && r.AcceptEdits:
			return map[string]any{"approval": "approve_accept_edits"}, nil
		case r.Approved:
			return map[string]any{"approval": "approve"}, nil
		}
		return compact(map[string]any{"approval": "reject", "feedback": r.Feedback}), nil
	case "tool.call":
		var p struct {
			ToolUseID string          `json:"toolUseId"`
			Name      string          `json:"name"`
			Input     json.RawMessage `json:"input"`
		}
		if err := json.Unmarshal(params, &p); err != nil {
			return nil, err
		}
		t, ok := s.tools[p.Name]
		if !ok || t.Handler == nil {
			return nil, fmt.Errorf("unknown host tool %s", p.Name)
		}
		r, err := safeInvoke(t, ToolInvocation{SessionID: s.SessionID, ToolCallID: p.ToolUseID, ToolName: p.Name, Arguments: p.Input})
		if err != nil {
			return map[string]any{"content": "Error: " + err.Error(), "isError": true}, nil
		}
		return r.wire(), nil
	}
	return nil, fmt.Errorf("unsupported callback %s", method)
}

func safeInvoke(t Tool, inv ToolInvocation) (r ToolResult, err error) {
	defer func() {
		if p := recover(); p != nil {
			err = fmt.Errorf("tool panicked: %v", p)
		}
	}()
	return t.Handler(inv)
}

func (s *Session) sendParams(m MessageOptions) map[string]any {
	params := map[string]any{"sessionId": s.SessionID, "prompt": m.Prompt}
	if len(m.Attachments) > 0 {
		atts := make([]map[string]any, 0, len(m.Attachments))
		for _, a := range m.Attachments {
			atts = append(atts, a.attachmentWire())
		}
		params["attachments"] = atts
	}
	return params
}

// Send starts a turn and returns once it is dispatched; follow it with On (a *TurnCompletedData event ends it).
// Failures are delivered as an *ErrorData event.
func (s *Session) Send(ctx context.Context, message MessageOptions) error {
	if s.client.cmd == nil {
		return ErrNotStarted
	}
	go func() {
		var r SendResult
		if err := s.client.call(context.WithoutCancel(ctx), "session.send", s.sendParams(message), &r); err != nil {
			s.dispatch(SessionEvent{Type: EventError, SessionID: s.SessionID,
				Data: &ErrorData{Code: "send_failed", Message: err.Error()}})
		}
	}()
	return nil
}

// SendAndWait runs a turn to completion. When ctx is cancelled (or its deadline passes) the turn is aborted.
func (s *Session) SendAndWait(ctx context.Context, message MessageOptions) (*SendResult, error) {
	var r SendResult
	err := s.client.call(ctx, "session.send", s.sendParams(message), &r)
	if ctx.Err() != nil {
		abortCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = s.Abort(abortCtx)
		return nil, ctx.Err()
	}
	if err != nil {
		return nil, err
	}
	return &r, nil
}

// StreamResult carries the final outcome of Stream.
type StreamResult struct {
	Result *SendResult
	Err    error
}

// Stream runs a turn and delivers its events on the returned channel (closed after the turn completes); the
// result arrives on the second channel.
func (s *Session) Stream(ctx context.Context, message MessageOptions) (<-chan SessionEvent, <-chan StreamResult) {
	events := make(chan SessionEvent, 1024)
	result := make(chan StreamResult, 1)
	watch := make(chan SessionEvent, 4096)
	unsubscribe := s.On(func(e SessionEvent) {
		select {
		case watch <- e:
		default:
		}
	})
	sent := make(chan StreamResult, 1)
	go func() {
		r, err := s.SendAndWait(ctx, message)
		sent <- StreamResult{Result: r, Err: err}
	}()
	go func() {
		defer close(events)
		defer unsubscribe()
		var final StreamResult
		for finished := false; !finished; {
			select {
			case e := <-watch:
				events <- e
				if e.Type == EventTurnCompleted && e.ParentToolUseID == "" {
					final = <-sent
					finished = true
				}
			case final = <-sent:
				for len(watch) > 0 {
					events <- <-watch
				}
				finished = true
			}
		}
		result <- final
	}()
	return events, result
}

func (s *Session) call(ctx context.Context, method string, extra map[string]any, out any) error {
	params := map[string]any{"sessionId": s.SessionID}
	for k, v := range extra {
		params[k] = v
	}
	return s.client.call(ctx, method, params, out)
}

// Abort cancels the running turn.
func (s *Session) Abort(ctx context.Context) error { return s.call(ctx, "session.abort", nil, nil) }

// SetModel switches the model (any configured provider).
func (s *Session) SetModel(ctx context.Context, model string) error {
	err := s.call(ctx, "session.setModel", map[string]any{"model": model}, nil)
	if err == nil {
		s.mu.Lock()
		s.model = model
		s.mu.Unlock()
	}
	return err
}

// SetPermissionMode changes the permission mode.
func (s *Session) SetPermissionMode(ctx context.Context, mode PermissionMode) error {
	return s.call(ctx, "session.setMode", map[string]any{"mode": mode}, nil)
}

// SetReasoningEffort changes the thinking budget.
func (s *Session) SetReasoningEffort(ctx context.Context, effort ReasoningEffort) error {
	return s.call(ctx, "session.setEffort", map[string]any{"effort": effort}, nil)
}

// Compact summarizes the conversation to free context.
func (s *Session) Compact(ctx context.Context, instructions string) error {
	return s.call(ctx, "session.compact", compact(map[string]any{"instructions": instructions}), nil)
}

// Clear empties the conversation.
func (s *Session) Clear(ctx context.Context) error { return s.call(ctx, "session.clear", nil, nil) }

// GetMessages returns the conversation so far (provider-neutral messages).
func (s *Session) GetMessages(ctx context.Context) ([]json.RawMessage, error) {
	var out struct {
		Messages []json.RawMessage `json:"messages"`
	}
	err := s.call(ctx, "session.messages", nil, &out)
	return out.Messages, err
}

// ListTools returns the tools available to the model (built-in, MCP and yours).
func (s *Session) ListTools(ctx context.Context) ([]ToolInfo, error) {
	var out struct {
		Tools []ToolInfo `json:"tools"`
	}
	err := s.call(ctx, "tools.list", nil, &out)
	return out.Tools, err
}

// Disconnect closes the session; the transcript stays on disk for Client.ResumeSession.
func (s *Session) Disconnect() error {
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	err := s.call(ctx, "session.close", nil, nil)
	s.client.sessMu.Lock()
	delete(s.client.sessions, s.SessionID)
	s.client.sessMu.Unlock()
	s.mu.Lock()
	s.handlers = map[int]func(SessionEvent){}
	s.mu.Unlock()
	return err
}
