package com.gravicode.dotcode;

import java.util.Map;

/** One call of a custom tool; {@code arguments} are the raw arguments sent by the model. */
public record ToolInvocation(String sessionId, String toolCallId, String toolName, Map<String, Object> arguments) {}
