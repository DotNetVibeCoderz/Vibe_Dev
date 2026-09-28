package dotcode

import (
	"context"
	"encoding/json"
	"fmt"
	"sync"
)

// Tool is implemented by your application and offered to the model.
type Tool struct {
	Name        string
	Description string
	// InputSchema is a JSON Schema object for the tool input.
	InputSchema map[string]any
	ReadOnly    bool
	Handler     func(ctx context.Context, input json.RawMessage) (string, error)
}

// SessionOptions configures a session; unset fields fall back to the user's DotCode settings.
type SessionOptions struct {
	// Model is provider:model, an alias or a role (e.g. "anthropic:claude-sonnet-4-5", "ollama:qwen3-coder").
	Model              string
	FallbackModel      string
	Cwd                string
	PermissionMode     string // default | acceptEdits | plan | bypassPermissions
	SystemPrompt       string
	AppendSystemPrompt string
	AllowedTools       []string
	DisallowedTools    []string
	BuiltinTools       []string
	Tools              []Tool
	McpServers         map[string]McpServer
	// Settings is merged over settings files (e.g. BYOK provider configuration).
	Settings       map[string]any
	MaxTurns       int
	Effort         string
	PersistSession *bool
	NoMcp          bool

	// OnPermissionRequest approves tool calls. Without it the session is deny-by-default.
	OnPermissionRequest func(PermissionRequest) PermissionDecision
	OnQuestion          func([]UserQuestion) []UserQuestionAnswer
	OnPlanReview        func(plan string) bool
	OnEvent             func(Event)
}

func (o SessionOptions) wire(defaultCwd string) map[string]any {
	m := map[string]any{}
	set := func(k string, v string) {
		if v != "" {
			m[k] = v
		}
	}
	cwd := o.Cwd
	if cwd == "" {
		cwd = defaultCwd
	}
	set("cwd", cwd)
	set("model", o.Model)
	set("fallbackModel", o.FallbackModel)
	set("permissionMode", o.PermissionMode)
	set("systemPrompt", o.SystemPrompt)
	set("appendSystemPrompt", o.AppendSystemPrompt)
	set("effort", o.Effort)
	if len(o.AllowedTools) > 0 {
		m["allowedTools"] = o.AllowedTools
	}
	if len(o.DisallowedTools) > 0 {
		m["disallowedTools"] = o.DisallowedTools
	}
	if len(o.BuiltinTools) > 0 {
		m["tools"] = o.BuiltinTools
	}
	if len(o.McpServers) > 0 {
		m["mcpServers"] = o.McpServers
	}
	if o.Settings != nil {
		m["settings"] = o.Settings
	}
	if o.MaxTurns > 0 {
		m["maxTurns"] = o.MaxTurns
	}
	if o.PersistSession != nil {
		m["persistSession"] = *o.PersistSession
	}
	if o.NoMcp {
		m["noMcp"] = true
	}
	if len(o.Tools) > 0 {
		tools := make([]map[string]any, 0, len(o.Tools))
		for _, t := range o.Tools {
			schema := t.InputSchema
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
	opts   SessionOptions
	ID     string
	Model  string
	Info   SessionInfo

	mu       sync.Mutex
	watchers []chan Event
}

func (s *Session) dispatch(e Event) {
	if e.Type == "model.changed" && e.Model != "" {
		s.Model = e.Model
	}
	if s.opts.OnEvent != nil {
		s.opts.OnEvent(e)
	}
	s.mu.Lock()
	for _, w := range s.watchers {
		select {
		case w <- e:
		default:
		}
	}
	s.mu.Unlock()
}

func (s *Session) handleServerRequest(method string, params json.RawMessage) (any, error) {
	switch method {
	case "permission.request":
		var p struct {
			Request PermissionRequest `json:"request"`
		}
		if err := json.Unmarshal(params, &p); err != nil {
			return nil, err
		}
		if s.opts.OnPermissionRequest == nil {
			return PermissionDecision{Decision: "deny", Feedback: "No permission handler registered in the SDK host (deny by default)."}, nil
		}
		return s.opts.OnPermissionRequest(p.Request), nil
	case "user.question":
		var p struct {
			Questions []UserQuestion `json:"questions"`
		}
		_ = json.Unmarshal(params, &p)
		answers := []UserQuestionAnswer{}
		if s.opts.OnQuestion != nil {
			answers = s.opts.OnQuestion(p.Questions)
		}
		return map[string]any{"answers": answers}, nil
	case "plan.review":
		var p struct {
			Plan string `json:"plan"`
		}
		_ = json.Unmarshal(params, &p)
		approval := "approve"
		if s.opts.OnPlanReview != nil && !s.opts.OnPlanReview(p.Plan) {
			approval = "reject"
		}
		return map[string]any{"approval": approval}, nil
	case "tool.call":
		var p struct {
			Name  string          `json:"name"`
			Input json.RawMessage `json:"input"`
		}
		if err := json.Unmarshal(params, &p); err != nil {
			return nil, err
		}
		for _, t := range s.opts.Tools {
			if t.Name == p.Name {
				out, err := t.Handler(context.Background(), p.Input)
				if err != nil {
					return map[string]any{"content": "Error: " + err.Error(), "isError": true}, nil
				}
				return map[string]any{"content": out}, nil
			}
		}
		return nil, fmt.Errorf("unknown host tool %s", p.Name)
	}
	return nil, fmt.Errorf("unsupported callback %s", method)
}

// Send runs a prompt to completion.
func (s *Session) Send(ctx context.Context, prompt string) (*SendResult, error) {
	var r SendResult
	err := s.client.call(ctx, "session.send", map[string]any{"sessionId": s.ID, "prompt": prompt}, &r)
	if err != nil {
		return nil, err
	}
	return &r, nil
}

// Stream runs a prompt and delivers events on the returned channel (closed after turn.completed).
// The final result (or error) arrives on the second channel.
func (s *Session) Stream(ctx context.Context, prompt string) (<-chan Event, <-chan StreamResult) {
	events := make(chan Event, 1024)
	result := make(chan StreamResult, 1)
	watch := make(chan Event, 4096)
	s.mu.Lock()
	s.watchers = append(s.watchers, watch)
	s.mu.Unlock()

	sent := make(chan StreamResult, 1)
	go func() {
		r, err := s.Send(ctx, prompt)
		sent <- StreamResult{Result: r, Err: err}
	}()
	go func() {
		defer close(events)
		defer s.unwatch(watch)
		var final StreamResult
		finished := false
		for !finished {
			select {
			case e := <-watch:
				events <- e
				if e.Type == "turn.completed" && e.ParentToolUseID == "" {
					final = <-sent
					finished = true
				}
			case final = <-sent:
				// Drain events that arrived before the response.
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

// StreamResult carries the final outcome of Stream.
type StreamResult struct {
	Result *SendResult
	Err    error
}

func (s *Session) unwatch(ch chan Event) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for i, w := range s.watchers {
		if w == ch {
			s.watchers = append(s.watchers[:i], s.watchers[i+1:]...)
			return
		}
	}
}

func (s *Session) simple(ctx context.Context, method string, extra map[string]any) error {
	params := map[string]any{"sessionId": s.ID}
	for k, v := range extra {
		params[k] = v
	}
	return s.client.call(ctx, method, params, nil)
}

// Abort interrupts the running turn.
func (s *Session) Abort(ctx context.Context) error { return s.simple(ctx, "session.abort", nil) }

// SetModel switches the model (any configured provider).
func (s *Session) SetModel(ctx context.Context, model string) error {
	err := s.simple(ctx, "session.setModel", map[string]any{"model": model})
	if err == nil {
		s.Model = model
	}
	return err
}

// SetPermissionMode changes the permission mode.
func (s *Session) SetPermissionMode(ctx context.Context, mode string) error {
	return s.simple(ctx, "session.setMode", map[string]any{"mode": mode})
}

// Compact summarizes the conversation to free context.
func (s *Session) Compact(ctx context.Context, instructions string) error {
	return s.simple(ctx, "session.compact", map[string]any{"instructions": instructions})
}

// Messages returns the raw transcript.
func (s *Session) Messages(ctx context.Context) ([]json.RawMessage, error) {
	var out struct {
		Messages []json.RawMessage `json:"messages"`
	}
	err := s.client.call(ctx, "session.messages", map[string]any{"sessionId": s.ID}, &out)
	return out.Messages, err
}

// Close ends the session on the server.
func (s *Session) Close(ctx context.Context) error {
	err := s.simple(ctx, "session.close", nil)
	s.client.sessMu.Lock()
	delete(s.client.sessions, s.ID)
	s.client.sessMu.Unlock()
	return err
}
