package com.gravicode.marbots;

import java.util.Map;

/** A tenant API key as listed; the key itself is only returned when created. */
public record ApiKeyInfo(String id, String tenant, String name, TenantRole role, String prefix, String lastUsedAt) {
    static ApiKeyInfo from(Map<String, Object> d) {
        return new ApiKeyInfo(W.str(d, "id"), W.str(d, "tenant"), W.str(d, "name"), TenantRole.parse(W.str(d, "role")), W.str(d, "prefix"), W.opt(d, "lastUsedAt"));
    }
}
