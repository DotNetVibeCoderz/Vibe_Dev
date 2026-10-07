using System.Diagnostics;
using System.Globalization;
using System.Text;
using Marbots.Abstractions;

namespace Marbots.Kernel;

/// <summary>
/// Computer use (pack "desktop", Windows): screenshots the bot can see, mouse clicks and keyboard input on the desktop of
/// the machine its tools run on. Needs an interactive session (the agent host runs as a logon task, not a service).
/// Coordinates are in the pixels of the latest screenshot; the scale is remembered per workspace.
/// </summary>
internal static class Desktop
{
    public const int MaxWidth = 1280;

    public static bool Supported => OperatingSystem.IsWindows();

    private const string DpiAware = """
        Add-Type -Namespace MB -Name Native -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); [DllImport("user32.dll")] public static extern void mouse_event(int f, int x, int y, int d, int e);'
        [MB.Native]::SetProcessDPIAware() | Out-Null
        """;

    public static async Task<(int Exit, string Output)> RunAsync(string script, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", DpiAware + "\n" + script },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw; }
        return (p.ExitCode, ((await stdout) + (await stderr)).Trim());
    }

    public static string StatePath(string workspace) => Path.Combine(workspace, ".marbots", "screen.txt");

    public static double Scale(string workspace) =>
        File.Exists(StatePath(workspace)) && double.TryParse(File.ReadAllText(StatePath(workspace)), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 1.0;

    public static string Escape(string s) => s.Replace("'", "''", StringComparison.Ordinal);
}

public sealed class ScreenshotFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "screenshot", "Take a screenshot of the desktop. You will see the image; it is also saved in the workspace under screenshots/. Use it before clicking, and again to check the result.",
        Schema(("name", "string", "Optional file name (without extension)", false)),
        "desktop", PermissionCategory.ReadOnly, RiskLevel.Medium);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        if (!Desktop.Supported) return FunctionResult.Fail("Desktop tools need a Windows host with an interactive session.");
        var name = Ids.Slug(call.GetString("name") ?? "") is { Length: > 0 } n ? n : "shot-" + DateTimeOffset.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture);
        var rel = $"screenshots/{name}.png";
        var full = WorkspacePaths.Resolve(ctx.WorkspacePath, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var script = $$"""
            Add-Type -AssemblyName System.Windows.Forms, System.Drawing
            $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
            $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
            $scale = [Math]::Min(1.0, {{Desktop.MaxWidth}} / $b.Width)
            $w = [int]($b.Width * $scale); $h = [int]($b.Height * $scale)
            $out = New-Object System.Drawing.Bitmap $w, $h
            $g2 = [System.Drawing.Graphics]::FromImage($out)
            $g2.InterpolationMode = 'HighQualityBicubic'
            $g2.DrawImage($bmp, 0, 0, $w, $h)
            $out.Save('{{Desktop.Escape(full)}}', [System.Drawing.Imaging.ImageFormat]::Png)
            "$w $h $($b.Width) $($b.Height) $([string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $scale))"
            """;
        var (exit, output) = await Desktop.RunAsync(script, ct);
        if (exit != 0 || !File.Exists(full)) return FunctionResult.Fail("Screenshot failed (is the host running in a logged-on desktop session?): " + output);
        var parts = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1].Trim().Split(' ');
        Directory.CreateDirectory(Path.GetDirectoryName(Desktop.StatePath(ctx.WorkspacePath))!);
        await File.WriteAllTextAsync(Desktop.StatePath(ctx.WorkspacePath), parts[^1], ct);
        var data = "data:image/png;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(full, ct));
        return FunctionResult.Ok($"Saved {rel} ({parts[0]}x{parts[1]}; real screen {parts[2]}x{parts[3]}). Coordinates for mouse_click are pixels of this image.") with { Images = [data] };
    }
}

public sealed class MouseClickFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "mouse_click", "Click on the desktop at x,y (pixels of the latest screenshot).",
        Schema(("x", "integer", "X in screenshot pixels", true), ("y", "integer", "Y in screenshot pixels", true),
               ("button", "string", "left (default) or right", false), ("double", "boolean", "Double-click", false)),
        "desktop", PermissionCategory.ProcessExecution, RiskLevel.Medium);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        if (!Desktop.Supported) return FunctionResult.Fail("Desktop tools need a Windows host with an interactive session.");
        var scale = Desktop.Scale(ctx.WorkspacePath);
        var x = (int)Math.Round(call.GetInt("x", 0) / scale);
        var y = (int)Math.Round(call.GetInt("y", 0) / scale);
        var (down, up) = call.GetString("button") is "right" ? (8, 16) : (2, 4);
        var clicks = call.GetString("double") is "true" or "True" ? 2 : 1;
        var script = $"[MB.Native]::SetCursorPos({x}, {y}) | Out-Null; Start-Sleep -Milliseconds 80; " +
                     string.Concat(Enumerable.Repeat($"[MB.Native]::mouse_event({down},0,0,0,0); [MB.Native]::mouse_event({up},0,0,0,0); Start-Sleep -Milliseconds 90; ", clicks)) + "'ok'";
        var (exit, output) = await Desktop.RunAsync(script, ct);
        return exit == 0 ? FunctionResult.Ok($"Clicked at {call.GetInt("x", 0)},{call.GetInt("y", 0)} (screen {x},{y}). Take a screenshot to see the result.") : FunctionResult.Fail(output);
    }
}

public sealed class TypeTextFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "type_text", "Type text into the focused window (click into a field first).",
        Schema(("text", "string", "Text to type", true)),
        "desktop", PermissionCategory.ProcessExecution, RiskLevel.Medium);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        if (!Desktop.Supported) return FunctionResult.Fail("Desktop tools need a Windows host with an interactive session.");
        var text = call.Require("text");
        // SendKeys treats + ^ % ~ ( ) { } [ ] as commands; wrap them in braces to type them literally.
        var escaped = new StringBuilder();
        foreach (var c in text) escaped.Append("+^%~(){}[]".Contains(c, StringComparison.Ordinal) ? $"{{{c}}}" : c.ToString());
        var (exit, output) = await Desktop.RunAsync($"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait('{Desktop.Escape(escaped.ToString())}'); 'ok'", ct);
        return exit == 0 ? FunctionResult.Ok($"Typed {text.Length} characters.") : FunctionResult.Fail(output);
    }
}

public sealed class PressKeysFunction : KernelFunctionBase
{
    public override FunctionDescriptor Descriptor { get; } = new(
        "press_keys", "Press keys or shortcuts using SendKeys syntax, e.g. {ENTER}, {TAB}, ^l (Ctrl+L), %{F4} (Alt+F4), ^s, #r is not supported.",
        Schema(("keys", "string", "SendKeys sequence", true)),
        "desktop", PermissionCategory.ProcessExecution, RiskLevel.Medium);

    protected override async ValueTask<FunctionResult> ExecuteAsync(FunctionCall call, FunctionExecutionContext ctx, CancellationToken ct)
    {
        if (!Desktop.Supported) return FunctionResult.Fail("Desktop tools need a Windows host with an interactive session.");
        var keys = call.Require("keys");
        var (exit, output) = await Desktop.RunAsync($"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait('{Desktop.Escape(keys)}'); 'ok'", ct);
        return exit == 0 ? FunctionResult.Ok($"Pressed {keys}.") : FunctionResult.Fail(output);
    }
}
