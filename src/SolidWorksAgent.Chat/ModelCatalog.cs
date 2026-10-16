using System.Text.Json;

namespace SolidWorksAgent.Chat;

public sealed record AgentModel(string Id, string DisplayName, string Description, bool IsDefault = false)
{
    public override string ToString() => DisplayName;
}

public static class ModelCatalog
{
    public static async Task<IReadOnlyList<AgentModel>> ListAsync(ChatSettings settings, CancellationToken token)
    {
        if (settings.Provider != AiProvider.Codex)
            throw new NotSupportedException($"{settings.Provider}のモデル一覧取得は未対応です。既定を使うか、モデル名を直接指定してください。");
        // Model discovery does not start a conversation or execute any CAD/inference task.
        await using var session = new CodexSession(settings, 0);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await session.ListModelsAsync(timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("モデル一覧の取得がタイムアウトしました。Codexのログインと接続を確認し、再取得してください。"); }
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
