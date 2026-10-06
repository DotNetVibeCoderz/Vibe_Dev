package com.gravicode.marbots;

/** State of an approval request. */
public enum ApprovalState {
    PENDING("Pending"),
    APPROVED("Approved"),
    REJECTED("Rejected"),
    EXPIRED("Expired");

    private final String wire;

    ApprovalState(String wire) { this.wire = wire; }

    /** The value used on the wire. */
    public String wire() { return wire; }

    @Override public String toString() { return wire; }

    /** Parses a wire value; returns {@code null} for unknown values (forward compatibility). */
    public static ApprovalState from(String value) {
        for (ApprovalState v : values()) if (v.wire.equals(value)) return v;
        return null;
    }
}
