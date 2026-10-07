package com.gravicode.marbots;

import java.util.Map;

/** A tenant in multi-tenant mode; {@code default} always exists. */
public record Tenant(String id, String name, boolean disabled, String createdAt) {
    static Tenant from(Map<String, Object> d) {
        return new Tenant(W.str(d, "id"), W.str(d, "name"), W.bool(d, "disabled"), W.str(d, "createdAt"));
    }
}
