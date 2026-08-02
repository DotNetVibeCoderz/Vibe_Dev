// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Runtime.CompilerServices;

namespace AutoCode.Providers.Internal;

/// <summary>One decoded <c>text/event-stream</c> frame.</summary>
internal readonly record struct SseFrame(string EventName, string Data);

/// <summary>
/// Minimal SSE reader shared by the Anthropic and Gemini clients.
/// Allocation-light on purpose: the agent loop streams thousands of frames per session.
/// </summary>
internal static class ServerSentEvents
{
    public static async IAsyncEnumerable<SseFrame> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);

        string? eventName = null;
        var data = new System.Text.StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
                break;

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new SseFrame(eventName ?? "message", data.ToString());
                    data.Clear();
                }

                eventName = null;
                continue;
            }

            if (line.StartsWith(':'))
                continue; // comment / keep-alive

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..].TrimStart(' ');

            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "data":
                    if (data.Length > 0) data.Append('\n');
                    data.Append(value);
                    break;
            }
        }

        if (data.Length > 0)
            yield return new SseFrame(eventName ?? "message", data.ToString());
    }
}
