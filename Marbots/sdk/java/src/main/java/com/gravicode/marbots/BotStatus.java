package com.gravicode.marbots;

/** A bot's lifecycle state. */
public enum BotStatus {
    READY("Ready"),
    RUNNING("Running"),
    PAUSED("Paused"),
    ARCHIVED("Archived"),
    DEGRADED("Degraded");

    private final String wire;

    BotStatus(String wire) { this.wire = wire; }

    /** The value used on the wire. */
    public String wire() { return wire; }

    @Override public String toString() { return wire; }

    /** Parses a wire value; returns {@code null} for unknown values (forward compatibility). */
    public static BotStatus from(String value) {
        for (BotStatus v : values()) if (v.wire.equals(value)) return v;
        return null;
    }
}
