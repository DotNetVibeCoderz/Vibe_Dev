package com.gravicode.dotcode;

/** Approves or rejects the plan when the agent leaves plan mode. */
public record ExitPlanModeResult(boolean approved, boolean acceptEdits, String feedback) {
    public static ExitPlanModeResult approve() { return new ExitPlanModeResult(true, false, null); }
    /** Approve and continue in acceptEdits mode. */
    public static ExitPlanModeResult approveAndAcceptEdits() { return new ExitPlanModeResult(true, true, null); }
    public static ExitPlanModeResult reject(String feedback) { return new ExitPlanModeResult(false, false, feedback); }
}
