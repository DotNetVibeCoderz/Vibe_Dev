#!/usr/bin/env node
// Minimal Model Context Protocol server over stdio (no dependencies) — a tiny notes database.
// Tools: add_note, list_notes, search_notes, delete_note. Prompt: summarize_notes. Resource: notes://all
// Usage: dotcode mcp add notes node samples/mcp-server-notes/server.mjs
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { resolve } from "node:path";
import { createInterface } from "node:readline";

const file = resolve(process.env.NOTES_FILE ?? "notes.json");
const load = () => (existsSync(file) ? JSON.parse(readFileSync(file, "utf8")) : []);
const save = (notes) => writeFileSync(file, JSON.stringify(notes, null, 2));

const tools = [
  {
    name: "add_note",
    description: "Save a note with a title, body and optional tags.",
    inputSchema: {
      type: "object",
      properties: { title: { type: "string" }, body: { type: "string" }, tags: { type: "array", items: { type: "string" } } },
      required: ["title", "body"],
    },
  },
  { name: "list_notes", description: "List all saved notes (id, title, tags).", inputSchema: { type: "object", properties: {} }, annotations: { readOnlyHint: true } },
  {
    name: "search_notes",
    description: "Full-text search across note titles, bodies and tags.",
    inputSchema: { type: "object", properties: { query: { type: "string" } }, required: ["query"] },
    annotations: { readOnlyHint: true },
  },
  { name: "delete_note", description: "Delete a note by id.", inputSchema: { type: "object", properties: { id: { type: "number" } }, required: ["id"] } },
];

function callTool(name, args) {
  const notes = load();
  switch (name) {
    case "add_note": {
      const note = { id: (notes.at(-1)?.id ?? 0) + 1, title: args.title, body: args.body, tags: args.tags ?? [], created: new Date().toISOString() };
      notes.push(note);
      save(notes);
      return `Saved note #${note.id}: ${note.title}`;
    }
    case "list_notes":
      return notes.length === 0 ? "No notes yet." : notes.map((n) => `#${n.id} ${n.title}${n.tags.length ? ` [${n.tags.join(", ")}]` : ""}`).join("\n");
    case "search_notes": {
      const q = String(args.query).toLowerCase();
      const hits = notes.filter((n) => `${n.title} ${n.body} ${n.tags.join(" ")}`.toLowerCase().includes(q));
      return hits.length === 0 ? `No notes match "${args.query}".` : hits.map((n) => `#${n.id} ${n.title}\n${n.body}`).join("\n\n");
    }
    case "delete_note": {
      const left = notes.filter((n) => n.id !== args.id);
      save(left);
      return left.length < notes.length ? `Deleted note #${args.id}` : `No note #${args.id}`;
    }
  }
  throw new Error(`Unknown tool ${name}`);
}

const send = (msg) => process.stdout.write(JSON.stringify(msg) + "\n");
const rl = createInterface({ input: process.stdin });
rl.on("line", (line) => {
  if (!line.trim()) return;
  const msg = JSON.parse(line);
  const reply = (result) => send({ jsonrpc: "2.0", id: msg.id, result });
  try {
    switch (msg.method) {
      case "initialize":
        return reply({
          protocolVersion: msg.params?.protocolVersion ?? "2025-06-18",
          capabilities: { tools: {}, prompts: {}, resources: {} },
          serverInfo: { name: "notes", version: "1.0.0" },
          instructions: "Use the notes tools to remember facts across sessions.",
        });
      case "notifications/initialized":
        return;
      case "ping":
        return reply({});
      case "tools/list":
        return reply({ tools });
      case "tools/call":
        return reply({ content: [{ type: "text", text: callTool(msg.params.name, msg.params.arguments ?? {}) }] });
      case "prompts/list":
        return reply({ prompts: [{ name: "summarize_notes", description: "Summarize all saved notes", arguments: [] }] });
      case "prompts/get":
        return reply({ messages: [{ role: "user", content: { type: "text", text: `Summarize these notes in a short bulleted list:\n\n${JSON.stringify(load(), null, 2)}` } }] });
      case "resources/list":
        return reply({ resources: [{ uri: "notes://all", name: "All notes", mimeType: "application/json" }] });
      case "resources/read":
        return reply({ contents: [{ uri: "notes://all", mimeType: "application/json", text: JSON.stringify(load(), null, 2) }] });
      default:
        if (msg.id !== undefined) send({ jsonrpc: "2.0", id: msg.id, error: { code: -32601, message: `Method not found: ${msg.method}` } });
    }
  } catch (e) {
    if (msg.id !== undefined) send({ jsonrpc: "2.0", id: msg.id, result: { content: [{ type: "text", text: `Error: ${e.message}` }], isError: true } });
  }
});
