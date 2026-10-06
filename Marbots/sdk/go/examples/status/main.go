// Prints the team with each bot's model and asks Boss Man a question.
// Usage: go run ./examples/status "your question"
package main

import (
	"context"
	"fmt"
	"os"

	marbots "github.com/DotNetVibeCoderz/Vibe_Dev/Marbots/sdk/go"
)

func main() {
	ctx := context.Background()
	url := os.Getenv("MARBOTS_URL")
	if url == "" {
		url = "http://localhost:5170"
	}
	c := marbots.New(url, marbots.WithAPIKey(os.Getenv("MARBOTS_API_KEY")))
	bots, err := c.Bots.List(ctx)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	for _, b := range bots {
		m, _ := c.Bots.GetModel(ctx, b.ID)
		fmt.Printf("%-12s %-32s %-8s %s\n", b.ID, b.Role, b.Status, m.Effective)
	}
	if len(os.Args) > 1 {
		reply, err := c.Chat(ctx, marbots.BossMan, os.Args[1])
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		fmt.Println(reply)
	}
}
