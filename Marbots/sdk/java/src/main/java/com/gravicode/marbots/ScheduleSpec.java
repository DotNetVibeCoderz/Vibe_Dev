package com.gravicode.marbots;

import java.util.LinkedHashMap;
import java.util.Map;

/**
 * A recurring ({@code cron}) or one-off ({@code runAt}, ISO-8601) job that sends {@code prompt} to {@code botId}.
 */
public record ScheduleSpec(String name, String botId, String prompt, String cron, String runAt, String timeZone) {
    /** A recurring job with a five-field cron expression, in UTC. */
    public static ScheduleSpec cron(String name, String botId, String prompt, String cron) {
        return new ScheduleSpec(name, botId, prompt, cron, null, "UTC");
    }

    Map<String, Object> toWire() {
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("name", name);
        m.put("botId", botId);
        m.put("prompt", prompt);
        m.put("cron", cron == null ? "" : cron);
        if (runAt != null) m.put("runAt", runAt);
        m.put("timeZone", timeZone == null ? "UTC" : timeZone);
        return m;
    }
}
