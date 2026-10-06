package com.gravicode.marbots;

import java.util.Map;

/** A saved schedule. */
public record ScheduleJob(String id, String name, String botId, String prompt, String cron, String timeZone, boolean enabled,
                          String nextRunAt, String lastRunAt) {
    static ScheduleJob from(Map<String, Object> d) {
        return new ScheduleJob(W.str(d, "id"), W.str(d, "name"), W.str(d, "botId"), W.str(d, "prompt"), W.str(d, "cron"),
            W.str(d, "timeZone"), W.bool(d, "enabled"), W.opt(d, "nextRunAt"), W.opt(d, "lastRunAt"));
    }
}
