// Package marbots is the typed Go client for Marbots, the multi-agent collaboration platform.
//
// Built by Gravicode Studios, led by Kang Fadhil.
//
//	c := marbots.New("http://localhost:5170")
//	_, _ = c.Bots.SetModel(ctx, "atlas", marbots.MustModel("azure", "gpt-5.6-luna"))
//	reply, err := c.Chat(ctx, marbots.BossMan, "Plan a product launch")
package marbots

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// Error is returned for non-2xx responses.
type Error struct {
	Status  int
	Message string
}

func (e *Error) Error() string { return fmt.Sprintf("marbots: HTTP %d: %s", e.Status, e.Message) }

// Option configures a Client.
type Option func(*Client)

// WithAPIKey sets the credential: a tenant key (mbk_...), the platform key (Marbots:ApiKey) or an OIDC access token.
func WithAPIKey(key string) Option { return func(c *Client) { c.apiKey = key } }

// WithTenant picks the tenant to act in with the platform key or a token (or end the base URL in /t/<tenant>).
func WithTenant(tenant string) Option { return func(c *Client) { c.tenant = tenant } }

// WithHTTPClient replaces the HTTP client (timeouts, proxies, tests).
func WithHTTPClient(h *http.Client) Option { return func(c *Client) { c.http = h } }

// Client talks to a Marbots server. Create it with New.
type Client struct {
	baseURL string
	apiKey  string
	tenant  string
	http    *http.Client

	Bots      *BotsAPI
	Templates *TemplatesAPI
	Models    *ModelsAPI
	Threads   *ThreadsAPI
	Tasks     *TasksAPI
	Approvals *ApprovalsAPI
	Skills    *SkillsAPI
	MCP       *MCPAPI
	Schedules *SchedulesAPI
	Memory    *MemoryAPI
	Events    *EventsAPI
	// AgentHosts manages the computers that run bots' tools.
	AgentHosts *AgentHostsAPI
	// Tenancy covers who-am-I, tenants, and this tenant's API keys and members.
	Tenancy *TenancyAPI
}

// New creates a client for baseURL (e.g. "http://localhost:5170").
func New(baseURL string, opts ...Option) *Client {
	c := &Client{baseURL: strings.TrimRight(baseURL, "/"), http: &http.Client{Timeout: 30 * time.Minute}}
	for _, o := range opts {
		o(c)
	}
	c.Bots = &BotsAPI{c}
	c.Templates = &TemplatesAPI{c}
	c.Models = &ModelsAPI{c}
	c.Threads = &ThreadsAPI{c}
	c.Tasks = &TasksAPI{c}
	c.Approvals = &ApprovalsAPI{c}
	c.Skills = &SkillsAPI{c}
	c.MCP = &MCPAPI{c}
	c.Schedules = &SchedulesAPI{c}
	c.Memory = &MemoryAPI{c}
	c.Events = &EventsAPI{c}
	c.AgentHosts = &AgentHostsAPI{c}
	c.Tenancy = &TenancyAPI{c}
	return c
}

// BaseURL is the server address.
func (c *Client) BaseURL() string { return c.baseURL }

func (c *Client) newRequest(ctx context.Context, method, path string, body io.Reader, contentType string) (*http.Request, error) {
	req, err := http.NewRequestWithContext(ctx, method, c.baseURL+path, body)
	if err != nil {
		return nil, err
	}
	if body != nil {
		req.Header.Set("Content-Type", contentType)
	}
	if c.apiKey != "" {
		// OIDC access tokens (JWT) go in Authorization; Marbots keys in X-Api-Key.
		if strings.Count(c.apiKey, ".") == 2 && !strings.HasPrefix(c.apiKey, "mbk_") {
			req.Header.Set("Authorization", "Bearer "+c.apiKey)
		} else {
			req.Header.Set("X-Api-Key", c.apiKey)
		}
	}
	if c.tenant != "" {
		req.Header.Set("X-Marbots-Tenant", c.tenant)
	}
	return req, nil
}

func (c *Client) raw(ctx context.Context, method, path string, body io.Reader, contentType string) ([]byte, error) {
	req, err := c.newRequest(ctx, method, path, body, contentType)
	if err != nil {
		return nil, err
	}
	resp, err := c.http.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, err
	}
	if resp.StatusCode >= 300 {
		msg := string(data)
		var pd struct {
			Detail string `json:"detail"`
			Title  string `json:"title"`
		}
		if json.Unmarshal(data, &pd) == nil {
			if pd.Detail != "" {
				msg = pd.Detail
			} else if pd.Title != "" {
				msg = pd.Title
			}
		}
		return nil, &Error{Status: resp.StatusCode, Message: msg}
	}
	return data, nil
}

func (c *Client) do(ctx context.Context, method, path string, in, out any) error {
	var body io.Reader
	if in != nil {
		b, err := json.Marshal(in)
		if err != nil {
			return err
		}
		body = bytes.NewReader(b)
	} else if method != http.MethodGet {
		body = bytes.NewReader(nil)
	}
	data, err := c.raw(ctx, method, path, body, "application/json")
	if err != nil || out == nil || len(data) == 0 {
		return err
	}
	return json.Unmarshal(data, out)
}

func esc(s string) string { return url.PathEscape(s) }

// System returns server information.
func (c *Client) System(ctx context.Context) (*SystemInfo, error) {
	var out SystemInfo
	return &out, c.do(ctx, http.MethodGet, "/api/v1/system", nil, &out)
}

// Hosts lists the machines that run bots.
func (c *Client) Hosts(ctx context.Context) ([]Host, error) {
	var out []Host
	return out, c.do(ctx, http.MethodGet, "/api/v1/hosts", nil, &out)
}

// Chat creates a thread with bot, sends text, waits, and returns the reply text.
func (c *Client) Chat(ctx context.Context, bot, text string) (string, error) {
	t, err := c.Threads.Create(ctx, bot, "")
	if err != nil {
		return "", err
	}
	r, err := c.Threads.Send(ctx, t.ID, text, SendOptions{Wait: true})
	if err != nil {
		return "", err
	}
	if r.Task.State != TaskCompleted {
		return "", fmt.Errorf("marbots: task %s: %s", r.Task.State, r.Task.Error)
	}
	return r.Text(), nil
}

// ---------------------------------------------------------------- bots

// BotsAPI manages bots.
type BotsAPI struct{ c *Client }

func (a *BotsAPI) List(ctx context.Context) ([]Bot, error) {
	var out []Bot
	return out, a.c.do(ctx, http.MethodGet, "/api/v1/bots", nil, &out)
}

func (a *BotsAPI) Get(ctx context.Context, idOrName string) (*Bot, error) {
	var out Bot
	return &out, a.c.do(ctx, http.MethodGet, "/api/v1/bots/"+esc(idOrName), nil, &out)
}

func (a *BotsAPI) Create(ctx context.Context, spec BotSpec) (*Bot, error) {
	var out Bot
	return &out, a.c.do(ctx, http.MethodPost, "/api/v1/bots", spec.wire(""), &out)
}

func (a *BotsAPI) Update(ctx context.Context, id string, spec BotSpec) (*Bot, error) {
	var out Bot
	return &out, a.c.do(ctx, http.MethodPut, "/api/v1/bots/"+esc(id), spec.wire(id), &out)
}

func (a *BotsAPI) Delete(ctx context.Context, id string) error {
	return a.c.do(ctx, http.MethodDelete, "/api/v1/bots/"+esc(id), nil, nil)
}

// Hire creates a bot from a gallery template.
func (a *BotsAPI) Hire(ctx context.Context, templateID, name string) (*Bot, error) {
	var out Bot
	return &out, a.c.do(ctx, http.MethodPost, "/api/v1/bots/from-template/"+esc(templateID), map[string]string{"name": name}, &out)
}

// GetModel returns the bot's model setting and the model it runs on.
func (a *BotsAPI) GetModel(ctx context.Context, id string) (*BotModelInfo, error) {
	var out BotModelInfo
	return &out, a.c.do(ctx, http.MethodGet, "/api/v1/bots/"+esc(id)+"/model", nil, &out)
}

// SetModel sets the bot's model: ModelDefault, ModelOf(provider, model) or a profile name.
func (a *BotsAPI) SetModel(ctx context.Context, id, model string) (*BotModelInfo, error) {
	var out BotModelInfo
	return &out, a.c.do(ctx, http.MethodPut, "/api/v1/bots/"+esc(id)+"/model", map[string]string{"model": model}, &out)
}

func (a *BotsAPI) Pause(ctx context.Context, id string) error {
	return a.c.do(ctx, http.MethodPost, "/api/v1/bots/"+esc(id)+"/pause", nil, nil)
}

func (a *BotsAPI) Resume(ctx context.Context, id string) error {
	return a.c.do(ctx, http.MethodPost, "/api/v1/bots/"+esc(id)+"/resume", nil, nil)
}

// Export downloads a .marbot package (secrets are never included).
func (a *BotsAPI) Export(ctx context.Context, id string, includeMemory bool) ([]byte, error) {
	return a.c.raw(ctx, http.MethodGet, fmt.Sprintf("/api/v1/bots/%s/export?includeMemory=%t", esc(id), includeMemory), nil, "")
}

// Import installs a .marbot package as a new bot.
func (a *BotsAPI) Import(ctx context.Context, pkg []byte) (*Bot, error) {
	data, err := a.c.raw(ctx, http.MethodPost, "/api/v1/bots/import", bytes.NewReader(pkg), "application/zip")
	if err != nil {
		return nil, err
	}
	var out Bot
	return &out, json.Unmarshal(data, &out)
}

// ---------------------------------------------------------------- templates & models

// TemplatesAPI searches the template gallery.
type TemplatesAPI struct{ c *Client }

func (a *TemplatesAPI) List(ctx context.Context, query, category string) ([]Template, error) {
	var out []Template
	return out, a.c.do(ctx, http.MethodGet, "/api/v1/templates?q="+url.QueryEscape(query)+"&category="+url.QueryEscape(category), nil, &out)
}

func (a *TemplatesAPI) Get(ctx context.Context, id string) (*Template, error) {
	var out Template
	return &out, a.c.do(ctx, http.MethodGet, "/api/v1/templates/"+esc(id), nil, &out)
}

// ModelsAPI reads and changes the workspace default model.
type ModelsAPI struct{ c *Client }

func (a *ModelsAPI) List(ctx context.Context) (*ModelCatalog, error) {
	var out ModelCatalog
	return &out, a.c.do(ctx, http.MethodGet, "/api/v1/models", nil, &out)
}

// SetDefault changes the model used by every bot whose model is ModelDefault.
func (a *ModelsAPI) SetDefault(ctx context.Context, model string) (string, error) {
	var out struct {
		Default string `json:"default"`
	}
	err := a.c.do(ctx, http.MethodPut, "/api/v1/models/default", map[string]string{"model": model}, &out)
	return out.Default, err
}

// ---------------------------------------------------------------- threads & tasks

// ThreadsAPI manages conversations.
type ThreadsAPI struct{ c *Client }

func (a *ThreadsAPI) List(ctx context.Context, botID string) ([]Thread, error) {
	path := "/api/v1/threads"
	if botID != "" {
		path += "?botId=" + url.QueryEscape(botID)
	}
	var out []Thread
	return out, a.c.do(ctx, http.MethodGet, path, nil, &out)
}

func (a *ThreadsAPI) Create(ctx context.Context, botID, title string) (*Thread, error) {
	var out Thread
	body := map[string]any{"botId": botID}
	if title != "" {
		body["title"] = title
	}
	return &out, a.c.do(ctx, http.MethodPost, "/api/v1/threads", body, &out)
}

// Send posts a message. With opts.Wait it returns after the bot finished.
func (a *ThreadsAPI) Send(ctx context.Context, threadID, text string, opts SendOptions) (*SendResult, error) {
	timeout := opts.TimeoutSeconds
	if timeout == 0 {
		timeout = 600
	}
	var out SendResult
	body := map[string]any{"text": text, "wait": opts.Wait, "timeoutSeconds": timeout}
	return &out, a.c.do(ctx, http.MethodPost, "/api/v1/threads/"+esc(threadID)+"/messages", body, &out)
}

func (a *ThreadsAPI) Messages(ctx context.Context, threadID string, afterSeq int64) ([]Message, error) {
	var out []Message
	return out, a.c.do(ctx, http.MethodGet, fmt.Sprintf("/api/v1/threads/%s/messages?after=%d", esc(threadID), afterSeq), nil, &out)
}

func (a *ThreadsAPI) Files(ctx context.Context, threadID string) ([]WorkspaceFile, error) {
	var out []WorkspaceFile
	return out, a.c.do(ctx, http.MethodGet, "/api/v1/threads/"+esc(threadID)+"/files", nil, &out)
}

func (a *ThreadsAPI) Download(ctx context.Context, threadID, path string) ([]byte, error) {
	return a.c.raw(ctx, http.MethodGet, "/api/v1/threads/"+esc(threadID)+"/files/"+path, nil, "")
}

func (a *ThreadsAPI) Delete(ctx context.Context, threadID string) error {
	return a.c.do(ctx, http.MethodDelete, "/api/v1/threads/"+esc(threadID), nil, nil)
}

// TasksAPI inspects and controls tasks.
type TasksAPI struct{ c *Client }

func (a *TasksAPI) List(ctx context.Context, threadID string) ([]Task, error) {
	path := "/api/v1/tasks"
	if threadID != "" {
		path += "?threadId=" + url.QueryEscape(threadID)
	}
	var out []Task
	return out, a.c.do(ctx, http.MethodGet, path, nil, &out)
}

func (a *TasksAPI) Get(ctx context.Context, id string) (*Task, error) {
	var out Task
	return &out, a.c.do(ctx, http.MethodGet, "/api/v1/tasks/"+esc(id), nil, &out)
}

func (a *TasksAPI) Cancel(ctx context.Context, id string) error {
	return a.c.do(ctx, http.MethodPost, "/api/v1/tasks/"+esc(id)+"/cancel", nil, nil)
}

// ---------------------------------------------------------------- approvals

// ApprovalsAPI resolves human-in-the-loop approvals.
type ApprovalsAPI struct{ c *Client }

func (a *ApprovalsAPI) Pending(ctx context.Context) ([]Approval, error) {
	var out []Approval
	return out, a.c.do(ctx, http.MethodGet, "/api/v1/approvals?state=pending", nil, &out)
}

func (a *ApprovalsAPI) Approve(ctx context.Context, id string, scope ApprovalScope) (*Approval, error) {
	var out Approval
	return &out, a.c.do(ctx, http.MethodPost, "/api/v1/approvals/"+esc(id)+"/approve", map[string]ApprovalScope{"scope": scope}, &out)
}

func (a *ApprovalsAPI) Reject(ctx context.Context, id string) (*Approval, error) {
	var out Approval
	return &out, a.c.do(ctx, http.MethodPost, "/api/v1/approvals/"+esc(id)+"/reject", nil, &out)
}

// SkipApprovals reports whether approvals are skipped (dangerous mode).
func (a *ApprovalsAPI) SkipApprovals(ctx context.Context) (bool, error) {
	var out struct {
		Skip bool `json:"dangerouslySkipApprovals"`
	}
	err := a.c.do(ctx, http.MethodGet, "/api/v1/system/approvals", nil, &out)
	return out.Skip, err
}

// SetSkipApprovals is dangerous, like --dangerously-skip-permissions: every action that would ask runs without a
// human. Turning it on also approves everything pending. Actions a bot's profile denies stay denied.
func (a *ApprovalsAPI) SetSkipApprovals(ctx context.Context, skip bool) (bool, error) {
	var out struct {
		Skip bool `json:"dangerouslySkipApprovals"`
	}
	err := a.c.do(ctx, http.MethodPut, "/api/v1/system/approvals", map[string]bool{"dangerouslySkipApprovals": skip}, &out)
	return out.Skip, err
}

// ---------------------------------------------------------------- skills, mcp, schedules, memory

// SkillsAPI manages SKILL.md packages.
type SkillsAPI struct{ c *Client }

func (a *SkillsAPI) List(ctx context.Context) ([]Skill, error) {
	var out []Skill
	return out, a.c.do(ctx, http.MethodGet, "/api/v1/skills", nil, &out)
}

func (a *SkillsAPI) Install(ctx context.Context, source string) ([]Skill, error) {
	var out []Skill
	return out, a.c.do(ctx, http.MethodPost, "/api/v1/skills/install", map[string]string{"source": source}, &out)
}

// Evaluations returns the learning evaluation: outcomes per skill version and a verdict.
func (a *SkillsAPI) Evaluations(ctx context.Context) ([]SkillEvaluation, error) {
	var out []SkillEvaluation
	return out, a.c.do(ctx, http.MethodGet, "/api/v1/skills/evaluations", nil, &out)
}

// Rollback restores the previous version of an installed skill and returns the restored version.
func (a *SkillsAPI) Rollback(ctx context.Context, name string) (string, error) {
	var out struct {
		Version string `json:"version"`
	}
	err := a.c.do(ctx, http.MethodPost, "/api/v1/skills/"+esc(name)+"/rollback", nil, &out)
	return out.Version, err
}

// Promote publishes a skill drafted by auto-learn.
func (a *SkillsAPI) Promote(ctx context.Context, name string) error {
	return a.c.do(ctx, http.MethodPost, "/api/v1/skills/"+esc(name)+"/approve", nil, nil)
}

// Discard deletes a skill draft.
func (a *SkillsAPI) Discard(ctx context.Context, name string) error {
	return a.c.do(ctx, http.MethodPost, "/api/v1/skills/"+esc(name)+"/reject", nil, nil)
}

// AutoRollback reports whether skills roll back automatically when their evaluation recommends it.
func (a *SkillsAPI) AutoRollback(ctx context.Context) (bool, error) {
	var out struct {
		On bool `json:"autoRollbackSkills"`
	}
	err := a.c.do(ctx, http.MethodGet, "/api/v1/system/learning", nil, &out)
	return out.On, err
}

// SetAutoRollback switches automatic skill rollback on or off.
func (a *SkillsAPI) SetAutoRollback(ctx context.Context, on bool) (bool, error) {
	var out struct {
		On bool `json:"autoRollbackSkills"`
	}
	err := a.c.do(ctx, http.MethodPut, "/api/v1/system/learning", map[string]bool{"autoRollbackSkills": on}, &out)
	return out.On, err
}

// AgentHostsAPI manages the computers that run bots' tools (see docs/en/computers.md).
type AgentHostsAPI struct{ c *Client }

func (a *AgentHostsAPI) List(ctx context.Context) ([]Host, error) {
	var out []Host
	return out, a.c.do(ctx, http.MethodGet, "/api/v1/hosts", nil, &out)
}

// CreateEnrollment returns a one-time token for `marbots-host enroll`.
func (a *AgentHostsAPI) CreateEnrollment(ctx context.Context, name string, validMinutes int) (*EnrollmentToken, error) {
	var out EnrollmentToken
	return &out, a.c.do(ctx, http.MethodPost, "/api/v1/hosts/enrollments", map[string]any{"name": name, "validMinutes": validMinutes}, &out)
}

// Bootstrap installs marbots-host on a computer over SSH. The password/key is used for this call only.
func (a *AgentHostsAPI) Bootstrap(ctx context.Context, opts BootstrapOptions) (*BootstrapResult, error) {
	if opts.Port == 0 {
		opts.Port = 22
	}
	if opts.Name == "" {
		opts.Name = opts.Host
	}
	var out BootstrapResult
	return &out, a.c.do(ctx, http.MethodPost, "/api/v1/hosts/bootstrap", opts, &out)
}

func (a *AgentHostsAPI) Disable(ctx context.Context, id string) error {
	return a.c.do(ctx, http.MethodPost, "/api/v1/hosts/"+esc(id)+"/disable", nil, nil)
}

func (a *AgentHostsAPI) Enable(ctx context.Context, id string) error {
	return a.c.do(ctx, http.MethodPost, "/api/v1/hosts/"+esc(id)+"/enable", nil, nil)
}

func (a *AgentHostsAPI) Remove(ctx context.Context, id string) error {
	return a.c.do(ctx, http.MethodDelete, "/api/v1/hosts/"+esc(id), nil, nil)
}

// MCPAPI manages MCP servers.
type MCPAPI struct{ c *Client }

func (a *MCPAPI) List(ctx context.Context) ([]MCPServer, error) {
	var out []MCPServer
	return out, a.c.do(ctx, http.MethodGet, "/api/v1/mcp", nil, &out)
}

func (a *MCPAPI) Install(ctx context.Context, id string) (*MCPServer, error) {
	var out MCPServer
	return &out, a.c.do(ctx, http.MethodPost, "/api/v1/mcp/"+esc(id)+"/install", nil, &out)
}

// SchedulesAPI manages cron and one-off jobs.
type SchedulesAPI struct{ c *Client }

func (a *SchedulesAPI) List(ctx context.Context) ([]Schedule, error) {
	var out []Schedule
	return out, a.c.do(ctx, http.MethodGet, "/api/v1/schedules", nil, &out)
}

func (a *SchedulesAPI) Create(ctx context.Context, spec ScheduleSpec) (*Schedule, error) {
	var out Schedule
	if spec.TimeZone == "" {
		spec.TimeZone = "UTC"
	}
	return &out, a.c.do(ctx, http.MethodPost, "/api/v1/schedules", spec, &out)
}

func (a *SchedulesAPI) Delete(ctx context.Context, id string) error {
	return a.c.do(ctx, http.MethodDelete, "/api/v1/schedules/"+esc(id), nil, nil)
}

// MemoryAPI reads and writes long-term memory.
type MemoryAPI struct{ c *Client }

func (a *MemoryAPI) List(ctx context.Context, owner string) ([]Memory, error) {
	var out []Memory
	return out, a.c.do(ctx, http.MethodGet, "/api/v1/memory/"+esc(owner), nil, &out)
}

func (a *MemoryAPI) Remember(ctx context.Context, owner, content string, kind MemoryKind) (*Memory, error) {
	var out Memory
	body := map[string]any{"owner": owner, "content": content, "kind": kind, "source": "sdk:go", "confidence": 1.0}
	return &out, a.c.do(ctx, http.MethodPost, "/api/v1/memory", body, &out)
}
