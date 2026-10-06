package marbots

import (
	"errors"
	"strings"
	"time"
)

// BossMan is the id of the protected manager bot.
const BossMan = "boss-man"

// KernelPack names a built-in tool pack a bot can enable.
type KernelPack string

const (
	KernelPackFiles  KernelPack = "files"
	KernelPackSearch KernelPack = "search"
	KernelPackShell  KernelPack = "shell"
	KernelPackWeb    KernelPack = "web"
	KernelPackMemory KernelPack = "memory"
	KernelPackTodo   KernelPack = "todo"
	KernelPackAgents KernelPack = "agents"
)

// PermissionProfile decides what a bot may do without asking.
type PermissionProfile string

const (
	PermissionReadOnly       PermissionProfile = "read-only"
	PermissionWorkspaceWrite PermissionProfile = "workspace-write"
	PermissionDeveloperSafe  PermissionProfile = "developer-safe"
	PermissionAutonomous     PermissionProfile = "autonomous"
	PermissionManager        PermissionProfile = "manager"
)

// BotStatus is a bot's lifecycle state.
type BotStatus string

const (
	BotStatusReady    BotStatus = "Ready"
	BotStatusRunning  BotStatus = "Running"
	BotStatusPaused   BotStatus = "Paused"
	BotStatusArchived BotStatus = "Archived"
	BotStatusDegraded BotStatus = "Degraded"
)

// TaskState is a task's lifecycle state.
type TaskState string

const (
	TaskQueued          TaskState = "Queued"
	TaskPreparing       TaskState = "Preparing"
	TaskRunning         TaskState = "Running"
	TaskWaitingForTool  TaskState = "WaitingForTool"
	TaskWaitingForAgent TaskState = "WaitingForAgent"
	TaskWaitingForHuman TaskState = "WaitingForHuman"
	TaskCompleted       TaskState = "Completed"
	TaskFailed          TaskState = "Failed"
	TaskCancelled       TaskState = "Cancelled"
	TaskTimedOut        TaskState = "TimedOut"
)

// Terminal reports whether the task has finished.
func (s TaskState) Terminal() bool {
	return s == TaskCompleted || s == TaskFailed || s == TaskCancelled || s == TaskTimedOut
}

// AutoLearnMode controls optional learning after tasks.
type AutoLearnMode string

const (
	AutoLearnOff           AutoLearnMode = "Off"
	AutoLearnMemoryOnly    AutoLearnMode = "MemoryOnly"
	AutoLearnSuggestSkills AutoLearnMode = "SuggestSkills"
)

// ApprovalScope is how far an approval reaches.
type ApprovalScope string

const (
	ApprovalOnce    ApprovalScope = "Once"
	ApprovalSession ApprovalScope = "Session"
)

// ApprovalState is the state of an approval request.
type ApprovalState string

const (
	ApprovalPending  ApprovalState = "Pending"
	ApprovalApproved ApprovalState = "Approved"
	ApprovalRejected ApprovalState = "Rejected"
	ApprovalExpired  ApprovalState = "Expired"
)

// MemoryKind classifies a long-term memory.
type MemoryKind string

const (
	MemorySemantic   MemoryKind = "Semantic"
	MemoryEpisodic   MemoryKind = "Episodic"
	MemoryProcedural MemoryKind = "Procedural"
	MemoryRelational MemoryKind = "Relational"
	MemoryArtifact   MemoryKind = "Artifact"
)

// EventType names an event on the live stream.
type EventType string

const (
	EventBotCreated                EventType = "BotCreated"
	EventBotUpdated                EventType = "BotUpdated"
	EventBotDeleted                EventType = "BotDeleted"
	EventBotStateChanged           EventType = "BotStateChanged"
	EventMessageAdded              EventType = "MessageAdded"
	EventTaskCreated               EventType = "TaskCreated"
	EventTaskStateChanged          EventType = "TaskStateChanged"
	EventTaskDelegated             EventType = "TaskDelegated"
	EventTaskProgressed            EventType = "TaskProgressed"
	EventAgentThinkingStarted      EventType = "AgentThinkingStarted"
	EventAgentThinkingCompleted    EventType = "AgentThinkingCompleted"
	EventToolCallStarted           EventType = "ToolCallStarted"
	EventToolCallCompleted         EventType = "ToolCallCompleted"
	EventApprovalRequested         EventType = "ApprovalRequested"
	EventApprovalResolved          EventType = "ApprovalResolved"
	EventMemoryWritten             EventType = "MemoryWritten"
	EventSkillLoaded               EventType = "SkillLoaded"
	EventContextCompacted          EventType = "ContextCompacted"
	EventAutoLearnCandidateCreated EventType = "AutoLearnCandidateCreated"
	EventScheduleTriggered         EventType = "ScheduleTriggered"
	EventHostConnected             EventType = "HostConnected"
	EventTodoUpdated               EventType = "TodoUpdated"
	EventSettingsChanged           EventType = "SettingsChanged"
)

// ModelDefault makes a bot follow the workspace default model.
const ModelDefault = "default"

// ModelOf builds a "provider/model" setting, e.g. ModelOf("azure", "gpt-5.6-luna").
func ModelOf(provider, model string) (string, error) {
	if provider == "" || model == "" || strings.Contains(provider, "/") {
		return "", errors.New("marbots: provider and model are required; provider cannot contain '/'")
	}
	return provider + "/" + model, nil
}

// MustModel is ModelOf that panics on invalid input (handy for constants in code).
func MustModel(provider, model string) string {
	m, err := ModelOf(provider, model)
	if err != nil {
		panic(err)
	}
	return m
}

// Bool returns a pointer to b (for optional boolean options).
func Bool(b bool) *bool { return &b }

// ---------------------------------------------------------------- bots & templates

// Bot is a durable AI teammate.
type Bot struct {
	ID          string `json:"id"`
	Name        string `json:"name"`
	Role        string `json:"role"`
	Description string `json:"description"`
	Persona     string `json:"persona"`
	Color       string `json:"color"`
	// Model is "default" (workspace default), a profile name, or "provider/model".
	Model             string            `json:"modelProfile"`
	ShortTermMemory   bool              `json:"shortTermMemory"`
	LongTermMemory    bool              `json:"longTermMemory"`
	AutoLearn         AutoLearnMode     `json:"autoLearn"`
	Skills            []string          `json:"skills"`
	McpServers        []string          `json:"mcpServers"`
	KernelFunctions   []KernelPack      `json:"kernelFunctions"`
	PermissionProfile PermissionProfile `json:"permissionProfile"`
	HostRef           string            `json:"hostRef"`
	MaxSteps          int               `json:"maxSteps"`
	IsSystem          bool              `json:"isSystem"`
	Status            BotStatus         `json:"status"`
	TemplateID        string            `json:"templateId,omitempty"`
}

// UsesDefaultModel reports whether the bot follows the workspace default model.
func (b Bot) UsesDefaultModel() bool { return b.Model == "" || b.Model == ModelDefault }

// BotSpec are the options for creating or updating a bot. Zero values take the server defaults.
type BotSpec struct {
	Name        string
	Role        string
	Description string
	Persona     string
	Color       string
	// Model: ModelDefault, ModelOf(provider, model) or a profile name. Empty means ModelDefault.
	Model             string
	KernelFunctions   []KernelPack
	Skills            []string
	McpServers        []string
	PermissionProfile PermissionProfile
	AutoLearn         AutoLearnMode
	ShortTermMemory   *bool
	LongTermMemory    *bool
	MaxSteps          int
}

func (s BotSpec) wire(id string) map[string]any {
	or := func(v, d string) string {
		if v == "" {
			return d
		}
		return v
	}
	packs := s.KernelFunctions
	if packs == nil {
		packs = []KernelPack{KernelPackFiles, KernelPackSearch, KernelPackWeb, KernelPackMemory, KernelPackTodo}
	}
	boolOr := func(p *bool) bool { return p == nil || *p }
	steps := s.MaxSteps
	if steps == 0 {
		steps = 24
	}
	return map[string]any{
		"id": id, "name": s.Name, "role": s.Role, "description": s.Description, "persona": s.Persona,
		"color": or(s.Color, "#2C3BA3"), "modelProfile": or(s.Model, ModelDefault), "kernelFunctions": packs,
		"skills": nonNil(s.Skills), "mcpServers": nonNil(s.McpServers),
		"permissionProfile": or(string(s.PermissionProfile), string(PermissionDeveloperSafe)),
		"autoLearn":         or(string(s.AutoLearn), string(AutoLearnOff)), "shortTermMemory": boolOr(s.ShortTermMemory),
		"longTermMemory": boolOr(s.LongTermMemory), "maxSteps": steps,
	}
}

func nonNil(v []string) []string {
	if v == nil {
		return []string{}
	}
	return v
}

// Template is a ready-made bot role from the gallery.
type Template struct {
	ID                string            `json:"id"`
	Name              string            `json:"name"`
	Category          string            `json:"category"`
	Role              string            `json:"role"`
	Description       string            `json:"description"`
	Persona           string            `json:"persona"`
	Model             string            `json:"modelProfile"`
	Skills            []string          `json:"skills"`
	McpServers        []string          `json:"mcpServers"`
	KernelFunctions   []KernelPack      `json:"kernelFunctions"`
	Tags              []string          `json:"tags"`
	PermissionProfile PermissionProfile `json:"permissionProfile"`
	IsBuiltIn         bool              `json:"isBuiltIn"`
}

// BotModelInfo is a bot's model setting and the model it actually runs on.
type BotModelInfo struct {
	BotID       string `json:"botId"`
	Setting     string `json:"setting"`
	Effective   string `json:"effective"`
	UsesDefault bool   `json:"usesDefault"`
	Warning     string `json:"warning,omitempty"`
}

// ModelProfileInfo is a named model profile.
type ModelProfileInfo struct {
	Name      string   `json:"name"`
	Provider  string   `json:"provider"`
	Model     string   `json:"model"`
	Fallbacks []string `json:"fallbacks"`
}

// ModelCatalog lists the default model, the choices and the profiles.
type ModelCatalog struct {
	Default  string             `json:"default"`
	Choices  []string           `json:"choices"`
	Profiles []ModelProfileInfo `json:"profiles"`
}

// ---------------------------------------------------------------- threads, messages, tasks

// Thread is a conversation with one bot.
type Thread struct {
	ID        string    `json:"id"`
	Title     string    `json:"title"`
	BotID     string    `json:"botId"`
	Pinned    bool      `json:"pinned"`
	Archived  bool      `json:"archived"`
	UpdatedAt time.Time `json:"updatedAt"`
}

// ToolCall is a tool invocation requested by a model.
type ToolCall struct {
	ID        string `json:"id"`
	Name      string `json:"name"`
	Arguments string `json:"arguments"`
}

// Message is one chat message.
type Message struct {
	ID         string     `json:"id"`
	ThreadID   string     `json:"threadId"`
	Seq        int64      `json:"seq"`
	Role       string     `json:"role"`
	Author     string     `json:"author"`
	Content    string     `json:"content"`
	ToolCalls  []ToolCall `json:"toolCalls,omitempty"`
	ToolCallID string     `json:"toolCallId,omitempty"`
	ToolName   string     `json:"toolName,omitempty"`
	TaskID     string     `json:"taskId,omitempty"`
	CreatedAt  time.Time  `json:"createdAt"`
}

// Task is a durable unit of work.
type Task struct {
	ID              string    `json:"id"`
	ParentTaskID    string    `json:"parentTaskId,omitempty"`
	ThreadID        string    `json:"threadId"`
	BotID           string    `json:"botId"`
	Depth           int       `json:"depth"`
	Objective       string    `json:"objective"`
	State           TaskState `json:"state"`
	Result          string    `json:"result,omitempty"`
	Error           string    `json:"error,omitempty"`
	CurrentActivity string    `json:"currentActivity,omitempty"`
	// Model is the provider/model that served the latest step.
	Model        string  `json:"model,omitempty"`
	Steps        int     `json:"steps"`
	InputTokens  int64   `json:"inputTokens"`
	OutputTokens int64   `json:"outputTokens"`
	CostUSD      float64 `json:"costUsd"`
}

// SendResult is returned by Threads.Send.
type SendResult struct {
	Task  Task     `json:"task"`
	Reply *Message `json:"reply,omitempty"`
}

// Text is the reply text (or the task result/error).
func (r SendResult) Text() string {
	if r.Reply != nil {
		return r.Reply.Content
	}
	if r.Task.Result != "" {
		return r.Task.Result
	}
	return r.Task.Error
}

// SendOptions control Threads.Send.
type SendOptions struct {
	// Wait for the bot to finish.
	Wait bool
	// TimeoutSeconds when waiting (default 600).
	TimeoutSeconds int
}

// WorkspaceFile is a file in a thread's project workspace.
type WorkspaceFile struct {
	Path     string    `json:"path"`
	Size     int64     `json:"size"`
	Modified time.Time `json:"modified"`
}

// ---------------------------------------------------------------- approvals, events, memory, skills, mcp, schedules

// Approval is a risky action waiting for (or resolved by) a human.
type Approval struct {
	ID         string        `json:"id"`
	TaskID     string        `json:"taskId"`
	ThreadID   string        `json:"threadId"`
	BotID      string        `json:"botId"`
	ToolName   string        `json:"toolName"`
	Arguments  string        `json:"arguments"`
	Category   string        `json:"category"`
	Risk       string        `json:"risk"`
	Reason     string        `json:"reason"`
	State      ApprovalState `json:"state"`
	ResolvedBy string        `json:"resolvedBy,omitempty"`
}

// Event is one item on the live event stream.
type Event struct {
	ID        int64     `json:"id"`
	Type      EventType `json:"type"`
	Timestamp time.Time `json:"timestamp"`
	ThreadID  string    `json:"threadId,omitempty"`
	TaskID    string    `json:"taskId,omitempty"`
	BotID     string    `json:"botId,omitempty"`
	Message   string    `json:"message,omitempty"`
	Data      string    `json:"data,omitempty"`
}

// TaskFinished reports whether the event marks a task as finished.
func (e Event) TaskFinished() bool {
	return e.Type == EventTaskStateChanged && TaskState(e.Data).Terminal()
}

// Memory is a long-term memory record.
type Memory struct {
	ID         string     `json:"id"`
	Owner      string     `json:"owner"`
	Kind       MemoryKind `json:"kind"`
	Content    string     `json:"content"`
	Source     string     `json:"source"`
	Confidence float64    `json:"confidence"`
}

// Skill is an installed SKILL.md package.
type Skill struct {
	Name        string `json:"name"`
	Version     string `json:"version"`
	Description string `json:"description"`
	Trust       string `json:"trust"`
	Source      string `json:"source"`
	Pending     bool   `json:"pending"`
}

// MCPServer is an MCP server from the gallery.
type MCPServer struct {
	ID             string   `json:"id"`
	Name           string   `json:"name"`
	Description    string   `json:"description"`
	Transport      string   `json:"transport"`
	Command        string   `json:"command,omitempty"`
	Args           []string `json:"args"`
	URL            string   `json:"url,omitempty"`
	Trust          string   `json:"trust"`
	IsCatalogEntry bool     `json:"isCatalogEntry"`
}

// ScheduleSpec describes a recurring (Cron) or one-off (RunAt) job.
type ScheduleSpec struct {
	Name     string     `json:"name"`
	BotID    string     `json:"botId"`
	Prompt   string     `json:"prompt"`
	Cron     string     `json:"cron,omitempty"`
	RunAt    *time.Time `json:"runAt,omitempty"`
	TimeZone string     `json:"timeZone,omitempty"`
}

// Schedule is a saved job.
type Schedule struct {
	ID        string     `json:"id"`
	Name      string     `json:"name"`
	BotID     string     `json:"botId"`
	Prompt    string     `json:"prompt"`
	Cron      string     `json:"cron"`
	TimeZone  string     `json:"timeZone"`
	Enabled   bool       `json:"enabled"`
	NextRunAt *time.Time `json:"nextRunAt,omitempty"`
	LastRunAt *time.Time `json:"lastRunAt,omitempty"`
}

// Host is a machine that runs bots.
type Host struct {
	ID             string `json:"id"`
	Name           string `json:"name"`
	Kind           string `json:"kind"`
	OS             string `json:"os"`
	Status         string `json:"status"`
	ProcessorCount int    `json:"processorCount"`
}

// SystemInfo describes the server.
type SystemInfo struct {
	Product         string   `json:"product"`
	Version         string   `json:"version"`
	Credits         string   `json:"credits"`
	CreditsEn       string   `json:"creditsEn"`
	ModelConfigured bool     `json:"modelConfigured"`
	Profiles        []string `json:"profiles"`
}
