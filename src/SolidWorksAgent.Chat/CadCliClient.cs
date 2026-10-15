using System.Text.Json;

namespace SolidWorksAgent.Chat;

public sealed class CadCliClient(ChatSettings settings, int processId)
{
    public async Task<string> ExecuteAsync(CadCommand command, bool overwrite)
    {
        settings.ValidateCad();
        var args = CommandCatalog.ToArguments(command, processId, overwrite).ToList();
        string? input = null;
        if (command.Command == "api execute")
        {
            var index = args.IndexOf("--plan"); input = args[index + 1]; args.RemoveRange(index,2); args.Add("--plan-stdin");
        }
        // Do not kill a CAD command mid-operation. It must finish rebuild/save/document cleanup.
        var result = await ProcessRunner.RunAsync(ProcessRunner.StartInfo(settings.CadExecutable, args, settings.ProjectDirectory), input, CancellationToken.None);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error.Length > 0 ? result.Error : result.Output);
        using var json = JsonDocument.Parse(result.Output);
        return json.RootElement.GetRawText();
    }
}
