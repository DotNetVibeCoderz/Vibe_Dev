package com.gravicode.dotcode;

import com.gravicode.dotcode.Types.Event;
import com.gravicode.dotcode.Types.PermissionDecision;
import com.gravicode.dotcode.Types.Tool;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/**
 * SDK conformance scenarios (same as the .NET, TypeScript, Python and Go SDKs) against a real {@code dotcode serve}
 * with the deterministic scripted provider. Dependency-free: run with
 * {@code java -cp target/classes:target/test-classes com.gravicode.dotcode.ConformanceTest}.
 */
public final class ConformanceTest {
    public static void main(String[] args) throws Exception {
        String cli = System.getenv().getOrDefault("DOTCODE_CLI_PATH",
                Path.of("../../src/DotCode.Cli/bin/Debug/net10.0/dotcode.dll").toAbsolutePath().normalize().toString());
        if (!Files.exists(Path.of(cli))) {
            System.out.println("SKIP: DotCode CLI not built");
            return;
        }
        customToolAndStreaming(cli);
        permissionHandler(cli);
        System.out.println("OK: 2 conformance tests passed");
    }

    static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }

    static Map<String, Object> scripted(Path dir, String script) throws Exception {
        Path p = dir.resolve("s.json");
        Files.writeString(p, script);
        return Map.of("providers", Map.of("mock", Map.of("type", "mock", "script", p.toString())));
    }

    static void customToolAndStreaming(String cli) throws Exception {
        Path dir = Files.createTempDirectory("dc-java-");
        try (var client = DotCodeClient.start(new DotCodeClient.Options().cliPath(cli).cwd(dir.toString()).env("DOTCODE_CONFIG_DIR", dir.resolve(".cfg").toString()))) {
            var session = client.createSession(SessionOptions.builder()
                    .model("mock:scripted")
                    .settings(scripted(dir, "{\"responses\":[{\"text\":\"Checking the weather.\",\"toolCalls\":[{\"name\":\"get_weather\",\"input\":{\"city\":\"Bogor\"}}]},{\"text\":\"It is rainy in Bogor.\"}]}"))
                    .persistSession(false).noMcp(true)
                    .tool(new Tool("get_weather", "Weather for a city",
                            Map.of("type", "object", "properties", Map.of("city", Map.of("type", "string")), "required", List.of("city")),
                            true, in -> in.get("city") + ": rainy, 24°C")));
            List<Event> events = new ArrayList<>();
            var result = session.stream("weather?", events::add);
            check("It is rainy in Bogor.".equals(result.result()), "result: " + result.result());
            check(events.stream().anyMatch(e -> "tool.completed".equals(e.type()) && "get_weather".equals(e.name()) && e.output().contains("rainy")), "tool.completed missing");
            check(events.stream().anyMatch(Event::isTurnCompleted), "turn.completed missing");
            check(session.messages().size() == 4, "messages: " + session.messages().size());
        }
    }

    static void permissionHandler(String cli) throws Exception {
        Path dir = Files.createTempDirectory("dc-java-");
        Path target = dir.resolve("out.txt");
        List<String> asked = new ArrayList<>();
        try (var client = DotCodeClient.start(new DotCodeClient.Options().cliPath(cli).cwd(dir.toString()).env("DOTCODE_CONFIG_DIR", dir.resolve(".cfg").toString()))) {
            var session = client.createSession(SessionOptions.builder()
                    .model("mock:scripted")
                    .settings(scripted(dir, "{\"responses\":[{\"toolCalls\":[{\"name\":\"Write\",\"input\":{\"file_path\":" + Json.write(target.toString()) + ",\"content\":\"from java\"}}]},{\"text\":\"written\"}]}"))
                    .persistSession(false).noMcp(true)
                    .onPermissionRequest(r -> { asked.add(r.toolName()); return PermissionDecision.allow(); }));
            var result = session.send("write a file");
            check("written".equals(result.result()), "result: " + result.result());
            check(asked.equals(List.of("Write")), "asked: " + asked);
            check("from java".equals(Files.readString(target)), "file content");
        }
    }
}
