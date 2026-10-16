using System.Text.Json;
using System.Diagnostics;

namespace SolidWorksAgent.Chat;

public sealed record AgentModel(string Id, string DisplayName, string Description, bool IsDefault = false)
{
    public override string ToString() => DisplayName;
}

public static class ModelCatalog
{
    public static async Task<IReadOnlyList<AgentModel>> ListAsync(ChatSettings settings, CancellationToken token)
    {
        if (settings.Provider == AiProvider.Claude) return await ListClaudeAsync(settings, token);
        if (settings.Provider != AiProvider.Codex) throw new NotSupportedException("このAIのモデル一覧は未対応です。");
        // Model discovery does not start a conversation or execute any CAD/inference task.
        await using var session = new CodexSession(settings, 0);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await session.ListModelsAsync(timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("モデル一覧の取得がタイムアウトしました。Codexのログインと接続を確認し、再取得してください。"); }
    }

    private static async Task<IReadOnlyList<AgentModel>> ListClaudeAsync(ChatSettings settings, CancellationToken token)
    {
        var (exe, prefix) = CliAiProvider.ResolveExecutable(AiProvider.Claude, settings.Providers[AiProvider.Claude].Executable, settings.NodeExecutable);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = ProcessRunner.StartInfo(exe,
            prefix.Concat(["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose"]), settings.ProjectDirectory) };
        if (!process.Start()) throw new IOException("Claude CLIを起動できませんでした。");
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            // Native SDK control handshake only: no user message, inference or CAD operation.
            await process.StandardInput.WriteLineAsync("{\"type\":\"control_request\",\"request_id\":\"models\",\"request\":{\"subtype\":\"initialize\"}}");
            await process.StandardInput.FlushAsync();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (ReadString(root, "type") != "control_response" || !root.TryGetProperty("response", out var response)
                    || ReadString(response, "request_id") != "models") continue;
                if (ReadString(response, "subtype") != "success")
                    throw new IOException("Claudeモデル一覧: " + (ReadString(response, "error") ?? "初期化に失敗しました。"));
                var models = ReadClaudeModels(response.GetProperty("response"));
                if (models.Count == 0) throw new IOException("Claude CLIからモデル一覧が返されませんでした。CLIの更新とログインを確認してください。");
                return models;
            }
            throw new IOException("Claude CLIとの接続が終了しました。ログインとCLIの設定を確認してください。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Claudeのモデル一覧取得がタイムアウトしました。ログインと接続を確認してください。"); }
        finally
        {
            try { if (!process.HasExited) process.Kill(false); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            await stderr;
        }
    }

    public static IReadOnlyList<AgentModel> ReadClaudeModels(JsonElement response)
    {
        List<AgentModel> models = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        if (!response.TryGetProperty("models", out var entries) || entries.ValueKind != JsonValueKind.Array) return models;
        foreach (var entry in entries.EnumerateArray())
        {
            var id = ReadString(entry, "value");
            if (string.IsNullOrWhiteSpace(id) || id == "default" || !ids.Add(id)) continue;
            models.Add(new(id, ReadString(entry, "displayName") ?? id, ReadString(entry, "description") ?? ""));
        }
        return models;
    }

    public static async Task<IReadOnlyList<AgentModel>> ReadPagesAsync(
        Func<string?, CancellationToken, Task<JsonElement>> fetch, CancellationToken token)
    {
        List<AgentModel> models = [];
        HashSet<string> ids = new(StringComparer.Ordinal), cursors = new(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            token.ThrowIfCancellationRequested();
            var page = await fetch(cursor, token);
            foreach (var item in page.GetProperty("data").EnumerateArray())
            {
                if (item.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
                var id = ReadString(item, "model") ?? ReadString(item, "id");
                if (string.IsNullOrWhiteSpace(id) || !ids.Add(id)) continue;
                models.Add(new(id, ReadString(item, "displayName") ?? id, ReadString(item, "description") ?? "",
                    item.TryGetProperty("isDefault", out var recommended) && recommended.ValueKind == JsonValueKind.True));
            }
            cursor = ReadString(page, "nextCursor");
            if (!string.IsNullOrEmpty(cursor) && (!cursors.Add(cursor) || cursors.Count > 100))
                throw new InvalidDataException("モデル一覧のページ取得を完了できませんでした。再取得してください。");
        } while (!string.IsNullOrEmpty(cursor));
        return models;
    }
    private static string? ReadString(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
