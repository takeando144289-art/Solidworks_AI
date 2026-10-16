using System.Text.Json;
using SolidWorksAgent.Chat;

internal static class NativeSessionChecks
{
    public static async Task<int> Run()
    {
        var failed = 0;
        async Task Test(string name, Func<Task> test)
        {
            try { await test(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }
        await Test("Native Codex tool failure/success reaches same agent and thread persists", async () =>
        {
            var settings = Settings(Environment.ProcessPath!); var calls = new List<string>(); var messages = new List<AgentEvent>();
            await using var session = new CodexSession(settings, 123, (input, _) =>
            {
                var command = input.GetProperty("arguments")[0].GetString()!; calls.Add(command);
                return Task.FromResult(command == "trial" ? new ProcessResult(1, "", "trial geometry failed") : new ProcessResult(0, "verified", ""));
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await session.SendAsync("make", messages.Add, (_, _) => Task.FromResult("拒否"), timeout.Token);
            await session.SendAsync("follow-up", messages.Add, (_, _) => Task.FromResult("拒否"), timeout.Token);
            Check(calls.SequenceEqual(new[] { "trial", "corrected" }));
            Check(messages.Any(m => m.Text == "verified") && messages.Any(m => m.Text == "same thread"));
        });
        await Test("Exceptional native approval still respects user denial", async () =>
        {
            await using var session = new CodexSession(Settings(Environment.ProcessPath!), 123); var asked = 0; var events = new List<AgentEvent>();
            await session.SendAsync("approval", events.Add, (_, _) => { asked++; return Task.FromResult("拒否"); }, CancellationToken.None);
            Check(asked == 1 && events.Any(e => e.Text == "decline"));
        });
        await Test("Codex stop uses turn interrupt and steering uses turn steer", async () =>
        {
            await using var session = new CodexSession(Settings(Environment.ProcessPath!), 123); using var cts = new CancellationTokenSource();
            var started = new TaskCompletionSource();
            var run = session.SendAsync("wait", e => { if (e.Text == "waiting") started.TrySetResult(); }, (_, _) => Task.FromResult("拒否"), cts.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await session.SteerAsync("追加指示", CancellationToken.None); cts.Cancel();
            try { await run; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { }
        });
        await Test("Native provider arguments retain tools and do not set reply schema", () =>
        {
            foreach (var provider in new[] { AiProvider.Claude, AiProvider.Gemini })
            {
                var args = NativeCliSession.Arguments(provider, "chosen-model", "session-123");
                Check(args.Contains("--resume") && args.Contains("session-123") && args.Contains("chosen-model"));
                Check(!args.Contains("--tools") && !args.Contains("--json-schema") && !args.Contains("--extensions"));
                if (provider == AiProvider.Claude)
                    Check(args[Array.IndexOf(args, "--permission-mode") + 1] == "auto" && !args.Contains("--dangerously-skip-permissions"));
            }
            return Task.CompletedTask;
        });
        await Test("Attachments keep independent snapshots and reject missing files", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "sw-attachment-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var source = Path.Combine(root, "図面 reference.txt"); await File.WriteAllTextAsync(source, "original");
                var first = await AttachmentStore.ImportAsync(root, [source], []);
                await File.WriteAllTextAsync(source, "changed");
                var second = await AttachmentStore.ImportAsync(root, [source], first);
                Check(File.ReadAllText(first[0].Path) == "original" && File.ReadAllText(second[0].Path) == "changed" && first[0].Path != second[0].Path);
                try { await AttachmentStore.ImportAsync(root, [source + ".missing"], []); throw new Exception("Expected missing file rejection"); }
                catch (FileNotFoundException) { }
                Check(File.ReadAllText(source) == "changed");
            }
            finally { Directory.Delete(root, true); }
        });
        await Test("Codex sends images as localImage and files as reference manifests", async () =>
        {
            await using var session = new CodexSession(Settings(Environment.ProcessPath!), 123);
            var events = new List<AgentEvent>();
            var message = new AgentMessage("attachments", [new("図面.png", @"C:\fixture\図面.png", 10), new("寸法.pdf", @"C:\fixture\寸法.pdf", 20)]);
            await session.SendAsync(message, events.Add, (_, _) => Task.FromResult("拒否"), CancellationToken.None);
            Check(events.Any(e => e.Text == "attachments verified"));
            Check(message.ToPrompt().Contains("reference files") && message.ToPrompt().Contains("fixture"));
        });
        await Test("Codex models use all pages, filter hidden entries and do not start a conversation", async () =>
        {
            var settings = Settings(Environment.ProcessPath!);
            await using var session = new CodexSession(settings, 123);
            var models = await session.ListModelsAsync(CancellationToken.None);
            Check(models.Select(x => x.Id).SequenceEqual(new[] { "fixture-model", "second-model" }));
            Check(models[0].DisplayName == "Fixture model");
            settings.Providers[AiProvider.Codex].Model = "second-model";
            var events = new List<AgentEvent>();
            await session.SendAsync("model-check", events.Add, (_, _) => Task.FromResult("拒否"), CancellationToken.None);
            Check(events.Any(x => x.Text == "second-model"));
            settings.Providers[AiProvider.Codex].Model = "fixture-model";
            events.Clear();
            await session.SendAsync("model-check", events.Add, (_, _) => Task.FromResult("拒否"), CancellationToken.None);
            Check(events.Any(x => x.Text == "fixture-model"));
            settings.Providers[AiProvider.Codex].Model = ""; events.Clear();
            await session.SendAsync("model-check", events.Add, (_, _) => Task.FromResult("拒否"), CancellationToken.None);
            Check(events.Any(x => x.Text == "fixture-default"));
        });
        await Test("Model discovery rejects looping cursors instead of hanging", async () =>
        {
            using var page = JsonDocument.Parse("{\"data\":[],\"nextCursor\":\"same\"}");
            try { await ModelCatalog.ReadPagesAsync((_, _) => Task.FromResult(page.RootElement), CancellationToken.None); throw new Exception("Expected rejection"); }
            catch (InvalidDataException) { }
        });
        return failed;
    }

    private static ChatSettings Settings(string executable)
    {
        var settings = new ChatSettings { ProjectDirectory = AppContext.BaseDirectory, CadExecutable = Environment.ProcessPath! };
        settings.Providers[AiProvider.Codex].Executable = executable; return settings;
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }

    public static async Task<int> ClaudeLive(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var settings = ChatSettings.Load(); settings.Provider = AiProvider.Claude;
        await using var session = new NativeCliSession(settings, int.Parse(args[0]));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var events = new List<AgentEvent>();
        await session.SendAsync("Read-only connection test authorized by the user. Use your native shell to run the supplied Windows CAD CLI with --help, then connect --solidworks-pid " + args[0] + " --json. Do not create, inspect, change, close or save any CAD document or edit any file. Do not change settings. Report the CLI exit code and actual JSON response. If a command fails for a non-permission reason, inspect the error and correct the invocation. Finish with a standalone line CONNECTION_OK only after the CLI returns connected=true for the supplied process. Respond in Japanese.",
            e => { events.Add(e); Console.WriteLine(e.Kind + ": " + e.Text); },
            (_, _) => throw new InvalidOperationException("No interactive approval handler"), timeout.Token);
        Console.WriteLine("Claude session: " + typeof(NativeCliSession).GetField("_session", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session));
        return events.Any(e => e.Kind == "message" && e.Text.Split('\n').Any(line => line.Trim() == "CONNECTION_OK")) ? 0 : 1;
    }

    public static async Task<int> Live(string[] args)
    {
        var settings = Settings(args[0]); settings.ProjectDirectory = args[1];
        var events = new List<AgentEvent>(); int calls = 0;
        await using var session = new CodexSession(settings, 123, (input, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? new ProcessResult(1, "", "Synthetic trial: requested size unsupported; inspect with arguments=[info], then use arguments=[create, corrected].")
                : new ProcessResult(0, "{\"synthetic\":true,\"available\":true,\"verified\":true}", ""));
        });
        using var token = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await session.SendAsync("This is a synthetic integration test. No real CAD or filesystem work is requested. Use only solidworks_cli: first arguments=[create, trial], stdin=null. If it fails, inspect via info then retry with corrected as instructed by the tool. Report the verified test result. Do not use other tools.", e => { events.Add(e); if(e.Kind != "delta") Console.WriteLine(e.Kind + ": " + e.Text); }, (_, _) => Task.FromResult("拒否"), token.Token);
        Console.WriteLine("Native tool calls: " + calls);
        return calls >= 3 && events.Any(e => e.Kind == "message") ? 0 : 1;
    }

    public static async Task<int> AttachmentLive(string[] args)
    {
        var settings = Settings(args[0]); settings.ProjectDirectory = args[1];
        var attachments = await AttachmentStore.ImportAsync(settings.ProjectDirectory, args.Skip(2), []);
        var messages = new List<string>();
        await using var session = new CodexSession(settings, 123, (_, _) => throw new InvalidOperationException("This test must not operate CAD."));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await session.SendAsync(new AgentMessage("Read-only attachment test: inspect the attached image visually and read the attached text file with your native file tools. Report the code printed inside the image, describe the colored shapes, and report the code from the text file. Respond in English. Do not change any files or access SOLIDWORKS. Do not guess unreadable contents.", attachments),
            e => { if (e.Kind == "message") { messages.Add(e.Text); Console.WriteLine(e.Text); } },
            (_, _) => Task.FromResult("拒否"), timeout.Token);
        var answer = string.Join("\n", messages);
        return answer.Contains("VISION-4829") && answer.Contains("REF-7392") ? 0 : 1;
    }

    public static async Task<int> ReviewLive(string[] args)
    {
        var settings = Settings(args[0]); settings.ProjectDirectory = args[1]; settings.CadExecutable = args[2];
        var events = new List<AgentEvent>(); int questions = 0;
        await using var session = new CodexSession(settings, int.Parse(args[3]));
        using var token = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await session.SendAsync("Verify a read-only Windows SOLIDWORKS connection using your NATIVE SHELL tool to invoke the supplied CAD executable with arguments: connect --solidworks-pid " + args[3] + " --json. Do not use solidworks_cli for this test. Use System.Diagnostics.ProcessStartInfo with FileName set to the CAD executable, Arguments set to the connect arguments, UseShellExecute=false, RedirectStandardOutput=true and RedirectStandardError=true; start the process, ReadToEndAsync both streams and WaitForExit, then report its exit code/output/error. Direct PowerShell invocation sometimes loses native-process output in this host. Do not inspect/read/create/change/save any CAD documents or source files. If COM cannot connect inside the sandbox, request the necessary escalation and retry. The user authorizes this connection check. Finish with CONNECTION_OK only after the CLI returned connected=true, otherwise report the actual error.",
            e => { events.Add(e); if (e.Kind != "delta") Console.WriteLine(e.Kind + ": " + e.Text); },
            (_, _) => { questions++; return Task.FromResult("拒否"); }, token.Token);
        Console.WriteLine("Manual approval requests: " + questions);
        return questions == 0 && events.Any(e => e.Kind == "message" && e.Text.Contains("CONNECTION_OK")) ? 0 : 1;
    }

    public static async Task<int> FixtureServer()
    {
        Console.InputEncoding = System.Text.Encoding.UTF8; Console.OutputEncoding = System.Text.Encoding.UTF8;
        int turns = 0; bool threadStarted = false; string pending = ""; string turn = "";
        void Send(object value) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
        void Event(string method, object parameters) => Send(new { method, @params = parameters });
        void Complete(string text)
        {
            Event("item/agentMessage/delta", new { threadId = "native-thread", itemId = "answer" + turns, delta = text });
            Event("item/completed", new { threadId = "native-thread", item = new { type = "agentMessage", id = "answer" + turns, text } });
            Event("turn/completed", new { threadId = "native-thread", turn = new { id = turn, status = "completed" } });
        }
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var doc = JsonDocument.Parse(line); var r = doc.RootElement;
            if (r.TryGetProperty("method", out var m))
            {
                var method = m.GetString(); if (method == "initialized") continue;
                var id = r.GetProperty("id").Clone(); var p = r.GetProperty("params");
                switch (method)
                {
                    case "initialize": Send(new { id, result = new { } }); break;
                    case "config/read": Send(new { id, result = new { config = new { model = "fixture-default" } } }); break;
                    case "model/list":
                        if (threadStarted || p.GetProperty("includeHidden").GetBoolean()) return 16;
                        var second = p.GetProperty("cursor").ValueKind == JsonValueKind.String;
                        var data = second
                            ? new[] { new { id="internal-b", model="second-model", displayName="Second model", hidden=false }, new { id="hidden", model="hidden", displayName="Hidden", hidden=true }, new { id="internal-a", model="fixture-model", displayName="Fixture model", hidden=false } }
                            : new[] { new { id="internal-a", model="fixture-model", displayName="Fixture model", hidden=false } };
                        Send(new { id, result = new { data, nextCursor = second ? null : "page-2" } }); break;
                    case "thread/start":
                        threadStarted = true;
                        if (p.GetProperty("approvalsReviewer").GetString() != "auto_review" || p.GetProperty("sandbox").GetString() != "workspace-write" || p.GetProperty("approvalPolicy").GetString() != "on-request") return 13;
                        if (p.TryGetProperty("baseInstructions", out _) || p.TryGetProperty("outputSchema", out _) || p.GetProperty("dynamicTools").GetArrayLength() != 1) return 9;
                        Send(new { id, result = new { thread = new { id = "native-thread" } } }); break;
                    case "turn/start":
                        if (p.GetProperty("approvalsReviewer").GetString() != "auto_review" || p.GetProperty("approvalPolicy").GetString() != "on-request") return 14;
                        if (p.GetProperty("threadId").GetString() != "native-thread") return 10;
                        turn = "turn" + ++turns;
                        Send(new { id, result = new { turn = new { id = turn } } });
                        var message = p.GetProperty("input")[0].GetProperty("text").GetString();
                        if (message?.StartsWith("attachments\n") == true)
                        {
                            var input = p.GetProperty("input");
                            if (input.GetArrayLength() != 2 || input[1].GetProperty("type").GetString() != "localImage" || input[1].GetProperty("path").GetString() != @"C:\fixture\図面.png" || !message.Contains(".pdf")) return 15;
                            Complete("attachments verified");
                        }
                        else if (message == "model-check") Complete(p.GetProperty("model").GetString()!);
                        else if (message == "make")
                        { pending = "trial"; Send(new { id = 900, method = "item/tool/call", @params = new { tool = "solidworks_cli", arguments = new { arguments = new[] { "trial" }, stdin = (string?)null } } }); }
                        else if (message == "approval")
                        { pending = "approval"; Send(new { id = 900, method = "item/commandExecution/requestApproval", @params = new { command = "fixture command", reason = "Fixture native approval" } }); }
                        else if (message == "wait") Event("item/agentMessage/delta", new { threadId = "native-thread", itemId = "wait", delta = "waiting" });
                        else Complete("same thread");
                        break;
                    case "turn/steer": Send(new { id, result = new { } }); break;
                    case "turn/interrupt": Send(new { id, result = new { } }); Event("turn/completed", new { threadId = "native-thread", turn = new { id = turn, status = "interrupted" } }); break;
                }
            }
            else if (r.TryGetProperty("result", out var result))
            {
                if (pending == "trial")
                {
                    if (result.GetProperty("success").GetBoolean() || !result.ToString().Contains("trial geometry failed")) return 11;
                    pending = "corrected";
                    Send(new { id = 901, method = "item/tool/call", @params = new { tool = "solidworks_cli", arguments = new { arguments = new[] { "corrected" }, stdin = (string?)null } } });
                }
                else if (pending == "corrected") { if (!result.GetProperty("success").GetBoolean()) return 12; Complete("verified"); }
                else if (pending == "approval") Complete(result.GetProperty("decision").GetString()!);
            }
        }
        return 0;
    }
}
