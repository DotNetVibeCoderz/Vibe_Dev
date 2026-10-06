// Package marbots is the Go client for the Marbots multi-agent collaboration platform.
//
// Created by Gravicode Studios, led by Kang Fadhil.
//
//	c := marbots.New("http://localhost:5170", "")
//	reply, err := c.Chat(ctx, "boss-man", "Plan a product launch")
package marbots

import (
	"bufio"
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

type Bot struct {
	ID                string   `json:"id"`
	Name              string   `json:"name"`
	Role              string   `json:"role"`
	Description       string   `json:"description"`
	Persona           string   `json:"persona"`
	Color             string   `json:"color,omitempty"`
	ModelProfile      string   `json:"modelProfile,omitempty"`
	Skills            []string `json:"skills"`
	McpServers        []string `json:"mcpServers"`
	KernelFunctions   []string `json:"kernelFunctions"`
	PermissionProfile string   `json:"permissionProfile,omitempty"`
	AutoLearn         string   `json:"autoLearn,omitempty"`
	Status            string   `json:"status,omitempty"`
	IsSystem          bool     `json:"isSystem,omitempty"`
}

type Template struct {
	ID          string   `json:"id"`
	Name        string   `json:"name"`
	Category    string   `json:"category"`
	Role        string   `json:"role"`
	Description string   `json:"description"`
	Skills      []string `json:"skills"`
	Tags        []string `json:"tags"`
}

type Thread struct {
	ID        string    `json:"id"`
	Title     string    `json:"title"`
	BotID     string    `json:"botId"`
	UpdatedAt time.Time `json:"updatedAt"`
}

type Message struct {
	ID       string `json:"id"`
	Seq      int64  `json:"seq"`
	Role     string `json:"role"`
	Author   string `json:"author"`
	Content  string `json:"content"`
	TaskID   string `json:"taskId,omitempty"`
	ToolName string `json:"toolName,omitempty"`
}

type Task struct {
	ID           string  `json:"id"`
	ParentTaskID string  `json:"parentTaskId,omitempty"`
	ThreadID     string  `json:"threadId"`
	BotID        string  `json:"botId"`
	Depth        int     `json:"depth"`
	Objective    string  `json:"objective"`
	State        string  `json:"state"`
	Result       string  `json:"result,omitempty"`
	Error        string  `json:"error,omitempty"`
	Steps        int     `json:"steps"`
	InputTokens  int64   `json:"inputTokens"`
	OutputTokens int64   `json:"outputTokens"`
	CostUsd      float64 `json:"costUsd"`
}

type Approval struct {
	ID        string `json:"id"`
	TaskID    string `json:"taskId"`
	ThreadID  string `json:"threadId"`
	BotID     string `json:"botId"`
	ToolName  string `json:"toolName"`
	Arguments string `json:"arguments"`
	Risk      string `json:"risk"`
	Reason    string `json:"reason"`
	State     string `json:"state"`
}

type Event struct {
	ID        int64     `json:"id"`
	Type      string    `json:"type"`
	Timestamp time.Time `json:"timestamp"`
	ThreadID  string    `json:"threadId,omitempty"`
	TaskID    string    `json:"taskId,omitempty"`
	BotID     string    `json:"botId,omitempty"`
	Message   string    `json:"message,omitempty"`
	Data      string    `json:"data,omitempty"`
}

type SendResult struct {
	Task  Task     `json:"task"`
	Reply *Message `json:"reply,omitempty"`
}

type WorkspaceFile struct {
	Path string `json:"path"`
	Size int64  `json:"size"`
}

// Error is returned for non-2xx responses.
type Error struct {
	Status  int
	Message string
}

func (e *Error) Error() string { return fmt.Sprintf("HTTP %d: %s", e.Status, e.Message) }

// Client talks to a Marbots server.
type Client struct {
	BaseURL string
	APIKey  string
	HTTP    *http.Client
}

// New creates a client. apiKey may be empty when the server does not require one.
func New(baseURL, apiKey string) *Client {
	return &Client{BaseURL: strings.TrimRight(baseURL, "/"), APIKey: apiKey, HTTP: &http.Client{Timeout: 30 * time.Minute}}
}

func (c *Client) do(ctx context.Context, method, path string, body any, out any) error {
	var rd io.Reader
	if body != nil {
		b, err := json.Marshal(body)
		if err != nil {
			return err
		}
		rd = bytes.NewReader(b)
	}
	req, err := http.NewRequestWithContext(ctx, method, c.BaseURL+path, rd)
	if err != nil {
		return err
	}
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	if c.APIKey != "" {
		req.Header.Set("X-Api-Key", c.APIKey)
	}
	resp, err := c.HTTP.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 300 {
		raw, _ := io.ReadAll(resp.Body)
		msg := string(raw)
		var pd struct {
			Detail string `json:"detail"`
			Title  string `json:"title"`
		}
		if json.Unmarshal(raw, &pd) == nil {
			if pd.Detail != "" {
				msg = pd.Detail
			} else if pd.Title != "" {
				msg = pd.Title
			}
		}
		return &Error{Status: resp.StatusCode, Message: msg}
	}
	if out == nil {
		return nil
	}
	return json.NewDecoder(resp.Body).Decode(out)
}

func (c *Client) Bots(ctx context.Context) ([]Bot, error) {
	var out []Bot
	return out, c.do(ctx, "GET", "/api/v1/bots", nil, &out)
}

func (c *Client) Bot(ctx context.Context, idOrName string) (*Bot, error) {
	var out Bot
	return &out, c.do(ctx, "GET", "/api/v1/bots/"+url.PathEscape(idOrName), nil, &out)
}

func (c *Client) CreateBot(ctx context.Context, b Bot) (*Bot, error) {
	var out Bot
	return &out, c.do(ctx, "POST", "/api/v1/bots", b, &out)
}

func (c *Client) Hire(ctx context.Context, templateID, name string) (*Bot, error) {
	var out Bot
	return &out, c.do(ctx, "POST", "/api/v1/bots/from-template/"+url.PathEscape(templateID), map[string]string{"name": name}, &out)
}

func (c *Client) Templates(ctx context.Context, query string) ([]Template, error) {
	var out []Template
	return out, c.do(ctx, "GET", "/api/v1/templates?q="+url.QueryEscape(query), nil, &out)
}

func (c *Client) CreateThread(ctx context.Context, botID, title string) (*Thread, error) {
	var out Thread
	return &out, c.do(ctx, "POST", "/api/v1/threads", map[string]string{"botId": botID, "title": title}, &out)
}

// Send posts a message. With wait=true it returns after the bot finishes.
func (c *Client) Send(ctx context.Context, threadID, text string, wait bool) (*SendResult, error) {
	var out SendResult
	body := map[string]any{"text": text, "wait": wait, "timeoutSeconds": 900}
	return &out, c.do(ctx, "POST", "/api/v1/threads/"+url.PathEscape(threadID)+"/messages", body, &out)
}

func (c *Client) Messages(ctx context.Context, threadID string) ([]Message, error) {
	var out []Message
	return out, c.do(ctx, "GET", "/api/v1/threads/"+url.PathEscape(threadID)+"/messages", nil, &out)
}

func (c *Client) Files(ctx context.Context, threadID string) ([]WorkspaceFile, error) {
	var out []WorkspaceFile
	return out, c.do(ctx, "GET", "/api/v1/threads/"+url.PathEscape(threadID)+"/files", nil, &out)
}

func (c *Client) Tasks(ctx context.Context) ([]Task, error) {
	var out []Task
	return out, c.do(ctx, "GET", "/api/v1/tasks", nil, &out)
}

func (c *Client) CancelTask(ctx context.Context, id string) error {
	return c.do(ctx, "POST", "/api/v1/tasks/"+url.PathEscape(id)+"/cancel", nil, nil)
}

func (c *Client) PendingApprovals(ctx context.Context) ([]Approval, error) {
	var out []Approval
	return out, c.do(ctx, "GET", "/api/v1/approvals?state=pending", nil, &out)
}

// Approve resolves an approval; scope is "Once" or "Session".
func (c *Client) Approve(ctx context.Context, id, scope string) error {
	return c.do(ctx, "POST", "/api/v1/approvals/"+url.PathEscape(id)+"/approve", map[string]string{"scope": scope}, nil)
}

func (c *Client) Reject(ctx context.Context, id string) error {
	return c.do(ctx, "POST", "/api/v1/approvals/"+url.PathEscape(id)+"/reject", nil, nil)
}

// Events streams live events (optionally for one thread) until ctx is cancelled.
func (c *Client) Events(ctx context.Context, threadID string) (<-chan Event, <-chan error) {
	events := make(chan Event, 64)
	errs := make(chan error, 1)
	go func() {
		defer close(events)
		defer close(errs)
		path := "/api/v1/events"
		if threadID != "" {
			path = "/api/v1/threads/" + url.PathEscape(threadID) + "/events"
		}
		req, err := http.NewRequestWithContext(ctx, "GET", c.BaseURL+path, nil)
		if err != nil {
			errs <- err
			return
		}
		req.Header.Set("Accept", "text/event-stream")
		if c.APIKey != "" {
			req.Header.Set("X-Api-Key", c.APIKey)
		}
		resp, err := (&http.Client{}).Do(req)
		if err != nil {
			errs <- err
			return
		}
		defer resp.Body.Close()
		sc := bufio.NewScanner(resp.Body)
		sc.Buffer(make([]byte, 64*1024), 4*1024*1024)
		for sc.Scan() {
			line := sc.Text()
			if !strings.HasPrefix(line, "data: ") {
				continue
			}
			var e Event
			if json.Unmarshal([]byte(line[6:]), &e) == nil {
				select {
				case events <- e:
				case <-ctx.Done():
					return
				}
			}
		}
		if err := sc.Err(); err != nil && ctx.Err() == nil {
			errs <- err
		}
	}()
	return events, errs
}

// Chat creates a thread with bot, sends text, waits, and returns the reply text.
func (c *Client) Chat(ctx context.Context, bot, text string) (string, error) {
	t, err := c.CreateThread(ctx, bot, "")
	if err != nil {
		return "", err
	}
	r, err := c.Send(ctx, t.ID, text, true)
	if err != nil {
		return "", err
	}
	if r.Reply != nil {
		return r.Reply.Content, nil
	}
	if r.Task.Error != "" {
		return "", fmt.Errorf("task %s: %s", r.Task.State, r.Task.Error)
	}
	return r.Task.Result, nil
}
