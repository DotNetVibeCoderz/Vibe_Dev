package com.gravicode.dotcode;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/**
 * An event streamed by the engine (the terminal UI consumes the exact same stream). Subscribe to one type with
 * {@code session.on(SessionEvent.ToolCompletedEvent.class, e -> ...)} or to all with {@code session.on(e -> ...)}.
 */
public sealed interface SessionEvent {
    /** The session that produced the event. */
    String sessionId();

    /** Set for events produced inside a subagent (null otherwise). */
    String parentToolUseId();

    /** The protocol type, e.g. {@code "tool.completed"}. */
    String type();

    record UserMessageEvent(String sessionId, String parentToolUseId, String text) implements SessionEvent {
        public String type() { return "user.message"; }
    }

    record AssistantTextDeltaEvent(String sessionId, String parentToolUseId, String text) implements SessionEvent {
        public String type() { return "assistant.text.delta"; }
    }

    record AssistantThinkingDeltaEvent(String sessionId, String parentToolUseId, String text) implements SessionEvent {
        public String type() { return "assistant.thinking.delta"; }
    }

    /** A tool call requested by the model. */
    record ToolCallInfo(String id, String name, Map<String, Object> input) {}

    record AssistantMessageEvent(String sessionId, String parentToolUseId, String messageId, String text, String thinking,
                                 List<ToolCallInfo> toolCalls, String model) implements SessionEvent {
        public String type() { return "assistant.message"; }
    }

    record ToolStartedEvent(String sessionId, String parentToolUseId, String toolUseId, String name, String displayName,
                            Map<String, Object> input) implements SessionEvent {
        public String type() { return "tool.started"; }
    }

    record ToolProgressEvent(String sessionId, String parentToolUseId, String toolUseId, String text) implements SessionEvent {
        public String type() { return "tool.progress"; }
    }

    record ToolCompletedEvent(String sessionId, String parentToolUseId, String toolUseId, String name, boolean isError,
                              String summary, String output, String diff, long durationMs, boolean rejected) implements SessionEvent {
        public String type() { return "tool.completed"; }
    }

    /** One entry of the agent's todo list; status is Pending, InProgress or Completed. */
    record TodoItem(String content, String status, String activeForm) {}

    record TodoUpdatedEvent(String sessionId, String parentToolUseId, List<TodoItem> todos) implements SessionEvent {
        public String type() { return "todo.updated"; }
    }

    record SubagentStartedEvent(String sessionId, String parentToolUseId, String toolUseId, String agentType,
                                String description, String model) implements SessionEvent {
        public String type() { return "subagent.started"; }
    }

    record SubagentCompletedEvent(String sessionId, String parentToolUseId, String toolUseId, String agentType, Usage usage,
                                  long durationMs, int toolUses) implements SessionEvent {
        public String type() { return "subagent.completed"; }
    }

    record ContextCompactedEvent(String sessionId, String parentToolUseId, long tokensBefore, long tokensAfter,
                                 boolean automatic) implements SessionEvent {
        public String type() { return "context.compacted"; }
    }

    record UsageUpdatedEvent(String sessionId, String parentToolUseId, Usage turnUsage, Usage sessionUsage,
                             double sessionCostUsd, long contextTokens, int contextWindow) implements SessionEvent {
        public String type() { return "usage.updated"; }
    }

    record ModelChangedEvent(String sessionId, String parentToolUseId, String model) implements SessionEvent {
        public String type() { return "model.changed"; }
    }

    record ModelFallbackEvent(String sessionId, String parentToolUseId, String from, String to, String reason) implements SessionEvent {
        public String type() { return "model.fallback"; }
    }

    record RetryEvent(String sessionId, String parentToolUseId, int attempt, int maxAttempts, double delaySeconds,
                      String reason) implements SessionEvent {
        public String type() { return "retry"; }
    }

    /** level is Info, Warning or Error. */
    record NoticeEvent(String sessionId, String parentToolUseId, String level, String text) implements SessionEvent {
        public String type() { return "notice"; }
    }

    record ErrorEvent(String sessionId, String parentToolUseId, String code, String message, boolean retryable) implements SessionEvent {
        public String type() { return "error"; }
    }

    record ModeChangedEvent(String sessionId, String parentToolUseId, PermissionMode mode) implements SessionEvent {
        public String type() { return "mode.changed"; }
    }

    record TurnCompletedEvent(String sessionId, String parentToolUseId, StopReason stopReason, String resultText, Usage usage,
                              double costUsd, long durationMs, int numModelCalls, boolean isError) implements SessionEvent {
        public String type() { return "turn.completed"; }
    }

    /** An event type this SDK version does not know. */
    record UnknownEvent(String sessionId, String parentToolUseId, String type, Map<String, Object> raw) implements SessionEvent {}

    @SuppressWarnings("unchecked")
    static SessionEvent fromWire(Map<String, Object> m) {
        String type = Wire.str(m, "type");
        String sid = Wire.str(m, "sessionId");
        String pid = Wire.str(m, "parentToolUseId");
        if (type == null) type = "";
        switch (type) {
            case "user.message": return new UserMessageEvent(sid, pid, Wire.str(m, "text"));
            case "assistant.text.delta": return new AssistantTextDeltaEvent(sid, pid, Wire.str(m, "text"));
            case "assistant.thinking.delta": return new AssistantThinkingDeltaEvent(sid, pid, Wire.str(m, "text"));
            case "assistant.message": {
                List<ToolCallInfo> calls = new ArrayList<>();
                for (Object c : Wire.list(m, "toolCalls")) {
                    Map<String, Object> cm = (Map<String, Object>) c;
                    calls.add(new ToolCallInfo(Wire.str(cm, "id"), Wire.str(cm, "name"), Wire.obj(cm, "input")));
                }
                return new AssistantMessageEvent(sid, pid, Wire.str(m, "messageId"), Wire.str(m, "text"), Wire.str(m, "thinking"), calls, Wire.str(m, "model"));
            }
            case "tool.started":
                return new ToolStartedEvent(sid, pid, Wire.str(m, "toolUseId"), Wire.str(m, "name"), Wire.str(m, "displayName"), Wire.obj(m, "input"));
            case "tool.progress": return new ToolProgressEvent(sid, pid, Wire.str(m, "toolUseId"), Wire.str(m, "text"));
            case "tool.completed":
                return new ToolCompletedEvent(sid, pid, Wire.str(m, "toolUseId"), Wire.str(m, "name"), Wire.bool(m, "isError"),
                        Wire.str(m, "summary"), Wire.str(m, "output"), Wire.str(m, "diff"), Wire.lng(m, "durationMs"), Wire.bool(m, "rejected"));
            case "todo.updated": {
                List<TodoItem> todos = new ArrayList<>();
                for (Object t : Wire.list(m, "todos")) {
                    Map<String, Object> tm = (Map<String, Object>) t;
                    todos.add(new TodoItem(Wire.str(tm, "content"), Wire.str(tm, "status"), Wire.str(tm, "activeForm")));
                }
                return new TodoUpdatedEvent(sid, pid, todos);
            }
            case "subagent.started":
                return new SubagentStartedEvent(sid, pid, Wire.str(m, "toolUseId"), Wire.str(m, "agentType"), Wire.str(m, "description"), Wire.str(m, "model"));
            case "subagent.completed":
                return new SubagentCompletedEvent(sid, pid, Wire.str(m, "toolUseId"), Wire.str(m, "agentType"), Usage.fromWire(Wire.obj(m, "usage")),
                        Wire.lng(m, "durationMs"), (int) Wire.num(m, "toolUses"));
            case "context.compacted":
                return new ContextCompactedEvent(sid, pid, Wire.lng(m, "tokensBefore"), Wire.lng(m, "tokensAfter"), Wire.bool(m, "automatic"));
            case "usage.updated":
                return new UsageUpdatedEvent(sid, pid, Usage.fromWire(Wire.obj(m, "turnUsage")), Usage.fromWire(Wire.obj(m, "sessionUsage")),
                        Wire.num(m, "sessionCostUsd"), Wire.lng(m, "contextTokens"), (int) Wire.num(m, "contextWindow"));
            case "model.changed": return new ModelChangedEvent(sid, pid, Wire.str(m, "model"));
            case "model.fallback": return new ModelFallbackEvent(sid, pid, Wire.str(m, "from"), Wire.str(m, "to"), Wire.str(m, "reason"));
            case "retry":
                return new RetryEvent(sid, pid, (int) Wire.num(m, "attempt"), (int) Wire.num(m, "maxAttempts"), Wire.num(m, "delaySeconds"), Wire.str(m, "reason"));
            case "notice": return new NoticeEvent(sid, pid, Wire.str(m, "level"), Wire.str(m, "text"));
            case "error": return new ErrorEvent(sid, pid, Wire.str(m, "code"), Wire.str(m, "message"), Wire.bool(m, "retryable"));
            case "mode.changed": return new ModeChangedEvent(sid, pid, PermissionMode.fromValue(Wire.str(m, "mode")));
            case "turn.completed":
                return new TurnCompletedEvent(sid, pid, StopReason.fromValue(Wire.str(m, "stopReason")), Wire.str(m, "resultText"),
                        Usage.fromWire(Wire.obj(m, "usage")), Wire.num(m, "costUsd"), Wire.lng(m, "durationMs"),
                        (int) Wire.num(m, "numModelCalls"), Wire.bool(m, "isError"));
            default: return new UnknownEvent(sid, pid, type, m);
        }
    }
}
