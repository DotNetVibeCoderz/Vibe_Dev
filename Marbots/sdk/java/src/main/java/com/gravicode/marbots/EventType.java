package com.gravicode.marbots;

/** Types of events on the live stream. */
public enum EventType {
    BOT_CREATED("BotCreated"),
    BOT_UPDATED("BotUpdated"),
    BOT_DELETED("BotDeleted"),
    BOT_STATE_CHANGED("BotStateChanged"),
    MESSAGE_ADDED("MessageAdded"),
    TASK_CREATED("TaskCreated"),
    TASK_STATE_CHANGED("TaskStateChanged"),
    TASK_DELEGATED("TaskDelegated"),
    TASK_PROGRESSED("TaskProgressed"),
    AGENT_THINKING_STARTED("AgentThinkingStarted"),
    AGENT_THINKING_COMPLETED("AgentThinkingCompleted"),
    TOOL_CALL_STARTED("ToolCallStarted"),
    TOOL_CALL_COMPLETED("ToolCallCompleted"),
    APPROVAL_REQUESTED("ApprovalRequested"),
    APPROVAL_RESOLVED("ApprovalResolved"),
    MEMORY_WRITTEN("MemoryWritten"),
    SKILL_LOADED("SkillLoaded"),
    CONTEXT_COMPACTED("ContextCompacted"),
    AUTO_LEARN_CANDIDATE_CREATED("AutoLearnCandidateCreated"),
    SCHEDULE_TRIGGERED("ScheduleTriggered"),
    HOST_CONNECTED("HostConnected"),
    TODO_UPDATED("TodoUpdated"),
    SETTINGS_CHANGED("SettingsChanged");

    private final String wire;

    EventType(String wire) { this.wire = wire; }

    /** The value used on the wire. */
    public String wire() { return wire; }

    @Override public String toString() { return wire; }

    /** Parses a wire value; returns {@code null} for unknown values (forward compatibility). */
    public static EventType from(String value) {
        for (EventType v : values()) if (v.wire.equals(value)) return v;
        return null;
    }
}
