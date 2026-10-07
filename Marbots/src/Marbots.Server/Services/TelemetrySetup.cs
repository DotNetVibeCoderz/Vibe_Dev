using Marbots.Runtime;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Marbots.Server.Services;

/// <summary>Exports Marbots spans and metrics (plus ASP.NET Core / HttpClient instrumentation and logs) over OTLP.</summary>
public static class TelemetrySetup
{
    /// <summary>True when an OTLP endpoint is configured (Marbots:Telemetry:OtlpEndpoint or OTEL_EXPORTER_OTLP_ENDPOINT).</summary>
    public static bool Enabled(MarbotsTelemetryOptions t) =>
        !string.IsNullOrWhiteSpace(t.OtlpEndpoint) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));

    public static WebApplicationBuilder AddMarbotsTelemetry(this WebApplicationBuilder builder, MarbotsOptions options)
    {
        var t = options.Telemetry;
        if (!Enabled(t)) return builder;

        void Exporter(OtlpExporterOptions o)
        {
            if (!string.IsNullOrWhiteSpace(t.OtlpEndpoint)) o.Endpoint = new Uri(t.OtlpEndpoint);
            o.Protocol = t.Protocol.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase) || t.Protocol.Equals("http", StringComparison.OrdinalIgnoreCase)
                ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
            if (t.HeadersSecret is { Length: > 0 } name &&
                (builder.Configuration[$"Marbots:Secrets:{name}"] ?? Environment.GetEnvironmentVariable(name)) is { Length: > 0 } headers)
                o.Headers = headers;
        }

        // With http/protobuf each signal has its own path; a bare endpoint gets /v1/traces etc. appended per signal.
        void ForSignal(OtlpExporterOptions o, string path)
        {
            Exporter(o);
            if (o.Protocol == OtlpExportProtocol.HttpProtobuf && !string.IsNullOrWhiteSpace(t.OtlpEndpoint) && new Uri(t.OtlpEndpoint).AbsolutePath is "/" or "")
                o.Endpoint = new Uri(new Uri(t.OtlpEndpoint), path);
        }

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(t.ServiceName, serviceVersion: typeof(MarbotsTelemetry).Assembly.GetName().Version?.ToString(3))
                .AddAttributes([new("marbots.multi_tenant", options.MultiTenant), new("deployment.environment.name", builder.Environment.EnvironmentName)]));
        if (t.Traces)
            otel.WithTracing(tr => tr
                .AddSource(MarbotsTelemetry.Name)
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/_blazor") && !ctx.Request.Path.StartsWithSegments("/_framework"))
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(o => ForSignal(o, "v1/traces")));
        if (t.Metrics)
            otel.WithMetrics(m => m
                .AddMeter(MarbotsTelemetry.Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(o => ForSignal(o, "v1/metrics")));
        if (t.Logs)
            builder.Logging.AddOpenTelemetry(l =>
            {
                l.IncludeScopes = true;
                l.IncludeFormattedMessage = true;
                l.AddOtlpExporter(o => ForSignal(o, "v1/logs"));
            });
        return builder;
    }
}
