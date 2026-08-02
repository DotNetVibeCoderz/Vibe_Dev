// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Configuration;
using Microsoft.Extensions.AI;

namespace AutoCode.Core.Cost;

/// <summary>Token and money accounting for a session.</summary>
public sealed class CostTracker(ProviderProfile profile)
{
    private readonly Lock _gate = new();

    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long CachedInputTokens { get; private set; }
    public int RequestCount { get; private set; }

    /// <summary>Tokens on the wire for the most recent request; drives the compaction trigger.</summary>
    public long LastContextTokens { get; private set; }

    /// <summary>Running estimate in USD. Zero when the profile carries no pricing.</summary>
    public decimal TotalCostUsd
    {
        get
        {
            lock (_gate)
            {
                var billableInput = Math.Max(0, InputTokens - CachedInputTokens);
                return billableInput / 1_000_000m * profile.InputCostPerMillionTokens
                     + OutputTokens / 1_000_000m * profile.OutputCostPerMillionTokens;
            }
        }
    }

    /// <summary>How full the context window is, as a fraction between 0 and 1.</summary>
    public double ContextUtilization =>
        profile.ContextWindow <= 0 ? 0 : Math.Clamp((double)LastContextTokens / profile.ContextWindow, 0, 1);

    public void Record(UsageDetails? usage)
    {
        if (usage is null)
            return;

        lock (_gate)
        {
            var input = usage.InputTokenCount ?? 0;
            var output = usage.OutputTokenCount ?? 0;

            InputTokens += input;
            OutputTokens += output;
            RequestCount++;
            LastContextTokens = input + output;

            if (usage.AdditionalCounts?.TryGetValue("cache_read", out var cached) == true)
                CachedInputTokens += cached;
        }
    }

    /// <summary>Snapshot of the accounting for one turn, taken by diffing against a saved baseline.</summary>
    public CostSnapshot Snapshot()
    {
        lock (_gate)
            return new CostSnapshot(InputTokens, OutputTokens, TotalCostUsd, RequestCount);
    }

    public string Format()
    {
        lock (_gate)
        {
            var tokens = $"{FormatTokens(InputTokens)} in / {FormatTokens(OutputTokens)} out";
            return profile.InputCostPerMillionTokens > 0 || profile.OutputCostPerMillionTokens > 0
                ? $"{tokens} · ${TotalCostUsd:F4}"
                : tokens;
        }
    }

    private static string FormatTokens(long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000.0:F1}M",
        >= 1_000 => $"{value / 1_000.0:F1}k",
        _ => value.ToString(),
    };
}

public readonly record struct CostSnapshot(long InputTokens, long OutputTokens, decimal CostUsd, int Requests)
{
    public CostSnapshot Since(CostSnapshot baseline) => new(
        InputTokens - baseline.InputTokens,
        OutputTokens - baseline.OutputTokens,
        CostUsd - baseline.CostUsd,
        Requests - baseline.Requests);
}
