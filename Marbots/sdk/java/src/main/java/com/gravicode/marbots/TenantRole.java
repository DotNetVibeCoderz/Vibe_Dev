package com.gravicode.marbots;

/** A role inside a tenant, weakest first. */
public enum TenantRole {
    VIEWER("Viewer"), OPERATOR("Operator"), ADMIN("Admin"), OWNER("Owner");

    private final String wire;

    TenantRole(String wire) { this.wire = wire; }

    public String wire() { return wire; }

    static TenantRole parse(String s) {
        for (TenantRole r : values()) if (r.wire.equalsIgnoreCase(s)) return r;
        return VIEWER;
    }
}
