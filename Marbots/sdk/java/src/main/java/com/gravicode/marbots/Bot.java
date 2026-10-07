package com.gravicode.marbots;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/**
 * A durable AI teammate.
 *
 * @param model "default" (workspace default model), a profile name, or "provider/model"
 * @param hostRef where the bot's files/shell/desktop tools run: {@link HostRef#LOCAL}, a host id or {@link HostRef#AUTO}
 * @param container Docker profile for the bot's shell, or {@code null}
 */
public record Bot(String id, String name, String role, String description, String persona, String color, String model,
                  List<KernelPack> kernelFunctions, List<String> skills, List<String> mcpServers,
                  PermissionProfile permissionProfile, AutoLearnMode autoLearn, boolean shortTermMemory,
                  boolean longTermMemory, int maxSteps, BotStatus status, boolean isSystem, String templateId,
                  String hostRef, ContainerProfile container) {

    /** True when the bot follows the workspace default model. */
    public boolean usesDefaultModel() { return model.isEmpty() || ModelRef.DEFAULT.equals(model); }

    static Bot from(Map<String, Object> d) {
        List<KernelPack> packs = new ArrayList<>();
        for (String s : W.strs(d, "kernelFunctions")) {
            KernelPack k = KernelPack.from(s);
            if (k != null) packs.add(k);
        }
        return new Bot(W.str(d, "id"), W.str(d, "name"), W.str(d, "role"), W.str(d, "description"), W.str(d, "persona"),
            W.str(d, "color"), W.str(d, "modelProfile", ModelRef.DEFAULT), List.copyOf(packs), W.strs(d, "skills"),
            W.strs(d, "mcpServers"), PermissionProfile.from(W.str(d, "permissionProfile")), AutoLearnMode.from(W.str(d, "autoLearn", "Off")),
            W.bool(d, "shortTermMemory"), W.bool(d, "longTermMemory"), (int) W.num(d, "maxSteps"),
            BotStatus.from(W.str(d, "status", "Ready")), W.bool(d, "isSystem"), W.opt(d, "templateId"),
            W.str(d, "hostRef", HostRef.LOCAL), ContainerProfile.from(d.get("container")));
    }
}
