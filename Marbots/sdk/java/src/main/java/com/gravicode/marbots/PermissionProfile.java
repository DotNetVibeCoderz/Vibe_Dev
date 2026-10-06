package com.gravicode.marbots;

/** What a bot may do without asking. */
public enum PermissionProfile {
    READ_ONLY("read-only"),
    WORKSPACE_WRITE("workspace-write"),
    DEVELOPER_SAFE("developer-safe"),
    AUTONOMOUS("autonomous"),
    MANAGER("manager");

    private final String wire;

    PermissionProfile(String wire) { this.wire = wire; }

    /** The value used on the wire. */
    public String wire() { return wire; }

    @Override public String toString() { return wire; }

    /** Parses a wire value; returns {@code null} for unknown values (forward compatibility). */
    public static PermissionProfile from(String value) {
        for (PermissionProfile v : values()) if (v.wire.equals(value)) return v;
        return null;
    }
}
