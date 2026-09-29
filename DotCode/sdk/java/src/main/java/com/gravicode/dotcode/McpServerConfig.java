package com.gravicode.dotcode;

import java.util.List;
import java.util.Map;

/** An MCP server: {@link #stdio} (child process) or {@link #http} / {@link #sse} (remote). */
public sealed interface McpServerConfig {
    record Stdio(String command, List<String> args, Map<String, String> env) implements McpServerConfig {}

    record Http(String url, Map<String, String> headers, boolean sse) implements McpServerConfig {}

    static Stdio stdio(String command, String... args) { return new Stdio(command, List.of(args), Map.of()); }
    static Http http(String url) { return new Http(url, Map.of(), false); }
    static Http http(String url, Map<String, String> headers) { return new Http(url, headers, false); }
    /** A legacy SSE server. */
    static Http sse(String url) { return new Http(url, Map.of(), true); }

    static Map<String, Object> toWire(McpServerConfig s) {
        if (s instanceof Stdio x)
            return Wire.map("type", "stdio", "command", x.command(), "args", x.args().isEmpty() ? null : x.args(),
                    "env", x.env().isEmpty() ? null : x.env());
        Http h = (Http) s;
        return Wire.map("type", h.sse() ? "sse" : "http", "url", h.url(), "headers", h.headers().isEmpty() ? null : h.headers());
    }
}
