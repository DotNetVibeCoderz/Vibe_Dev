package dotcode

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func cliPath(t *testing.T) string {
	if p := os.Getenv("DOTCODE_CLI_PATH"); p != "" {
		return p
	}
	p, _ := filepath.Abs("../../src/DotCode.Cli/bin/Debug/net10.0/dotcode.dll")
	if _, err := os.Stat(p); err != nil {
		t.Skip("DotCode CLI not built")
	}
	return p
}

func scripted(t *testing.T, dir string, script string) map[string]any {
	path := filepath.Join(dir, "s.json")
	if err := os.WriteFile(path, []byte(script), 0o644); err != nil {
		t.Fatal(err)
	}
	return map[string]any{"providers": map[string]any{"mock": map[string]any{"type": "mock", "script": path}}}
}

func TestCustomToolAndStreaming(t *testing.T) {
	dir := t.TempDir()
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	client, err := NewClient(ctx, ClientOptions{CLIPath: cliPath(t), Cwd: dir, Env: map[string]string{"DOTCODE_CONFIG_DIR": filepath.Join(dir, ".cfg")}})
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	persist := false
	session, err := client.CreateSession(ctx, SessionOptions{
		Model:          "mock:scripted",
		Settings:       scripted(t, dir, `{"responses":[{"text":"Checking the weather.","toolCalls":[{"name":"get_weather","input":{"city":"Bogor"}}]},{"text":"It is rainy in Bogor."}]}`),
		PersistSession: &persist,
		NoMcp:          true,
		Tools: []Tool{{
			Name:        "get_weather",
			Description: "Weather for a city",
			InputSchema: map[string]any{"type": "object", "properties": map[string]any{"city": map[string]any{"type": "string"}}, "required": []string{"city"}},
			ReadOnly:    true,
			Handler: func(ctx context.Context, input json.RawMessage) (string, error) {
				var in struct{ City string }
				_ = json.Unmarshal(input, &in)
				return in.City + ": rainy, 24°C", nil
			},
		}},
	})
	if err != nil {
		t.Fatal(err)
	}
	events, result := session.Stream(ctx, "weather?")
	var all []Event
	for e := range events {
		all = append(all, e)
	}
	r := <-result
	if r.Err != nil {
		t.Fatal(r.Err)
	}
	last := all[len(all)-1]
	if last.Type != "turn.completed" || last.ResultText != "It is rainy in Bogor." {
		t.Fatalf("unexpected last event %+v", last)
	}
	found := false
	for _, e := range all {
		if e.Type == "tool.completed" && e.Name == "get_weather" && strings.Contains(e.Output, "rainy") {
			found = true
		}
	}
	if !found {
		t.Fatal("tool.completed for get_weather not seen")
	}
	msgs, err := session.Messages(ctx)
	if err != nil || len(msgs) != 4 {
		t.Fatalf("messages: %d %v", len(msgs), err)
	}
}

func TestPermissionHandler(t *testing.T) {
	dir := t.TempDir()
	target := filepath.Join(dir, "out.txt")
	targetJSON, _ := json.Marshal(target)
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	client, err := NewClient(ctx, ClientOptions{CLIPath: cliPath(t), Cwd: dir, Env: map[string]string{"DOTCODE_CONFIG_DIR": filepath.Join(dir, ".cfg")}})
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	var asked []string
	persist := false
	session, err := client.CreateSession(ctx, SessionOptions{
		Model:          "mock:scripted",
		Settings:       scripted(t, dir, `{"responses":[{"toolCalls":[{"name":"Write","input":{"file_path":`+string(targetJSON)+`,"content":"from go"}}]},{"text":"written"}]}`),
		PersistSession: &persist,
		NoMcp:          true,
		OnPermissionRequest: func(r PermissionRequest) PermissionDecision {
			asked = append(asked, r.ToolName)
			return Allow
		},
	})
	if err != nil {
		t.Fatal(err)
	}
	res, err := session.Send(ctx, "write a file")
	if err != nil {
		t.Fatal(err)
	}
	if res.Result != "written" || len(asked) != 1 || asked[0] != "Write" {
		t.Fatalf("unexpected: %+v asked=%v", res, asked)
	}
	if data, _ := os.ReadFile(target); string(data) != "from go" {
		t.Fatalf("file content %q", data)
	}
}
