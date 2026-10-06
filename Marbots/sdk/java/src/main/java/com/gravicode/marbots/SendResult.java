package com.gravicode.marbots;

import java.util.Map;

/** Returned by {@code threads().send(...)}; {@code reply} is set when the call waited for the bot. */
public record SendResult(TaskRecord task, ChatMessage reply) {
    /** The reply text, or the task result/error. */
    public String text() {
        if (reply != null) return reply.content();
        return task.result() != null ? task.result() : (task.error() != null ? task.error() : "");
    }

    static SendResult from(Map<String, Object> d) {
        Object r = d.get("reply");
        return new SendResult(TaskRecord.from(W.obj(d.get("task"))), r instanceof Map<?, ?> ? ChatMessage.from(W.obj(r)) : null);
    }
}
