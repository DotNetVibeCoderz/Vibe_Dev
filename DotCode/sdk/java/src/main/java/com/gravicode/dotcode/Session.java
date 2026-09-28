package com.gravicode.dotcode;

import com.gravicode.dotcode.Types.Event;
import com.gravicode.dotcode.Types.PermissionRequest;
import com.gravicode.dotcode.Types.SendResult;
import com.gravicode.dotcode.Types.Tool;
import com.gravicode.dotcode.Types.UserQuestion;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.function.Consumer;

/** One conversation with the agent. */
public final class Session {
    private final DotCodeClient client;
    private final SessionOptions options;
    private final Map<String, Object> info;
    private final String id;
    private volatile String model;
    private final List<Consumer<Event>> listeners = new CopyOnWriteArrayList<>();

    Session(DotCodeClient client, Map<String, Object> info, SessionOptions options) {
        this.client = client;
        this.info = info;
        this.options = options;
        this.id = String.valueOf(info.get("sessionId"));
        this.model = String.valueOf(info.get("model"));
    }

    public String id() { return id; }
    public String model() { return model; }
    public Map<String, Object> info() { return info; }

    /** Subscribes to engine events; returns a handle that unsubscribes. */
    public Runnable onEvent(Consumer<Event> listener) {
        listeners.add(listener);
        return () -> listeners.remove(listener);
    }

    void dispatch(Event e) {
        if ("model.changed".equals(e.type())) model = String.valueOf(e.raw().get("model"));
        if (options.onEvent != null) options.onEvent.accept(e);
        for (Consumer<Event> l : listeners) l.accept(e);
    }

    @SuppressWarnings("unchecked")
    Object handleServerRequest(String method, Map<String, Object> params) {
        switch (method) {
            case "permission.request": {
                var request = new PermissionRequest((Map<String, Object>) params.get("request"));
                if (options.onPermissionRequest == null)
                    return Types.PermissionDecision.deny("No permission handler registered in the SDK host (deny by default).").toWire();
                return options.onPermissionRequest.apply(request).toWire();
            }
            case "user.question": {
                List<UserQuestion> qs = new ArrayList<>();
                for (Object q : (List<Object>) params.getOrDefault("questions", List.of())) qs.add(new UserQuestion((Map<String, Object>) q));
                return DotCodeClient.mapOf("answers", options.onQuestion == null ? List.of() : options.onQuestion.apply(qs));
            }
            case "plan.review": {
                boolean ok = options.onPlanReview == null || options.onPlanReview.test(String.valueOf(params.get("plan")));
                return DotCodeClient.mapOf("approval", ok ? "approve" : "reject");
            }
            case "tool.call": {
                String name = String.valueOf(params.get("name"));
                for (Tool t : options.tools) {
                    if (!t.name().equals(name)) continue;
                    try {
                        Object in = params.get("input");
                        return DotCodeClient.mapOf("content", t.handler().apply(in instanceof Map<?, ?> m ? (Map<String, Object>) m : Map.of()));
                    } catch (RuntimeException e) {
                        return DotCodeClient.mapOf("content", "Error: " + e.getMessage(), "isError", true);
                    }
                }
                throw new DotCodeClient.RpcException(-32601, "Unknown host tool " + name);
            }
            default:
                throw new DotCodeClient.RpcException(-32601, method);
        }
    }

    /** Runs a prompt to completion. */
    @SuppressWarnings("unchecked")
    public SendResult send(String prompt) {
        return new SendResult((Map<String, Object>) client.call("session.send", DotCodeClient.mapOf("sessionId", id, "prompt", prompt)));
    }

    /** Runs a prompt asynchronously. */
    @SuppressWarnings("unchecked")
    public CompletableFuture<SendResult> sendAsync(String prompt) {
        return client.callAsync("session.send", DotCodeClient.mapOf("sessionId", id, "prompt", prompt))
                .thenApply(r -> new SendResult((Map<String, Object>) r));
    }

    /** Runs a prompt, delivering every event to {@code onEvent} until the turn completes. */
    public SendResult stream(String prompt, Consumer<Event> onEvent) {
        Runnable off = onEvent(onEvent);
        try { return send(prompt); } finally { off.run(); }
    }

    private void simple(String method, Object... extra) {
        Object[] kv = new Object[extra.length + 2];
        kv[0] = "sessionId";
        kv[1] = id;
        System.arraycopy(extra, 0, kv, 2, extra.length);
        client.call(method, DotCodeClient.mapOf(kv));
    }

    public void abort() { simple("session.abort"); }
    public void setModel(String m) { simple("session.setModel", "model", m); model = m; }
    public void setPermissionMode(String mode) { simple("session.setMode", "mode", mode); }
    public void compact(String instructions) { simple("session.compact", "instructions", instructions); }

    @SuppressWarnings("unchecked")
    public List<Map<String, Object>> messages() {
        var r = (Map<String, Object>) client.call("session.messages", DotCodeClient.mapOf("sessionId", id));
        return (List<Map<String, Object>>) r.get("messages");
    }

    public void close() {
        simple("session.close");
        client.sessions.remove(id);
    }
}
