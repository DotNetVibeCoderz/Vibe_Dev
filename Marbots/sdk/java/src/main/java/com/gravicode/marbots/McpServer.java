package com.gravicode.marbots;

import java.util.List;
import java.util.Map;

/** An MCP server from the gallery. */
public record McpServer(String id, String name, String description, String transport, String command, List<String> args,
                        String url, String trust, boolean installed) {
    static McpServer from(Map<String, Object> d) {
        return new McpServer(W.str(d, "id"), W.str(d, "name"), W.str(d, "description"), W.str(d, "transport"), W.opt(d, "command"),
            W.strs(d, "args"), W.opt(d, "url"), W.str(d, "trust"), !W.bool(d, "isCatalogEntry"));
    }
}
