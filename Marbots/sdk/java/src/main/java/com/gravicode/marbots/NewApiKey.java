package com.gravicode.marbots;

import java.util.Map;

/** A freshly created key; {@code key} is shown only this once. */
public record NewApiKey(String id, String key, String tenant, TenantRole role) {
    static NewApiKey from(Map<String, Object> d) {
        return new NewApiKey(W.str(d, "id"), W.str(d, "key"), W.str(d, "tenant"), TenantRole.parse(W.str(d, "role")));
    }
}
