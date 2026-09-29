package com.gravicode.dotcode;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionException;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;

/**
 * DotCode SDK for Java — embed the DotCode multi-LLM coding agent in JVM applications. Starts {@code dotcode serve}
 * and speaks JSON-RPC 2.0 over its stdio. Built by Gravicode Studios, led by Kang Fadhil.
 *
 * <pre>{@code
 * try (var client = new DotCodeClient()) {
 *     client.start().get();
 *     var session = client.createSession(new SessionConfig()
 *             .setModel("openai:gpt-5")
 *             .setOnPermissionRequest(PermissionHandler.APPROVE_ALL)).get();
 *     session.on(SessionEvent.AssistantTextDeltaEvent.class, e -> System.out.print(e.text()));
 *     SendResult result = session.sendAndWait(new MessageOptions("Summarize README.md")).get();
 * }
 * }</pre>
 */
public final class DotCodeClient implements AutoCloseable {
    public static final String PROTOCOL_VERSION = "1.0";
    static final String SDK_VERSION = "0.2.0";

    /** JSON-RPC error returned by the server. */
    public static final class RpcException extends RuntimeException {
        private static final long serialVersionUID = 1L;
        public final int code;

        public RpcException(int code, String message) {
            super(message);
            this.code = code;
        }
    }

    private final DotCodeClientOptions options;
    private volatile Process process;
    private volatile OutputStream stdin;
    private CompletableFuture<Void> started;
    private final Object writeLock = new Object();
    private final AtomicLong nextId = new AtomicLong();
    private final Map<Long, CompletableFuture<Object>> pending = new ConcurrentHashMap<>();
    final Map<String, DotCodeSession> sessions = new ConcurrentHashMap<>();
    private final Map<String, List<SessionEvent>> early = new ConcurrentHashMap<>();
    private final AtomicInteger opening = new AtomicInteger();
    private final ExecutorService callbacks = Executors.newCachedThreadPool(r -> {
        Thread t = new Thread(r, "dotcode-callback");
        t.setDaemon(true);
        return t;
    });
    private volatile String serverVersion;

    public DotCodeClient() { this(new DotCodeClientOptions()); }

    public DotCodeClient(DotCodeClientOptions options) { this.options = options; }

    public String getServerVersion() { return serverVersion; }

    /** Starts the server and performs the protocol handshake (other methods call it lazily). */
    public synchronized CompletableFuture<Void> start() {
        if (started != null) return started;
        String cli = options.cliPath != null ? options.cliPath : System.getenv().getOrDefault("DOTCODE_CLI_PATH", "dotcode");
        List<String> cmd = new ArrayList<>();
        if (cli.toLowerCase().endsWith(".dll")) cmd.add("dotnet");
        cmd.add(cli);
        cmd.add("serve");
        cmd.addAll(options.cliArgs);
        ProcessBuilder pb = new ProcessBuilder(cmd).redirectError(ProcessBuilder.Redirect.DISCARD);
        if (options.cwd != null) pb.directory(new java.io.File(options.cwd));
        pb.environment().putAll(options.env);
        try {
            process = pb.start();
        } catch (IOException e) {
            return CompletableFuture.failedFuture(new IOException("Could not start '" + cli + "'. Install the DotCode CLI or set DOTCODE_CLI_PATH.", e));
        }
        stdin = process.getOutputStream();
        Thread reader = new Thread(this::readLoop, "dotcode-reader");
        reader.setDaemon(true);
        reader.start();
        started = callAsync("initialize", Wire.map(
                "protocolVersion", PROTOCOL_VERSION,
                "clientInfo", Wire.map("name", "dotcode-sdk-java", "version", SDK_VERSION),
                "capabilities", Wire.map("permissions", true, "questions", true)))
                .thenAccept(r -> {
                    if (r instanceof Map<?, ?> m && m.get("serverInfo") instanceof Map<?, ?> info) serverVersion = String.valueOf(info.get("version"));
                });
        return started;
    }

    /** Creates a session. Without an {@code onPermissionRequest} handler it is deny-by-default. */
    public CompletableFuture<DotCodeSession> createSession(SessionConfig config) {
        return open("session.create", config, Map.of());
    }

    /** Resumes a saved session. */
    public CompletableFuture<DotCodeSession> resumeSession(String sessionId, SessionConfig config) {
        return open("session.resume", config, Wire.map("sessionId", sessionId, "fork", false));
    }

    /** Continues a saved session under a new id, leaving the original transcript untouched. */
    public CompletableFuture<DotCodeSession> forkSession(String sessionId, SessionConfig config) {
        return open("session.resume", config, Wire.map("sessionId", sessionId, "fork", true));
    }

    @SuppressWarnings("unchecked")
    private CompletableFuture<DotCodeSession> open(String method, SessionConfig config, Map<String, Object> extra) {
        return start().thenCompose(v -> {
            Map<String, Object> params = config.toWire(options.cwd);
            params.putAll(extra);
            opening.incrementAndGet();
            return callAsync(method, params).whenComplete((r, e) -> {
                if (opening.decrementAndGet() == 0) early.clear();
            });
        }).thenApply(r -> {
            DotCodeSession s = new DotCodeSession(this, (Map<String, Object>) r, config);
            sessions.put(s.getSessionId(), s);
            List<SessionEvent> buffered = early.remove(s.getSessionId());
            if (buffered != null) buffered.forEach(s::dispatch);
            return s;
        });
    }

    /** Models offered by the configured providers. */
    @SuppressWarnings("unchecked")
    public CompletableFuture<List<ModelInfo>> listModels() {
        return start().thenCompose(v -> callAsync("models.list", Wire.map("cwd", options.cwd))).thenApply(r -> {
            List<ModelInfo> out = new ArrayList<>();
            for (Object o : Wire.list((Map<String, Object>) r, "models")) {
                Map<String, Object> m = (Map<String, Object>) o;
                out.add(new ModelInfo(Wire.str(m, "provider"), Wire.str(m, "id"), Wire.str(m, "qualifiedId")));
            }
            return out;
        });
    }

    /** Saved sessions for the working directory. */
    @SuppressWarnings("unchecked")
    public CompletableFuture<List<SessionMetadata>> listSessions() {
        return start().thenCompose(v -> callAsync("session.list", Wire.map("cwd", options.cwd))).thenApply(r -> {
            List<SessionMetadata> out = new ArrayList<>();
            for (Object o : Wire.list((Map<String, Object>) r, "sessions")) {
                Map<String, Object> m = (Map<String, Object>) o;
                out.add(new SessionMetadata(Wire.str(m, "id"), Wire.str(m, "title"), Wire.str(m, "firstPrompt"), Wire.str(m, "modified"),
                        (int) Wire.num(m, "messageCount")));
            }
            return out;
        });
    }

    public CompletableFuture<Void> ping() {
        return start().thenCompose(v -> callAsync("ping", Map.of())).thenAccept(r -> { });
    }

    // ------------------------------------------------------------------ JSON-RPC plumbing

    CompletableFuture<Object> callAsync(String method, Object params) {
        long id = nextId.incrementAndGet();
        CompletableFuture<Object> f = new CompletableFuture<>();
        pending.put(id, f);
        try {
            write(Wire.map("jsonrpc", "2.0", "id", id, "method", method, "params", params));
        } catch (RuntimeException e) {
            pending.remove(id);
            f.completeExceptionally(e);
        }
        return f;
    }

    private void write(Map<String, Object> msg) {
        byte[] bytes = (Json.write(msg) + "\n").getBytes(StandardCharsets.UTF_8);
        synchronized (writeLock) {
            if (stdin == null) throw new IllegalStateException("DotCode client not started");
            try {
                stdin.write(bytes);
                stdin.flush();
            } catch (IOException e) {
                throw new UncheckedIOException("DotCode server connection lost", e);
            }
        }
    }

    @SuppressWarnings("unchecked")
    private void readLoop() {
        try (BufferedReader r = new BufferedReader(new InputStreamReader(process.getInputStream(), StandardCharsets.UTF_8))) {
            String line;
            while ((line = r.readLine()) != null) {
                if (line.isBlank()) continue;
                Map<String, Object> msg;
                try { msg = Json.parseObject(line); } catch (RuntimeException e) { continue; }
                Object method = msg.get("method");
                Object id = msg.get("id");
                if (method == null && id instanceof Number n) {
                    CompletableFuture<Object> f = pending.remove(n.longValue());
                    if (f == null) continue;
                    if (msg.get("error") instanceof Map<?, ?> err)
                        f.completeExceptionally(new RpcException(((Number) err.get("code")).intValue(), String.valueOf(err.get("message"))));
                    else f.complete(msg.get("result"));
                    continue;
                }
                Map<String, Object> params = msg.get("params") instanceof Map<?, ?> p ? (Map<String, Object>) p : Map.of();
                if (id == null) {
                    if ("session.event".equals(method) && params.get("event") instanceof Map<?, ?> ev)
                        onEvent(Wire.str(params, "sessionId"), SessionEvent.fromWire((Map<String, Object>) ev));
                    continue;
                }
                callbacks.submit(() -> answer(id, String.valueOf(method), params));
            }
        } catch (IOException ignored) {
        } finally {
            for (CompletableFuture<Object> f : pending.values()) f.completeExceptionally(new RpcException(-32000, "server connection closed"));
            pending.clear();
        }
    }

    private void onEvent(String sessionId, SessionEvent e) {
        DotCodeSession s = sessionId == null ? null : sessions.get(sessionId);
        if (s != null) s.dispatch(e);
        else if (sessionId != null && opening.get() > 0) early.computeIfAbsent(sessionId, k -> new ArrayList<>()).add(e);
    }

    private void answer(Object id, String method, Map<String, Object> params) {
        DotCodeSession s = sessions.get(String.valueOf(params.get("sessionId")));
        try {
            if (s == null) throw new RpcException(-32001, "unknown session");
            write(Wire.map("jsonrpc", "2.0", "id", id, "result", s.handleServerRequest(method, params)));
        } catch (RpcException e) {
            write(Wire.map("jsonrpc", "2.0", "id", id, "error", Wire.map("code", e.code, "message", e.getMessage())));
        } catch (Exception e) {
            write(Wire.map("jsonrpc", "2.0", "id", id, "error", Wire.map("code", -32603, "message", String.valueOf(e.getMessage()))));
        }
    }

    // ------------------------------------------------------------------ lifecycle

    /** Stops the server gracefully. */
    public CompletableFuture<Void> stop() {
        return CompletableFuture.runAsync(this::close);
    }

    /** Kills the server without a graceful shutdown. */
    public void forceStop() {
        Process p = process;
        if (p != null) p.destroyForcibly();
        cleanup();
    }

    @Override
    public void close() {
        Process p = process;
        if (p == null) return;
        try { callAsync("shutdown", Map.of()).get(1500, TimeUnit.MILLISECONDS); } catch (Exception ignored) { }
        try { stdin.close(); } catch (IOException ignored) { }
        try {
            if (!p.waitFor(3, TimeUnit.SECONDS)) p.destroyForcibly();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            p.destroyForcibly();
        }
        cleanup();
    }

    private synchronized void cleanup() {
        process = null;
        synchronized (writeLock) { stdin = null; }
        started = null;
        sessions.clear();
        callbacks.shutdownNow();
    }

    static RuntimeException unwrap(Throwable t) {
        Throwable c = t instanceof CompletionException || t instanceof java.util.concurrent.ExecutionException ? t.getCause() : t;
        return c instanceof RuntimeException re ? re : new RuntimeException(c);
    }
}
