#:project ../../src/Marbots.Sdk/Marbots.Sdk.csproj
// Creates "Nova", a software engineer that works on another computer (an enrolled agent host).
// Run: dotnet run samples/remote-host/create-nova.cs [host-name]
using Marbots.Abstractions;
using Marbots.Sdk;

var url = Environment.GetEnvironmentVariable("MARBOTS_URL") ?? "http://localhost:5170";
using var client = new MarbotsClient(new Uri(url), Environment.GetEnvironmentVariable("MARBOTS_API_KEY"));
var hostName = args.FirstOrDefault() ?? "DEV2";

var host = (await client.Hosts.ListAsync()).FirstOrDefault(h => h.Name.Equals(hostName, StringComparison.OrdinalIgnoreCase))
    ?? throw new InvalidOperationException($"No host named {hostName}. Add one with: marbots hosts bootstrap user@address --server <url>");
Console.WriteLine($"Host {host.Name} ({host.Id}) is {host.Status}: {string.Join(", ", host.Capabilities)}");

var nova = await client.Bots.CreateAsync(new BotDefinition
{
    Name = "Nova",
    Role = ".NET and web engineer",
    Description = "Builds Blazor, Avalonia and console apps on the build PC.",
    Persona = """
        You are Nova, a pragmatic .NET engineer. You build, run and verify what you make: install missing
        prerequisites with install_package, compile with the dotnet CLI, and prove results with screenshots
        (Playwright for web pages). Follow the frontend-design skill for any UI. Report the files you produced.
        """,
    Color = "#0EA5A4",
    HostRef = host.Id,
    KernelFunctions = ["files", "search", "shell", "web", "memory", "todo"],
    Skills = ["frontend-design", "webapp-testing"],
    PermissionProfile = PermissionProfiles.Autonomous,
    ModelProfile = ModelRef.Of("azure", "gpt-5.6-luna"),
    MaxSteps = 60,
});
Console.WriteLine($"Created {nova.Name} ({nova.Id}) on {nova.HostRef}, model {nova.ModelProfile}");
