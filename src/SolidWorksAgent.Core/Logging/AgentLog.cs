using System.Text;

namespace SolidWorksAgent.Core.Logging;

public interface IAgentLog
{
    void Write(string category, string message);
}

public sealed class FileAgentLog(string path) : IAgentLog
{
    private readonly object _gate = new();

    public void Write(string category, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{category}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.AppendAllText(path, line, new UTF8Encoding(false));
        }
    }
}

public sealed class NullAgentLog : IAgentLog
{
    public void Write(string category, string message) { }
}

