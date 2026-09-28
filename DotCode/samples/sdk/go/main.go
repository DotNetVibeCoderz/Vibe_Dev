// DotCode Go SDK sample. Run: go run . azure:gpt-5-mini
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"os"

	dotcode "github.com/DotNetVibeCoderz/Vibe_Dev/DotCode/sdk/go"
)

func main() {
	ctx := context.Background()
	client, err := dotcode.NewClient(ctx, dotcode.ClientOptions{})
	if err != nil {
		panic(err)
	}
	defer client.Close()

	model := ""
	if len(os.Args) > 1 {
		model = os.Args[1]
	}
	persist := false
	session, err := client.CreateSession(ctx, dotcode.SessionOptions{
		Model:          model,
		PersistSession: &persist,
		Tools: []dotcode.Tool{{
			Name:        "get_exchange_rate",
			Description: "Get the exchange rate between two currencies",
			InputSchema: map[string]any{"type": "object", "properties": map[string]any{"from": map[string]any{"type": "string"}, "to": map[string]any{"type": "string"}}, "required": []string{"from", "to"}},
			ReadOnly:    true,
			Handler: func(ctx context.Context, input json.RawMessage) (string, error) {
				var in struct{ From, To string }
				_ = json.Unmarshal(input, &in)
				rate := "0.92"
				if in.To == "IDR" {
					rate = "16,250"
				}
				return fmt.Sprintf("1 %s = %s %s (demo data)", in.From, rate, in.To), nil
			},
		}},
		OnPermissionRequest: func(r dotcode.PermissionRequest) dotcode.PermissionDecision { return dotcode.Allow },
	})
	if err != nil {
		panic(err)
	}
	fmt.Printf("DotCode SDK (Go) · model %s\n\n", session.Model)
	events, result := session.Stream(ctx, "How many Indonesian Rupiah is 250 US dollars? Use the tool, then answer in one sentence.")
	for e := range events {
		switch e.Type {
		case "assistant.text.delta":
			fmt.Print(e.Text)
		case "tool.started":
			fmt.Printf("● %s\n", e.DisplayName)
		case "tool.completed":
			fmt.Printf("  ⎿  %s\n", e.Output)
		case "turn.completed":
			fmt.Printf("\n\n✔ %d model calls · $%.4f · %d ms\n", e.NumModelCalls, e.CostUSD, e.DurationMs)
		}
	}
	if r := <-result; r.Err != nil {
		panic(r.Err)
	}
}
