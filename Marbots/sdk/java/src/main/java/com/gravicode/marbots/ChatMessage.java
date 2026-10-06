package com.gravicode.marbots;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/** One chat message. */
public record ChatMessage(String id, String threadId, long seq, String role, String author, String content,
                          List<ToolCall> toolCalls, String toolName, String taskId, String createdAt) {
    /** A tool invocation requested by a model. */
    public record ToolCall(String id, String name, String arguments) {}

    static ChatMessage from(Map<String, Object> d) {
        List<ToolCall> calls = new ArrayList<>();
        for (Map<String, Object> c : W.objs(d.get("toolCalls"))) calls.add(new ToolCall(W.str(c, "id"), W.str(c, "name"), W.str(c, "arguments", "{}")));
        return new ChatMessage(W.str(d, "id"), W.str(d, "threadId"), W.num(d, "seq"), W.str(d, "role"), W.str(d, "author"),
            W.str(d, "content"), List.copyOf(calls), W.opt(d, "toolName"), W.opt(d, "taskId"), W.str(d, "createdAt"));
    }
}
