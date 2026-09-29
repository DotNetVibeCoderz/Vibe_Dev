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

// scripted returns a client and base config that plays the given responses with the scripted provider.
func scripted(t *testing.T, responses string) (*Client, SessionConfig, string) {
	dir := t.TempDir()
	path := filepath.Join(dir, "s.json")
	if err := os.WriteFile(path, []byte(`{"responses":`+responses+`}`), 0o644); err != nil {
		t.Fatal(err)
	}
	client := NewClient(&ClientOptions{CLIPath: cliPath(t), Cwd: dir, Env: map[string]string{"DOTCODE_CONFIG_DIR": filepath.Join(dir, ".cfg")}})
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	if err := client.Start(ctx); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = client.Stop() })
	return client, SessionConfig{
		Model:          "mock:scripted",
		Providers:      map[string]ProviderConfig{"mock": {Type: ProviderMock, Script: path}},
		PersistSession: Bool(false),
		DisableMcp:     true,
	}, dir
}

type weatherParams struct {
	City string `json:"city" jsonschema:"City name"`
}

var getWeather = DefineTool("get_weather", "Weather for a city",
	func(p weatherParams, inv ToolInvocation) (any, error) {
		return p.City + ": rainy, 24°C (" + inv.ToolName + ")", nil
	}).WithReadOnly()

func TestCustomToolAndStreaming(t *testing.T) {
	client, config, _ := scripted(t, `[{"text":"Checking the weather.","toolCalls":[{"name":"get_weather","input":{"city":"Bogor"}}]},{"text":"It is rainy in Bogor."}]`)
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	config.Tools = []Tool{getWeather}
	session, err := client.CreateSession(ctx, &config)
	if err != nil {
		t.Fatal(err)
	}
	defer session.Disconnect()
	events, result := session.Stream(ctx, MessageOptions{Prompt: "weather?"})
	var completed *ToolCompletedData
	var last SessionEvent
	for e := range events {
		if d, ok := e.Data.(*ToolCompletedData); ok {
			completed = d
		}
		last = e
	}
	if r := <-result; r.Err != nil {
		t.Fatal(r.Err)
	}
	done, ok := last.Data.(*TurnCompletedData)
	if !ok || done.ResultText != "It is rainy in Bogor." {
		t.Fatalf("unexpected last event %+v", last)
	}
	if completed == nil || completed.Name != "get_weather" || !strings.Contains(completed.Output, "rainy, 24°C (get_weather)") {
		t.Fatalf("tool.completed for get_weather not seen: %+v", completed)
	}
	msgs, err := session.GetMessages(ctx)
	if err != nil || len(msgs) != 4 {
		t.Fatalf("messages: %d %v", len(msgs), err)
	}
}

func TestPermissionHandler(t *testing.T) {
	dir := t.TempDir()
	target := filepath.Join(dir, "out.txt")
	targetJSON, _ := json.Marshal(target)
	client, config, _ := scripted(t, `[{"toolCalls":[{"name":"Write","input":{"file_path":`+string(targetJSON)+`,"content":"from go"}}]},{"text":"written"}]`)
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	var asked []string
	config.OnPermissionRequest = func(r PermissionRequest, _ Invocation) (PermissionDecision, error) {
		asked = append(asked, r.ToolName)
		return &PermissionDecisionApproveOnce{}, nil
	}
	session, err := client.CreateSession(ctx, &config)
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan *TurnCompletedData, 1)
	session.On(func(e SessionEvent) {
		if d, ok := e.Data.(*TurnCompletedData); ok {
			done <- d
		}
	})
	if err := session.Send(ctx, MessageOptions{Prompt: "write a file"}); err != nil {
		t.Fatal(err)
	}
	var res *TurnCompletedData
	select {
	case res = <-done:
	case <-ctx.Done():
		t.Fatal("turn did not complete")
	}
	if res.ResultText != "written" || len(asked) != 1 || asked[0] != "Write" {
		t.Fatalf("unexpected: %+v asked=%v", res, asked)
	}
	if data, _ := os.ReadFile(target); string(data) != "from go" {
		t.Fatalf("file content %q", data)
	}
}

func TestInvalidArgumentsAreReportedToTheModel(t *testing.T) {
	client, config, _ := scripted(t, `[{"toolCalls":[{"name":"get_weather","input":{"city":42}}]},{"text":"done"}]`)
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	defer cancel()
	config.Tools = []Tool{getWeather}
	session, err := client.CreateSession(ctx, &config)
	if err != nil {
		t.Fatal(err)
	}
	var completed *ToolCompletedData
	session.On(func(e SessionEvent) {
		if d, ok := e.Data.(*ToolCompletedData); ok {
			completed = d
		}
	})
	r, err := session.SendAndWait(ctx, MessageOptions{Prompt: "weather?"})
	if err != nil {
		t.Fatal(err)
	}
	if r.Result != "done" || completed == nil || !completed.IsError || !strings.Contains(completed.Output, "city") {
		t.Fatalf("unexpected: %+v %+v", r, completed)
	}
}

type unit string

func (unit) EnumValues() []string { return []string{"celsius", "fahrenheit"} }

func TestSchemaFor(t *testing.T) {
	type params struct {
		City  string   `json:"city" jsonschema:"City name"`
		Unit  unit     `json:"unit,omitempty"`
		Days  *int     `json:"days"`
		Tags  []string `json:"tags,omitempty" jsonschema:"Tags"`
		Mode  string   `json:"mode" enum:"fast,slow"`
		Inner struct {
			On bool `json:"on"`
		} `json:"inner"`
		skip string
	}
	got, _ := json.Marshal(SchemaFor[params]())
	want := `{"additionalProperties":false,"properties":{"city":{"description":"City name","type":"string"},"days":{"type":"integer"},"inner":{"additionalProperties":false,"properties":{"on":{"type":"boolean"}},"required":["on"],"type":"object"},"mode":{"enum":["fast","slow"],"type":"string"},"tags":{"description":"Tags","items":{"type":"string"},"type":"array"},"unit":{"enum":["celsius","fahrenheit"],"type":"string"}},"required":["city","mode","inner"],"type":"object"}`
	if string(got) != want {
		t.Fatalf("schema\n got %s\nwant %s", got, want)
	}
	_ = params{}.skip
}
