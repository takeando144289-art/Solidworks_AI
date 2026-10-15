using System.Text.Json;

namespace SolidWorksAgent.Chat;

public sealed class CadCliClient(ChatSettings settings, int processId)
{
    public async Task<string> ExecuteAsync(CadCommand command, bool overwrite)
    {
        settings.ValidateCad();
        var args = CommandCatalog.ToArguments(command, processId, overwrite);
        // Do not kill a CAD command mid-operation. It must finish rebuild/save/document cleanup.
        var result = await ProcessRunner.RunAsync(ProcessRunner.StartInfo(settings.CadExecutable, args, settings.ProjectDirectory), null, CancellationToken.None);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error.Length > 0 ? result.Error : result.Output);
        using var json = JsonDocument.Parse(result.Output);
        return json.RootElement.GetRawText();
    }
}
