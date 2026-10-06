package com.gravicode.marbots;

import java.util.Map;

/** A conversation with one bot. */
public record ChatThread(String id, String title, String botId, boolean pinned, boolean archived, String updatedAt) {
    static ChatThread from(Map<String, Object> d) {
        return new ChatThread(W.str(d, "id"), W.str(d, "title"), W.str(d, "botId"), W.bool(d, "pinned"), W.bool(d, "archived"), W.str(d, "updatedAt"));
    }
}
