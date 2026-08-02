// Auto Code — Gravicode Studios (Kang Fadhil)

using AutoCode.Core.Utilities;
using Xunit;

namespace AutoCode.Tests;

public sealed class GlobTests
{
    [Theory]
    [InlineData("*.cs", "Program.cs", true)]
    [InlineData("*.cs", "src/Program.cs", false)]
    [InlineData("**/*.cs", "src/deep/Program.cs", true)]
    [InlineData("src/**/*.cs", "src/a/b/C.cs", true)]
    [InlineData("src/**/*.cs", "src/C.cs", true)]
    [InlineData("src/**/*.cs", "tests/C.cs", false)]
    [InlineData("src/*.cs", "src/a/C.cs", false)]
    [InlineData("*.{json,yml}", "app.yml", true)]
    [InlineData("*.{json,yml}", "app.toml", false)]
    [InlineData("file?.txt", "file1.txt", true)]
    [InlineData("file?.txt", "file12.txt", false)]
    public void PathMatching_follows_glob_semantics(string pattern, string path, bool expected) =>
        Assert.Equal(expected, Glob.IsPathMatch(pattern, path));

    [Fact]
    public void Separators_are_normalised_so_windows_paths_match_posix_patterns() =>
        Assert.True(Glob.IsPathMatch("src/**/*.cs", @"src\deep\Program.cs"));

    [Fact]
    public void Non_path_mode_treats_slashes_as_ordinary_characters() =>
        Assert.True(Glob.IsMatch("npm run *", "npm run build"));

    [Fact]
    public void Literal_dollar_sign_in_an_alternation_is_not_swallowed() =>
        Assert.True(Glob.IsMatch("{a$,b}", "a$"));
}
