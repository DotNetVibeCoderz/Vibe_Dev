using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;
using DotCode.Engine.Configuration;
using DotCode.Providers;
using DotCode.Providers.Http;

namespace DotCode.Engine.Observability;

/// <summary>DotCode instrumentation built on the standard .NET <see cref="ActivitySource"/> / <see cref="Meter"/>
/// APIs (source "DotCode"), so an embedding app can also attach the official OpenTelemetry SDK. The CLI ships a
/// small built-in OTLP/HTTP JSON exporter (<see cref="OtlpExporter"/>) enabled by settings or OTEL_* variables.</summary>
public static class Telemetry
{
    public const string SourceName = "DotCode";
    public static readonly ActivitySource Source = new(SourceName, AppInfo.Version);
    public static readonly Meter Meter = new(SourceName, AppInfo.Version);

    public static readonly Counter<long> Tokens = Meter.CreateCounter<long>("dotcode.tokens", "{token}", "Tokens consumed, by model and type");
    public static readonly Counter<double> Cost = Meter.CreateCounter<double>("dotcode.cost", "USD", "Estimated cost, by model");
    public static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>("dotcode.tool.calls", "{call}", "Tool calls, by tool and outcome");
    public static readonly Counter<long> Turns = Meter.CreateCounter<long>("dotcode.turns", "{turn}", "Completed user turns");
    public static readonly Histogram<double> LlmDuration = Meter.CreateHistogram<double>("dotcode.llm.duration", "ms", "Model call latency");
    public static readonly Histogram<double> ToolDuration = Meter.CreateHistogram<double>("dotcode.tool.duration", "ms", "Tool execution time");

    public static void RecordUsage(string model, string provider, Usage usage, decimal cost)
    {
        var m = new KeyValuePair<string, object?>("gen_ai.request.model", model);
        var p = new KeyValuePair<string, object?>("gen_ai.system", provider);
        if (usage.InputTokens > 0) Tokens.Add(usage.InputTokens, m, p, new("type", "input"));
        if (usage.OutputTokens > 0) Tokens.Add(usage.OutputTokens, m, p, new("type", "output"));
        if (usage.CacheReadTokens > 0) Tokens.Add(usage.CacheReadTokens, m, p, new("type", "cache_read"));
        if (usage.CacheWriteTokens > 0) Tokens.Add(usage.CacheWriteTokens, m, p, new("type", "cache_write"));
        if (cost > 0) Cost.Add((double)cost, m, p);
    }
}

/// <summary>Resolved exporter configuration: settings "otel" block, then standard OTEL_* environment variables.</summary>
public sealed record OtelConfig(string Endpoint, IReadOnlyDictionary<string, string> Headers, string ServiceName, bool LogPrompts)
{
    public static OtelConfig? Resolve(Settings settings)
    {
        var s = settings.Otel;
        var enabled = s?.Enabled ?? (settings.Telemetry == true || Environment.GetEnvironmentVariable("DOTCODE_ENABLE_TELEMETRY") == "1");
        if (!enabled) return null;
        var endpoint = ConfigValue.Expand(s?.Endpoint)
                       ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
                       ?? "http://localhost:4318";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS") is { Length: > 0 } envHeaders)
            foreach (var pair in envHeaders.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0) headers[pair[..eq].Trim()] = Uri.UnescapeDataString(pair[(eq + 1)..].Trim());
            }
        if (s?.Headers is not null)
            foreach (var (k, v) in s.Headers) headers[k] = ConfigValue.Expand(v) ?? "";
        var service = s?.ServiceName ?? Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "dotcode";
        return new OtelConfig(endpoint.TrimEnd('/'), headers, service, s?.LogPrompts == true);
    }
}

/// <summary>Minimal OTLP/HTTP JSON exporter for traces and metrics (no external packages, NativeAOT-safe).
/// Spans are batched and flushed every few seconds; metrics are exported as cumulative sums/histograms.</summary>
public sealed class OtlpExporter : IAsyncDisposable
{
    private static OtlpExporter? _instance;
    private static readonly Lock StartGate = new();

    private readonly OtelConfig _config;
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;
    private readonly ConcurrentQueue<Activity> _spans = new();
    private readonly ConcurrentDictionary<(string Name, string Attrs), MetricPoint> _metrics = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly long _startNanos = UnixNanos(DateTime.UtcNow);
    private readonly string _host = Environment.MachineName;

    private sealed class MetricPoint
    {
        public required string Name;
        public required string Unit;
        public required string Kind;   // sum | histogram
        public required KeyValuePair<string, object?>[] Tags;
        public double Sum;
        public long Count;
        public double Min = double.MaxValue, Max = double.MinValue;
        public bool IsDouble;
        public readonly Lock Gate = new();
    }

    /// <summary>Starts the process-wide exporter once (subsequent calls return the running instance).</summary>
    public static OtlpExporter? Start(OtelConfig? config)
    {
        if (config is null) return null;
        lock (StartGate) return _instance ??= new OtlpExporter(config);
    }

    public static Task FlushAsync() => _instance?.ExportAsync(CancellationToken.None) ?? Task.CompletedTask;

    private OtlpExporter(OtelConfig config)
    {
        _config = config;
        _activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == Telemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => _spans.Enqueue(a),
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == Telemetry.SourceName) listener.EnableMeasurementEvents(instrument);
            },
        };
        _meterListener.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(i, v, tags, false));
        _meterListener.SetMeasurementEventCallback<double>((i, v, tags, _) => Record(i, v, tags, true));
        _meterListener.Start();
        _loop = Task.Run(LoopAsync);
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, bool isDouble)
    {
        var tagArray = tags.ToArray();
        Array.Sort(tagArray, (a, b) => string.CompareOrdinal(a.Key, b.Key));
        var key = (instrument.Name, string.Join(';', tagArray.Select(t => $"{t.Key}={t.Value}")));
        var point = _metrics.GetOrAdd(key, _ => new MetricPoint
        {
            Name = instrument.Name,
            Unit = instrument.Unit ?? "",
            Kind = instrument is Histogram<double> ? "histogram" : "sum",
            Tags = tagArray,
            IsDouble = isDouble,
        });
        lock (point.Gate)
        {
            point.Sum += value;
            point.Count++;
            point.Min = Math.Min(point.Min, value);
            point.Max = Math.Max(point.Max, value);
        }
    }

    private async Task LoopAsync()
    {
        var sinceMetrics = 0;
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            await ExportSpansAsync(CancellationToken.None).ConfigureAwait(false);
            if (++sinceMetrics >= 6) { sinceMetrics = 0; await ExportMetricsAsync(CancellationToken.None).ConfigureAwait(false); }
        }
    }

    public async Task ExportAsync(CancellationToken ct)
    {
        await ExportSpansAsync(ct).ConfigureAwait(false);
        await ExportMetricsAsync(ct).ConfigureAwait(false);
    }

    private static long UnixNanos(DateTime utc) => (utc.Ticks - DateTime.UnixEpoch.Ticks) * 100;

    private void WriteResource(Utf8JsonWriter w)
    {
        w.WriteStartObject("resource");
        w.WriteStartArray("attributes");
        WriteAttr(w, "service.name", _config.ServiceName);
        WriteAttr(w, "service.version", AppInfo.Version);
        WriteAttr(w, "host.name", _host);
        WriteAttr(w, "os.type", OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "darwin" : "linux");
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteAttr(Utf8JsonWriter w, string key, object? value)
    {
        w.WriteStartObject();
        w.WriteString("key", key);
        w.WriteStartObject("value");
        switch (value)
        {
            case bool b: w.WriteBoolean("boolValue", b); break;
            case int or long or short: w.WriteString("intValue", Convert.ToInt64(value).ToString()); break;
            case double or float or decimal: w.WriteNumber("doubleValue", Convert.ToDouble(value)); break;
            default: w.WriteString("stringValue", value?.ToString() ?? ""); break;
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private async Task ExportSpansAsync(CancellationToken ct)
    {
        if (_spans.IsEmpty) return;
        var batch = new List<Activity>();
        while (batch.Count < 512 && _spans.TryDequeue(out var a)) batch.Add(a);
        var body = ProviderHttp.JsonContent(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("resourceSpans");
            w.WriteStartObject();
            WriteResource(w);
            w.WriteStartArray("scopeSpans");
            w.WriteStartObject();
            w.WriteStartObject("scope"); w.WriteString("name", Telemetry.SourceName); w.WriteString("version", AppInfo.Version); w.WriteEndObject();
            w.WriteStartArray("spans");
            foreach (var a in batch)
            {
                w.WriteStartObject();
                w.WriteString("traceId", a.TraceId.ToHexString());
                w.WriteString("spanId", a.SpanId.ToHexString());
                if (a.ParentSpanId != default) w.WriteString("parentSpanId", a.ParentSpanId.ToHexString());
                w.WriteString("name", a.DisplayName);
                w.WriteNumber("kind", a.Kind == ActivityKind.Client ? 3 : 1);
                var start = UnixNanos(a.StartTimeUtc);
                w.WriteString("startTimeUnixNano", start.ToString());
                w.WriteString("endTimeUnixNano", (start + a.Duration.Ticks * 100).ToString());
                w.WriteStartArray("attributes");
                foreach (var t in a.TagObjects) WriteAttr(w, t.Key, t.Value);
                w.WriteEndArray();
                w.WriteStartObject("status");
                w.WriteNumber("code", a.Status switch { ActivityStatusCode.Ok => 1, ActivityStatusCode.Error => 2, _ => 0 });
                if (a.StatusDescription is { } d) w.WriteString("message", d);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        });
        await PostAsync("/v1/traces", body, ct).ConfigureAwait(false);
    }

    private async Task ExportMetricsAsync(CancellationToken ct)
    {
        if (_metrics.IsEmpty) return;
        var now = UnixNanos(DateTime.UtcNow).ToString();
        var body = ProviderHttp.JsonContent(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("resourceMetrics");
            w.WriteStartObject();
            WriteResource(w);
            w.WriteStartArray("scopeMetrics");
            w.WriteStartObject();
            w.WriteStartObject("scope"); w.WriteString("name", Telemetry.SourceName); w.WriteEndObject();
            w.WriteStartArray("metrics");
            foreach (var group in _metrics.Values.GroupBy(p => p.Name))
            {
                var first = group.First();
                w.WriteStartObject();
                w.WriteString("name", first.Name);
                w.WriteString("unit", first.Unit);
                w.WriteStartObject(first.Kind);
                w.WriteNumber("aggregationTemporality", 2); // cumulative
                if (first.Kind == "sum") w.WriteBoolean("isMonotonic", true);
                w.WriteStartArray("dataPoints");
                foreach (var p in group)
                {
                    lock (p.Gate)
                    {
                        w.WriteStartObject();
                        w.WriteStartArray("attributes");
                        foreach (var t in p.Tags) WriteAttr(w, t.Key, t.Value);
                        w.WriteEndArray();
                        w.WriteString("startTimeUnixNano", _startNanos.ToString());
                        w.WriteString("timeUnixNano", now);
                        if (p.Kind == "sum")
                        {
                            if (p.IsDouble) w.WriteNumber("asDouble", p.Sum); else w.WriteString("asInt", ((long)p.Sum).ToString());
                        }
                        else
                        {
                            w.WriteString("count", p.Count.ToString());
                            w.WriteNumber("sum", p.Sum);
                            w.WriteNumber("min", p.Min);
                            w.WriteNumber("max", p.Max);
                        }
                        w.WriteEndObject();
                    }
                }
                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        });
        await PostAsync("/v1/metrics", body, ct).ConfigureAwait(false);
    }

    private async Task PostAsync(string path, HttpContent body, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _config.Endpoint + path) { Content = body };
            foreach (var (k, v) in _config.Headers) req.Headers.TryAddWithoutValidation(k, v);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await ProviderHttp.GetClient().SendAsync(req, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) { /* telemetry must never break the agent */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { await _loop.ConfigureAwait(false); } catch { }
        _meterListener.Dispose();
        _activityListener.Dispose();
        await ExportAsync(CancellationToken.None).ConfigureAwait(false);
        lock (StartGate) if (_instance == this) _instance = null;
    }

    public static ValueTask StopAsync() => _instance?.DisposeAsync() ?? ValueTask.CompletedTask;
}
