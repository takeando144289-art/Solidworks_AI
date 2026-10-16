using System.Text.Json;

namespace SolidWorksAgent.Chat;

public sealed class ProviderSettings
{
    public string Executable { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class ChatSettings
{
    public static IReadOnlyList<AiProvider> AvailableProviders { get; } = Array.AsReadOnly(new[] { AiProvider.Codex, AiProvider.Claude });
    public string ProjectDirectory { get; set; } = "";
    public string CadExecutable { get; set; } = "";
    public string NodeExecutable { get; set; } = "node.exe";
    public AiProvider Provider { get; set; } = AiProvider.Codex;
    public Dictionary<AiProvider, ProviderSettings> Providers { get; set; } = new()
    {
        [AiProvider.Codex] = new() { Executable = "codex.exe" },
        [AiProvider.Claude] = new() { Executable = "claude.exe" }
    };
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolidWorksAgent", "chat-settings.json");

    public static ChatSettings Load()
    {
        var path = File.Exists(SettingsPath) ? SettingsPath : Path.Combine(Path.GetDirectoryName(typeof(ChatSettings).Assembly.Location)!, "chat-defaults.json");
        if (!File.Exists(path)) return new();
        return FromJson(File.ReadAllText(path));
    }
    public static ChatSettings FromJson(string json)
    {
        var settings = JsonSerializer.Deserialize<ChatSettings>(json, ChatProtocol.Json)
            ?? throw new InvalidDataException("チャット設定が空です。");
        // Retain legacy provider entries so loading old settings loses no saved paths/models.
        if (!AvailableProviders.Contains(settings.Provider)) settings.Provider = AiProvider.Codex;
        foreach (var provider in AvailableProviders)
            if (!settings.Providers.ContainsKey(provider)) settings.Providers[provider] = new ChatSettings().Providers[provider];
        return settings;
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, ChatProtocol.Json));
        File.Move(temporary, SettingsPath, true);
    }
    public void ValidateCad()
    {
        if (!Directory.Exists(ProjectDirectory)) throw new DirectoryNotFoundException("設定で作業フォルダーを選択してください。");
        if (!Path.IsPathFullyQualified(CadExecutable) || !File.Exists(CadExecutable) || !CadExecutable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("設定で swagent.exe を選択してください。");
    }
}
