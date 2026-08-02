// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text.RegularExpressions;
using AutoCode.Cli;
using Xunit;

namespace AutoCode.Tests;

/// <summary>
/// Keeps the catalogue, the dispatcher and the documentation describing the same product.
///
/// EN: these drifted once already — the docs advertised nineteen commands while the catalogue had
/// thirty-eight, and nothing failed. A CLI that documents a command it does not have, or ships one
/// it never mentions, is lying to its users in a way no amount of care prevents by hand.
/// ID: ketiganya pernah menyimpang — dokumentasi menyebut sembilan belas perintah sementara
/// katalognya tiga puluh delapan, dan tidak ada yang gagal. Kesesuaian ini harus dijaga tes.
/// </summary>
public sealed class SlashCommandCatalogTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory.FullName;
    }

    [Fact]
    public void Every_command_has_a_name_and_a_summary()
    {
        foreach (var command in SlashCommandCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(command.Name), $"'{command.Usage}' has no name.");
            Assert.False(string.IsNullOrWhiteSpace(command.Summary), $"/{command.Name} has no summary.");
            Assert.DoesNotContain(' ', command.Name);
            Assert.StartsWith("/", command.Usage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Names_are_unique()
    {
        var duplicates = SlashCommandCatalog.All
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Every_command_belongs_to_a_declared_group()
    {
        foreach (var command in SlashCommandCatalog.All)
            Assert.Contains(command.Group, SlashCommandCatalog.Groups);
    }

    [Theory]
    [InlineData("docs/en/cli-reference.md")]
    [InlineData("docs/id/cli-reference.md")]
    public void Every_command_is_documented(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot(), relativePath);
        Assert.True(File.Exists(path), $"{relativePath} is missing.");

        var documented = Regex.Matches(File.ReadAllText(path), @"`/([a-z-]+)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = SlashCommandCatalog.All
            .Select(c => c.Name)
            .Where(name => !documented.Contains(name))
            .ToList();

        Assert.True(missing.Count == 0,
            $"{relativePath} does not mention: {string.Join(", ", missing.Select(m => "/" + m))}");
    }

    [Fact]
    public void Matching_narrows_as_the_prefix_grows()
    {
        var all = SlashCommandCatalog.Match("/", []);
        var narrow = SlashCommandCatalog.Match("/co", []);

        Assert.Equal(SlashCommandCatalog.All.Count, all.Count);
        Assert.True(narrow.Count < all.Count);
        Assert.All(narrow, c => Assert.StartsWith("co", c.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Skills_appear_alongside_built_in_commands()
    {
        var matches = SlashCommandCatalog.Match("/dep", ["deploy"]);

        Assert.Contains(matches, c => c.Name == "deploy");
    }

    [Fact]
    public void Tab_completion_fills_in_only_what_every_candidate_agrees_on()
    {
        // /code-review and /compact and /config and /context all start with "co", so Tab may only
        // advance that far — completing further would put text the user did not choose on the line.
        var matches = SlashCommandCatalog.Match("/co", []);

        Assert.Equal("co", SlashCommandCatalog.CommonPrefix(matches));
    }

    [Fact]
    public void A_single_match_completes_to_the_whole_name()
    {
        var matches = SlashCommandCatalog.Match("/secu", []);

        Assert.Single(matches);
        Assert.Equal("security-review", SlashCommandCatalog.CommonPrefix(matches));
    }

    [Fact]
    public void An_unmatched_prefix_returns_nothing_rather_than_everything()
    {
        Assert.Empty(SlashCommandCatalog.Match("/zzzz", []));
        Assert.Equal("", SlashCommandCatalog.CommonPrefix([]));
    }
}
