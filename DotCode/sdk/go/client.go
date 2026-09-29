// Package dotcode is the Go SDK for DotCode — embed a multi-LLM coding agent (Anthropic, OpenAI, Azure,
// Gemini, DeepSeek, Ollama…) in Go applications. It talks JSON-RPC 2.0 to a `dotcode serve` child process.
//
// Built by Gravicode Studios, led by Kang Fadhil.
//
//	client := dotcode.NewClient(nil)
//	if err := client.Start(ctx); err != nil { log.Fatal(err) }
//	defer client.Stop()
//	session, err := client.CreateSession(ctx, &dotcode.SessionConfig{
//		Model:               "openai:gpt-5",
//		OnPermissionRequest: dotcode.PermissionHandler.ApproveAll,
//	})
//	defer session.Disconnect()
//	result, err := session.SendAndWait(ctx, dotcode.MessageOptions{Prompt: "Summarize README.md"})
package dotcode

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// ProtocolVersion is the DotCode agent protocol version implemented by this SDK.
const ProtocolVersion = "1.0"

const sdkVersion = "0.2.0"

// RPCError is a JSON-RPC error returned by the server.
type RPCError struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
}

func (e *RPCError) Error() string { return fmt.Sprintf("dotcode: %s (code %d)", e.Message, e.Code) }

// ErrNotStarted is returned when the client is used before Start.
var ErrNotStarted = errors.New("dotcode: client not started (call Start first)")

// ClientOptions configures how the SDK starts the DotCode server.
type ClientOptions struct {
	// CLIPath is the dotcode executable (or dotcode.dll). Defaults to $DOTCODE_CLI_PATH or "dotcode" on PATH.
	CLIPath string
	// CLIArgs are extra arguments for `dotcode serve`.
	CLIArgs []string
	// Cwd is the working directory of the server and the default for sessions.
	Cwd string
	// Env adds environment variables for the server process.
	Env map[string]string
}

// Client owns the server process and its sessions.
type Client struct {
	opts ClientOptions

	startMu  sync.Mutex
	cmd      *exec.Cmd
	stdin    io.WriteCloser
	done     chan struct{}
	writeMu  sync.Mutex
	nextID   atomic.Int64
	pendMu   sync.Mutex
	pending  map[int64]chan rpcResponse
	sessMu   sync.RWMutex
	sessions map[string]*Session
	early    map[string][]SessionEvent
	opening  int

	// ServerVersion is reported by the server during the handshake.
	ServerVersion string
}

type rpcResponse struct {
	result json.RawMessage
	err    *RPCError
}

type rpcMessage struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      json.RawMessage `json:"id,omitempty"`
	Method  string          `json:"method,omitempty"`
	Params  json.RawMessage `json:"params,omitempty"`
	Result  json.RawMessage `json:"result,omitempty"`
	Error   *RPCError       `json:"error,omitempty"`
}

// NewClient creates a client; call Start to launch the server.
func NewClient(opts *ClientOptions) *Client {
	c := &Client{pending: map[int64]chan rpcResponse{}, sessions: map[string]*Session{}, early: map[string][]SessionEvent{}}
	if opts != nil {
		c.opts = *opts
	}
	return c
}

// Start launches `dotcode serve` and performs the protocol handshake.
func (c *Client) Start(ctx context.Context) error {
	c.startMu.Lock()
	defer c.startMu.Unlock()
	if c.cmd != nil {
		return nil
	}
	cli := c.opts.CLIPath
	if cli == "" {
		cli = os.Getenv("DOTCODE_CLI_PATH")
	}
	if cli == "" {
		cli = "dotcode"
	}
	args := append([]string{"serve"}, c.opts.CLIArgs...)
	var cmd *exec.Cmd
	if strings.HasSuffix(strings.ToLower(cli), ".dll") {
		cmd = exec.Command("dotnet", append([]string{cli}, args...)...)
	} else {
		cmd = exec.Command(cli, args...)
	}
	cmd.Dir = c.opts.Cwd
	cmd.Env = os.Environ()
	for k, v := range c.opts.Env {
		cmd.Env = append(cmd.Env, k+"="+v)
	}
	stdin, err := cmd.StdinPipe()
	if err != nil {
		return err
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return err
	}
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("dotcode: could not start %q (install the DotCode CLI or set DOTCODE_CLI_PATH): %w", cli, err)
	}
	c.cmd, c.stdin, c.done = cmd, stdin, make(chan struct{})
	go c.readLoop(stdout)

	var init struct {
		ServerInfo struct {
			Version string `json:"version"`
		} `json:"serverInfo"`
	}
	err = c.call(ctx, "initialize", map[string]any{
		"protocolVersion": ProtocolVersion,
		"clientInfo":      map[string]string{"name": "dotcode-sdk-go", "version": sdkVersion},
		"capabilities":    map[string]bool{"permissions": true, "questions": true},
	}, &init)
	if err != nil {
		c.kill()
		return err
	}
	c.ServerVersion = init.ServerInfo.Version
	return nil
}

func (c *Client) readLoop(r io.Reader) {
	defer close(c.done)
	scanner := bufio.NewScanner(r)
	scanner.Buffer(make([]byte, 1024*1024), 64*1024*1024)
	for scanner.Scan() {
		line := scanner.Bytes()
		if len(strings.TrimSpace(string(line))) == 0 {
			continue
		}
		var msg rpcMessage
		if json.Unmarshal(line, &msg) != nil {
			continue
		}
		switch {
		case msg.Method == "" && len(msg.ID) > 0:
			var id int64
			_ = json.Unmarshal(msg.ID, &id)
			c.pendMu.Lock()
			ch := c.pending[id]
			delete(c.pending, id)
			c.pendMu.Unlock()
			if ch != nil {
				ch <- rpcResponse{result: msg.Result, err: msg.Error}
			}
		case msg.Method == "session.event":
			var p struct {
				SessionID string          `json:"sessionId"`
				Event     json.RawMessage `json:"event"`
			}
			if json.Unmarshal(msg.Params, &p) == nil {
				c.onEvent(p.SessionID, parseEvent(p.Event))
			}
		case msg.Method != "" && len(msg.ID) > 0:
			go c.answer(msg)
		}
	}
	c.pendMu.Lock()
	for id, ch := range c.pending {
		ch <- rpcResponse{err: &RPCError{Code: -32000, Message: "server connection closed"}}
		delete(c.pending, id)
	}
	c.pendMu.Unlock()
}

func (c *Client) onEvent(sessionID string, e SessionEvent) {
	c.sessMu.Lock()
	s := c.sessions[sessionID]
	if s == nil && c.opening > 0 {
		c.early[sessionID] = append(c.early[sessionID], e)
	}
	c.sessMu.Unlock()
	if s != nil {
		s.dispatch(e)
	}
}

func (c *Client) session(id string) *Session {
	c.sessMu.RLock()
	defer c.sessMu.RUnlock()
	return c.sessions[id]
}

func (c *Client) write(v any) error {
	data, err := json.Marshal(v)
	if err != nil {
		return err
	}
	c.writeMu.Lock()
	defer c.writeMu.Unlock()
	if c.stdin == nil {
		return ErrNotStarted
	}
	_, err = c.stdin.Write(append(data, '\n'))
	return err
}

func (c *Client) call(ctx context.Context, method string, params any, out any) error {
	id := c.nextID.Add(1)
	ch := make(chan rpcResponse, 1)
	c.pendMu.Lock()
	c.pending[id] = ch
	c.pendMu.Unlock()
	if err := c.write(map[string]any{"jsonrpc": "2.0", "id": id, "method": method, "params": params}); err != nil {
		c.pendMu.Lock()
		delete(c.pending, id)
		c.pendMu.Unlock()
		return err
	}
	select {
	case r := <-ch:
		if r.err != nil {
			return r.err
		}
		if out != nil && len(r.result) > 0 {
			return json.Unmarshal(r.result, out)
		}
		return nil
	case <-ctx.Done():
		c.pendMu.Lock()
		delete(c.pending, id)
		c.pendMu.Unlock()
		return ctx.Err()
	}
}

func (c *Client) answer(msg rpcMessage) {
	var head struct {
		SessionID string `json:"sessionId"`
	}
	_ = json.Unmarshal(msg.Params, &head)
	s := c.session(head.SessionID)
	var result any
	var rpcErr *RPCError
	if s == nil {
		rpcErr = &RPCError{Code: -32001, Message: "unknown session"}
	} else {
		var err error
		result, err = s.handleServerRequest(msg.Method, msg.Params)
		if err != nil {
			rpcErr = &RPCError{Code: -32603, Message: err.Error()}
		}
	}
	if rpcErr != nil {
		_ = c.write(map[string]any{"jsonrpc": "2.0", "id": msg.ID, "error": rpcErr})
		return
	}
	_ = c.write(map[string]any{"jsonrpc": "2.0", "id": msg.ID, "result": result})
}

// CreateSession starts a new agent session. Without OnPermissionRequest it is deny-by-default.
func (c *Client) CreateSession(ctx context.Context, config *SessionConfig) (*Session, error) {
	if config == nil {
		config = &SessionConfig{}
	}
	return c.openSession(ctx, "session.create", config, nil)
}

// ResumeSessionConfig configures ResumeSession.
type ResumeSessionConfig struct {
	SessionConfig
	// Fork continues under a new session id, leaving the original transcript untouched.
	Fork bool
}

// ResumeSession reopens a saved session.
func (c *Client) ResumeSession(ctx context.Context, sessionID string, config *ResumeSessionConfig) (*Session, error) {
	if config == nil {
		config = &ResumeSessionConfig{}
	}
	return c.openSession(ctx, "session.resume", &config.SessionConfig, map[string]any{"sessionId": sessionID, "fork": config.Fork})
}

func (c *Client) openSession(ctx context.Context, method string, config *SessionConfig, extra map[string]any) (*Session, error) {
	if c.cmd == nil {
		if err := c.Start(ctx); err != nil {
			return nil, err
		}
	}
	params := config.wire(c.opts.Cwd)
	for k, v := range extra {
		params[k] = v
	}
	c.sessMu.Lock()
	c.opening++
	c.sessMu.Unlock()
	defer func() {
		c.sessMu.Lock()
		if c.opening--; c.opening == 0 {
			c.early = map[string][]SessionEvent{}
		}
		c.sessMu.Unlock()
	}()
	var info SessionInfo
	if err := c.call(ctx, method, params, &info); err != nil {
		return nil, err
	}
	s := newSession(c, info, *config)
	c.sessMu.Lock()
	c.sessions[s.SessionID] = s
	early := c.early[s.SessionID]
	delete(c.early, s.SessionID)
	c.sessMu.Unlock()
	for _, e := range early {
		s.dispatch(e)
	}
	return s, nil
}

// ListModels returns configured providers and available models.
func (c *Client) ListModels(ctx context.Context) (*ModelList, error) {
	var out ModelList
	err := c.call(ctx, "models.list", map[string]any{"cwd": c.opts.Cwd}, &out)
	return &out, err
}

// ListSessions returns saved sessions for the working directory.
func (c *Client) ListSessions(ctx context.Context) ([]SessionMetadata, error) {
	var out struct {
		Sessions []SessionMetadata `json:"sessions"`
	}
	err := c.call(ctx, "session.list", map[string]any{"cwd": c.opts.Cwd}, &out)
	return out.Sessions, err
}

// Ping round-trips a message to the server.
func (c *Client) Ping(ctx context.Context) error {
	return c.call(ctx, "ping", map[string]any{}, nil)
}

// Stop shuts the server down gracefully.
func (c *Client) Stop() error {
	c.startMu.Lock()
	defer c.startMu.Unlock()
	if c.cmd == nil {
		return nil
	}
	ctx, cancel := context.WithTimeout(context.Background(), 1500*time.Millisecond)
	defer cancel()
	_ = c.call(ctx, "shutdown", map[string]any{}, nil)
	_ = c.stdin.Close()
	select {
	case <-c.done:
	case <-time.After(3 * time.Second):
		_ = c.cmd.Process.Kill()
	}
	_ = c.cmd.Wait()
	c.reset()
	return nil
}

// ForceStop kills the server without a graceful shutdown.
func (c *Client) ForceStop() {
	c.startMu.Lock()
	defer c.startMu.Unlock()
	c.kill()
}

func (c *Client) kill() {
	if c.cmd == nil {
		return
	}
	_ = c.cmd.Process.Kill()
	_ = c.cmd.Wait()
	c.reset()
}

func (c *Client) reset() {
	c.writeMu.Lock()
	c.cmd, c.stdin = nil, nil
	c.writeMu.Unlock()
	c.sessMu.Lock()
	c.sessions = map[string]*Session{}
	c.sessMu.Unlock()
}
