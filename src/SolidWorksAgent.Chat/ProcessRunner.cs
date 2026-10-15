using System.Diagnostics;
using System.Text;

namespace SolidWorksAgent.Chat;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public static class ProcessRunner
{
    // Never use cmd.exe / PowerShell to interpolate a model-generated command.
    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> args, string directory)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    public static async Task<ProcessResult> RunAsync(ProcessStartInfo info, string? input, CancellationToken token)
    {
        using var process = new Process { StartInfo = info };
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("プロセスを起動できませんでした。");
        using var registration = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        try
        {
            void Kill() { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
            var output = ReadBoundedAsync(process.StandardOutput, token, Kill);
            var error = ReadBoundedAsync(process.StandardError, token, Kill);
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), token);
            process.StandardInput.Close();
            await Task.WhenAll(output, error, process.WaitForExitAsync(token));
            token.ThrowIfCancellationRequested();
            return new(process.ExitCode, output.Result, error.Result);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader stream, CancellationToken token, Action kill)
    {
        var text = new StringBuilder(); var buffer = new char[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            text.Append(buffer, 0, count);
            if (text.Length > 2_000_000) { kill(); throw new IOException("CLI出力が上限を超えました。"); }
        }
        return text.ToString();
    }
}
