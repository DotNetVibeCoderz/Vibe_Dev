package com.gravicode.marbots;

import java.util.List;
import java.util.Map;

/** Who the caller is: tenant, role and the tenants they may switch to. */
public record WhoAmI(String tenant, TenantRole role, String user, boolean platformAdmin, boolean multiTenant, List<String> tenants) {
    static WhoAmI from(Map<String, Object> d) {
        return new WhoAmI(W.str(d, "tenant"), TenantRole.parse(W.str(d, "role")), W.opt(d, "user"), W.bool(d, "platformAdmin"),
            W.bool(d, "multiTenant"), W.strs(d, "tenants"));
    }
}
