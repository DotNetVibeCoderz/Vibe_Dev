package com.gravicode.marbots;

import java.util.Map;

/** An OIDC user's role in a tenant (matched by e-mail or subject). */
public record TenantMember(String tenant, String subject, TenantRole role) {
    static TenantMember from(Map<String, Object> d) {
        return new TenantMember(W.str(d, "tenant"), W.str(d, "subject"), TenantRole.parse(W.str(d, "role")));
    }
}
