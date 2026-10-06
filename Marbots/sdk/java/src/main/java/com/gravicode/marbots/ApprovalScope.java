package com.gravicode.marbots;

/** How far an approval reaches. */
public enum ApprovalScope {
    ONCE("Once"),
    SESSION("Session");

    private final String wire;

    ApprovalScope(String wire) { this.wire = wire; }

    /** The value used on the wire. */
    public String wire() { return wire; }

    @Override public String toString() { return wire; }

    /** Parses a wire value; returns {@code null} for unknown values (forward compatibility). */
    public static ApprovalScope from(String value) {
        for (ApprovalScope v : values()) if (v.wire.equals(value)) return v;
        return null;
    }
}
