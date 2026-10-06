package com.gravicode.marbots;

import java.util.Map;

/** A risky action waiting for (or resolved by) a human. */
public record ApprovalRequest(String id, String taskId, String threadId, String botId, String toolName, String arguments,
                              String category, String risk, String reason, ApprovalState state, String resolvedBy) {
    static ApprovalRequest from(Map<String, Object> d) {
        return new ApprovalRequest(W.str(d, "id"), W.str(d, "taskId"), W.str(d, "threadId"), W.str(d, "botId"), W.str(d, "toolName"),
            W.str(d, "arguments", "{}"), W.str(d, "category"), W.str(d, "risk"), W.str(d, "reason"),
            ApprovalState.from(W.str(d, "state", "Pending")), W.opt(d, "resolvedBy"));
    }
}
