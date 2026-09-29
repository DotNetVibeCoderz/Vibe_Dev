package com.gravicode.dotcode;

/** Thinking budget for models that support it. */
public enum ReasoningEffort {
    OFF("off"),
    LOW("low"),
    MEDIUM("medium"),
    HIGH("high"),
    XHIGH("xhigh");

    private final String value;

    ReasoningEffort(String value) { this.value = value; }

    /** The protocol value. */
    public String value() { return value; }

    static ReasoningEffort fromValue(String v) {
        for (ReasoningEffort x : values()) if (x.value.equals(v)) return x;
        return null;
    }

    @Override
    public String toString() { return value; }
}
