using System.Text.Json;

namespace SolidWorksAgent.Chat;

public sealed record AgentEvent(string Kind, string Text, string? ItemId = null);
public sealed record AgentQuestion(string Text, string[] Options);

/// <summary>A transport to the vendor's own agent runtime, not an application planning loop.</summary>
public interface IAgentSession : IAsyncDisposable
{
    Task SteerAsync(string message, CancellationToken token);
    Task SendAsync(string message, Action<AgentEvent> display,
        Func<AgentQuestion, CancellationToken, Task<string>> ask, CancellationToken token);
}

public static class AgentSession
{
    public static IAgentSession Create(ChatSettings settings, int processId) => settings.Provider == AiProvider.Codex
        ? new CodexSession(settings, processId) : new NativeCliSession(settings, processId);

    public static string Instructions(ChatSettings settings, int processId) => $"""
        You are running directly inside a Windows SOLIDWORKS chat pane. Respond in the user's language.
        This is a normal agent conversation; there is no JSON reply schema or host planning/approval loop.
        Project: {settings.ProjectDirectory}
        Existing Windows CAD CLI: {settings.CadExecutable}
        Target SOLIDWORKS process ID: {processId}
        Read AGENTS.md and relevant project documentation. The CLI --help and docs/api-automation.md describe existing tools.
        Prefer the solidworks_cli tool when available; otherwise invoke the Windows CLI with --solidworks-pid {processId} --json.
        It accepts any existing CLI command and optional stdin, including api search/describe/execute --plan-stdin.
        You also have your native agent tools. Inspect source/docs and use or extend existing tools when the task needs it.
        Decide how to inspect, execute, verify and recover using your own agent workflow. The pane only relays messages and tools.
        Protect existing CAD files and unsaved user work. Overwrite only when the user explicitly authorizes it and --overwrite is passed.
        Rebuild and validate geometry after features; keep the saved final output open and close only your own intermediate documents.
        COM calls belong in src/SolidWorksAgent.SwApi. Public dimensions are mm, converted at the COM boundary.
        Do not change WSL settings or switch execution to WSL. Do not stop, unload or replace the hosting add-in during a CAD task.
        """;
}

/// <summary>Thin adapter for the existing CLI. CAD lifecycle/validation remains in SwApi.</summary>
public sealed class NativeCadTool(ChatSettings settings, int processId)
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    public async Task<ProcessResult> ExecuteAsync(JsonElement input, CancellationToken token)
    {
        settings.ValidateCad();
        if (processId <= 0) throw new InvalidOperationException("SOLIDWORKSアドイン内で実行してください。");
        var args = input.GetProperty("arguments").EnumerateArray().Select(a => a.GetString() ?? throw new ArgumentException("Arguments must be strings.")).ToList();
        // Pin only the host identity. There is no command catalog, plan interpreter or retry logic here.
        if (args.Any(a => a.Equals("--solidworks-pid", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--solidworks-pid=", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The host supplies --solidworks-pid; omit it.");
        args.AddRange(["--solidworks-pid", processId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json"]);
        var stdin = input.TryGetProperty("stdin", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        await _serial.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            // Let an in-flight CAD command finish save/rebuild/cleanup even when the turn is interrupted.
            return await ProcessRunner.RunAsync(ProcessRunner.StartInfo(settings.CadExecutable, args, settings.ProjectDirectory), stdin, CancellationToken.None);
        }
        finally { _serial.Release(); }
    }
    public async Task DrainAsync() { await _serial.WaitAsync(); _serial.Release(); }
}
