using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DotCode.Abstractions;

namespace DotCode.Providers.Http;

/// <summary>Shared HTTP plumbing: one pooled handler for all providers, JSON body helpers, error mapping.</summary>
public static class ProviderHttp
{
    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            EnableMultipleHttp2Connections = true,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DotCode", "0.1.0"));
        return client;
    });

    public static HttpClient Client => SharedClient.Value;

    /// <summary>Test hook: replace the transport (mock servers in contract tests).</summary>
    public static HttpMessageHandler? OverrideHandler { get; set; }

    public static HttpClient GetClient() =>
        OverrideHandler is { } h ? new HttpClient(h, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan } : Client;

    public static ByteArrayContent JsonContent(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            write(w);
        var content = new ByteArrayContent(buffer.WrittenSpan.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    /// <summary>Sends and returns a response whose body has not been buffered; non-success is mapped to <see cref="ModelProviderException"/>.</summary>
    public static async Task<HttpResponseMessage> SendAsync(string providerId, HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await GetClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex)
        {
            throw new ModelProviderException(providerId, "network", $"Network error contacting {providerId}: {ex.Message}", true, inner: ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new ModelProviderException(providerId, "timeout", $"Request to {providerId} timed out", true, inner: ex);
        }

        if (response.IsSuccessStatusCode) return response;

        string body;
        try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { body = ""; }
        response.Dispose();
        throw MapError(providerId, (int)response.StatusCode, body, response.Headers.RetryAfter?.Delta);
    }

    public static ModelProviderException MapError(string providerId, int status, string body, TimeSpan? retryAfter = null)
    {
        var message = ExtractErrorMessage(body) ?? (body.Length > 500 ? body[..500] : body);
        var lower = message.ToLowerInvariant();
        var (code, retryable) = status switch
        {
            429 => ("rate_limit", true),
            529 => ("overloaded", true),
            408 => ("timeout", true),
            401 or 403 => ("auth", false),
            413 => ("context_length", false),
            >= 500 => ("server_error", true),
            400 when lower.Contains("context") && (lower.Contains("length") || lower.Contains("window") || lower.Contains("too long")) => ("context_length", false),
            400 when lower.Contains("prompt is too long") || lower.Contains("maximum context") || lower.Contains("too many tokens") => ("context_length", false),
            _ => ("invalid_request", false),
        };
        if (lower.Contains("overloaded")) (code, retryable) = ("overloaded", true);
        return new ModelProviderException(providerId, code, $"{providerId} API error {status}: {message}", retryable, status, retryAfter);
    }

    private static string? ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body[0] is not ('{' or '[')) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            if (root.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return err.GetString();
                if (err.TryGetProperty("message", out var m)) return m.GetString();
            }
            if (root.TryGetProperty("message", out var msg)) return msg.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>Reads a Server-Sent Events stream, yielding (event, data) pairs. Handles multi-line data and CRLF.</summary>
    public static async IAsyncEnumerable<SseEvent> ReadSseAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 16384);
        string? eventName = null;
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                if (data.Length > 0) yield return new SseEvent(eventName, data.ToString());
                yield break;
            }
            if (line.Length == 0)
            {
                if (data.Length > 0 || eventName is not null)
                {
                    yield return new SseEvent(eventName, data.ToString());
                    data.Clear();
                    eventName = null;
                }
                continue;
            }
            if (line[0] == ':') continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.Length > 0 && value[0] == ' ') value = value[1..];
            switch (field)
            {
                case "event": eventName = value; break;
                case "data":
                    if (data.Length > 0) data.Append('\n');
                    data.Append(value);
                    break;
            }
        }
    }

    /// <summary>Reads newline-delimited JSON (Ollama).</summary>
    public static async IAsyncEnumerable<string> ReadLinesAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 16384);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            if (line.Length > 0) yield return line;
    }

    public static string TrimSlash(string url) => url.TrimEnd('/');
}

public readonly record struct SseEvent(string? Event, string Data);
