using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SolidWorksAgent.Chat;

/// <summary>Claude/Gemini native agent CLI, with default tools/config and native session continuation.</summary>
public sealed class NativeCliSession(ChatSettings settings, int processId) : IAgentSession
{
    private readonly AiProvider _provider = settings.Provider;
    private string? _session;
    private Process? _process;

    public static string[] Arguments(AiProvider provider, string model, string? session)
    {
        List<string> args = provider switch
        {
            AiProvider.Claude => ["-p", "--verbose", "--output-format", "stream-json"],
            AiProvider.Gemini => ["--prompt", "Read the user request from standard input.", "--output-format", "stream-json"],
            _ => throw new ArgumentException("Use Codex app-server for Codex.")
        };
        if (session is not null) args.AddRange(["--resume", session]);
        if (!string.IsNullOrWhiteSpace(model)) args.AddRange(["--model", model]);
        return args.ToArray();
    }

    public async Task SendAsync(string message, Action<AgentEvent> display,
        Func<AgentQuestion, CancellationToken, Task<string>> ask, CancellationToken token)
    {
        var provider = _provider; var options = settings.Providers[provider];
        var (exe, prefix) = CliAiProvider.ResolveExecutable(provider, options.Executable, settings.NodeExecutable);
        using var process = new Process { StartInfo = ProcessRunner.StartInfo(exe, prefix.Concat(Arguments(provider, options.Model, _session)), settings.ProjectDirectory) };
        _process = process;
        if (!process.Start()) throw new IOException("AI CLIを起動できませんでした。");
        // Terminate only the agent, never its CAD descendants midway through save/cleanup.
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(false); } catch (InvalidOperationException) { } });
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync((_session is null ? AgentSession.Instructions(settings, processId) + "\n\n" : "") + message);
        process.StandardInput.Close();
        var seenText = false;
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
                if (root.TryGetProperty("session_id", out var session)) _session = session.GetString();
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
                if (provider == AiProvider.Claude && type == "assistant" && root.TryGetProperty("message", out var msg))
                {
                    foreach (var item in msg.GetProperty("content").EnumerateArray())
                        if (item.TryGetProperty("text", out var text)) { display(new("message", text.GetString()!)); seenText = true; }
                        else if (item.TryGetProperty("name", out var name)) display(new("status", name.GetString() + " を実行しています…"));
                }
                else if (provider == AiProvider.Gemini && type == "message" && root.GetProperty("role").GetString() == "assistant")
                { display(new("delta", root.GetProperty("content").GetString()!, "gemini")); seenText = true; }
                else if (type is "tool_use" or "tool_result") display(new("status", "AIがツールを使って作業しています…"));
                else if (type == "result")
                {
                    if (root.TryGetProperty("is_error", out var failure) && failure.ValueKind == JsonValueKind.True
                        || root.TryGetProperty("status", out var status) && status.GetString() == "error")
                        throw new InvalidOperationException(root.ToString());
                    if (!seenText && root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
                        display(new("message", result.GetString()!));
                }
            }
            await process.WaitForExitAsync();
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        finally { _process = null; }
    }
    public Task SteerAsync(string message, CancellationToken token) => throw new InvalidOperationException("このCLIでは応答後に追加の指示を送信してください。");
    public ValueTask DisposeAsync()
    {
        try { if (_process is { HasExited: false }) _process.Kill(false); } catch (InvalidOperationException) { }
        return ValueTask.CompletedTask;
    }
}
