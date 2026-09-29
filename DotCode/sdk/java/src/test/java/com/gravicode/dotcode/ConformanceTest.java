package com.gravicode.dotcode;

import com.gravicode.dotcode.SessionEvent.ToolCompletedEvent;
import com.gravicode.dotcode.SessionEvent.TurnCompletedEvent;

import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

/**
 * SDK conformance scenarios (same as the .NET, TypeScript, Python, Go and Rust SDKs) against a real
 * {@code dotcode serve} with the deterministic scripted provider. Dependency-free: run with
 * {@code java -cp target/classes:target/test-classes com.gravicode.dotcode.ConformanceTest}.
 */
public final class ConformanceTest {
    record WeatherParams(@ToolParam("City name") String city) {}

    enum Unit { CELSIUS, FAHRENHEIT }

    record SchemaParams(@ToolParam("City name") String city, @ToolParam(value = "Unit", required = false) Unit unit,
                        int days, @ToolParam(required = false) List<String> tags) {}

    static final ToolDefinition GET_WEATHER = ToolDefinition.fromWithToolInvocation("get_weather", "Weather for a city",
            WeatherParams.class, (p, inv) -> p.city() + ": rainy, 24°C (" + inv.toolName() + ")").readOnly(true);

    public static void main(String[] args) throws Exception {
        schemaGeneration();
        String cli = System.getenv().getOrDefault("DOTCODE_CLI_PATH",
                Path.of("../../src/DotCode.Cli/bin/Debug/net10.0/dotcode.dll").toAbsolutePath().normalize().toString());
        if (!Files.exists(Path.of(cli))) {
            System.out.println("SKIP: DotCode CLI not built (schema test passed)");
            return;
        }
        customToolAndEvents(cli);
        permissionHandler(cli);
        invalidArguments(cli);
        System.out.println("OK: 4 conformance tests passed");
    }

    static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }

    static void schemaGeneration() {
        ToolDefinition t = ToolDefinition.from("s", "schema", SchemaParams.class, p -> p.city());
        String schema = Json.write(t.getParameters());
        String want = "{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\",\"description\":\"City name\"},"
                + "\"unit\":{\"type\":\"string\",\"enum\":[\"CELSIUS\",\"FAHRENHEIT\"],\"description\":\"Unit\"},"
                + "\"days\":{\"type\":\"integer\"},\"tags\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}},"
                + "\"required\":[\"city\",\"days\"],\"additionalProperties\":false}";
        check(want.equals(schema), "schema: " + schema);
        Map<String, Object> ok = t.invoke(Map.of("city", "Bogor", "unit", "CELSIUS", "days", 2L), null);
        check("Bogor".equals(ok.get("content")), "bind: " + ok);
        Map<String, Object> missing = t.invoke(Map.of("unit", "CELSIUS", "days", 1L), null);
        check(Boolean.TRUE.equals(missing.get("isError")) && String.valueOf(missing.get("content")).contains("city: is required"), "missing: " + missing);
        ToolDefinition two = ToolDefinition.from("add", "Adds", Param.of(Integer.class, "a", "First"), Param.of(Integer.class, "b", "Second"), Integer::sum);
        check("3".equals(two.invoke(Map.of("a", 1L, "b", 2L), null).get("content")), "params");
    }

    static Path script(Path dir, String responses) throws Exception {
        Path p = dir.resolve("s.json");
        Files.writeString(p, "{\"responses\":" + responses + "}");
        return p;
    }

    static DotCodeClient client(Path dir) {
        return new DotCodeClient(new DotCodeClientOptions()
                .setCliPath(System.getenv().getOrDefault("DOTCODE_CLI_PATH",
                        Path.of("../../src/DotCode.Cli/bin/Debug/net10.0/dotcode.dll").toAbsolutePath().normalize().toString()))
                .setCwd(dir.toString())
                .putEnv("DOTCODE_CONFIG_DIR", dir.resolve(".cfg").toString()));
    }

    static SessionConfig base(Path script) {
        return new SessionConfig()
                .setModel("mock:scripted")
                .putProvider("mock", new ProviderConfig(ProviderType.MOCK).setScript(script.toString()))
                .setPersistSession(false)
                .setDisableMcp(true);
    }

    static void customToolAndEvents(String cli) throws Exception {
        Path dir = Files.createTempDirectory("dc-java-");
        Path s = script(dir, "[{\"text\":\"Checking the weather.\",\"toolCalls\":[{\"name\":\"get_weather\",\"input\":{\"city\":\"Bogor\"}}]},{\"text\":\"It is rainy in Bogor.\"}]");
        try (var client = client(dir)) {
            client.start().get(30, TimeUnit.SECONDS);
            try (var session = client.createSession(base(s).addTool(GET_WEATHER)).get(30, TimeUnit.SECONDS)) {
                List<ToolCompletedEvent> completed = new ArrayList<>();
                List<SessionEvent> all = new ArrayList<>();
                session.on(ToolCompletedEvent.class, completed::add);
                session.on(all::add);
                SendResult result = session.sendAndWait(new MessageOptions("weather?"), Duration.ofSeconds(60)).get();
                check("It is rainy in Bogor.".equals(result.result()), "result: " + result.result());
                check(result.stopReason() == StopReason.END_TURN, "stop reason: " + result.stopReason());
                check(completed.size() == 1 && "get_weather".equals(completed.get(0).name())
                        && completed.get(0).output().contains("rainy, 24°C (get_weather)"), "tool.completed: " + completed);
                check(all.stream().anyMatch(e -> e instanceof TurnCompletedEvent t && t.parentToolUseId() == null), "turn.completed missing");
                check(session.getMessages().get().size() == 4, "messages");
            }
        }
    }

    static void permissionHandler(String cli) throws Exception {
        Path dir = Files.createTempDirectory("dc-java-");
        Path target = dir.resolve("out.txt");
        Path s = script(dir, "[{\"toolCalls\":[{\"name\":\"Write\",\"input\":{\"file_path\":" + Json.write(target.toString())
                + ",\"content\":\"from java\"}}]},{\"text\":\"written\"}]");
        List<String> asked = new ArrayList<>();
        try (var client = client(dir)) {
            var session = client.createSession(base(s).setOnPermissionRequest((req, inv) -> {
                asked.add(req.toolName());
                return PermissionDecision.approveOnce();
            })).get(30, TimeUnit.SECONDS);
            CompletableFuture<TurnCompletedEvent> done = new CompletableFuture<>();
            session.on(TurnCompletedEvent.class, done::complete);
            session.send(new MessageOptions("write a file")).get();
            TurnCompletedEvent turn = done.get(60, TimeUnit.SECONDS);
            check("written".equals(turn.resultText()), "result: " + turn.resultText());
            check(asked.equals(List.of("Write")), "asked: " + asked);
            check("from java".equals(Files.readString(target)), "file content");
        }
    }

    static void invalidArguments(String cli) throws Exception {
        Path dir = Files.createTempDirectory("dc-java-");
        Path s = script(dir, "[{\"toolCalls\":[{\"name\":\"get_weather\",\"input\":{\"city\":42}}]},{\"text\":\"done\"}]");
        try (var client = client(dir)) {
            var session = client.createSession(base(s).addTool(GET_WEATHER)).get(30, TimeUnit.SECONDS);
            List<ToolCompletedEvent> completed = new ArrayList<>();
            session.on(ToolCompletedEvent.class, completed::add);
            SendResult result = session.sendAndWait(new MessageOptions("weather?")).get(60, TimeUnit.SECONDS);
            check("done".equals(result.result()), "result: " + result.result());
            check(completed.get(0).isError() && completed.get(0).output().contains("city"), "error: " + completed);
        }
    }
}
