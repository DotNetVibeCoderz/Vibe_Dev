package com.gravicode.dotcode;

import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.function.Function;

/** Protocol value types (see schema/protocol.schema.json). */
public final class Types {
    private Types() {}

    static String str(Map<String, Object> m, String k) {
        Object v = m.get(k);
        return v == null ? null : v.toString();
    }

    static double num(Map<String, Object> m, String k) {
        Object v = m.get(k);
        return v instanceof Number n ? n.doubleValue() : 0;
    }

    /**
     * An engine event. {@link #type()} is one of session.started, user.message, assistant.text.delta,
     * assistant.thinking.delta, assistant.message, tool.started, tool.progress, tool.completed, todo.updated,
     * subagent.started, subagent.completed, context.compacted, usage.updated, model.changed, model.fallback, retry,
     * notice, error, mode.changed, turn.completed. {@link #raw()} holds every field.
     */
    public record Event(Map<String, Object> raw) {
        public String type() { return str(raw, "type"); }
        public String text() { return str(raw, "text"); }
        public String name() { return str(raw, "name"); }
        public String displayName() { return str(raw, "displayName"); }
        public String output() { return str(raw, "output"); }
        public String summary() { return str(raw, "summary"); }
        public String diff() { return str(raw, "diff"); }
        public String resultText() { return str(raw, "resultText"); }
        public String parentToolUseId() { return str(raw, "parentToolUseId"); }
        public boolean isError() { return Boolean.TRUE.equals(raw.get("isError")); }
        public double costUsd() { return num(raw, "costUsd"); }
        public long durationMs() { return (long) num(raw, "durationMs"); }
        public int numModelCalls() { return (int) num(raw, "numModelCalls"); }
        public boolean isTurnCompleted() { return "turn.completed".equals(type()) && parentToolUseId() == null; }
    }

    /** A tool call awaiting approval. */
    public record PermissionRequest(Map<String, Object> raw) {
        public String toolName() { return str(raw, "toolName"); }
        public String displayName() { return str(raw, "displayName"); }
        public String title() { return str(raw, "title"); }
        public String suggestedRule() { return str(raw, "suggestedRule"); }
        public String diff() { return str(raw, "diff"); }
        @SuppressWarnings("unchecked")
        public Map<String, Object> input() { return (Map<String, Object>) raw.getOrDefault("input", Collections.emptyMap()); }
    }

    /** Answer to a permission request: allow | allow_always | allow_session | deny. */
    public record PermissionDecision(String decision, String feedback, String rule) {
        public static PermissionDecision allow() { return new PermissionDecision("allow", null, null); }
        public static PermissionDecision allowAlways(String rule) { return new PermissionDecision("allow_always", null, rule); }
        public static PermissionDecision allowSession() { return new PermissionDecision("allow_session", null, null); }
        public static PermissionDecision deny(String feedback) { return new PermissionDecision("deny", feedback, null); }

        Map<String, Object> toWire() {
            Map<String, Object> m = new LinkedHashMap<>();
            m.put("decision", decision);
            m.put("feedback", feedback);
            m.put("rule", rule);
            return m;
        }
    }

    /**
     * A tool implemented by the host application.
     *
     * @param inputSchema JSON Schema as nested maps/lists (e.g. {@code Map.of("type","object",...)})
     */
    public record Tool(String name, String description, Map<String, Object> inputSchema, boolean readOnly,
                       Function<Map<String, Object>, String> handler) {
        public static Tool of(String name, String description, Map<String, Object> inputSchema, Function<Map<String, Object>, String> handler) {
            return new Tool(name, description, inputSchema, false, handler);
        }

        Map<String, Object> toWire() {
            Map<String, Object> m = new LinkedHashMap<>();
            m.put("name", name);
            m.put("description", description);
            m.put("inputSchema", inputSchema == null ? Map.of("type", "object", "properties", Map.of()) : inputSchema);
            m.put("readOnly", readOnly);
            return m;
        }
    }

    /** Result of {@link Session#send(String)}. */
    public record SendResult(Map<String, Object> raw) {
        public String result() { return str(raw, "result"); }
        public String stopReason() { return str(raw, "stopReason"); }
        public boolean isError() { return Boolean.TRUE.equals(raw.get("isError")); }
        public String error() { return str(raw, "error"); }
        public double costUsd() { return num(raw, "costUsd"); }
        public double totalCostUsd() { return num(raw, "totalCostUsd"); }
        public long durationMs() { return (long) num(raw, "durationMs"); }
        public int numModelCalls() { return (int) num(raw, "numModelCalls"); }
        @SuppressWarnings("unchecked")
        public Map<String, Object> usage() { return (Map<String, Object>) raw.getOrDefault("usage", Map.of()); }
    }

    /** Question asked by the model (AskUserQuestion). */
    public record UserQuestion(Map<String, Object> raw) {
        public String question() { return str(raw, "question"); }
        @SuppressWarnings("unchecked")
        public List<Map<String, Object>> options() { return (List<Map<String, Object>>) raw.getOrDefault("options", List.of()); }
    }
}
