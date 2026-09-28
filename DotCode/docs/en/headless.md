# Headless mode (`-p`)

> 🇮🇩 [Bahasa Indonesia](../id/headless.md)

`dotcode -p "<prompt>"` runs one prompt without the UI and exits — for scripts, CI and pipes.

```bash
dotcode -p "What does src/Engine do?"
cat error.log | dotcode -p "explain this stack trace"
dotcode -p "/review" --output-format json
dotcode -c -p "now add tests for it"          # continue the latest session
dotcode -r 1f3a9c -p "and update the docs"     # resume a session
```

## Output formats

| Format | Output |
|---|---|
| `text` (default) | The final answer. Errors and retries go to stderr; `--verbose` also prints tool calls to stderr. |
| `json` | One result object (below). |
| `stream-json` | Newline-delimited engine events (same schema as the SDK `session.event`), then the result object. Add `--include-partial-messages` for text/thinking deltas. |

```json
{
  "type": "result", "subtype": "success", "is_error": false,
  "duration_ms": 14450, "duration_api_ms": 13790, "num_turns": 5,
  "result": "…final answer…", "session_id": "…", "model": "deepseek:deepseek-v4-flash",
  "total_cost_usd": 0.0061,
  "usage": { "input_tokens": 4950, "output_tokens": 2881, "cache_read_input_tokens": 37632, "cache_creation_input_tokens": 0, "reasoning_tokens": 443 },
  "permission_denials": []
}
```

`--input-format stream-json` reads one message per line from stdin (`{"prompt":"…"}` or `{"type":"user","message":{"content":"…"}}`) and answers each in the same session.

## Permissions in headless mode

There is nobody to ask, so tools that would need approval are **denied with guidance** (the model is told how to proceed) and listed in `permission_denials`. Grant what the task needs:

```bash
dotcode -p "fix the build" --permission-mode acceptEdits --allowedTools "Bash(dotnet build*)" "Bash(dotnet test*)"
dotcode -p "refactor everything" --dangerously-skip-permissions   # sandboxes/CI only
```

## Useful flags

`--model`, `--fallback-model`, `--max-turns`, `--effort`, `--system-prompt`, `--append-system-prompt`, `--tools`, `--add-dir`, `--mcp-config`, `--no-session-persistence`, `--settings '{"…":…}'`, `--record script.json`.

Exit code: `0` success, `1` error, `2` usage error, `130` interrupted.

## GitHub Actions example

```yaml
- name: DotCode review
  env: { DEEPSEEK_API_KEY: "${{ secrets.DEEPSEEK_API_KEY }}" }
  run: |
    git diff origin/main... | dotcode -p "Review this diff; list bugs with file:line" \
      --model deepseek:deepseek-v4-flash --output-format json > review.json
```
