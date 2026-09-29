package dotcode

import "encoding/json"

// PermissionMode controls how tool calls are approved.
type PermissionMode string

const (
	PermissionModeDefault           PermissionMode = "default"
	PermissionModeAcceptEdits       PermissionMode = "acceptEdits"
	PermissionModeAuto              PermissionMode = "auto"
	PermissionModePlan              PermissionMode = "plan"
	PermissionModeBypassPermissions PermissionMode = "bypassPermissions"
)

// ReasoningEffort is the thinking budget for models that support it.
type ReasoningEffort string

const (
	ReasoningEffortOff    ReasoningEffort = "off"
	ReasoningEffortLow    ReasoningEffort = "low"
	ReasoningEffortMedium ReasoningEffort = "medium"
	ReasoningEffortHigh   ReasoningEffort = "high"
	ReasoningEffortXHigh  ReasoningEffort = "xhigh"
)

// BuiltinTool names a built-in tool (for SessionConfig.AvailableTools and permission rules).
type BuiltinTool string

const (
	BuiltinToolRead            BuiltinTool = "Read"
	BuiltinToolWrite           BuiltinTool = "Write"
	BuiltinToolEdit            BuiltinTool = "Edit"
	BuiltinToolNotebookEdit    BuiltinTool = "NotebookEdit"
	BuiltinToolGlob            BuiltinTool = "Glob"
	BuiltinToolGrep            BuiltinTool = "Grep"
	BuiltinToolBash            BuiltinTool = "Bash"
	BuiltinToolPowerShell      BuiltinTool = "PowerShell"
	BuiltinToolBashOutput      BuiltinTool = "BashOutput"
	BuiltinToolKillShell       BuiltinTool = "KillShell"
	BuiltinToolWebFetch        BuiltinTool = "WebFetch"
	BuiltinToolWebSearch       BuiltinTool = "WebSearch"
	BuiltinToolTodoWrite       BuiltinTool = "TodoWrite"
	BuiltinToolAgent           BuiltinTool = "Agent"
	BuiltinToolSkill           BuiltinTool = "Skill"
	BuiltinToolAskUserQuestion BuiltinTool = "AskUserQuestion"
	BuiltinToolExitPlanMode    BuiltinTool = "ExitPlanMode"
	BuiltinToolLSP             BuiltinTool = "LSP"
)

// Rule builds a permission rule for the tool, e.g. BuiltinToolBash.Rule("npm test:*") → "Bash(npm test:*)".
func (t BuiltinTool) Rule(specifier string) string { return string(t) + "(" + specifier + ")" }

// Bool returns a pointer to b (for optional boolean options).
func Bool(b bool) *bool { return &b }

// ---------------------------------------------------------------- permissions

// PermissionRequest is sent when a tool call needs approval.
type PermissionRequest struct {
	ToolUseID   string          `json:"toolUseId"`
	ToolName    string          `json:"toolName"`
	DisplayName string          `json:"displayName"`
	Input       json.RawMessage `json:"input"`
	Title       string          `json:"title"`
	Detail      string          `json:"detail,omitempty"`
	// SuggestedRule is a rule the user could persist, e.g. "Bash(npm test:*)".
	SuggestedRule string `json:"suggestedRule,omitempty"`
	// Diff is a unified diff preview for file edits.
	Diff            string `json:"diff,omitempty"`
	ParentToolUseID string `json:"parentToolUseId,omitempty"`
}

// Invocation is the context passed to handlers.
type Invocation struct {
	SessionID string
}

// PermissionDecision answers a PermissionRequest. It is implemented by the PermissionDecision* types.
type PermissionDecision interface {
	permissionWire() map[string]any
}

// PermissionDecisionApproveOnce allows this single call (optionally with modified input).
type PermissionDecisionApproveOnce struct{ UpdatedInput map[string]any }

// PermissionDecisionApproveForSession allows matching calls for the rest of the session.
type PermissionDecisionApproveForSession struct{ UpdatedInput map[string]any }

// PermissionDecisionApproveAlways allows and persists a rule (default: the request's suggested rule).
type PermissionDecisionApproveAlways struct{ Rule string }

// PermissionDecisionReject denies the call; Feedback is returned to the model.
type PermissionDecisionReject struct{ Feedback string }

func (d *PermissionDecisionApproveOnce) permissionWire() map[string]any {
	return compact(map[string]any{"decision": "allow", "updatedInput": nilIfEmpty(d.UpdatedInput)})
}
func (d *PermissionDecisionApproveForSession) permissionWire() map[string]any {
	return compact(map[string]any{"decision": "allow_session", "updatedInput": nilIfEmpty(d.UpdatedInput)})
}
func (d *PermissionDecisionApproveAlways) permissionWire() map[string]any {
	return compact(map[string]any{"decision": "allow_always", "rule": d.Rule})
}
func (d *PermissionDecisionReject) permissionWire() map[string]any {
	return compact(map[string]any{"decision": "deny", "feedback": d.Feedback})
}

// PermissionHandlerFunc decides whether a tool call may run.
type PermissionHandlerFunc func(request PermissionRequest, invocation Invocation) (PermissionDecision, error)

// PermissionHandler holds ready-made handlers.
var PermissionHandler = struct {
	// ApproveAll approves every request.
	ApproveAll PermissionHandlerFunc
	// RejectAll rejects every request (same as no handler).
	RejectAll PermissionHandlerFunc
}{
	ApproveAll: func(PermissionRequest, Invocation) (PermissionDecision, error) {
		return &PermissionDecisionApproveOnce{}, nil
	},
	RejectAll: func(PermissionRequest, Invocation) (PermissionDecision, error) {
		return &PermissionDecisionReject{Feedback: "Rejected by the SDK host."}, nil
	},
}

// ---------------------------------------------------------------- user input & plan mode

// QuestionOption is one choice of a UserQuestion.
type QuestionOption struct {
	Label       string `json:"label"`
	Description string `json:"description,omitempty"`
}

// UserQuestion is asked by the model through the AskUserQuestion tool.
type UserQuestion struct {
	Question    string           `json:"question"`
	Header      string           `json:"header"`
	Options     []QuestionOption `json:"options"`
	MultiSelect bool             `json:"multiSelect,omitempty"`
}

// UserInputRequest carries the model's questions.
type UserInputRequest struct {
	Questions []UserQuestion
}

// UserQuestionAnswer answers one UserQuestion (option labels, comma-separated for multi-select, or free text).
type UserQuestionAnswer struct {
	Question string `json:"question"`
	Answer   string `json:"answer"`
}

// UserInputHandlerFunc answers AskUserQuestion.
type UserInputHandlerFunc func(request UserInputRequest, invocation Invocation) ([]UserQuestionAnswer, error)

// ExitPlanModeRequest carries the plan (markdown) the agent wants to execute.
type ExitPlanModeRequest struct {
	Plan string
}

// ExitPlanModeResult approves or rejects a plan.
type ExitPlanModeResult struct {
	Approved bool
	// AcceptEdits continues in acceptEdits mode (when approved).
	AcceptEdits bool
	// Feedback explains a rejection to the model.
	Feedback string
}

// ExitPlanModeHandlerFunc reviews the plan when the agent leaves plan mode.
type ExitPlanModeHandlerFunc func(request ExitPlanModeRequest, invocation Invocation) (ExitPlanModeResult, error)

// ---------------------------------------------------------------- configuration

// ProviderType is the wire protocol of a model provider.
type ProviderType string

const (
	ProviderAnthropic        ProviderType = "anthropic"
	ProviderOpenAI           ProviderType = "openai"
	ProviderAzure            ProviderType = "azure"
	ProviderGemini           ProviderType = "gemini"
	ProviderDeepSeek         ProviderType = "deepseek"
	ProviderOllama           ProviderType = "ollama"
	ProviderOpenAICompatible ProviderType = "openai-compatible"
	ProviderBedrock          ProviderType = "bedrock"
	ProviderVertex           ProviderType = "vertex"
	ProviderVertexGemini     ProviderType = "vertex-gemini"
	ProviderMock             ProviderType = "mock"
)

// ProviderConfig is a named model provider (BYOK). Values may reference environment variables: "${env:MY_KEY}".
type ProviderConfig struct {
	Type    ProviderType      `json:"type"`
	BaseURL string            `json:"baseUrl,omitempty"`
	APIKey  string            `json:"apiKey,omitempty"`
	API     string            `json:"api,omitempty"` // OpenAI family: "responses" or "chat"
	Headers map[string]string `json:"headers,omitempty"`
	// Profile is the quirk profile for OpenAI-compatible servers (deepseek, openrouter, lmstudio, vllm, …).
	Profile        string   `json:"profile,omitempty"`
	Models         []string `json:"models,omitempty"`
	TimeoutSeconds int      `json:"timeoutSeconds,omitempty"`
	NumCtx         int      `json:"numCtx,omitempty"` // Ollama context window
	// Region is the AWS region (bedrock) or Google Cloud location (vertex).
	Region          string `json:"region,omitempty"`
	AWSProfile      string `json:"awsProfile,omitempty"`
	Project         string `json:"project,omitempty"` // Google Cloud project (vertex)
	CredentialsFile string `json:"credentialsFile,omitempty"`
	Auth            string `json:"auth,omitempty"` // azure: "entra" for Microsoft Entra ID tokens
	TenantID        string `json:"tenantId,omitempty"`
	ClientID        string `json:"clientId,omitempty"`
	ClientSecret    string `json:"clientSecret,omitempty"`
	Script          string `json:"script,omitempty"` // scripted provider (tests)
}

// McpServerConfig is an MCP server: *McpStdioServer or *McpHTTPServer.
type McpServerConfig interface{ mcpWire() map[string]any }

// McpStdioServer is an MCP server started as a child process.
type McpStdioServer struct {
	Command string
	Args    []string
	Env     map[string]string
}

// McpHTTPServer is a remote MCP server (streamable HTTP, or legacy SSE with SSE=true).
type McpHTTPServer struct {
	URL     string
	Headers map[string]string
	SSE     bool
}

func (s *McpStdioServer) mcpWire() map[string]any {
	return compact(map[string]any{"type": "stdio", "command": s.Command, "args": s.Args, "env": s.Env})
}
func (s *McpHTTPServer) mcpWire() map[string]any {
	t := "http"
	if s.SSE {
		t = "sse"
	}
	return compact(map[string]any{"type": t, "url": s.URL, "headers": s.Headers})
}

// SystemMessageMode selects how SystemMessageConfig.Content is applied.
type SystemMessageMode string

const (
	SystemMessageAppend  SystemMessageMode = "append"
	SystemMessageReplace SystemMessageMode = "replace"
)

// SystemMessageConfig customizes the system prompt.
type SystemMessageConfig struct {
	Mode    SystemMessageMode // default append
	Content string
}

// Attachment is sent with a message: *FileAttachment or *ImageAttachment.
type Attachment interface{ attachmentWire() map[string]any }

// FileAttachment references a file the agent should read.
type FileAttachment struct{ Path string }

// ImageAttachment is an inline image (base64 data).
type ImageAttachment struct {
	Data      string
	MediaType string // image/png, image/jpeg, image/gif, image/webp
}

func (a *FileAttachment) attachmentWire() map[string]any {
	return map[string]any{"type": "file", "path": a.Path}
}
func (a *ImageAttachment) attachmentWire() map[string]any {
	mt := a.MediaType
	if mt == "" {
		mt = "image/png"
	}
	return map[string]any{"type": "image", "data": a.Data, "mediaType": mt}
}

// MessageOptions is one user message.
type MessageOptions struct {
	Prompt      string
	Attachments []Attachment
}

// ---------------------------------------------------------------- results

// Usage reports token consumption.
type Usage struct {
	InputTokens      int64 `json:"inputTokens"`
	OutputTokens     int64 `json:"outputTokens"`
	CacheReadTokens  int64 `json:"cacheReadTokens"`
	CacheWriteTokens int64 `json:"cacheWriteTokens"`
	ReasoningTokens  int64 `json:"reasoningTokens"`
}

// StopReason says why a turn ended.
type StopReason string

const (
	StopReasonEndTurn      StopReason = "EndTurn"
	StopReasonToolUse      StopReason = "ToolUse"
	StopReasonMaxTokens    StopReason = "MaxTokens"
	StopReasonStopSequence StopReason = "StopSequence"
	StopReasonRefusal      StopReason = "Refusal"
	StopReasonAborted      StopReason = "Aborted"
	StopReasonError        StopReason = "Error"
)

// SendResult is the outcome of one turn.
type SendResult struct {
	SessionID     string     `json:"sessionId"`
	StopReason    StopReason `json:"stopReason"`
	Result        string     `json:"result"`
	IsError       bool       `json:"isError"`
	Error         string     `json:"error,omitempty"`
	DurationMs    int64      `json:"durationMs"`
	NumModelCalls int        `json:"numModelCalls"`
	CostUSD       float64    `json:"costUsd"`
	TotalCostUSD  float64    `json:"totalCostUsd"`
	Usage         Usage      `json:"usage"`
}

// SessionInfo describes a created session.
type SessionInfo struct {
	SessionID      string         `json:"sessionId"`
	Model          string         `json:"model"`
	Cwd            string         `json:"cwd"`
	PermissionMode PermissionMode `json:"permissionMode"`
	MessageCount   int            `json:"messageCount"`
	TotalCostUSD   float64        `json:"totalCostUsd"`
	TranscriptPath string         `json:"transcriptPath,omitempty"`
	Tools          []string       `json:"tools"`
}

// SessionMetadata describes a saved session.
type SessionMetadata struct {
	ID           string `json:"id"`
	Title        string `json:"title,omitempty"`
	FirstPrompt  string `json:"firstPrompt"`
	Modified     string `json:"modified"`
	MessageCount int    `json:"messageCount"`
}

// ModelInfo is a model offered by a configured provider.
type ModelInfo struct {
	Provider    string `json:"provider"`
	ID          string `json:"id"`
	QualifiedID string `json:"qualifiedId"`
}

// ModelList is returned by Client.ListModels.
type ModelList struct {
	Default   string `json:"default"`
	Providers []struct {
		Name string `json:"name"`
		Type string `json:"type"`
	} `json:"providers"`
	Models []ModelInfo `json:"models"`
}

// ToolInfo describes a tool available to the model.
type ToolInfo struct {
	Name        string          `json:"name"`
	Description string          `json:"description"`
	InputSchema json.RawMessage `json:"inputSchema"`
}

func compact(m map[string]any) map[string]any {
	for k, v := range m {
		switch x := v.(type) {
		case nil:
			delete(m, k)
		case string:
			if x == "" {
				delete(m, k)
			}
		case []string:
			if len(x) == 0 {
				delete(m, k)
			}
		case map[string]string:
			if len(x) == 0 {
				delete(m, k)
			}
		case map[string]any:
			if x == nil {
				delete(m, k)
			}
		}
	}
	return m
}

func nilIfEmpty(m map[string]any) any {
	if len(m) == 0 {
		return nil
	}
	return m
}
