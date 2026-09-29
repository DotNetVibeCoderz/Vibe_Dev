package com.gravicode.dotcode;

/** Reviews the plan (markdown) when the agent leaves plan mode. */
@FunctionalInterface
public interface ExitPlanModeHandler {
    ExitPlanModeResult handle(String plan, Invocation invocation) throws Exception;
}
