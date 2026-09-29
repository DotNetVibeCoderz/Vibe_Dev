// DotCode Java SDK sample (single-file program, Java 17+):
//   javac -d out -cp ../../../sdk/java/target/classes Example.java && java -cp "out;../../../sdk/java/target/classes" Example azure:gpt-5-mini
import com.gravicode.dotcode.DotCodeClient;
import com.gravicode.dotcode.MessageOptions;
import com.gravicode.dotcode.PermissionDecision;
import com.gravicode.dotcode.SessionConfig;
import com.gravicode.dotcode.SessionEvent.AssistantTextDeltaEvent;
import com.gravicode.dotcode.SessionEvent.ToolCompletedEvent;
import com.gravicode.dotcode.SessionEvent.ToolStartedEvent;
import com.gravicode.dotcode.ToolDefinition;
import com.gravicode.dotcode.ToolParam;

public class Example {
    /** Tool parameters are a record: the schema is generated from it and a typo does not compile. */
    record ExchangeRateParams(@ToolParam("ISO currency code, e.g. USD") String from,
                              @ToolParam("ISO currency code, e.g. IDR") String to) {}

    static final ToolDefinition GET_EXCHANGE_RATE = ToolDefinition.from("get_exchange_rate",
            "Get the exchange rate between two currencies", ExchangeRateParams.class,
            p -> "1 " + p.from() + " = " + ("IDR".equals(p.to()) ? "16,250" : "0.92") + " " + p.to() + " (demo data)")
            .readOnly(true);

    public static void main(String[] args) throws Exception {
        try (var client = new DotCodeClient()) {
            client.start().get();
            try (var session = client.createSession(new SessionConfig()
                    .setModel(args.length > 0 ? args[0] : null)
                    .setPersistSession(false)
                    .addTool(GET_EXCHANGE_RATE)
                    .setOnPermissionRequest((request, invocation) -> {
                        System.out.println("  [permission] " + request.displayName() + " → allowed");
                        return PermissionDecision.approveOnce();
                    })).get()) {
                session.on(AssistantTextDeltaEvent.class, e -> System.out.print(e.text()));
                session.on(ToolStartedEvent.class, e -> System.out.println("● " + e.displayName()));
                session.on(ToolCompletedEvent.class, e -> System.out.println("  ⎿  " + e.output()));

                System.out.println("DotCode SDK (Java) · model " + session.getModel() + "\n");
                var result = session.sendAndWait(new MessageOptions(
                        "How many Indonesian Rupiah is 250 US dollars? Use the tool, then answer in one sentence.")).get();
                System.out.printf("%n%n✔ %d model calls · $%.4f · %d ms%n", result.numModelCalls(), result.costUsd(), result.durationMs());
            }
        }
    }
}
