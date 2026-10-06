package com.gravicode.marbots;

/** A task's lifecycle state. */
public enum TaskState {
    QUEUED("Queued"),
    PREPARING("Preparing"),
    RUNNING("Running"),
    WAITING_FOR_TOOL("WaitingForTool"),
    WAITING_FOR_AGENT("WaitingForAgent"),
    WAITING_FOR_HUMAN("WaitingForHuman"),
    COMPLETED("Completed"),
    FAILED("Failed"),
    CANCELLED("Cancelled"),
    TIMED_OUT("TimedOut");

    private final String wire;

    TaskState(String wire) { this.wire = wire; }

    /** True for Completed, Failed, Cancelled and TimedOut. */
    public boolean isTerminal() { return this == COMPLETED || this == FAILED || this == CANCELLED || this == TIMED_OUT; }

    /** The value used on the wire. */
    public String wire() { return wire; }

    @Override public String toString() { return wire; }

    /** Parses a wire value; returns {@code null} for unknown values (forward compatibility). */
    public static TaskState from(String value) {
        for (TaskState v : values()) if (v.wire.equals(value)) return v;
        return null;
    }
}
