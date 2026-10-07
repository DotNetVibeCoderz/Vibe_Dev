package com.gravicode.marbots;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Options for creating or updating a bot. Build with {@link #builder(String)}. */
public final class BotSpec {
    private final Map<String, Object> wire = new LinkedHashMap<>();

    private BotSpec(String name) {
        wire.put("name", name);
        wire.put("color", "#2C3BA3");
        wire.put("modelProfile", ModelRef.DEFAULT);
        wire.put("kernelFunctions", List.of("files", "search", "web", "memory", "todo"));
        wire.put("skills", List.of());
        wire.put("mcpServers", List.of());
        wire.put("permissionProfile", PermissionProfile.DEVELOPER_SAFE.wire());
        wire.put("autoLearn", AutoLearnMode.OFF.wire());
        wire.put("shortTermMemory", true);
        wire.put("longTermMemory", true);
        wire.put("maxSteps", 24);
        wire.put("hostRef", HostRef.LOCAL);
    }

    /** Starts a spec for a bot named {@code name}. */
    public static BotSpec builder(String name) { return new BotSpec(name); }

    public BotSpec role(String v) { wire.put("role", v); return this; }
    public BotSpec description(String v) { wire.put("description", v); return this; }
    public BotSpec persona(String v) { wire.put("persona", v); return this; }
    public BotSpec color(String v) { wire.put("color", v); return this; }
    /** {@link ModelRef#DEFAULT}, {@link ModelRef#of(String, String)} or a profile name. */
    public BotSpec model(String v) { wire.put("modelProfile", v); return this; }
    public BotSpec kernelFunctions(KernelPack... packs) {
        List<String> l = new ArrayList<>();
        for (KernelPack p : packs) l.add(p.wire());
        wire.put("kernelFunctions", l);
        return this;
    }
    public BotSpec skills(String... v) { wire.put("skills", List.of(v)); return this; }
    public BotSpec mcpServers(String... v) { wire.put("mcpServers", List.of(v)); return this; }
    public BotSpec permissionProfile(PermissionProfile v) { wire.put("permissionProfile", v.wire()); return this; }
    public BotSpec autoLearn(AutoLearnMode v) { wire.put("autoLearn", v.wire()); return this; }
    public BotSpec shortTermMemory(boolean v) { wire.put("shortTermMemory", v); return this; }
    public BotSpec longTermMemory(boolean v) { wire.put("longTermMemory", v); return this; }
    public BotSpec maxSteps(int v) { wire.put("maxSteps", v); return this; }
    /** {@link HostRef#LOCAL} (default), a host id, or {@link HostRef#AUTO}. */
    public BotSpec hostRef(String v) { wire.put("hostRef", v); return this; }
    /** Run the bot's shell commands in a Docker container. */
    public BotSpec container(ContainerProfile v) { wire.put("container", v == null ? null : v.toWire()); return this; }

    Map<String, Object> toWire(String id) {
        Map<String, Object> m = new LinkedHashMap<>(wire);
        m.put("id", id);
        return m;
    }
}
