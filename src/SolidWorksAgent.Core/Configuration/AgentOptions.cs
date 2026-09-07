using System.Text.Json;

namespace SolidWorksAgent.Core.Configuration;

public sealed record AgentOptions
{
    public string WorkspaceRoot { get; init; } = "workspace";
    public string? PartTemplate { get; init; }
    public string? AssemblyTemplate { get; init; }
    public string? DrawingTemplate { get; init; }
    public string LogFile { get; init; } = "logs/swagent.log";

    public static AgentOptions Load(string? configPath = null)
    {
        configPath ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(configPath)) return new AgentOptions();
        return JsonSerializer.Deserialize<AgentOptions>(File.ReadAllText(configPath), JsonOptions)
            ?? throw new InvalidOperationException($"Configuration is empty: {configPath}");
    }

    public AgentOptions ResolvePaths(string baseDirectory) => this with
    {
        WorkspaceRoot = Resolve(baseDirectory, WorkspaceRoot),
        PartTemplate = string.IsNullOrWhiteSpace(PartTemplate) ? FindInstalledPartTemplate() : Resolve(baseDirectory, PartTemplate),
        AssemblyTemplate = string.IsNullOrWhiteSpace(AssemblyTemplate) ? FindInstalledTemplate("*.asmdot") : Resolve(baseDirectory, AssemblyTemplate),
        DrawingTemplate = string.IsNullOrWhiteSpace(DrawingTemplate) ? FindInstalledTemplate("*.drwdot") : Resolve(baseDirectory, DrawingTemplate),
        LogFile = Resolve(baseDirectory, LogFile)
    };

    private static string Resolve(string root, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));

    public static string? FindInstalledPartTemplate()
        => FindInstalledTemplate("*.prtdot");

    private static string? FindInstalledTemplate(string pattern)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var solidWorksRoot = Path.Combine(programData, "SOLIDWORKS");
        if (!Directory.Exists(solidWorksRoot)) return null;
        return Directory.EnumerateFiles(solidWorksRoot, pattern, SearchOption.AllDirectories)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
