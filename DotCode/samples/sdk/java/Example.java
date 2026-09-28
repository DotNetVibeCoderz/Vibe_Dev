// DotCode Java SDK sample (single-file program, Java 17+):
//   javac -d out -cp ../../../sdk/java/target/classes Example.java && java -cp "out;../../../sdk/java/target/classes" Example azure:gpt-5-mini
import com.gravicode.dotcode.DotCodeClient;
import com.gravicode.dotcode.SessionOptions;
import com.gravicode.dotcode.Types.PermissionDecision;
import com.gravicode.dotcode.Types.Tool;

import java.util.List;
import java.util.Map;

public class Example {
    public static void main(String[] args) throws Exception {
        try (var client = DotCodeClient.start(new DotCodeClient.Options())) {
            var session = client.createSession(SessionOptions.builder()
                    .model(args.length > 0 ? args[0] : null)
                    .persistSession(false)
                    .tool(new Tool("get_exchange_rate", "Get the exchange rate between two currencies",
                            Map.of("type", "object", "properties", Map.of("from", Map.of("type", "string"), "to", Map.of("type", "string")), "required", List.of("from", "to")),
                            true, in -> "1 " + in.get("from") + " = " + ("IDR".equals(in.get("to")) ? "16,250" : "0.92") + " " + in.get("to") + " (demo data)"))
                    .onPermissionRequest(r -> PermissionDecision.allow()));
            System.out.println("DotCode SDK (Java) · model " + session.model() + "\n");
            var result = session.stream("How many Indonesian Rupiah is 250 US dollars? Use the tool, then answer in one sentence.", e -> {
                switch (e.type()) {
                    case "assistant.text.delta" -> System.out.print(e.text());
                    case "tool.started" -> System.out.println("● " + e.displayName());
                    case "tool.completed" -> System.out.println("  ⎿  " + e.output());
                    default -> { }
                }
            });
            System.out.printf("%n%n✔ %d model calls · $%.4f · %d ms%n", result.numModelCalls(), result.costUsd(), result.durationMs());
        }
    }
}
