package com.gravicode.marbots;

import java.util.Map;

/**
 * One item on the live event stream.
 *
 * @param type the event type ({@code null} for types newer than this SDK; see {@code rawType})
 */
public record AgentEvent(long id, EventType type, String rawType, String timestamp, String threadId, String taskId,
                         String botId, String message, String data) {
    /** True when this event says a task finished. */
    public boolean isTaskFinished() {
        if (type != EventType.TASK_STATE_CHANGED || data == null) return false;
        TaskState s = TaskState.from(data);
        return s != null && s.isTerminal();
    }

    static AgentEvent from(Map<String, Object> d) {
        String raw = W.str(d, "type");
        return new AgentEvent(W.num(d, "id"), EventType.from(raw), raw, W.str(d, "timestamp"), W.opt(d, "threadId"),
            W.opt(d, "taskId"), W.opt(d, "botId"), W.opt(d, "message"), W.opt(d, "data"));
    }
}
