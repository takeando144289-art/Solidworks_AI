using System.Text.Json;

namespace SolidWorksAgent.Chat;

/// <summary>Read only this pane's older native transcripts. Never execute imported content.</summary>
public static class NativeHistoryImport
{
    public static int Import(ChatHistoryStore store, string project, CancellationToken token,
        string? codexHome = null, string? claudeHome = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        codexHome ??= Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex");
        claudeHome ??= Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude");
        var known = store.List().SelectMany(r => r.Sessions.Select(s => $"{s.Key}:{s.Value}")).ToHashSet();
        int count = 0;
        foreach (var (provider, root) in new[] { (AiProvider.Codex, Path.Combine(codexHome, "sessions")), (AiProvider.Claude, Path.Combine(claudeHome, "projects")) })
        {
            if (!Directory.Exists(root)) continue;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", options))
            {
                token.ThrowIfCancellationRequested();
                if (provider == AiProvider.Claude && file.Contains(Path.DirectorySeparatorChar + "subagents" + Path.DirectorySeparatorChar)) continue;
                ChatRecord? record;
                try { record = Read(file, provider, project, token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                { System.Diagnostics.Trace.WriteLine($"Import {file}: {ex.Message}"); continue; }
                if (record is null) continue;
                var key = $"{provider}:{record.Sessions[provider]}";
                if (!known.Add(key)) continue;
                store.Save(record); count++;
            }
        }
        return count;
    }
    public static ChatRecord? Read(string file, AiProvider provider, string project, CancellationToken token)
    {
        var record = new ChatRecord { Provider = provider, UpdatedAt = File.GetLastWriteTimeUtc(file) };
        bool belongs = false;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; } // partially written final line
            using (doc)
            {
                var root = doc.RootElement; var type = Str(root, "type");
                if (provider == AiProvider.Codex)
                {
                    if (!root.TryGetProperty("payload", out var p)) continue;
                    if (type == "session_meta")
                    {
                        if (Str(p, "originator") != "solidworks_chat" || !SameProject(Str(p, "cwd"), project)
                            || Str(p, "parent_thread_id") is not null || (p.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)) return null;
                        var id = Str(p, "id"); if (id is null) return null;
                        record.Sessions[provider] = id; belongs = true;
                    }
                    else if (!belongs) return null;
                    else if (type == "turn_context" && Str(p, "model") is { } model) record.Models[provider] = model;
                    else if (type == "response_item" && Str(p, "type") == "message" && Str(p, "role") is "user" or "assistant")
                    {
                        var text = Content(p);
                        if (Str(p, "role") == "user" && (text.StartsWith("# AGENTS.md instructions for ") || text.StartsWith("<environment_context>") || text.StartsWith("<permissions instructions>"))) continue;
                        Add(record, Str(p, "role") == "user" ? "あなた" : "Codex", text);
                    }
                }
                else if (type is "user" or "assistant" && root.TryGetProperty("message", out var msg))
                {
                    if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) continue;
                    if (!SameProject(Str(root, "cwd"), project)) return null;
                    var text = Content(msg);
                    if (!belongs)
                    {
                        if (type != "user" || !text.StartsWith("You are running directly inside a Windows SOLIDWORKS chat pane.")) return null;
                        var id = Str(root, "sessionId"); if (id is null) return null;
                        record.Sessions[provider] = id; belongs = true;
                    }
                    if (text.StartsWith("You are running directly inside a Windows SOLIDWORKS chat pane."))
                    {
                        var end = text.IndexOf("Do not stop, unload or replace the hosting add-in during a CAD task.", StringComparison.Ordinal);
                        if (end < 0) continue;
                        text = text[(end + "Do not stop, unload or replace the hosting add-in during a CAD task.".Length)..].TrimStart();
                    }
                    if (type == "assistant" && Str(msg, "model") is { } model) record.Models[provider] = model;
                    Add(record, type == "user" ? "あなた" : "Claude", text);
                }
            }
        }
        var first = record.Messages.FirstOrDefault(x => x.Role == "あなた")?.Text;
        if (!belongs || string.IsNullOrWhiteSpace(first)) return null;
        first = first.Replace('\r', ' ').Replace('\n', ' ');
        record.Title = first[..Math.Min(first.Length, 60)];
        return record;
    }
    private static void Add(ChatRecord record, string role, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var manifest = text.IndexOf("\n\nAttached reference files (JSON manifest;", StringComparison.Ordinal);
        if (manifest >= 0) text = text[..manifest] + "\n（添付ファイルはAI側の会話履歴に保持されています）";
        record.Messages.Add(new(role, text));
    }
    private static bool SameProject(string? value, string project) => value is not null &&
        value.Replace('\\', '/').TrimEnd('/').Equals(project.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    private static string? Str(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string Content(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        return string.Join("\n", content.EnumerateArray().Where(x => Str(x, "type") is "text" or "input_text" or "output_text").Select(x => Str(x, "text") ?? ""));
    }
}
