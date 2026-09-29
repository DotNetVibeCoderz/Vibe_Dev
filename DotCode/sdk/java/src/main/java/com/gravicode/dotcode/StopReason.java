package com.gravicode.dotcode;

/** Why a turn ended. */
public enum StopReason {
    END_TURN("EndTurn"),
    TOOL_USE("ToolUse"),
    MAX_TOKENS("MaxTokens"),
    STOP_SEQUENCE("StopSequence"),
    REFUSAL("Refusal"),
    ABORTED("Aborted"),
    ERROR("Error");

    private final String value;

    StopReason(String value) { this.value = value; }

    /** The protocol value. */
    public String value() { return value; }

    static StopReason fromValue(String v) {
        for (StopReason x : values()) if (x.value.equals(v)) return x;
        return null;
    }

    @Override
    public String toString() { return value; }
}
