package com.gravicode.dotcode;

import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.function.Consumer;

/** One conversation with the agent. {@link #close()} disconnects it (the transcript stays on disk). */
public final class DotCodeSession implements AutoCloseable {
    private final DotCodeClient client;
    private final SessionConfig config;
    private final Map<String, Object> info;
    private final String sessionId;
    private volatile String model;
    private final List<Consumer<SessionEvent>> handlers = new CopyOnWriteArrayList<>();

    DotCodeSession(DotCodeClient client, Map<String, Object> info, SessionConfig config) {
        this.client = client;
        this.info = info;
        this.config = config;
        this.sessionId = Wire.str(info, "sessionId");
        this.model = Wire.str(info, "model");
    }

    public String getSessionId() { return sessionId; }
    public String getModel() { return model; }
    /** The raw session info returned by the server (cwd, tools, transcript path…). */
    public Map<String, Object> getInfo() { return info; }

    // ------------------------------------------------------------------ events

    /** Subscribes to all events; returns a handle that unsubscribes. */
    public Runnable on(Consumer<SessionEvent> handler) {
        handlers.add(handler);
        return () -> handlers.remove(handler);
    }

    /** Subscribes to one event type, e.g. {@code on(SessionEvent.ToolCompletedEvent.class, e -> ...)}. */
    public <E extends SessionEvent> Runnable on(Class<E> type, Consumer<E> handler) {
        return on(e -> {
            if (type.isInstance(e)) handler.accept(type.cast(e));
        });
    }

    void dispatch(SessionEvent e) {
        if (e instanceof SessionEvent.ModelChangedEvent mc) model = mc.model();
        if (config.onEvent != null) safe(config.onEvent, e);
        for (Consumer<SessionEvent> h : handlers) safe(h, e);
    }

    private static void safe(Consumer<SessionEvent> h, SessionEvent e) {
        try {
            h.accept(e);
        } catch (RuntimeException ignored) {
            // a failing listener must not break the session
        }
    }

    @SuppressWarnings("unchecked")
    Object handleServerRequest(String method, Map<String, Object> params) throws Exception {
        Invocation invocation = new Invocation(sessionId);
        switch (method) {
            case "permission.request": {
                if (config.onPermissionRequest == null)
                    return PermissionDecision.toWire(PermissionDecision.reject("No permission handler registered in the SDK host (deny by default)."));
                PermissionDecision d = config.onPermissionRequest.handle(PermissionRequest.fromWire(Wire.obj(params, "request")), invocation);
                return PermissionDecision.toWire(d == null ? PermissionDecision.reject(null) : d);
            }
            case "user.question": {
                if (config.onUserInputRequest == null) return Wire.map("answers", List.of());
                List<UserQuestion> questions = new ArrayList<>();
                for (Object q : Wire.list(params, "questions")) questions.add(UserQuestion.fromWire((Map<String, Object>) q));
                List<Object> answers = new ArrayList<>();
                for (UserQuestionAnswer a : config.onUserInputRequest.handle(questions, invocation))
                    answers.add(Wire.map("question", a.question(), "answer", a.answer()));
                return Wire.map("answers", answers);
            }
            case "plan.review": {
                if (config.onExitPlanMode == null) return Wire.map("approval", "approve");
                ExitPlanModeResult r = config.onExitPlanMode.handle(Wire.str(params, "plan"), invocation);
                if (r.approved()) return Wire.map("approval", r.acceptEdits() ? "approve_accept_edits" : "approve");
                return Wire.map("approval", "reject", "feedback", r.feedback());
            }
            case "tool.call": {
                String name = Wire.str(params, "name");
                for (ToolDefinition t : config.tools) {
                    if (!t.getName().equals(name)) continue;
                    Map<String, Object> args = Wire.obj(params, "input");
                    return t.invoke(args, new ToolInvocation(sessionId, Wire.str(params, "toolUseId"), name, args));
                }
                throw new DotCodeClient.RpcException(-32601, "Unknown host tool " + name);
            }
            default:
                throw new DotCodeClient.RpcException(-32601, method);
        }
    }

    // ------------------------------------------------------------------ turns

    private Map<String, Object> sendParams(MessageOptions message) {
        List<Object> attachments = new ArrayList<>();
        for (Attachment a : message.getAttachments()) attachments.add(Attachment.toWire(a));
        return Wire.map("sessionId", sessionId, "prompt", message.getPrompt(), "attachments", attachments.isEmpty() ? null : attachments);
    }

    /**
     * Starts a turn and completes once it is dispatched; follow it with {@link #on} ({@code TurnCompletedEvent} ends
     * it). Failures are delivered as an {@link SessionEvent.ErrorEvent}.
     */
    public CompletableFuture<Void> send(MessageOptions message) {
        client.callAsync("session.send", sendParams(message)).exceptionally(e -> {
            dispatch(new SessionEvent.ErrorEvent(sessionId, null, "send_failed", DotCodeClient.unwrap(e).getMessage(), false));
            return null;
        });
        return CompletableFuture.completedFuture(null);
    }

    /** Runs a turn to completion and returns its result. */
    @SuppressWarnings("unchecked")
    public CompletableFuture<SendResult> sendAndWait(MessageOptions message) {
        return client.callAsync("session.send", sendParams(message)).thenApply(r -> SendResult.fromWire((Map<String, Object>) r));
    }

    /** Runs a turn to completion; when it takes longer than {@code timeout} the turn is aborted. */
    public CompletableFuture<SendResult> sendAndWait(MessageOptions message, Duration timeout) {
        return sendAndWait(message).orTimeout(timeout.toMillis(), TimeUnit.MILLISECONDS).whenComplete((r, e) -> {
            if (e instanceof TimeoutException || (e != null && e.getCause() instanceof TimeoutException)) abort();
        });
    }

    // ------------------------------------------------------------------ control

    private CompletableFuture<Object> call(String method, Object... extra) {
        Object[] kv = new Object[extra.length + 2];
        kv[0] = "sessionId";
        kv[1] = sessionId;
        System.arraycopy(extra, 0, kv, 2, extra.length);
        return client.callAsync(method, Wire.map(kv));
    }

    /** Cancels the running turn. */
    public CompletableFuture<Void> abort() { return call("session.abort").thenAccept(r -> { }); }

    public CompletableFuture<Void> setModel(String value) {
        return call("session.setModel", "model", value).thenAccept(r -> model = value);
    }

    public CompletableFuture<Void> setPermissionMode(PermissionMode mode) {
        return call("session.setMode", "mode", mode.value()).thenAccept(r -> { });
    }

    public CompletableFuture<Void> setReasoningEffort(ReasoningEffort effort) {
        return call("session.setEffort", "effort", effort.value()).thenAccept(r -> { });
    }

    /** Summarizes the conversation to free context ({@code instructions} may be null). */
    public CompletableFuture<Void> compact(String instructions) {
        return call("session.compact", "instructions", instructions).thenAccept(r -> { });
    }

    /** Clears the conversation. */
    public CompletableFuture<Void> clear() { return call("session.clear").thenAccept(r -> { }); }

    /** The conversation so far (provider-neutral messages). */
    @SuppressWarnings("unchecked")
    public CompletableFuture<List<Map<String, Object>>> getMessages() {
        return call("session.messages").thenApply(r -> (List<Map<String, Object>>) (List<?>) Wire.list((Map<String, Object>) r, "messages"));
    }

    /** Tools available to the model (built-in, MCP and yours). */
    @SuppressWarnings("unchecked")
    public CompletableFuture<List<ToolInfo>> listTools() {
        return call("tools.list").thenApply(r -> {
            List<ToolInfo> out = new ArrayList<>();
            for (Object o : Wire.list((Map<String, Object>) r, "tools")) {
                Map<String, Object> m = (Map<String, Object>) o;
                out.add(new ToolInfo(Wire.str(m, "name"), Wire.str(m, "description"), Wire.obj(m, "inputSchema")));
            }
            return out;
        });
    }

    /** Closes the session on the server; the transcript stays on disk for {@link DotCodeClient#resumeSession}. */
    public CompletableFuture<Void> disconnect() {
        return call("session.close").handle((r, e) -> {
            client.sessions.remove(sessionId);
            handlers.clear();
            return null;
        });
    }

    @Override
    public void close() {
        try {
            disconnect().get(5, TimeUnit.SECONDS);
        } catch (Exception ignored) {
            // best effort
        }
    }
}
