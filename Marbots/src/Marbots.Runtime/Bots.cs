using System.Reflection;
using System.Text.Json;
using Marbots.Abstractions;

namespace Marbots.Runtime;

public sealed class BotValidationException(string message) : Exception(message);

/// <summary>Bot registry: CRUD, validation, Boss Man protection and starter-team seeding.</summary>
public sealed class BotRegistry(
    IDocumentStore<BotDefinition> bots,
    IDocumentStore<BotTemplate> templates,
    IPolicyEngine policy,
    IEventBus bus,
    MarbotsOptions options)
{
    public const string BossManPersona = """
        You are Boss Man, the manager and orchestrator of a team of AI coworkers on the Marbots platform.
        You are calm, decisive and pragmatic. You speak in the user's language (Bahasa Indonesia or English).

        How you work:
        - Small questions or quick tasks: answer directly yourself.
        - Bigger goals: break the work into clear sub-tasks, pick the best teammates with list_bots, and hand the work
          off with delegate_tasks. Independent sub-tasks run in parallel; use depends_on for ordering.
        - Write each objective so a teammate can act on it without seeing this chat: goal, inputs, expected output,
          file names to use in the shared workspace, and the definition of done.
        - If no teammate fits, create one with create_bot (optionally from a template found with list_templates).
        - After delegation, review the results critically, then give the user one synthesized answer: what was done,
          where the artifacts are (workspace paths), and any open issues.
        - Recurring work ("every Monday…") goes through schedule_task.
        - Never invent results. If a teammate failed, say so and propose the next step.
        """;

    public async Task EnsureSeedAsync(CancellationToken ct)
    {
        var existing = await bots.GetAsync(WellKnown.BossManId, ct);
        if (existing is null)
        {
            await bots.UpsertAsync(new BotDefinition
            {
                Id = WellKnown.BossManId, Name = "Boss Man", Role = "Manager & orchestrator",
                Description = "Default manager. Plans work, delegates to the team and synthesizes results.",
                Persona = BossManPersona, Color = "#1F2A6B", IsSystem = true, PermissionProfile = "manager",
                Skills = ["*"], KernelFunctions = ["files", "search", "web", "memory", "todo", "agents", "management"],
                LongTermMemory = true, AutoLearn = AutoLearnMode.MemoryOnly, MaxSteps = 30,
            }, ct);
        }
        else if (!existing.IsSystem)
        {
            existing.IsSystem = true;
            await bots.UpsertAsync(existing, ct);
        }

        if (!options.SeedStarterBots) return;
        var all = await bots.ListAsync(ct);
        if (all.Count > 1) return;
        (string Template, string Name, string Color)[] starters =
        [
            ("researcher", "Atlas", "#0E7C86"),
            ("software-engineer", "Alice", "#6D3FC0"),
            ("qa-engineer", "Quinn", "#C2410C"),
            ("technical-writer", "Wren", "#B4235A"),
        ];
        foreach (var (templateId, name, color) in starters)
        {
            var t = await templates.GetAsync(templateId, ct);
            if (t is null) continue;
            var bot = FromTemplate(t, name);
            bot.Color = color;
            await CreateAsync(bot, ct);
        }
    }

    public static BotDefinition FromTemplate(BotTemplate t, string? name = null) => new()
    {
        Name = string.IsNullOrWhiteSpace(name) ? t.Name : name,
        Role = t.Role,
        Description = t.Description,
        Persona = t.Persona,
        Color = t.Color,
        ModelProfile = t.ModelProfile,
        Skills = [.. t.Skills],
        McpServers = [.. t.McpServers],
        KernelFunctions = [.. t.KernelFunctions],
        PermissionProfile = t.PermissionProfile,
        AutoLearn = t.AutoLearn,
        LongTermMemory = t.LongTermMemory,
        TemplateId = t.Id,
    };

    public Task<IReadOnlyList<BotDefinition>> ListAsync(CancellationToken ct = default) => bots.ListAsync(ct);

    public async Task<IReadOnlyList<BotDefinition>> ActiveAsync(CancellationToken ct = default) =>
        (await bots.ListAsync(ct)).Where(b => b.Status != BotStatus.Archived)
            .OrderByDescending(b => b.IsSystem).ThenBy(b => b.Name).ToList();

    public Task<BotDefinition?> GetAsync(string id, CancellationToken ct = default) => bots.GetAsync(id, ct);

    /// <summary>Find by id, then by case-insensitive name.</summary>
    public async Task<BotDefinition?> ResolveAsync(string idOrName, CancellationToken ct = default)
    {
        var bot = await bots.GetAsync(idOrName, ct);
        if (bot is not null) return bot;
        var all = await bots.ListAsync(ct);
        return all.FirstOrDefault(b => b.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(b => Ids.Slug(b.Name) == Ids.Slug(idOrName));
    }

    public void Validate(BotDefinition bot)
    {
        bot.ModelProfile = string.IsNullOrWhiteSpace(bot.ModelProfile) ? ModelRouter.DefaultProfile : bot.ModelProfile.Trim();
        if (string.IsNullOrWhiteSpace(bot.Name)) throw new BotValidationException("Name is required.");
        if (bot.Name.Length > 60) throw new BotValidationException("Name must be 60 characters or fewer.");
        if (!policy.Profiles.Contains(bot.PermissionProfile, StringComparer.OrdinalIgnoreCase))
            throw new BotValidationException($"Unknown permission profile '{bot.PermissionProfile}'. Use one of: {string.Join(", ", policy.Profiles)}.");
        if (bot.MaxSteps is < 1 or > 100) throw new BotValidationException("Max steps must be between 1 and 100.");
        if (!bot.IsSystem && bot.KernelFunctions.Contains("management"))
            throw new BotValidationException("Only Boss Man can use the management pack.");
    }

    public async Task<BotDefinition> CreateAsync(BotDefinition bot, CancellationToken ct = default)
    {
        bot.IsSystem = false;
        Validate(bot);
        if (string.IsNullOrWhiteSpace(bot.Id)) bot.Id = await UniqueIdAsync(bot.Name, ct);
        else if (await bots.GetAsync(bot.Id, ct) is not null) throw new BotValidationException($"A bot with id '{bot.Id}' already exists.");
        bot.CreatedAt = bot.UpdatedAt = DateTimeOffset.UtcNow;
        bot.Status = BotStatus.Ready;
        await bots.UpsertAsync(bot, ct);
        await bus.PublishAsync(new AgentEvent { Type = EventTypes.BotCreated, BotId = bot.Id, Message = $"{bot.Name} joined the team as {bot.Role}" }, ct);
        return bot;
    }

    public async Task<BotDefinition> UpdateAsync(BotDefinition bot, CancellationToken ct = default)
    {
        var existing = await bots.GetAsync(bot.Id, ct) ?? throw new BotValidationException("Bot not found.");
        bot.IsSystem = existing.IsSystem;
        bot.CreatedAt = existing.CreatedAt;
        if (existing.IsSystem)
        {
            // Core manager capability is protected; users may tune model, persona additions and limits.
            bot.KernelFunctions = existing.KernelFunctions.Union(bot.KernelFunctions).ToList();
            bot.PermissionProfile = existing.PermissionProfile;
        }
        Validate(bot);
        bot.UpdatedAt = DateTimeOffset.UtcNow;
        await bots.UpsertAsync(bot, ct);
        await bus.PublishAsync(new AgentEvent { Type = EventTypes.BotUpdated, BotId = bot.Id, Message = $"{bot.Name} was updated" }, ct);
        return bot;
    }

    public async Task SetStatusAsync(string id, BotStatus status, CancellationToken ct = default)
    {
        var bot = await bots.GetAsync(id, ct);
        if (bot is null || bot.Status == status) return;
        bot.Status = status;
        await bots.UpsertAsync(bot, ct);
        await bus.PublishAsync(new AgentEvent { Type = EventTypes.BotStateChanged, BotId = id, Message = status.ToString() }, ct);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var bot = await bots.GetAsync(id, ct);
        if (bot is null) return false;
        if (bot.IsSystem) throw new BotValidationException("Boss Man is a protected system bot and cannot be deleted.");
        await bots.DeleteAsync(id, ct);
        await bus.PublishAsync(new AgentEvent { Type = EventTypes.BotDeleted, BotId = id, Message = $"{bot.Name} left the team" }, ct);
        return true;
    }

    public async Task<string> UniqueIdAsync(string name, CancellationToken ct)
    {
        var slug = Ids.Slug(name);
        if (slug.Length == 0) slug = "bot";
        var id = slug;
        for (var i = 2; await bots.GetAsync(id, ct) is not null; i++) id = $"{slug}-{i}";
        return id;
    }
}

/// <summary>Template Gallery: built-in templates are seeded from an embedded catalogue; users can add their own.</summary>
public sealed class TemplateService(IDocumentStore<BotTemplate> store)
{
    public async Task SeedAsync(CancellationToken ct)
    {
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Marbots.Runtime.builtin-templates.json")
            ?? throw new InvalidOperationException("Embedded template catalogue is missing.");
        var builtIns = await JsonSerializer.DeserializeAsync(stream, MarbotsJsonContext.Default.ListBotTemplate, ct) ?? [];
        foreach (var t in builtIns)
        {
            t.IsBuiltIn = true;
            await store.UpsertAsync(t, ct);
        }
    }

    public async Task<IReadOnlyList<BotTemplate>> ListAsync(string? query = null, string? category = null, CancellationToken ct = default)
    {
        IEnumerable<BotTemplate> all = await store.ListAsync(ct);
        if (!string.IsNullOrWhiteSpace(category)) all = all.Where(t => t.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            all = all.Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Role.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || t.Description.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Tags.Any(x => x.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }
        return all.OrderBy(t => t.Category).ThenBy(t => t.Name).ToList();
    }

    public Task<BotTemplate?> GetAsync(string id, CancellationToken ct = default) => store.GetAsync(id, ct);

    public async Task<BotTemplate> SaveAsync(BotTemplate t, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(t.Name)) throw new BotValidationException("Template name is required.");
        if (string.IsNullOrWhiteSpace(t.Id)) t.Id = "custom-" + Ids.Slug(t.Name) + "-" + Guid.NewGuid().ToString("N")[..4];
        var existing = await store.GetAsync(t.Id, ct);
        if (existing is { IsBuiltIn: true }) throw new BotValidationException("Built-in templates are read-only. Duplicate it to customise.");
        t.IsBuiltIn = false;
        await store.UpsertAsync(t, ct);
        return t;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        var t = await store.GetAsync(id, ct);
        if (t is null) return false;
        if (t.IsBuiltIn) throw new BotValidationException("Built-in templates cannot be deleted.");
        return await store.DeleteAsync(id, ct);
    }
}
