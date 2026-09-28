package com.gravicode.dotcode;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicLong;

/**
 * DotCode SDK for Java — embed the DotCode multi-LLM coding agent in JVM applications. Starts {@code dotcode serve}
 * and speaks JSON-RPC 2.0 over its stdio. Built by Gravicode Studios, led by Kang Fadhil.
 *
 * <pre>{@code
 * try (var client = DotCodeClient.start(new DotCodeClient.Options())) {
 *     var session = client.createSession(SessionOptions.builder().model("openai:gpt-5"));
 *     System.out.println(session.send("Summarize README.md").result());
 * }
 * }</pre>
 */
public final class DotCodeClient implements AutoCloseable {
    public static final String PROTOCOL_VERSION = "1.0";

    /** Client options. */
    public static final class Options {
        String cliPath;
        String cwd;
        final Map<String, String> env = new LinkedHashMap<>();
        final List<String> serverArgs = new ArrayList<>();

        /** dotcode executable or dotcode.dll (defaults to DOTCODE_CLI_PATH or "dotcode" on PATH). */
        public Options cliPath(String v) { cliPath = v; return this; }
        public Options cwd(String v) { cwd = v; return this; }
        public Options env(String key, String value) { env.put(key, value); return this; }
        public Options serverArg(String arg) { serverArgs.add(arg); return this; }
    }

    /** JSON-RPC error returned by the server. */
    public static final class RpcException extends RuntimeException {
        private static final long serialVersionUID = 1L;
        public final int code;
        public RpcException(int code, String message) { super(message); this.code = code; }
    }

    private final Process process;
    private final OutputStream stdin;
    private final Object writeLock = new Object();
    private final AtomicLong nextId = new AtomicLong();
    private final Map<Long, CompletableFuture<Object>> pending = new ConcurrentHashMap<>();
    final Map<String, Session> sessions = new ConcurrentHashMap<>();
    private final ExecutorService callbacks = Executors.newCachedThreadPool(r -> {
        Thread t = new Thread(r, "dotcode-callback");
        t.setDaemon(true);
        return t;
    });
    private final Options options;
    private String serverVersion;

    private DotCodeClient(Options options) throws IOException {
        this.options = options;
        String cli = options.cliPath != null ? options.cliPath : System.getenv().getOrDefault("DOTCODE_CLI_PATH", "dotcode");
        List<String> cmd = new ArrayList<>();
        if (cli.toLowerCase().endsWith(".dll")) cmd.add("dotnet");
        cmd.add(cli);
        cmd.add("serve");
        cmd.addAll(options.serverArgs);
        ProcessBuilder pb = new ProcessBuilder(cmd).redirectError(ProcessBuilder.Redirect.DISCARD);
        if (options.cwd != null) pb.directory(new java.io.File(options.cwd));
        pb.environment().putAll(options.env);
        try {
            process = pb.start();
        } catch (IOException e) {
            throw new IOException("Could not start '" + cli + "'. Install the DotCode CLI or set DOTCODE_CLI_PATH.", e);
        }
        stdin = process.getOutputStream();
        Thread reader = new Thread(this::readLoop, "dotcode-reader");
        reader.setDaemon(true);
        reader.start();
    }

    /** Starts the server and performs the protocol handshake. */
    @SuppressWarnings("unchecked")
    public static DotCodeClient start(Options options) throws IOException {
        DotCodeClient c = new DotCodeClient(options);
        Map<String, Object> caps = Map.of("permissions", true, "questions", true);
        Map<String, Object> init = (Map<String, Object>) c.call("initialize", Map.of(
                "protocolVersion", PROTOCOL_VERSION,
                "clientInfo", Map.of("name", "dotcode-sdk-java", "version", "0.1.0"),
                "capabilities", caps));
        Object info = init.get("serverInfo");
        if (info instanceof Map<?, ?> m) c.serverVersion = String.valueOf(m.get("version"));
        return c;
    }

    public String serverVersion() { return serverVersion; }

    /** Creates a session. */
    @SuppressWarnings("unchecked")
    public Session createSession(SessionOptions opts) {
        Map<String, Object> info = (Map<String, Object>) call("session.create", opts.toWire(options.cwd));
        Session s = new Session(this, info, opts);
        sessions.put(s.id(), s);
        return s;
    }

    /** Resumes a saved session ({@code fork} copies it under a new id). */
    @SuppressWarnings("unchecked")
    public Session resumeSession(String sessionId, SessionOptions opts, boolean fork) {
        Map<String, Object> params = opts.toWire(options.cwd);
        params.put("sessionId", sessionId);
        params.put("fork", fork);
        Map<String, Object> info = (Map<String, Object>) call("session.resume", params);
        Session s = new Session(this, info, opts);
        sessions.put(s.id(), s);
        return s;
    }

    @SuppressWarnings("unchecked")
    public Map<String, Object> listModels() { return (Map<String, Object>) call("models.list", mapOf("cwd", options.cwd)); }

    @SuppressWarnings("unchecked")
    public Map<String, Object> listSessions() { return (Map<String, Object>) call("session.list", mapOf("cwd", options.cwd)); }

    static Map<String, Object> mapOf(Object... kv) {
        Map<String, Object> m = new LinkedHashMap<>();
        for (int i = 0; i < kv.length; i += 2) m.put((String) kv[i], kv[i + 1]);
        return m;
    }

    Object call(String method, Object params) {
        try {
            return callAsync(method, params).get();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new RuntimeException(e);
        } catch (java.util.concurrent.ExecutionException e) {
            if (e.getCause() instanceof RuntimeException re) throw re;
            throw new RuntimeException(e.getCause());
        }
    }

    CompletableFuture<Object> callAsync(String method, Object params) {
        long id = nextId.incrementAndGet();
        CompletableFuture<Object> f = new CompletableFuture<>();
        pending.put(id, f);
        write(mapOf("jsonrpc", "2.0", "id", id, "method", method, "params", params));
        return f;
    }

    private void write(Map<String, Object> msg) {
        byte[] bytes = (Json.write(msg) + "\n").getBytes(StandardCharsets.UTF_8);
        synchronized (writeLock) {
            try {
                stdin.write(bytes);
                stdin.flush();
            } catch (IOException e) {
                throw new RuntimeException("DotCode server connection lost", e);
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
                    if ("session.event".equals(method)) {
                        Session s = sessions.get(String.valueOf(params.get("sessionId")));
                        if (s != null && params.get("event") instanceof Map<?, ?> ev) s.dispatch(new Types.Event((Map<String, Object>) ev));
                    }
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

    private void answer(Object id, String method, Map<String, Object> params) {
        Session s = sessions.get(String.valueOf(params.get("sessionId")));
        try {
            if (s == null) throw new RpcException(-32001, "unknown session");
            write(mapOf("jsonrpc", "2.0", "id", id, "result", s.handleServerRequest(method, params)));
        } catch (RpcException e) {
            write(mapOf("jsonrpc", "2.0", "id", id, "error", mapOf("code", e.code, "message", e.getMessage())));
        } catch (RuntimeException e) {
            write(mapOf("jsonrpc", "2.0", "id", id, "error", mapOf("code", -32603, "message", String.valueOf(e.getMessage()))));
        }
    }

    @Override
    public void close() {
        try { callAsync("shutdown", Map.of()).get(1500, TimeUnit.MILLISECONDS); } catch (Exception ignored) { }
        try { stdin.close(); } catch (IOException ignored) { }
        try {
            if (!process.waitFor(3, TimeUnit.SECONDS)) process.destroyForcibly();
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            process.destroyForcibly();
        }
        callbacks.shutdownNow();
    }
}
