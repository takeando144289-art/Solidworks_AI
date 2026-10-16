using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SolidWorksAgent.Chat;

public sealed record HistoryMessage(string Role, string Text);
public sealed class ChatRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新しいチャット";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public AiProvider Provider { get; set; }
    public Dictionary<AiProvider, string> Sessions { get; set; } = [];
    public Dictionary<AiProvider, string> Models { get; set; } = [];
    public List<HistoryMessage> Messages { get; set; } = [];
    public string Draft { get; set; } = "";
    public List<ChatAttachment> Attachments { get; set; } = [];
    public override string ToString() => $"{UpdatedAt.ToLocalTime():MM/dd HH:mm}  {Provider}  {Title}";
}

/// <summary>Pane transcripts and native session identifiers; no replay of old requests.</summary>
public sealed class ChatHistoryStore(string directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string DirectoryPath => directory;
    public static ChatHistoryStore ForProject(string project)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(project).TrimEnd('\\', '/').ToUpperInvariant())))[..24];
        return new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidWorksAgent", "chats", key));
    }
    private string FilePath(string id)
    {
        if (!Guid.TryParse(id, out _)) throw new InvalidDataException("履歴IDが不正です。");
        return Path.Combine(directory, id + ".json");
    }
    public void Save(ChatRecord record)
    {
        Directory.CreateDirectory(directory);
        var path = FilePath(record.Id);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, record, JsonOptions); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public ChatRecord Load(string id)
    {
        var record = JsonSerializer.Deserialize<ChatRecord>(File.ReadAllText(FilePath(id)))
            ?? throw new InvalidDataException("履歴を読み取れませんでした。");
        if (record.Id != id || record.Messages is null || record.Sessions is null || record.Models is null || record.Attachments is null || record.Messages.Any(x => x is null || x.Role is null || x.Text is null) || record.Title is null)
            throw new InvalidDataException("履歴の内容が不正です。");
        return record;
    }
    public IReadOnlyList<ChatRecord> List()
    {
        if (!Directory.Exists(directory)) return [];
        List<ChatRecord> records = [];
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try { records.Add(Load(Path.GetFileNameWithoutExtension(path))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { System.Diagnostics.Trace.WriteLine($"Chat history {path}: {ex.Message}"); }
        }
        return records.OrderByDescending(x => x.UpdatedAt).ToArray();
    }
}
