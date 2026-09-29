package com.gravicode.dotcode;

/** Decides whether a tool call may run. Without a handler sessions are deny-by-default. */
@FunctionalInterface
public interface PermissionHandler {
    PermissionDecision handle(PermissionRequest request, Invocation invocation) throws Exception;

    /** Approves every request. */
    PermissionHandler APPROVE_ALL = (request, invocation) -> PermissionDecision.approveOnce();

    /** Rejects every request (same as no handler). */
    PermissionHandler REJECT_ALL = (request, invocation) -> PermissionDecision.reject("Rejected by the SDK host.");
}
