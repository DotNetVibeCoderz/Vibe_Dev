// Auto Code — Gravicode Studios (Kang Fadhil)

using System.Text.Json;
using System.Text.Json.Nodes;
using AutoCode.Core.Configuration;
using AutoCode.Studio.Services;
using Xunit;

namespace AutoCode.Tests;

/// <summary>
/// Covers the promise Studio has to keep: editing providers must never disturb anything else in a
/// settings file. A GUI that eats your hooks the first time you change a model is worse than no GUI.
/// </summary>
public sealed class SettingsStoreTests
{
    private const string RichSettings =
        """
        {
          "activeProvider": "deepseek",
          "providers": {
            "deepseek": {
              "kind": "OpenAICompatible",
              "endpoint": "https://api.deepseek.com",
              "model": "deepseek-v4-flash",
              "apiKey": "env:DEEPSEEK_API_KEY",
              "contextWindow": 128000
            },
            "ollama": { "model": "qwen2.5-coder:14b" }
          },
          "permissionMode": "AcceptEdits",
          "permissions": { "deny": ["Bash(rm -rf:*)"] },
          "verifyCommands": ["dotnet build", "dotnet test"],
          "hooks": { "PostToolUse": [ { "matcher": "Edit", "command": "dotnet format" } ] },
          "mcpServers": { "github": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-github"] } }
        }
        """;

    [Fact]
    public void A_kind_written_as_a_string_is_understood()
    {
        // System.Text.Json will not map "OpenAICompatible" onto the enum without a converter, while
        // the CLI's configuration binder does. Studio has to read what the CLI writes.
        using var workspace = new TempWorkspace();
        var path = workspace.Write(".autocode/settings.json", RichSettings);

        var (active, providers) = new SettingsStore().Load(path);

        Assert.Equal("deepseek", active);
        Assert.Equal(2, providers.Count);
        Assert.Equal(ProviderKind.OpenAICompatible, providers["deepseek"].Kind);
        Assert.Equal("deepseek-v4-flash", providers["deepseek"].Model);
    }

    [Fact]
    public void A_preset_fills_in_what_the_file_leaves_out()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.Write(".autocode/settings.json", RichSettings);

        var (_, providers) = new SettingsStore().Load(path);

        // The file gives ollama only a model; the endpoint comes from the vendor preset.
        Assert.Equal("http://localhost:11434/v1", providers["ollama"].Endpoint);
    }

    [Fact]
    public void Saving_providers_leaves_every_other_setting_untouched()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.Write(".autocode/settings.json", RichSettings);

        var store = new SettingsStore();
        var (_, providers) = store.Load(path);

        providers["deepseek"].Model = "deepseek-reasoner";
        store.Save(path, "deepseek", providers.Values);

        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        Assert.Equal("deepseek-reasoner", saved["providers"]!["deepseek"]!["model"]!.GetValue<string>());
        Assert.Equal("AcceptEdits", saved["permissionMode"]!.GetValue<string>());
        Assert.Equal("Bash(rm -rf:*)", saved["permissions"]!["deny"]![0]!.GetValue<string>());
        Assert.Equal(2, saved["verifyCommands"]!.AsArray().Count);
        Assert.Equal("dotnet format", saved["hooks"]!["PostToolUse"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("npx", saved["mcpServers"]!["github"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void A_profile_studio_cannot_parse_survives_a_save()
    {
        // Load, fail to understand, save — the profile must still be there. This is the exact
        // sequence that would otherwise delete a user's configuration without a word.
        using var workspace = new TempWorkspace();
        var path = workspace.Write(".autocode/settings.json",
            """
            {
              "providers": {
                "good": { "model": "gpt-4.1" },
                "strange": { "model": ["not", "a", "string"] }
              }
            }
            """);

        var store = new SettingsStore();
        var (_, providers) = store.Load(path);

        Assert.Single(providers);
        Assert.Contains("strange", store.UnparsedProfiles);

        store.Save(path, "good", providers.Values);

        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        Assert.NotNull(saved["providers"]!["good"]);
        Assert.NotNull(saved["providers"]!["strange"]);
    }

    [Fact]
    public void Settings_nested_under_an_AutoCode_section_are_read_and_written_in_place()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.Write("appsettings.json",
            """{ "Logging": { "LogLevel": "Debug" }, "AutoCode": { "providers": { "openai": { "model": "gpt-4.1" } } } }""");

        var store = new SettingsStore();
        var (_, providers) = store.Load(path);

        Assert.Single(providers);

        store.Save(path, "openai", providers.Values);

        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        Assert.NotNull(saved["Logging"]);
        Assert.NotNull(saved["AutoCode"]!["providers"]!["openai"]);
        Assert.Null(saved["providers"]);
    }

    [Fact]
    public void A_saved_file_is_readable_by_the_configuration_loader()
    {
        // The round trip that actually matters: whatever Studio writes, the CLI must load.
        using var workspace = new TempWorkspace();
        var path = workspace.Write(".autocode/settings.json", RichSettings);

        var store = new SettingsStore();
        var (_, providers) = store.Load(path);

        providers["deepseek"].Model = "deepseek-reasoner";
        store.Save(path, "deepseek", providers.Values);

        var options = ConfigurationLoader.Load(workspace.Root);
        var profile = options.ResolveActiveProfile();

        Assert.NotNull(profile);
        Assert.Equal("deepseek", profile.Name);
        Assert.Equal("deepseek-reasoner", profile.Model);
        Assert.Equal(ProviderKind.OpenAICompatible, profile.Kind);
        Assert.Equal(Core.Permissions.PermissionMode.AcceptEdits, options.PermissionMode);
    }

    [Fact]
    public void Environment_exports_resolve_the_key_rather_than_emitting_the_indirection()
    {
        Environment.SetEnvironmentVariable("AUTOCODE_STORE_TEST_KEY", "sk-resolved");

        try
        {
            var profile = new ProviderProfile
            {
                Name = "deepseek",
                Model = "deepseek-chat",
                ApiKey = "env:AUTOCODE_STORE_TEST_KEY",
                Endpoint = "https://api.deepseek.com",
            };

            var script = SettingsStore.ToEnvironmentScript([profile], powershell: false);

            // "env:NAME" inside an environment variable would just be a literal string the loader
            // never expands twice, so the resolved value is what has to be emitted.
            Assert.Contains("AUTOCODE_PROVIDERS__DEEPSEEK__APIKEY=\"sk-resolved\"", script);
            Assert.DoesNotContain("env:AUTOCODE_STORE_TEST_KEY", script);
            Assert.Contains("export ", script);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AUTOCODE_STORE_TEST_KEY", null);
        }
    }

    [Fact]
    public void Powershell_exports_use_the_dollar_env_form()
    {
        var profile = new ProviderProfile { Name = "openai", Model = "gpt-4.1", ApiKey = "sk-literal" };

        var script = SettingsStore.ToEnvironmentScript([profile], powershell: true);

        Assert.Contains("$env:AUTOCODE_PROVIDERS__OPENAI__MODEL = \"gpt-4.1\"", script);
        Assert.DoesNotContain("export ", script);
    }

    [Fact]
    public void A_context_window_matching_the_preset_is_still_written_as_a_real_number()
    {
        // Trimming numerics to a sentinel zero would write contextWindow: 0, which reads back as a
        // real setting and disables compaction.
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "settings.json");

        var store = new SettingsStore();
        var profile = ProviderPresets.TryCreate("openai")!;

        store.Save(path, "openai", [profile]);

        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var window = saved["providers"]!["openai"]!["contextWindow"]!.GetValue<int>();

        Assert.True(window > 0);
    }
}
