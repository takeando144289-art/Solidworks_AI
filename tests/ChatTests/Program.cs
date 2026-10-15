using System.Diagnostics;
using System.Text.Json;
using SolidWorksAgent.Chat;

if (args.FirstOrDefault() == "app-server") return await NativeSessionChecks.FixtureServer();
if (args.FirstOrDefault() == "--native-live") return await NativeSessionChecks.Live(args.Skip(1).ToArray());
if (args.FirstOrDefault() == "--echo")
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.InputEncoding = System.Text.Encoding.UTF8;
    Console.Write(JsonSerializer.Serialize(new { args = args.Skip(1).ToArray(), input = await Console.In.ReadToEndAsync() })); return 0;
}
if (args.FirstOrDefault() == "--hang") { await Task.Delay(Timeout.Infinite); return 0; }
if (args.FirstOrDefault() == "--flood") { Console.Out.Close(); Console.Error.Write(new string('x', 2_010_000)); return 0; }
if (args.Contains("fixture-model"))
{
    var prompt = await Console.In.ReadToEndAsync();
    if (!prompt.Contains("CONVERSATION") || !prompt.Contains("CURRENT CAD CONTEXT")) return 9;
    var answer = "{\"reply\":\"fixture OK\",\"commands\":[]}";
    if (args[0] == "exec") await File.WriteAllTextAsync(args[Array.IndexOf(args, "--output-last-message") + 1], answer);
    else if (args[0] == "-p") Console.Write("{\"structured_output\":" + answer + "}");
    else
    {
        var settingsPath = Environment.GetEnvironmentVariable("GEMINI_CLI_SYSTEM_SETTINGS_PATH");
        if (settingsPath is null || !File.ReadAllText(settingsPath).Contains("adminPolicyPaths")) return 10;
        Console.Write(JsonSerializer.Serialize(new { response = answer }));
    }
    return 0;
}
var fixture = """{"reply":"円柱を作成します。","commands":[{"command":"part create-cylinder","arguments":[{"name":"diameter","value":"30"},{"name":"length","value":"20"}]}]}""";
CadCommand Cylinder(params CommandArgument[] extras) => new("part create-cylinder", [new("diameter", "30"), new("length", "20"), .. extras]);
var tests = new List<(string, Action)>
{
    ("API method names do not make execution read-only", () => Check(!ChatIntents.IsReadOnly(new("api execute",[])))),
    ("API metadata discovery is read-only", () => Check(ChatIntents.IsReadOnly(new("api describe",[new("interface","IFeatureManager")])))),
    ("Plate accepts explicit thread positions", () => CommandCatalog.Validate(new("part create-plate",[new("width","150"),new("height","20"),new("thickness","3"),new("radius","5"),new("holes","[[10,10],[20,10]]"),new("thread","M3x0.5")]))),
    ("Drawing can use any active model without shaft parameters", () => CommandCatalog.Validate(new("drawing create", []))),
    ("General drawing supports assembly and large paper", () => CommandCatalog.Validate(new("drawing create", [new("model", "C:\\Parts\\assembly.SLDASM"),new("paper", "A0"),new("scale", "0.01"),new("views", "all")]))),
    ("Drawing rejects invalid scale", () => Reject(() => CommandCatalog.Validate(new("drawing create", [new("scale", "NaN")])))),
    ("Drawing creation is a mutation", () => Check(!ChatIntents.IsReadOnly(new("drawing create", [])))),
    ("Sketch mutations require inspected fingerprint", () => Reject(() => CommandCatalog.Validate(new("sketch fully-define", [new("sketch", "Sketch1")])))),
    ("Sketch inspections alone are read-only", () => Check(ChatIntents.IsReadOnly(new("sketch inspect", [])) && !ChatIntents.IsReadOnly(new("sketch fully-define", [])))),
    ("Fix is not a substitute for dimensions", () => Reject(() => CommandCatalog.Validate(new("sketch add-relation", [new("sketch", "Sketch1"), new("kind", "fixed"), new("entities", "s:0:1"), new("expected", new string('A',64))])))),
    ("Approval is a complete user message", () => Check(ChatIntents.Parse("実行して。") == ChatIntent.Execute)),
    ("Negation never approves", () => Check(ChatIntents.Parse("実行しないで") == ChatIntent.Message)),
    ("Quoted approval never approves", () => Check(ChatIntents.Parse("「実行して」と言えばいい？") == ChatIntent.Message)),
    ("Revised request never approves old plan", () => Check(ChatIntents.Parse("直径を20mmに変えて実行して") == ChatIntent.Message)),
    ("Overwrite requires explicit complete instruction", () => Check(ChatIntents.Parse("上書きを許可して実行") == ChatIntent.ExecuteWithOverwrite && ChatIntents.Parse("上書きは許可しないで実行して") == ChatIntent.Message)),
    ("Cancellation recognized", () => Check(ChatIntents.Parse("やめて") == ChatIntent.Cancel)),
    ("Inspectors are distinguished from mutations", () => Check(ChatIntents.IsReadOnly(new("info", [])) && !ChatIntents.IsReadOnly(Cylinder()) && !ChatIntents.IsReadOnly(new("view isometric", [])))),
    ("Valid plan and Unicode", () => Check(ChatProtocol.Parse(fixture).Commands.Length == 1)),
    ("Markdown JSON accepted", () => Check(ChatProtocol.Parse("```json\n" + fixture + "\n```").Commands.Length == 1)),
    ("Truncated response rejected", () => Reject(() => ChatProtocol.Parse(fixture[..^2]))),
    ("Missing commands rejected", () => Reject(() => ChatProtocol.Parse("{\"reply\":\"hello\"}"))),
    ("Unknown JSON fields rejected", () => Reject(() => ChatProtocol.Parse(fixture.Replace("\"reply\"", "\"shell\":\"evil\",\"reply\"")))),
    ("Unknown command rejected", () => Reject(() => CommandCatalog.Validate(new("powershell", [])))),
    ("Close/discard unavailable", () => Reject(() => CommandCatalog.Validate(new("documents close", [new("title", "user"), new("discard", "true")])))),
    ("AI cannot grant overwrite", () => Reject(() => CommandCatalog.Validate(Cylinder(new CommandArgument("overwrite", "true"))))),
    ("AI cannot select another PID", () => Reject(() => CommandCatalog.Validate(Cylinder(new CommandArgument("solidworks-pid", "1"))))),
    ("AI cannot change config", () => Reject(() => CommandCatalog.Validate(Cylinder(new CommandArgument("config", "other.json"))))),
    ("Duplicate parameters rejected", () => Reject(() => CommandCatalog.Validate(Cylinder(new CommandArgument("length", "1"))))),
    ("Null argument rejected", () => Reject(() => CommandCatalog.Validate(new("info", [null!])))),
    ("Missing dimension rejected", () => Reject(() => CommandCatalog.Validate(new("part create-cylinder", [new("length", "20")])))),
    ("NaN dimension rejected", () => Reject(() => CommandCatalog.Validate(new("part create-cylinder", [new("diameter", "NaN"), new("length", "20")])))),
    ("Infinite dimension rejected", () => Reject(() => CommandCatalog.Validate(new("part create-cylinder", [new("diameter", "1e999"), new("length", "20")])))),
    ("Tube thickness validated", () => Reject(() => CommandCatalog.Validate(new("part create-tube", [new("outer-diameter", "10"), new("inner-diameter", "12"), new("length", "20")])))),
    ("Shaft stage counts validated", () => Reject(() => CommandCatalog.Validate(new("part create-shaft", [new("diameters", "10,8"), new("lengths", "20")])))),
    ("Host selects exact process", () => Check(CommandCatalog.ToArguments(Cylinder(), 12345, false).Contains("12345"))),
    ("Overwrite requires host grant", () => Check(!CommandCatalog.ToArguments(Cylinder(), 12345, false).Contains("--overwrite") && CommandCatalog.ToArguments(Cylinder(), 12345, true).Contains("--overwrite"))),
    ("Invalid process rejected", () => Reject(() => CommandCatalog.ToArguments(Cylinder(), 0, false))),
    ("Claude structured response", () => Check(ChatProtocol.Parse(CliAiProvider.ExtractResponse(AiProvider.Claude, "{\"structured_output\":" + fixture + "}")).Commands.Length == 1)),
    ("Claude text response", () => Check(ChatProtocol.Parse(CliAiProvider.ExtractResponse(AiProvider.Claude, JsonSerializer.Serialize(new { result = fixture }))).Commands.Length == 1)),
    ("Gemini response", () => Check(ChatProtocol.Parse(CliAiProvider.ExtractResponse(AiProvider.Gemini, JsonSerializer.Serialize(new { response = fixture }))).Commands.Length == 1)),
    ("Provider errors rejected", () => Reject(() => CliAiProvider.ExtractResponse(AiProvider.Claude, "{\"is_error\":true,\"result\":\"no\"}"))),
    ("Codex schema and read-only mode", () => { var a = CliAiProvider.Arguments(AiProvider.Codex, "", "s", "o"); Check(a.Contains("--output-schema") && a.Contains("read-only") && a[^1] == "-"); }),
    ("Claude tools disabled", () => { var a = CliAiProvider.Arguments(AiProvider.Claude, "", "s", "o"); Check(a[Array.IndexOf(a, "--tools") + 1] == "" && a.Contains("--strict-mcp-config")); }),
    ("Gemini never uses yolo", () => Check(!CliAiProvider.Arguments(AiProvider.Gemini, "", "s", "o").Contains("yolo"))),
    ("Provider switch retains context", () => Check(ChatProtocol.Prompt([new("host", "Saved C:\\Parts\\shaft.SLDPRT")], "active") .Contains("shaft.SLDPRT"))),
    ("All commands validated before any execution", () => Reject(() => ChatProtocol.Parse(fixture[..^2] + ",{\"command\":\"evil\",\"arguments\":[]}]}")))
};
var failed = 0;
foreach (var (name, test) in tests)
{
    try { test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
}
foreach (var (name, test) in new (string, Func<Task>)[]
{
    ("Windows argument boundary and UTF8 stdin", async () =>
    {
        string[] values = ["", "C:\\日本語 空白\\part.SLDPRT", "a\"b", "$(bad) & | > %PATH%", "trailing\\"];
        var result = await ProcessRunner.RunAsync(ProcessRunner.StartInfo(Environment.ProcessPath!, ["--echo", .. values], AppContext.BaseDirectory), "直径30 mm", CancellationToken.None);
        using var json = JsonDocument.Parse(result.Output);
        Check(result.ExitCode == 0 && json.RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString()).SequenceEqual(values));
        Check(json.RootElement.GetProperty("input").GetString() == "直径30 mm");
    }),
    ("AI cancellation terminates process", async () =>
    {
        using var cts = new CancellationTokenSource(400);
        try { await ProcessRunner.RunAsync(ProcessRunner.StartInfo(Environment.ProcessPath!, ["--hang"], AppContext.BaseDirectory), null, cts.Token); throw new Exception("Did not cancel"); }
        catch (OperationCanceledException) { }
    }),
    ("Output bound after other stream ends", async () =>
    {
        using var cts = new CancellationTokenSource(5000);
        try { await ProcessRunner.RunAsync(ProcessRunner.StartInfo(Environment.ProcessPath!, ["--flood"], AppContext.BaseDirectory), null, cts.Token); throw new Exception("Did not limit"); }
        catch (IOException) { }
    })
})
{
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
}
foreach (var provider in Enum.GetValues<AiProvider>())
{
    try
    {
        var settings = new ChatSettings { Provider = provider };
        settings.Providers[provider] = new() { Executable = Environment.ProcessPath!, Model = "fixture-model" };
        var reply = await new CliAiProvider().ReplyAsync(settings, [new("user", "hello")], "CAD fixture", CancellationToken.None);
        Check(reply.Reply == "fixture OK" && reply.Commands.Length == 0);
        Console.WriteLine("PASS " + provider + " subprocess adapter fixture");
    }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + provider + ": " + ex.Message); }
}
Console.WriteLine($"{tests.Count + 6 - failed}/{tests.Count + 6} checks passed");
failed += await NativeSessionChecks.Run();
return failed == 0 ? 0 : 1;

static void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or InvalidOperationException) { return; } throw new Exception("Expected rejection"); }
