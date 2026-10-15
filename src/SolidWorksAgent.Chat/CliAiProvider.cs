using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SolidWorksAgent.Chat;

public interface IChatProvider
{
    Task<ChatReply> ReplyAsync(ChatSettings settings, IReadOnlyList<ChatMessage> history, string context, CancellationToken token);
}

public sealed class CliAiProvider : IChatProvider
{
    public async Task<ChatReply> ReplyAsync(ChatSettings settings, IReadOnlyList<ChatMessage> history, string context, CancellationToken token)
    {
        var provider = settings.Provider;
        var options = settings.Providers[provider];
        var directory = Path.Combine(Path.GetTempPath(), "SolidWorksAgent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var schemaPath = Path.Combine(directory, "reply.schema.json");
            var replyPath = Path.Combine(directory, "reply.json");
            await File.WriteAllTextAsync(schemaPath, ChatProtocol.Schema, token);
            var args = Arguments(provider, options.Model, schemaPath, replyPath);
            var (exe, prefix) = ResolveExecutable(provider, options.Executable, settings.NodeExecutable);
            var start = ProcessRunner.StartInfo(exe, prefix.Concat(args), directory);
            if (provider == AiProvider.Gemini)
            {
                // A child-process-only overlay preserves existing administrative settings and adds deny rules.
                var policy = Path.Combine(directory, "no-tools.toml");
                await File.WriteAllTextAsync(policy, "[[rule]]\ntoolName = \"*\"\ndecision = \"deny\"\npriority = 999\n\n[[rule]]\ntoolName = \"*\"\nmcpName = \"*\"\ndecision = \"deny\"\npriority = 999\n", token);
                var source = Environment.GetEnvironmentVariable("GEMINI_CLI_SYSTEM_SETTINGS_PATH")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "gemini-cli", "settings.json");
                var overlay = File.Exists(source) ? JsonNode.Parse(await File.ReadAllTextAsync(source, token))!.AsObject() : new JsonObject();
                var paths = overlay["adminPolicyPaths"] as JsonArray ?? new JsonArray();
                if (overlay["adminPolicyPaths"] is null) overlay["adminPolicyPaths"] = paths;
                paths.Add(policy);
                var hooks = overlay["hooksConfig"] as JsonObject ?? new JsonObject();
                if (overlay["hooksConfig"] is null) overlay["hooksConfig"] = hooks;
                hooks["enabled"] = false;
                var overlayPath = Path.Combine(directory, "gemini-settings.json");
                await File.WriteAllTextAsync(overlayPath, overlay.ToJsonString(), token);
                start.Environment["GEMINI_CLI_SYSTEM_SETTINGS_PATH"] = overlayPath;
            }
            var result = await ProcessRunner.RunAsync(start, ChatProtocol.Prompt(history, context), timeout.Token);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"{provider} の実行に失敗しました。Windows側CLIのログインとモデル設定を確認してください。\n" + Limit(result.Error.Length > 0 ? result.Error : result.Output));
            var response = provider == AiProvider.Codex
                ? await File.ReadAllTextAsync(replyPath, token)
                : ExtractResponse(provider, result.Output);
            return ChatProtocol.Parse(response);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("AIの応答待ちが5分を超えました。ログイン状態・接続を確認してください。"); }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static string[] Arguments(AiProvider provider, string model, string schemaPath, string outputPath)
    {
        List<string> args = provider switch
        {
            AiProvider.Codex => ["exec", "--ephemeral", "--skip-git-repo-check", "--sandbox", "read-only",
                "--config", "features.shell_tool=false", "--config", "web_search=\"disabled\"",
                "--output-schema", schemaPath, "--output-last-message", outputPath],
            AiProvider.Claude => ["-p", "--tools", "", "--disallowedTools", "mcp__*", "--strict-mcp-config",
                "--mcp-config", "{\"mcpServers\":{}}", "--setting-sources", "", "--disable-slash-commands",
                "--no-session-persistence", "--output-format", "json", "--json-schema", ChatProtocol.Schema],
            AiProvider.Gemini => ["--prompt", "Read the request from standard input and return only the requested JSON.",
                "--output-format", "json", "--approval-mode", "default", "--extensions", "none"],
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        if (!string.IsNullOrWhiteSpace(model)) args.AddRange(["--model", model.Trim()]);
        if (provider == AiProvider.Codex) args.Add("-");
        return args.ToArray();
    }

    public static string ExtractResponse(AiProvider provider, string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True
            || root.TryGetProperty("error", out var failure) && failure.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("AI CLIがエラーを返しました: " + Limit(text));
        if (provider == AiProvider.Claude && root.TryGetProperty("structured_output", out var structured) && structured.ValueKind == JsonValueKind.Object)
            return structured.GetRawText();
        var property = provider == AiProvider.Claude ? "result" : "response";
        if (!root.TryGetProperty(property, out var response) || response.ValueKind != JsonValueKind.String)
            throw new FormatException("AI CLIから最終応答を取得できませんでした。");
        return response.GetString()!;
    }

    public static (string Executable, string[] Prefix) ResolveExecutable(AiProvider provider, string configured, string node)
    {
        if (string.IsNullOrWhiteSpace(configured)) throw new FileNotFoundException("設定でWindows版AI CLIを指定してください。");
        configured = Environment.ExpandEnvironmentVariables(configured);
        if (Path.GetExtension(configured).Equals(".js", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(configured).Equals(".mjs", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(configured)) throw new FileNotFoundException("CLIのJavaScriptファイルがありません。", configured);
            return (node, [Path.GetFullPath(configured)]);
        }
        if (File.Exists(configured) && configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return (Path.GetFullPath(configured), []);
        if (!Path.IsPathFullyQualified(configured))
        {
            foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(path.Trim('"'), configured);
                if (File.Exists(candidate) && candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return (candidate, []);
            }
            var native = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", configured);
            if (File.Exists(native) && native.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return (native, []);
        }
        // npm .cmd shims are resolved to their known JS entry point; no shell is involved.
        var npm = Path.IsPathFullyQualified(configured) && configured.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(configured)! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        var entry = provider switch
        {
            AiProvider.Codex => "@openai/codex/bin/codex.js",
            AiProvider.Claude => "@anthropic-ai/claude-code/cli.js",
            AiProvider.Gemini => "@google/gemini-cli/dist/index.js",
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        var js = Path.Combine(npm, "node_modules", entry.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(js)) return (node, [js]);
        throw new FileNotFoundException($"{provider} のWindows版CLIが見つかりません。設定で .exe または npm の .js を指定してください。", configured);
    }
    private static string Limit(string text) => text.Length <= 3000 ? text : text[..3000] + "…";
}
