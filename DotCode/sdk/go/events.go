package dotcode

import "encoding/json"

// SessionEventType identifies an event.
type SessionEventType string

const (
	EventUserMessage            SessionEventType = "user.message"
	EventAssistantTextDelta     SessionEventType = "assistant.text.delta"
	EventAssistantThinkingDelta SessionEventType = "assistant.thinking.delta"
	EventAssistantMessage       SessionEventType = "assistant.message"
	EventToolStarted            SessionEventType = "tool.started"
	EventToolProgress           SessionEventType = "tool.progress"
	EventToolCompleted          SessionEventType = "tool.completed"
	EventTodoUpdated            SessionEventType = "todo.updated"
	EventSubagentStarted        SessionEventType = "subagent.started"
	EventSubagentCompleted      SessionEventType = "subagent.completed"
	EventContextCompacted       SessionEventType = "context.compacted"
	EventUsageUpdated           SessionEventType = "usage.updated"
	EventModelChanged           SessionEventType = "model.changed"
	EventModelFallback          SessionEventType = "model.fallback"
	EventRetry                  SessionEventType = "retry"
	EventNotice                 SessionEventType = "notice"
	EventError                  SessionEventType = "error"
	EventModeChanged            SessionEventType = "mode.changed"
	EventTurnCompleted          SessionEventType = "turn.completed"
)

// SessionEvent is streamed by the engine (the terminal UI consumes the exact same stream). Switch on the type of
// Data to read the payload:
//
//	session.On(func(e dotcode.SessionEvent) {
//		switch d := e.Data.(type) {
//		case *dotcode.AssistantTextDeltaData:
//			fmt.Print(d.Text)
//		case *dotcode.TurnCompletedData:
//			fmt.Println(d.CostUSD)
//		}
//	})
type SessionEvent struct {
	Type      SessionEventType
	SessionID string
	// ParentToolUseID is set for events produced inside a subagent.
	ParentToolUseID string
	// Data is a pointer to the *Data struct matching Type (*UnknownEventData for unknown types).
	Data SessionEventData
	// Raw is the event as received.
	Raw json.RawMessage
}

// SessionEventData is implemented by every event payload.
type SessionEventData interface{ eventType() SessionEventType }

type UserMessageData struct {
	Text string `json:"text"`
}
type AssistantTextDeltaData struct {
	Text string `json:"text"`
}
type AssistantThinkingDeltaData struct {
	Text string `json:"text"`
}

// ToolCallInfo is a tool call requested by the model.
type ToolCallInfo struct {
	ID    string          `json:"id"`
	Name  string          `json:"name"`
	Input json.RawMessage `json:"input"`
}
type AssistantMessageData struct {
	MessageID string         `json:"messageId"`
	Text      string         `json:"text"`
	Thinking  string         `json:"thinking,omitempty"`
	ToolCalls []ToolCallInfo `json:"toolCalls"`
	Model     string         `json:"model"`
}
type ToolStartedData struct {
	ToolUseID   string          `json:"toolUseId"`
	Name        string          `json:"name"`
	DisplayName string          `json:"displayName"`
	Input       json.RawMessage `json:"input"`
}
type ToolProgressData struct {
	ToolUseID string `json:"toolUseId"`
	Text      string `json:"text"`
}
type ToolCompletedData struct {
	ToolUseID  string `json:"toolUseId"`
	Name       string `json:"name"`
	IsError    bool   `json:"isError"`
	Summary    string `json:"summary"`
	Output     string `json:"output"`
	Diff       string `json:"diff,omitempty"`
	DurationMs int64  `json:"durationMs"`
	Rejected   bool   `json:"rejected"`
}

// TodoItem is one entry of the agent's todo list.
type TodoItem struct {
	Content    string `json:"content"`
	Status     string `json:"status"` // Pending | InProgress | Completed
	ActiveForm string `json:"activeForm,omitempty"`
}
type TodoUpdatedData struct {
	Todos []TodoItem `json:"todos"`
}
type SubagentStartedData struct {
	ToolUseID   string `json:"toolUseId"`
	AgentType   string `json:"agentType"`
	Description string `json:"description"`
	Model       string `json:"model"`
}
type SubagentCompletedData struct {
	ToolUseID  string `json:"toolUseId"`
	AgentType  string `json:"agentType"`
	Usage      Usage  `json:"usage"`
	DurationMs int64  `json:"durationMs"`
	ToolUses   int    `json:"toolUses"`
}
type ContextCompactedData struct {
	TokensBefore int64 `json:"tokensBefore"`
	TokensAfter  int64 `json:"tokensAfter"`
	Automatic    bool  `json:"automatic"`
}
type UsageUpdatedData struct {
	TurnUsage      Usage   `json:"turnUsage"`
	SessionUsage   Usage   `json:"sessionUsage"`
	SessionCostUSD float64 `json:"sessionCostUsd"`
	ContextTokens  int64   `json:"contextTokens"`
	ContextWindow  int     `json:"contextWindow"`
}
type ModelChangedData struct {
	Model string `json:"model"`
}
type ModelFallbackData struct {
	From   string `json:"from"`
	To     string `json:"to"`
	Reason string `json:"reason"`
}
type RetryData struct {
	Attempt      int     `json:"attempt"`
	MaxAttempts  int     `json:"maxAttempts"`
	DelaySeconds float64 `json:"delaySeconds"`
	Reason       string  `json:"reason"`
}
type NoticeData struct {
	Level string `json:"level"` // Info | Warning | Error
	Text  string `json:"text"`
}
type ErrorData struct {
	Code      string `json:"code"`
	Message   string `json:"message"`
	Retryable bool   `json:"retryable"`
}
type ModeChangedData struct {
	Mode PermissionMode `json:"mode"`
}
type TurnCompletedData struct {
	StopReason    StopReason `json:"stopReason"`
	ResultText    string     `json:"resultText"`
	Usage         Usage      `json:"usage"`
	CostUSD       float64    `json:"costUsd"`
	DurationMs    int64      `json:"durationMs"`
	NumModelCalls int        `json:"numModelCalls"`
	IsError       bool       `json:"isError"`
}

// UnknownEventData holds an event type this SDK version does not know.
type UnknownEventData struct{ Type SessionEventType }

func (*UserMessageData) eventType() SessionEventType            { return EventUserMessage }
func (*AssistantTextDeltaData) eventType() SessionEventType     { return EventAssistantTextDelta }
func (*AssistantThinkingDeltaData) eventType() SessionEventType { return EventAssistantThinkingDelta }
func (*AssistantMessageData) eventType() SessionEventType       { return EventAssistantMessage }
func (*ToolStartedData) eventType() SessionEventType            { return EventToolStarted }
func (*ToolProgressData) eventType() SessionEventType           { return EventToolProgress }
func (*ToolCompletedData) eventType() SessionEventType          { return EventToolCompleted }
func (*TodoUpdatedData) eventType() SessionEventType            { return EventTodoUpdated }
func (*SubagentStartedData) eventType() SessionEventType        { return EventSubagentStarted }
func (*SubagentCompletedData) eventType() SessionEventType      { return EventSubagentCompleted }
func (*ContextCompactedData) eventType() SessionEventType       { return EventContextCompacted }
func (*UsageUpdatedData) eventType() SessionEventType           { return EventUsageUpdated }
func (*ModelChangedData) eventType() SessionEventType           { return EventModelChanged }
func (*ModelFallbackData) eventType() SessionEventType          { return EventModelFallback }
func (*RetryData) eventType() SessionEventType                  { return EventRetry }
func (*NoticeData) eventType() SessionEventType                 { return EventNotice }
func (*ErrorData) eventType() SessionEventType                  { return EventError }
func (*ModeChangedData) eventType() SessionEventType            { return EventModeChanged }
func (*TurnCompletedData) eventType() SessionEventType          { return EventTurnCompleted }
func (d *UnknownEventData) eventType() SessionEventType         { return d.Type }

func newEventData(t SessionEventType) SessionEventData {
	switch t {
	case EventUserMessage:
		return &UserMessageData{}
	case EventAssistantTextDelta:
		return &AssistantTextDeltaData{}
	case EventAssistantThinkingDelta:
		return &AssistantThinkingDeltaData{}
	case EventAssistantMessage:
		return &AssistantMessageData{}
	case EventToolStarted:
		return &ToolStartedData{}
	case EventToolProgress:
		return &ToolProgressData{}
	case EventToolCompleted:
		return &ToolCompletedData{}
	case EventTodoUpdated:
		return &TodoUpdatedData{}
	case EventSubagentStarted:
		return &SubagentStartedData{}
	case EventSubagentCompleted:
		return &SubagentCompletedData{}
	case EventContextCompacted:
		return &ContextCompactedData{}
	case EventUsageUpdated:
		return &UsageUpdatedData{}
	case EventModelChanged:
		return &ModelChangedData{}
	case EventModelFallback:
		return &ModelFallbackData{}
	case EventRetry:
		return &RetryData{}
	case EventNotice:
		return &NoticeData{}
	case EventError:
		return &ErrorData{}
	case EventModeChanged:
		return &ModeChangedData{}
	case EventTurnCompleted:
		return &TurnCompletedData{}
	}
	return &UnknownEventData{Type: t}
}

func parseEvent(raw json.RawMessage) SessionEvent {
	var head struct {
		Type            SessionEventType `json:"type"`
		SessionID       string           `json:"sessionId"`
		ParentToolUseID string           `json:"parentToolUseId"`
	}
	_ = json.Unmarshal(raw, &head)
	data := newEventData(head.Type)
	if _, unknown := data.(*UnknownEventData); !unknown {
		_ = json.Unmarshal(raw, data)
	}
	return SessionEvent{Type: head.Type, SessionID: head.SessionID, ParentToolUseID: head.ParentToolUseID, Data: data, Raw: raw}
}
