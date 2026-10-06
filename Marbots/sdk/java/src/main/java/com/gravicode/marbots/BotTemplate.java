package com.gravicode.marbots;

import java.util.List;
import java.util.Map;

/** A ready-made bot role from the template gallery. */
public record BotTemplate(String id, String name, String category, String role, String description, String persona,
                          String model, List<String> skills, List<String> mcpServers, List<String> kernelFunctions,
                          List<String> tags, String permissionProfile, boolean isBuiltIn) {
    static BotTemplate from(Map<String, Object> d) {
        return new BotTemplate(W.str(d, "id"), W.str(d, "name"), W.str(d, "category"), W.str(d, "role"), W.str(d, "description"),
            W.str(d, "persona"), W.str(d, "modelProfile", ModelRef.DEFAULT), W.strs(d, "skills"), W.strs(d, "mcpServers"),
            W.strs(d, "kernelFunctions"), W.strs(d, "tags"), W.str(d, "permissionProfile"), W.bool(d, "isBuiltIn"));
    }
}
