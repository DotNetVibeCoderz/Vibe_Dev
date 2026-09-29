package com.gravicode.dotcode;

/** How tool calls are approved. */
public enum PermissionMode {
    DEFAULT("default"),
    ACCEPT_EDITS("acceptEdits"),
    AUTO("auto"),
    PLAN("plan"),
    BYPASS_PERMISSIONS("bypassPermissions");

    private final String value;

    PermissionMode(String value) { this.value = value; }

    /** The protocol value. */
    public String value() { return value; }

    static PermissionMode fromValue(String v) {
        for (PermissionMode x : values()) if (x.value.equals(v)) return x;
        return null;
    }

    @Override
    public String toString() { return value; }
}
