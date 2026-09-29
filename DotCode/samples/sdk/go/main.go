// DotCode Go SDK sample. Run: go run . azure:gpt-5-mini
package main

import (
	"context"
	"fmt"
	"log"
	"os"

	dotcode "github.com/DotNetVibeCoderz/Vibe_Dev/DotCode/sdk/go"
)

type exchangeRateParams struct {
	From string `json:"from" jsonschema:"ISO currency code, e.g. USD"`
	To   string `json:"to" jsonschema:"ISO currency code, e.g. IDR"`
}

var getExchangeRate = dotcode.DefineTool("get_exchange_rate", "Get the exchange rate between two currencies",
	func(p exchangeRateParams, _ dotcode.ToolInvocation) (any, error) {
		rate := "0.92"
		if p.To == "IDR" {
			rate = "16,250"
		}
		return fmt.Sprintf("1 %s = %s %s (demo data)", p.From, rate, p.To), nil
	}).WithReadOnly()

func main() {
	ctx := context.Background()
	client := dotcode.NewClient(nil)
	if err := client.Start(ctx); err != nil {
		log.Fatal(err)
	}
	defer client.Stop()

	model := ""
	if len(os.Args) > 1 {
		model = os.Args[1]
	}
	session, err := client.CreateSession(ctx, &dotcode.SessionConfig{
		Model:          model,
		PersistSession: dotcode.Bool(false),
		Tools:          []dotcode.Tool{getExchangeRate},
		OnPermissionRequest: func(r dotcode.PermissionRequest, _ dotcode.Invocation) (dotcode.PermissionDecision, error) {
			fmt.Printf("  [permission] %s → allowed\n", r.DisplayName)
			return &dotcode.PermissionDecisionApproveOnce{}, nil
		},
	})
	if err != nil {
		log.Fatal(err)
	}
	defer session.Disconnect()

	session.On(func(e dotcode.SessionEvent) {
		switch d := e.Data.(type) {
		case *dotcode.AssistantTextDeltaData:
			fmt.Print(d.Text)
		case *dotcode.ToolStartedData:
			fmt.Printf("● %s\n", d.DisplayName)
		case *dotcode.ToolCompletedData:
			fmt.Printf("  ⎿  %s\n", d.Output)
		}
	})

	fmt.Printf("DotCode SDK (Go) · model %s\n\n", session.Model())
	r, err := session.SendAndWait(ctx, dotcode.MessageOptions{Prompt: "How many Indonesian Rupiah is 250 US dollars? Use the tool, then answer in one sentence."})
	if err != nil {
		log.Fatal(err)
	}
	fmt.Printf("\n\n✔ %d model calls · $%.4f · %d ms\n", r.NumModelCalls, r.CostUSD, r.DurationMs)
}
