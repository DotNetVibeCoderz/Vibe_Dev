// Prints the team and asks Boss Man a question. Usage: go run ./examples/status "your question"
package main

import (
	"context"
	"fmt"
	"os"

	marbots "github.com/DotNetVibeCoderz/Vibe_Dev/Marbots/sdk/go"
)

func main() {
	ctx := context.Background()
	c := marbots.New(envOr("MARBOTS_URL", "http://localhost:5170"), os.Getenv("MARBOTS_API_KEY"))
	bots, err := c.Bots(ctx)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	for _, b := range bots {
		fmt.Printf("%-12s %-30s %s\n", b.ID, b.Role, b.Status)
	}
	if len(os.Args) > 1 {
		reply, err := c.Chat(ctx, "boss-man", os.Args[1])
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		fmt.Println(reply)
	}
}

func envOr(k, d string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return d
}
