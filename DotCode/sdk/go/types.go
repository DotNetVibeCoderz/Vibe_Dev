package dotcode

import "encoding/json"

// Usage reports token consumption.
type Usage struct {
	InputTokens      int64 `json:"inputTokens"`
	OutputTokens     int64 `json:"outputTokens"`
	CacheReadTokens  int64 `json:"cacheReadTokens"`
	CacheWriteTokens int64 `json:"cacheWriteTokens"`
	ReasoningTokens  int64 `json:"reasoningTokens"`
}

// Event is an engine event (text deltas, tool calls, usage, turn completion...). Type is one of:
// session.started, user.message, assistant.text.delta, assistant.thinking.delta, assistant.message,
// tool.started, tool.progress, tool.completed, todo.updated, subagent.started, subagent.completed,
// context.compacted, usage.updated, model.changed, model.fallback, retry, notice, error, mode.changed,
// turn.completed. Commonly used fields are decoded; Raw holds the full JSON.
type Event struct {
	Type            string          `json:"type"`
	SessionID       string          `json:"sessionId,omitempty"`
	ParentToolUseID string          `json:"parentToolUseId,omitempty"`
	Text            string          `json:"text,omitempty"`
	ToolUseID       string          `json:"toolUseId,omitempty"`
	Name            string          `json:"name,omitempty"`
	DisplayName     string          `json:"displayName,omitempty"`
	Summary         string          `json:"summary,omitempty"`
	Output          string          `json:"output,omitempty"`
	Diff            string          `json:"diff,omitempty"`
	IsError         bool            `json:"isError,omitempty"`
	Model           string          `json:"model,omitempty"`
	ResultText      string          `json:"resultText,omitempty"`
	StopReason      string          `json:"stopReason,omitempty"`
	CostUSD         float64         `json:"costUsd,omitempty"`
	DurationMs      int64           `json:"durationMs,omitempty"`
	NumModelCalls   int             `json:"numModelCalls,omitempty"`
	Message         string          `json:"message,omitempty"`
	Raw             json.RawMessage `json:"-"`
}

// PermissionRequest is sent when a tool call needs approval.
type PermissionRequest struct {
	ToolUseID     string          `json:"toolUseId"`
	ToolName      string          `json:"toolName"`
	DisplayName   string          `json:"displayName"`
	Input         json.RawMessage `json:"input"`
	Title         string          `json:"title"`
	Detail        string          `json:"detail,omitempty"`
	SuggestedRule string          `json:"suggestedRule,omitempty"`
	Diff          string          `json:"diff,omitempty"`
}

// PermissionDecision answers a PermissionRequest. Decision: allow | allow_always | allow_session | deny.
type PermissionDecision struct {
	Decision     string          `json:"decision"`
	Feedback     string          `json:"feedback,omitempty"`
	Rule         string          `json:"rule,omitempty"`
	UpdatedInput json.RawMessage `json:"updatedInput,omitempty"`
}

// Allow and Deny are convenience decisions.
var (
	Allow = PermissionDecision{Decision: "allow"}
	Deny  = PermissionDecision{Decision: "deny"}
)

// UserQuestion is asked by the model through the AskUserQuestion tool.
type UserQuestion struct {
	Question    string `json:"question"`
	Header      string `json:"header"`
	MultiSelect bool   `json:"multiSelect,omitempty"`
	Options     []struct {
		Label       string `json:"label"`
		Description string `json:"description,omitempty"`
	} `json:"options"`
}

// UserQuestionAnswer answers one UserQuestion.
type UserQuestionAnswer struct {
	Question string `json:"question"`
	Answer   string `json:"answer"`
}

// SendResult is the outcome of one turn.
type SendResult struct {
	SessionID     string  `json:"sessionId"`
	StopReason    string  `json:"stopReason"`
	Result        string  `json:"result"`
	IsError       bool    `json:"isError"`
	Error         string  `json:"error,omitempty"`
	DurationMs    int64   `json:"durationMs"`
	NumModelCalls int     `json:"numModelCalls"`
	CostUSD       float64 `json:"costUsd"`
	TotalCostUSD  float64 `json:"totalCostUsd"`
	Usage         Usage   `json:"usage"`
}

// SessionInfo describes a created session.
type SessionInfo struct {
	SessionID      string   `json:"sessionId"`
	Model          string   `json:"model"`
	Cwd            string   `json:"cwd"`
	PermissionMode string   `json:"permissionMode"`
	MessageCount   int      `json:"messageCount"`
	TotalCostUSD   float64  `json:"totalCostUsd"`
	TranscriptPath string   `json:"transcriptPath,omitempty"`
	Tools          []string `json:"tools"`
}

// McpServer configures an MCP server for a session.
type McpServer struct {
	Type    string            `json:"type,omitempty"`
	Command string            `json:"command,omitempty"`
	Args    []string          `json:"args,omitempty"`
	Env     map[string]string `json:"env,omitempty"`
	URL     string            `json:"url,omitempty"`
	Headers map[string]string `json:"headers,omitempty"`
}
