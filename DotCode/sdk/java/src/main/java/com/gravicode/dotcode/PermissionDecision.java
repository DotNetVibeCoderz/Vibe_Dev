package com.gravicode.dotcode;

import java.util.Map;

/** The answer to a {@link PermissionRequest}. Build it with the static factories. */
public sealed interface PermissionDecision {
    /** Allow this single call ({@code updatedInput} may replace the arguments, or be null). */
    record ApproveOnce(Map<String, Object> updatedInput) implements PermissionDecision {}

    /** Allow matching calls for the rest of the session. */
    record ApproveForSession(Map<String, Object> updatedInput) implements PermissionDecision {}

    /** Allow and persist a rule (null: the request's suggested rule). */
    record ApproveAlways(String rule) implements PermissionDecision {}

    /** Deny; {@code feedback} is returned to the model. */
    record Reject(String feedback) implements PermissionDecision {}

    static PermissionDecision approveOnce() { return new ApproveOnce(null); }
    static PermissionDecision approveForSession() { return new ApproveForSession(null); }
    static PermissionDecision approveAlways(String rule) { return new ApproveAlways(rule); }
    static PermissionDecision reject(String feedback) { return new Reject(feedback); }

    static Map<String, Object> toWire(PermissionDecision d) {
        if (d instanceof ApproveOnce a) return Wire.map("decision", "allow", "updatedInput", a.updatedInput());
        if (d instanceof ApproveForSession a) return Wire.map("decision", "allow_session", "updatedInput", a.updatedInput());
        if (d instanceof ApproveAlways a) return Wire.map("decision", "allow_always", "rule", a.rule());
        Reject r = (Reject) d;
        return Wire.map("decision", "deny", "feedback", r.feedback());
    }
}
