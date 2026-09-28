// Package dotcode is the Go SDK for DotCode — embed a multi-LLM coding agent (Anthropic, OpenAI, Azure,
// Gemini, DeepSeek, Ollama…) in Go applications. It talks JSON-RPC 2.0 to a `dotcode serve` child process.
//
// Built by Gravicode Studios, led by Kang Fadhil.
//
//	client, err := dotcode.NewClient(ctx, dotcode.ClientOptions{})
//	defer client.Close()
//	session, err := client.CreateSession(ctx, dotcode.SessionOptions{Model: "openai:gpt-5"})
//	result, err := session.Send(ctx, "Summarize README.md")
//	fmt.Println(result.Result)
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

// RPCError is a JSON-RPC error returned by the server.
type RPCError struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
}

func (e *RPCError) Error() string { return fmt.Sprintf("dotcode: %s (code %d)", e.Message, e.Code) }

// ClientOptions configures how the SDK starts the DotCode server.
type ClientOptions struct {
	// CLIPath is the dotcode executable (or dotcode.dll). Defaults to $DOTCODE_CLI_PATH or "dotcode" on PATH.
	CLIPath string
	// ServerArgs are extra arguments for `dotcode serve`.
	ServerArgs []string
	// Cwd is the default working directory for sessions.
	Cwd string
	// Env adds environment variables for the server process.
	Env map[string]string
}

// Client owns the server process and its sessions.
type Client struct {
	cmd      *exec.Cmd
	stdin    io.WriteCloser
	writeMu  sync.Mutex
	nextID   atomic.Int64
	pendMu   sync.Mutex
	pending  map[int64]chan rpcResponse
	sessMu   sync.RWMutex
	sessions map[string]*Session
	opts     ClientOptions
	done     chan struct{}
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

// NewClient starts `dotcode serve` and performs the protocol handshake.
func NewClient(ctx context.Context, opts ClientOptions) (*Client, error) {
	cli := opts.CLIPath
	if cli == "" {
		cli = os.Getenv("DOTCODE_CLI_PATH")
	}
	if cli == "" {
		cli = "dotcode"
	}
	args := append([]string{"serve"}, opts.ServerArgs...)
	var cmd *exec.Cmd
	if strings.HasSuffix(strings.ToLower(cli), ".dll") {
		cmd = exec.Command("dotnet", append([]string{cli}, args...)...)
	} else {
		cmd = exec.Command(cli, args...)
	}
	cmd.Dir = opts.Cwd
	cmd.Env = os.Environ()
	for k, v := range opts.Env {
		cmd.Env = append(cmd.Env, k+"="+v)
	}
	stdin, err := cmd.StdinPipe()
	if err != nil {
		return nil, err
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return nil, err
	}
	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("dotcode: could not start %q (install the DotCode CLI or set DOTCODE_CLI_PATH): %w", cli, err)
	}
	c := &Client{cmd: cmd, stdin: stdin, pending: map[int64]chan rpcResponse{}, sessions: map[string]*Session{}, opts: opts, done: make(chan struct{})}
	go c.readLoop(stdout)

	var init struct {
		ServerInfo struct {
			Version string `json:"version"`
		} `json:"serverInfo"`
	}
	err = c.call(ctx, "initialize", map[string]any{
		"protocolVersion": ProtocolVersion,
		"clientInfo":      map[string]string{"name": "dotcode-sdk-go", "version": "0.1.0"},
		"capabilities":    map[string]bool{"permissions": true, "questions": true},
	}, &init)
	if err != nil {
		_ = c.Close()
		return nil, err
	}
	c.ServerVersion = init.ServerInfo.Version
	return c, nil
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
				if s := c.session(p.SessionID); s != nil {
					var e Event
					_ = json.Unmarshal(p.Event, &e)
					e.Raw = p.Event
					s.dispatch(e)
				}
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

// CreateSession starts a new agent session.
func (c *Client) CreateSession(ctx context.Context, opts SessionOptions) (*Session, error) {
	return c.openSession(ctx, "session.create", opts, nil)
}

// ResumeSession reopens a saved session (fork=true copies it under a new id).
func (c *Client) ResumeSession(ctx context.Context, sessionID string, opts SessionOptions, fork bool) (*Session, error) {
	return c.openSession(ctx, "session.resume", opts, map[string]any{"sessionId": sessionID, "fork": fork})
}

func (c *Client) openSession(ctx context.Context, method string, opts SessionOptions, extra map[string]any) (*Session, error) {
	params := opts.wire(c.opts.Cwd)
	for k, v := range extra {
		params[k] = v
	}
	var info SessionInfo
	if err := c.call(ctx, method, params, &info); err != nil {
		return nil, err
	}
	s := &Session{client: c, Info: info, ID: info.SessionID, Model: info.Model, opts: opts}
	c.sessMu.Lock()
	c.sessions[s.ID] = s
	c.sessMu.Unlock()
	return s, nil
}

// ListModels returns configured providers and available models.
func (c *Client) ListModels(ctx context.Context) (json.RawMessage, error) {
	var out json.RawMessage
	err := c.call(ctx, "models.list", map[string]any{"cwd": c.opts.Cwd}, &out)
	return out, err
}

// ListSessions returns saved sessions for the working directory.
func (c *Client) ListSessions(ctx context.Context) (json.RawMessage, error) {
	var out json.RawMessage
	err := c.call(ctx, "session.list", map[string]any{"cwd": c.opts.Cwd}, &out)
	return out, err
}

// Close shuts the server down.
func (c *Client) Close() error {
	ctx, cancel := context.WithTimeout(context.Background(), 1500*time.Millisecond)
	defer cancel()
	_ = c.call(ctx, "shutdown", map[string]any{}, nil)
	_ = c.stdin.Close()
	waited := make(chan error, 1)
	go func() { waited <- c.cmd.Wait() }()
	select {
	case <-waited:
	case <-time.After(3 * time.Second):
		_ = c.cmd.Process.Kill()
	}
	return nil
}

// ErrNoHandler is returned to the model when the host has no handler for a callback.
var ErrNoHandler = errors.New("no handler registered")
