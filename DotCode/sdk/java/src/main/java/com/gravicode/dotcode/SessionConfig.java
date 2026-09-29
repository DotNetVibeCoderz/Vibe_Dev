package com.gravicode.dotcode;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.function.Consumer;

/**
 * Session configuration (fluent setters). Unset values fall back to the user's DotCode settings.
 *
 * <pre>{@code
 * new SessionConfig()
 *     .setModel("openai:gpt-5")
 *     .addTool(getWeather)
 *     .setOnPermissionRequest(PermissionHandler.APPROVE_ALL)
 * }</pre>
 */
public final class SessionConfig {
    String model, fallbackModel, workingDirectory;
    PermissionMode permissionMode;
    ReasoningEffort reasoningEffort;
    SystemMessageConfig systemMessage;
    final List<ToolDefinition> tools = new ArrayList<>();
    List<BuiltinTool> availableTools;
    final List<String> allowedTools = new ArrayList<>();
    final List<String> excludedTools = new ArrayList<>();
    final Map<String, McpServerConfig> mcpServers = new LinkedHashMap<>();
    boolean disableMcp;
    final Map<String, ProviderConfig> providers = new LinkedHashMap<>();
    Map<String, Object> settings;
    Integer maxTurns;
    boolean persistSession = true;
    Object worktree;
    PermissionHandler onPermissionRequest;
    UserInputHandler onUserInputRequest;
    ExitPlanModeHandler onExitPlanMode;
    Consumer<SessionEvent> onEvent;

    /** provider:model, alias or role — e.g. "anthropic:claude-sonnet-4-5", "openai:gpt-5", "ollama:qwen3-coder". */
    public SessionConfig setModel(String v) { model = v; return this; }
    public SessionConfig setFallbackModel(String v) { fallbackModel = v; return this; }
    /** Working directory of the session (default: the client's cwd). */
    public SessionConfig setWorkingDirectory(String v) { workingDirectory = v; return this; }
    public SessionConfig setPermissionMode(PermissionMode v) { permissionMode = v; return this; }
    public SessionConfig setReasoningEffort(ReasoningEffort v) { reasoningEffort = v; return this; }
    public SessionConfig setSystemMessage(SystemMessageConfig v) { systemMessage = v; return this; }
    /** Adds a custom tool implemented by your application ({@link ToolDefinition}). */
    public SessionConfig addTool(ToolDefinition tool) { tools.add(tool); return this; }
    public SessionConfig setTools(List<ToolDefinition> v) { tools.clear(); tools.addAll(v); return this; }
    /** Restricts the built-in tools. */
    public SessionConfig setAvailableTools(List<BuiltinTool> v) { availableTools = v; return this; }
    /** Pre-approves matching calls, e.g. {@code BuiltinTool.BASH.rule("npm test:*")}. */
    public SessionConfig addAllowedTool(String rule) { allowedTools.add(rule); return this; }
    public SessionConfig addAllowedTool(BuiltinTool tool) { allowedTools.add(tool.value()); return this; }
    /** Removes a tool / denies matching calls. */
    public SessionConfig addExcludedTool(String rule) { excludedTools.add(rule); return this; }
    public SessionConfig addExcludedTool(BuiltinTool tool) { excludedTools.add(tool.value()); return this; }
    public SessionConfig putMcpServer(String name, McpServerConfig server) { mcpServers.put(name, server); return this; }
    /** Skips MCP servers from settings files. */
    public SessionConfig setDisableMcp(boolean v) { disableMcp = v; return this; }
    /** Adds a named provider (BYOK), referenced as {@code "<name>:<model>"}. */
    public SessionConfig putProvider(String name, ProviderConfig provider) { providers.put(name, provider); return this; }
    /** Advanced: raw settings merged over the settings files (prefer the typed setters). */
    public SessionConfig setSettings(Map<String, Object> v) { settings = v; return this; }
    public SessionConfig setMaxTurns(int v) { maxTurns = v; return this; }
    /** Saves the transcript for {@link DotCodeClient#resumeSession} (default true). */
    public SessionConfig setPersistSession(boolean v) { persistSession = v; return this; }
    /** Runs in a fresh git worktree; removed on close when unchanged. */
    public SessionConfig setWorktree(boolean v) { worktree = v ? Boolean.TRUE : null; return this; }
    /** Runs in the named git worktree (created or reused). */
    public SessionConfig setWorktree(String name) { worktree = name; return this; }
    /** Approves tool calls. Without it the session is deny-by-default. */
    public SessionConfig setOnPermissionRequest(PermissionHandler v) { onPermissionRequest = v; return this; }
    /** Answers the model's AskUserQuestion tool. */
    public SessionConfig setOnUserInputRequest(UserInputHandler v) { onUserInputRequest = v; return this; }
    /** Reviews the plan when the agent leaves plan mode (default: approve). */
    public SessionConfig setOnExitPlanMode(ExitPlanModeHandler v) { onExitPlanMode = v; return this; }
    /** Receives every event, including those emitted while the session is created. */
    public SessionConfig setOnEvent(Consumer<SessionEvent> v) { onEvent = v; return this; }

    Map<String, Object> toWire(String defaultCwd) {
        Map<String, Object> s = new LinkedHashMap<>();
        if (settings != null) s.putAll(settings);
        if (!providers.isEmpty()) {
            Map<String, Object> p = new LinkedHashMap<>();
            if (s.get("providers") instanceof Map<?, ?> existing) existing.forEach((k, v) -> p.put(String.valueOf(k), v));
            providers.forEach((k, v) -> p.put(k, v.toWire()));
            s.put("providers", p);
        }
        Map<String, Object> mcp = new LinkedHashMap<>();
        mcpServers.forEach((k, v) -> mcp.put(k, McpServerConfig.toWire(v)));
        return Wire.map(
                "cwd", workingDirectory != null ? workingDirectory : defaultCwd,
                "model", model,
                "fallbackModel", fallbackModel,
                "permissionMode", permissionMode == null ? null : permissionMode.value(),
                "effort", reasoningEffort == null ? null : reasoningEffort.value(),
                "systemPrompt", systemMessage != null && systemMessage.replace() ? systemMessage.content() : null,
                "appendSystemPrompt", systemMessage != null && !systemMessage.replace() ? systemMessage.content() : null,
                "allowedTools", allowedTools.isEmpty() ? null : allowedTools,
                "disallowedTools", excludedTools.isEmpty() ? null : excludedTools,
                "tools", availableTools == null ? null : availableTools.stream().map(BuiltinTool::value).toList(),
                "hostTools", tools.isEmpty() ? null : tools.stream().map(ToolDefinition::toWire).toList(),
                "mcpServers", mcp.isEmpty() ? null : mcp,
                "settings", s.isEmpty() ? null : s,
                "maxTurns", maxTurns,
                "persistSession", persistSession,
                "noMcp", disableMcp ? Boolean.TRUE : null,
                "worktree", worktree);
    }
}
