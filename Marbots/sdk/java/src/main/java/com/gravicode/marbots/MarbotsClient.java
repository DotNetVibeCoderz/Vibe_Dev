package com.gravicode.marbots;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.net.URI;
import java.net.URLEncoder;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.function.Consumer;

/**
 * Typed client for a Marbots server (multi-agent collaboration platform). No runtime dependencies.
 *
 * <pre>{@code
 * var mb = MarbotsClient.create("http://localhost:5170");
 * Bot sari = mb.bots().create(BotSpec.builder("Sari").role("UX designer")
 *     .kernelFunctions(KernelPack.FILES, KernelPack.WEB)
 *     .permissionProfile(PermissionProfile.WORKSPACE_WRITE)
 *     .model(ModelRef.of("azure", "gpt-5.6-luna")));
 * var thread = mb.threads().create(sari.id());
 * SendResult r = mb.threads().send(thread.id(), "Sketch a wireframe", true);
 * System.out.println(r.task().model() + ": " + r.text());
 * }</pre>
 *
 * Built by Gravicode Studios, led by Kang Fadhil.
 */
public final class MarbotsClient {
    private final String baseUrl;
    private final String apiKey;
    private final HttpClient http;
    private final Duration timeout;

    private final Bots bots = new Bots();
    private final Templates templates = new Templates();
    private final Models models = new Models();
    private final Threads threads = new Threads();
    private final Tasks tasks = new Tasks();
    private final Approvals approvals = new Approvals();
    private final Skills skills = new Skills();
    private final Mcp mcp = new Mcp();
    private final Schedules schedules = new Schedules();
    private final Memory memory = new Memory();
    private final Events events = new Events();

    private MarbotsClient(String baseUrl, String apiKey, Duration timeout) {
        this.baseUrl = baseUrl.endsWith("/") ? baseUrl.substring(0, baseUrl.length() - 1) : baseUrl;
        this.apiKey = apiKey;
        this.timeout = timeout;
        this.http = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(15)).build();
    }

    /** A client for {@code baseUrl} (e.g. {@code http://localhost:5170}). */
    public static MarbotsClient create(String baseUrl) { return new MarbotsClient(baseUrl, null, Duration.ofMinutes(30)); }

    /** A client that sends {@code X-Api-Key} (needed when the server sets Marbots:ApiKey). */
    public static MarbotsClient create(String baseUrl, String apiKey) { return new MarbotsClient(baseUrl, apiKey, Duration.ofMinutes(30)); }

    public String baseUrl() { return baseUrl; }
    public Bots bots() { return bots; }
    public Templates templates() { return templates; }
    public Models models() { return models; }
    public Threads threads() { return threads; }
    public Tasks tasks() { return tasks; }
    public Approvals approvals() { return approvals; }
    public Skills skills() { return skills; }
    public Mcp mcp() { return mcp; }
    public Schedules schedules() { return schedules; }
    public Memory memory() { return memory; }
    public Events events() { return events; }

    public SystemInfo system() { return SystemInfo.from(W.obj(getJson("/api/v1/system"))); }

    public List<HostInfo> hosts() { return list(getJson("/api/v1/hosts"), HostInfo::from); }

    /** Creates a thread with {@code bot}, sends {@code text}, waits, and returns the reply text. */
    public String chat(String bot, String text) {
        ChatThread t = threads.create(bot, null);
        return threads.send(t.id(), text, true).text();
    }

    // ---------------------------------------------------------------- transport

    private HttpRequest.Builder request(String path) {
        HttpRequest.Builder b = HttpRequest.newBuilder(URI.create(baseUrl + path)).timeout(timeout);
        if (apiKey != null && !apiKey.isEmpty()) b.header("X-Api-Key", apiKey);
        return b;
    }

    private byte[] send(HttpRequest req) {
        try {
            HttpResponse<byte[]> resp = http.send(req, HttpResponse.BodyHandlers.ofByteArray());
            if (resp.statusCode() >= 300) {
                String body = new String(resp.body(), StandardCharsets.UTF_8);
                String message = body;
                try {
                    Map<String, Object> pd = Json.parseObject(body);
                    if (pd.get("detail") instanceof String d) message = d;
                    else if (pd.get("title") instanceof String t) message = t;
                } catch (RuntimeException ignored) {
                    // not JSON
                }
                throw new MarbotsException(resp.statusCode(), message);
            }
            return resp.body();
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new MarbotsException(0, "interrupted");
        }
    }

    private Object parse(byte[] body) { return body.length == 0 ? null : Json.parse(new String(body, StandardCharsets.UTF_8)); }

    Object getJson(String path) { return parse(send(request(path).GET().build())); }

    Object call(String method, String path, Object body) {
        String json = body == null ? "" : Json.write(body);
        HttpRequest req = request(path).header("Content-Type", "application/json")
            .method(method, HttpRequest.BodyPublishers.ofString(json)).build();
        return parse(send(req));
    }

    byte[] raw(String path) { return send(request(path).GET().build()); }

    byte[] postBytes(String path, byte[] body, String contentType) {
        return send(request(path).header("Content-Type", contentType).POST(HttpRequest.BodyPublishers.ofByteArray(body)).build());
    }

    private static <T> List<T> list(Object v, java.util.function.Function<Map<String, Object>, T> f) {
        List<T> out = new ArrayList<>();
        for (Map<String, Object> m : W.objs(v)) out.add(f.apply(m));
        return List.copyOf(out);
    }

    private static String e(String s) { return URLEncoder.encode(s, StandardCharsets.UTF_8).replace("+", "%20"); }

    private static Map<String, Object> map(Object... kv) {
        Map<String, Object> m = new LinkedHashMap<>();
        for (int i = 0; i < kv.length; i += 2) m.put((String) kv[i], kv[i + 1]);
        return m;
    }

    // ---------------------------------------------------------------- APIs

    /** Bot management. */
    public final class Bots {
        private Bots() {}

        public List<Bot> list() { return MarbotsClient.list(getJson("/api/v1/bots"), Bot::from); }
        public Bot get(String idOrName) { return Bot.from(W.obj(getJson("/api/v1/bots/" + e(idOrName)))); }
        public Bot create(BotSpec spec) { return Bot.from(W.obj(call("POST", "/api/v1/bots", spec.toWire("")))); }
        public Bot update(String id, BotSpec spec) { return Bot.from(W.obj(call("PUT", "/api/v1/bots/" + e(id), spec.toWire(id)))); }
        public void delete(String id) { call("DELETE", "/api/v1/bots/" + e(id), null); }
        /** Creates a bot from a gallery template. */
        public Bot hire(String templateId, String name) { return Bot.from(W.obj(call("POST", "/api/v1/bots/from-template/" + e(templateId), map("name", name)))); }
        public BotModelInfo getModel(String id) { return BotModelInfo.from(W.obj(getJson("/api/v1/bots/" + e(id) + "/model"))); }
        /** Sets the bot's model: {@link ModelRef#DEFAULT}, {@link ModelRef#of(String, String)} or a profile name. */
        public BotModelInfo setModel(String id, String model) { return BotModelInfo.from(W.obj(call("PUT", "/api/v1/bots/" + e(id) + "/model", map("model", model)))); }
        public void pause(String id) { call("POST", "/api/v1/bots/" + e(id) + "/pause", null); }
        public void resume(String id) { call("POST", "/api/v1/bots/" + e(id) + "/resume", null); }
        /** Downloads a .marbot package (secrets are never included). */
        public byte[] export(String id, boolean includeMemory) { return raw("/api/v1/bots/" + e(id) + "/export?includeMemory=" + includeMemory); }
        public Bot importPackage(byte[] pkg) { return Bot.from(W.obj(parse(postBytes("/api/v1/bots/import", pkg, "application/zip")))); }
    }

    /** Template gallery. */
    public final class Templates {
        private Templates() {}

        public List<BotTemplate> list(String query, String category) {
            return MarbotsClient.list(getJson("/api/v1/templates?q=" + e(query == null ? "" : query) + "&category=" + e(category == null ? "" : category)), BotTemplate::from);
        }
        public BotTemplate get(String id) { return BotTemplate.from(W.obj(getJson("/api/v1/templates/" + e(id)))); }
    }

    /** Workspace default model and model choices. */
    public final class Models {
        private Models() {}

        public ModelCatalog list() { return ModelCatalog.from(W.obj(getJson("/api/v1/models"))); }
        /** Changes the model used by every bot whose model is {@link ModelRef#DEFAULT}; returns the new default. */
        public String setDefault(String model) { return W.str(W.obj(call("PUT", "/api/v1/models/default", map("model", model))), "default"); }
    }

    /** Conversations. */
    public final class Threads {
        private Threads() {}

        public List<ChatThread> list(String botId) {
            return MarbotsClient.list(getJson("/api/v1/threads" + (botId == null ? "" : "?botId=" + e(botId))), ChatThread::from);
        }
        public ChatThread create(String botId, String title) { return ChatThread.from(W.obj(call("POST", "/api/v1/threads", map("botId", botId, "title", title)))); }
        /** Sends a message; with {@code wait} the call returns after the bot finished (10 minute limit). */
        public SendResult send(String threadId, String text, boolean wait) { return send(threadId, text, wait, 600); }
        public SendResult send(String threadId, String text, boolean wait, int timeoutSeconds) {
            return SendResult.from(W.obj(call("POST", "/api/v1/threads/" + e(threadId) + "/messages", map("text", text, "wait", wait, "timeoutSeconds", timeoutSeconds))));
        }
        public List<ChatMessage> messages(String threadId) { return MarbotsClient.list(getJson("/api/v1/threads/" + e(threadId) + "/messages"), ChatMessage::from); }
        public List<WorkspaceFile> files(String threadId) { return MarbotsClient.list(getJson("/api/v1/threads/" + e(threadId) + "/files"), WorkspaceFile::from); }
        public byte[] download(String threadId, String path) { return raw("/api/v1/threads/" + e(threadId) + "/files/" + path); }
        public void delete(String threadId) { call("DELETE", "/api/v1/threads/" + e(threadId), null); }
    }

    /** Tasks. */
    public final class Tasks {
        private Tasks() {}

        public List<TaskRecord> list(String threadId) {
            return MarbotsClient.list(getJson("/api/v1/tasks" + (threadId == null ? "" : "?threadId=" + e(threadId))), TaskRecord::from);
        }
        public TaskRecord get(String id) { return TaskRecord.from(W.obj(getJson("/api/v1/tasks/" + e(id)))); }
        public void cancel(String id) { call("POST", "/api/v1/tasks/" + e(id) + "/cancel", null); }
    }

    /** Human-in-the-loop approvals. */
    public final class Approvals {
        private Approvals() {}

        public List<ApprovalRequest> pending() { return MarbotsClient.list(getJson("/api/v1/approvals?state=pending"), ApprovalRequest::from); }
        public ApprovalRequest approve(String id, ApprovalScope scope) {
            return ApprovalRequest.from(W.obj(call("POST", "/api/v1/approvals/" + e(id) + "/approve", map("scope", scope.wire()))));
        }
        public ApprovalRequest reject(String id) { return ApprovalRequest.from(W.obj(call("POST", "/api/v1/approvals/" + e(id) + "/reject", null))); }
        /** True when approvals are skipped (dangerous mode). */
        public boolean skipApprovals() { return W.bool(W.obj(getJson("/api/v1/system/approvals")), "dangerouslySkipApprovals"); }
        /**
         * Dangerous, like {@code --dangerously-skip-permissions}: every action that would ask runs without a human.
         * Turning it on also approves everything pending. Actions a bot's profile denies stay denied.
         */
        public boolean setSkipApprovals(boolean skip) {
            return W.bool(W.obj(call("PUT", "/api/v1/system/approvals", map("dangerouslySkipApprovals", skip))), "dangerouslySkipApprovals");
        }
    }

    /** SKILL.md packages. */
    public final class Skills {
        private Skills() {}

        public List<SkillInfo> list() { return MarbotsClient.list(getJson("/api/v1/skills"), SkillInfo::from); }
        public List<SkillInfo> install(String source) { return MarbotsClient.list(call("POST", "/api/v1/skills/install", map("source", source)), SkillInfo::from); }
    }

    /** MCP servers. */
    public final class Mcp {
        private Mcp() {}

        public List<McpServer> list() { return MarbotsClient.list(getJson("/api/v1/mcp"), McpServer::from); }
        public McpServer install(String id) { return McpServer.from(W.obj(call("POST", "/api/v1/mcp/" + e(id) + "/install", null))); }
    }

    /** Cron and one-off jobs. */
    public final class Schedules {
        private Schedules() {}

        public List<ScheduleJob> list() { return MarbotsClient.list(getJson("/api/v1/schedules"), ScheduleJob::from); }
        public ScheduleJob create(ScheduleSpec spec) { return ScheduleJob.from(W.obj(call("POST", "/api/v1/schedules", spec.toWire()))); }
        public void delete(String id) { call("DELETE", "/api/v1/schedules/" + e(id), null); }
    }

    /** Long-term memory. */
    public final class Memory {
        private Memory() {}

        public List<MemoryRecord> list(String owner) { return MarbotsClient.list(getJson("/api/v1/memory/" + e(owner)), MemoryRecord::from); }
        public MemoryRecord remember(String owner, String content, MemoryKind kind) {
            return MemoryRecord.from(W.obj(call("POST", "/api/v1/memory", map("owner", owner, "content", content, "kind", kind.wire(), "source", "sdk:java", "confidence", 1.0))));
        }
    }

    /** Live events (Server-Sent Events). */
    public final class Events {
        private Events() {}

        /**
         * Subscribes to the event stream (one thread, or everything when {@code threadId} is null). Events are delivered on
         * a background thread; close the returned subscription to stop.
         */
        public AutoCloseable subscribe(String threadId, Consumer<AgentEvent> handler) {
            String path = threadId == null ? "/api/v1/events" : "/api/v1/threads/" + e(threadId) + "/events";
            HttpRequest req = HttpRequest.newBuilder(URI.create(baseUrl + path)).header("Accept", "text/event-stream")
                .headers(apiKey == null || apiKey.isEmpty() ? new String[] {"X-Marbots-Sdk", "java"} : new String[] {"X-Api-Key", apiKey}).GET().build();
            var body = new java.util.concurrent.atomic.AtomicReference<java.util.stream.Stream<String>>();
            CompletableFuture<HttpResponse<java.util.stream.Stream<String>>> future = http.sendAsync(req, HttpResponse.BodyHandlers.ofLines());
            future.thenAcceptAsync(resp -> {
                try (var lines = resp.body()) {
                    body.set(lines);
                    lines.filter(l -> l.startsWith("data: ")).forEach(l -> handler.accept(AgentEvent.from(Json.parseObject(l.substring(6)))));
                } catch (UncheckedIOException ignored) {
                    // stream closed by the subscriber
                }
            });
            return () -> {
                future.cancel(true);
                var lines = body.get();
                if (lines != null) lines.close();
            };
        }
    }
}
