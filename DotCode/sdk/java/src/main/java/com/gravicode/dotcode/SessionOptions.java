package com.gravicode.dotcode;

import com.gravicode.dotcode.Types.Event;
import com.gravicode.dotcode.Types.PermissionDecision;
import com.gravicode.dotcode.Types.PermissionRequest;
import com.gravicode.dotcode.Types.Tool;
import com.gravicode.dotcode.Types.UserQuestion;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.function.Consumer;
import java.util.function.Function;
import java.util.function.Predicate;

/** Session configuration (builder style). Unset values fall back to the user's DotCode settings. */
public final class SessionOptions {
    String model, fallbackModel, cwd, permissionMode, systemPrompt, appendSystemPrompt, effort;
    final List<String> allowedTools = new ArrayList<>();
    final List<String> disallowedTools = new ArrayList<>();
    List<String> builtinTools;
    final List<Tool> tools = new ArrayList<>();
    Map<String, Object> mcpServers;
    Map<String, Object> settings;
    Integer maxTurns;
    boolean persistSession = true;
    boolean noMcp;
    Function<PermissionRequest, PermissionDecision> onPermissionRequest;
    Function<List<UserQuestion>, List<Map<String, String>>> onQuestion;
    Predicate<String> onPlanReview;
    Consumer<Event> onEvent;

    public static SessionOptions builder() { return new SessionOptions(); }

    /** provider:model, alias or role — e.g. "anthropic:claude-sonnet-4-5", "openai:gpt-5", "ollama:qwen3-coder". */
    public SessionOptions model(String v) { model = v; return this; }
    public SessionOptions fallbackModel(String v) { fallbackModel = v; return this; }
    public SessionOptions cwd(String v) { cwd = v; return this; }
    /** default | acceptEdits | plan | bypassPermissions */
    public SessionOptions permissionMode(String v) { permissionMode = v; return this; }
    public SessionOptions systemPrompt(String v) { systemPrompt = v; return this; }
    public SessionOptions appendSystemPrompt(String v) { appendSystemPrompt = v; return this; }
    public SessionOptions effort(String v) { effort = v; return this; }
    public SessionOptions allowTool(String rule) { allowedTools.add(rule); return this; }
    public SessionOptions denyTool(String rule) { disallowedTools.add(rule); return this; }
    public SessionOptions builtinTools(List<String> v) { builtinTools = v; return this; }
    public SessionOptions tool(Tool t) { tools.add(t); return this; }
    public SessionOptions mcpServers(Map<String, Object> v) { mcpServers = v; return this; }
    /** Inline settings merged over settings files (e.g. {@code {"providers":{...}}} for BYOK). */
    public SessionOptions settings(Map<String, Object> v) { settings = v; return this; }
    public SessionOptions maxTurns(int v) { maxTurns = v; return this; }
    public SessionOptions persistSession(boolean v) { persistSession = v; return this; }
    public SessionOptions noMcp(boolean v) { noMcp = v; return this; }
    /** Approves tool calls. Without it the session is deny-by-default. */
    public SessionOptions onPermissionRequest(Function<PermissionRequest, PermissionDecision> v) { onPermissionRequest = v; return this; }
    public SessionOptions onQuestion(Function<List<UserQuestion>, List<Map<String, String>>> v) { onQuestion = v; return this; }
    public SessionOptions onPlanReview(Predicate<String> v) { onPlanReview = v; return this; }
    public SessionOptions onEvent(Consumer<Event> v) { onEvent = v; return this; }

    Map<String, Object> toWire(String defaultCwd) {
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("cwd", cwd != null ? cwd : defaultCwd);
        m.put("model", model);
        m.put("fallbackModel", fallbackModel);
        m.put("permissionMode", permissionMode);
        m.put("systemPrompt", systemPrompt);
        m.put("appendSystemPrompt", appendSystemPrompt);
        m.put("effort", effort);
        if (!allowedTools.isEmpty()) m.put("allowedTools", allowedTools);
        if (!disallowedTools.isEmpty()) m.put("disallowedTools", disallowedTools);
        if (builtinTools != null) m.put("tools", builtinTools);
        if (!tools.isEmpty()) m.put("hostTools", tools.stream().map(Tool::toWire).toList());
        m.put("mcpServers", mcpServers);
        m.put("settings", settings);
        m.put("maxTurns", maxTurns);
        m.put("persistSession", persistSession);
        if (noMcp) m.put("noMcp", true);
        return m;
    }
}
