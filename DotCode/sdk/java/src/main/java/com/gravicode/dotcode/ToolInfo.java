package com.gravicode.dotcode;

import java.util.Map;

/** A tool available to the model. */
public record ToolInfo(String name, String description, Map<String, Object> inputSchema) {}
