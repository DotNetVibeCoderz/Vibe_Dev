package com.gravicode.dotcode;

import java.util.Map;

/**
 * A tool call awaiting approval.
 *
 * @param suggestedRule rule the user could persist, e.g. {@code Bash(npm test:*)} (may be null)
 * @param diff          unified diff preview for file edits (may be null)
 */
public record PermissionRequest(String toolUseId, String toolName, String displayName, Map<String, Object> input,
                                String title, String detail, String suggestedRule, String diff, String parentToolUseId) {

    @SuppressWarnings("unchecked")
    static PermissionRequest fromWire(Map<String, Object> m) {
        Object input = m.get("input");
        return new PermissionRequest(Wire.str(m, "toolUseId"), Wire.str(m, "toolName"), Wire.str(m, "displayName"),
                input instanceof Map<?, ?> in ? (Map<String, Object>) in : Map.of(), Wire.str(m, "title"), Wire.str(m, "detail"),
                Wire.str(m, "suggestedRule"), Wire.str(m, "diff"), Wire.str(m, "parentToolUseId"));
    }
}
