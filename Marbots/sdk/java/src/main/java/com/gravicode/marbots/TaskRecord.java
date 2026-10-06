package com.gravicode.marbots;

import java.util.Map;

/**
 * A durable unit of work.
 *
 * @param model the provider/model that served the latest step
 */
public record TaskRecord(String id, String parentTaskId, String threadId, String botId, int depth, String objective,
                         TaskState state, String result, String error, String currentActivity, String model, int steps,
                         long inputTokens, long outputTokens, double costUsd) {
    static TaskRecord from(Map<String, Object> d) {
        return new TaskRecord(W.str(d, "id"), W.opt(d, "parentTaskId"), W.str(d, "threadId"), W.str(d, "botId"), (int) W.num(d, "depth"),
            W.str(d, "objective"), TaskState.from(W.str(d, "state", "Queued")), W.opt(d, "result"), W.opt(d, "error"),
            W.opt(d, "currentActivity"), W.opt(d, "model"), (int) W.num(d, "steps"), W.num(d, "inputTokens"),
            W.num(d, "outputTokens"), W.dbl(d, "costUsd"));
    }
}
