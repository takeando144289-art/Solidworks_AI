using SolidWorksAgent.Core.Automation;
using SolidWorksAgent.SwApi.Automation;
using System.Text.Json;
using System.Reflection;
using SolidWorksAgent.Core.Configuration;
using SolidWorksAgent.Core.Design;
using SolidWorksAgent.Core.Logging;
using SolidWorksAgent.SwApi;
using SolidWorksAgent.SwApi.Parts;
using SolidWorksAgent.SwApi.Assembly;
using SolidWorksAgent.SwApi.Drawing;
using SolidWorksAgent.Tools.Cli;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => Run(args);

static string? SketchOutput(Arguments parsed, AgentOptions options)
{
    var value = parsed.Value("--output");
    return value is null ? null : Path.IsPathRooted(value) ? value : Path.Combine(options.WorkspaceRoot, "parts", value);
}

static int Run(string[] args)
{
    var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
    try
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        var cwd = Directory.GetCurrentDirectory();
        var parsed = new Arguments(args);
        var options = AgentOptions.Load(parsed.Value("--config")).ResolvePaths(cwd);
        var log = new FileAgentLog(options.LogFile);
        using var connection = new SwConnection(log);
        if (parsed.Has("--start"))
            throw new ArgumentException("Automatic startup is disabled because this SolidWorks installation requires interactive authentication. Start and authenticate SolidWorks manually, then run the command without --start.");
        int? processId = null;
        if (parsed.Has("--solidworks-pid"))
        {
            if (!int.TryParse(parsed.Value("--solidworks-pid"), out var requestedPid) || requestedPid <= 0)
                throw new ArgumentException("--solidworks-pid requires a positive process ID.");
            processId = requestedPid;
        }
        if (args[0] == "api" && args.Length > 1 && args[1] is "search" or "describe")
        {
            var catalog = new ApiCatalog();
            object metadata = args[1] == "search" ? catalog.Search(parsed.Value("--query") ?? "", parsed.Value("--interface"), int.Parse(parsed.Value("--offset") ?? "0"))
                : catalog.Describe(parsed.Value("--interface") ?? throw new ArgumentException("--interface is required"), parsed.Value("--member"));
            Console.WriteLine(JsonSerializer.Serialize(metadata, JsonOptions)); return 0;
        }
        connection.ConnectToRunningSolidWorks(processId);
        bool managed=args[0] is "part" or "assembly" or "drawing" or "api" || (args[0]=="documents"&&args.Length>1&&args[1]=="open-final");
        using var documents=managed?connection.BeginDocumentSession(options.WorkspaceRoot):null;

        object result = args[0].ToLowerInvariant() switch
        {
            "api" when args.Length > 1 && args[1] == "execute" => ExecuteApi(connection, options, parsed),
            "part" when args.Length > 1 && args[1] == "create-plate" => CreatePlate(connection, options, parsed),
            "connect" => new { connected = true },
            "chat" when args.Length > 1 && args[1] == "load-addin" => connection.LoadChatAddin(parsed.Value("--path") ?? throw new ArgumentException("--path is required")),
            "chat" when args.Length > 1 && args[1] == "unload-addin" => connection.UnloadChatAddin(parsed.Value("--path") ?? throw new ArgumentException("--path is required")),
            "chat" when args.Length > 1 && args[1] == "check-connection" => connection.CheckChatConnection(),
            "sketch" when args.Length > 1 && args[1] == "verify" => new SketchEditor(connection).Verify(options.PartTemplate ?? throw new FileNotFoundException("Part template missing"), parsed.Value("--output") ?? throw new ArgumentException("--output is required")),
            "sketch" when args.Length > 1 && args[1] is "list" or "inspect" => new SketchEditor(connection).Inspect(parsed.Value("--sketch")),
            "sketch" when args.Length > 1 && args[1] == "fully-define" => new SketchEditor(connection).FullyDefine(parsed.Value("--sketch") ?? throw new ArgumentException("--sketch is required"), parsed.Value("--expected"), SketchOutput(parsed, options), parsed.Has("--overwrite")),
            "sketch" when args.Length > 1 && args[1] == "add-dimension" => new SketchEditor(connection).AddDimension(parsed.Value("--sketch") ?? throw new ArgumentException("--sketch is required"), parsed.Value("--kind") ?? throw new ArgumentException("--kind is required"), parsed.Value("--entities") ?? throw new ArgumentException("--entities is required"), parsed.Has("--value") ? parsed.RequiredDouble("--value") : null, parsed.Value("--expected"), SketchOutput(parsed, options), parsed.Has("--overwrite")),
            "sketch" when args.Length > 1 && args[1] == "add-relation" => new SketchEditor(connection).AddRelation(parsed.Value("--sketch") ?? throw new ArgumentException("--sketch is required"), parsed.Value("--kind") ?? throw new ArgumentException("--kind is required"), parsed.Value("--entities") ?? throw new ArgumentException("--entities is required"), parsed.Value("--expected"), SketchOutput(parsed, options), parsed.Has("--overwrite")),
            "info" => connection.GetActiveDocument(),
            "documents" when args.Length > 1 && args[1].Equals("list", StringComparison.OrdinalIgnoreCase) => connection.ListOpenDocuments(),
            "documents" when args.Length > 1 && args[1].Equals("close", StringComparison.OrdinalIgnoreCase) => CloseNamedDocument(connection,parsed),
            "documents" when args.Length > 1 && args[1].Equals("open-final", StringComparison.OrdinalIgnoreCase) => connection.OpenFinalDocument(parsed.Value("--path")??throw new ArgumentException("--path is required")),
            "documents" when args.Length > 1 && args[1].Equals("verify-cleanup", StringComparison.OrdinalIgnoreCase) => connection.VerifyDocumentCleanup(parsed.Value("--path")??throw new ArgumentException("--path is required"),options.DrawingTemplate??throw new FileNotFoundException("Drawing template not found."),options.WorkspaceRoot),
            "part" when args.Length > 1 && args[1].Equals("create-cylinder", StringComparison.OrdinalIgnoreCase)
                => CreateCylinder(connection, log, options, parsed),
            "part" when args.Length > 1 && args[1].Equals("inspect", StringComparison.OrdinalIgnoreCase)
                => parsed.Value("--model") is string inspectPath
                    ? new PartInspector(connection).InspectFile(inspectPath)
                    : new PartInspector(connection).InspectActivePart(),
            "part" when args.Length > 1 && args[1].Equals("create-shaft", StringComparison.OrdinalIgnoreCase)
                => CreateShaft(connection, log, options, parsed),
            "part" when args.Length > 1 && args[1].Equals("create-tube", StringComparison.OrdinalIgnoreCase)
                => CreateTube(connection, log, options, parsed),
            "assembly" when args.Length > 1 && args[1].Equals("create-bearing-test", StringComparison.OrdinalIgnoreCase)
                => CreateBearingAssembly(connection, log, options, parsed),
            "assembly" when args.Length > 1 && args[1].Equals("analyze", StringComparison.OrdinalIgnoreCase)
                => new AssemblyInspector(connection).AnalyzeActiveAssembly(),
            "view" when args.Length > 1 && args[1].Equals("isometric", StringComparison.OrdinalIgnoreCase)
                => SetIsometricView(connection),
            "drawing" when args.Length > 1 && args[1] == "create" => CreateGeneralDrawing(connection, options, parsed),
            "drawing" when args.Length > 1 && args[1].Equals("create-shaft", StringComparison.OrdinalIgnoreCase)
                => CreateShaftDrawing(connection, log, options, parsed),
            "drawing" when args.Length > 1 && args[1].Equals("inspect", StringComparison.OrdinalIgnoreCase)
                => new DrawingBuilder(connection, log).InspectDrawing(parsed.Value("--drawing") ?? throw new ArgumentException("--drawing is required")),
            "drawing" when args.Length > 1 && args[1].Equals("inspect-sheet-metal", StringComparison.OrdinalIgnoreCase)
                => new SheetMetalDrawingBuilder(connection).Inspect(parsed.Value("--model") ?? throw new ArgumentException("--model is required")),
            "drawing" when args.Length > 1 && args[1].Equals("prepare-sheet-metal", StringComparison.OrdinalIgnoreCase)
                => new SheetMetalDrawingBuilder(connection).Prepare(parsed.Value("--model")!, parsed.Value("--model-copy")!, parsed.Value("--output")!, options.DrawingTemplate!, parsed.Value("--probe")!),
            "drawing" when args.Length > 1 && args[1].Equals("dimension-sheet-metal", StringComparison.OrdinalIgnoreCase)
                => new SheetMetalDrawingBuilder(connection).Annotate(parsed.Value("--drawing")!,parsed.Value("--plan")!,parsed.Value("--output")!,parsed.Value("--pdf")!),
            "drawing" when args.Length > 1 && args[1].Equals("aux-sheet-metal", StringComparison.OrdinalIgnoreCase)
                => new SheetMetalDrawingBuilder(connection).AddAuxiliary(parsed.Value("--drawing")!,parsed.Value("--output")!,parsed.Value("--pdf")!,parsed.Value("--probe")!),
            "view" when args.Length > 1 && args[1].Equals("snapshot", StringComparison.OrdinalIgnoreCase)
                => new { path = connection.SaveActiveViewBitmap(parsed.Value("--output") ?? Path.Combine(options.WorkspaceRoot, "exports", "active-view.bmp")) },
            _ => throw new ArgumentException("Unknown command. Run 'swagent help' for usage.")
        };

        if(documents!=null) {
            bool creates=args.Length>1&&(args[1].StartsWith("create-",StringComparison.OrdinalIgnoreCase)||args[1] is "execute" or "create" or "prepare-sheet-metal" or "dimension-sheet-metal" or "aux-sheet-metal" or "open-final");
            var value=JsonSerializer.SerializeToElement(result);
            var finals=creates?new[]{"Drawing","DrawingPath","Path"}.Where(key=>value.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.String).Select(key=>value.GetProperty(key).GetString()!).Where(path=>new[]{".SLDPRT",".SLDASM",".SLDDRW"}.Contains(Path.GetExtension(path).ToUpperInvariant())).Take(1).ToArray():[];
            var cleanup=documents.Complete(finals,saveGeneratedChanges:creates&&args[1]!="open-final");
            var enriched=System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText())!.AsObject();
            enriched["DocumentCleanup"]=JsonSerializer.SerializeToNode(cleanup);result=enriched;
        }
        if (json) Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        else PrintHuman(result);
        return 0;
    }
    catch (Exception ex)
    {
        if (args.Contains("--diagnostic")) Console.Error.WriteLine(ex);
        var useful = Unwrap(ex);
        if (json) Console.Error.WriteLine(JsonSerializer.Serialize(new { error = useful.Message }, JsonOptions));
        else Console.Error.WriteLine($"Error: {useful.Message}");
        return 1;
    }
}

static Exception Unwrap(Exception exception)
{
    while (exception is TargetInvocationException && exception.InnerException is not null)
        exception = exception.InnerException;
    return exception;
}
static object CloseNamedDocument(SwConnection connection,Arguments args)
{
    if(!args.Has("--discard"))throw new ArgumentException("Use --discard to explicitly discard changes in the named document.");
    connection.CloseDocument(args.Value("--title")??throw new ArgumentException("--title is required"));return connection.ListOpenDocuments();
}

static object CreateCylinder(SwConnection connection, IAgentLog log, AgentOptions options, Arguments args)
{
    var spec = new CylinderSpec
    {
        DiameterMm = args.RequiredDouble("--diameter"),
        LengthMm = args.RequiredDouble("--length")
    };
    var requestedOutput = args.Value("--output") ?? "Cylinder.SLDPRT";
    var output = Path.IsPathRooted(requestedOutput)
        ? requestedOutput
        : Path.Combine(options.WorkspaceRoot, "parts", requestedOutput);
    var template = args.Value("--template") ?? options.PartTemplate
        ?? throw new FileNotFoundException("No SolidWorks part template was found. Pass --template <file.prtdot>.");
    return new PartBuilder(connection, log).CreateCylinder(spec, output, template, args.Has("--overwrite"));
}

static object CreateShaft(SwConnection connection, IAgentLog log, AgentOptions options, Arguments args)
{
    var diameters = args.RequiredDoubleList("--diameters");
    var lengths = args.RequiredDoubleList("--lengths");
    if (diameters.Count != lengths.Count) throw new ArgumentException("--diameters and --lengths must contain the same number of values.");
    var spec = new SteppedShaftSpec
    {
        Stages = diameters.Zip(lengths, (diameter, length) => new ShaftStage(diameter, length)).ToArray()
    };
    var requestedOutput = args.Value("--output") ?? "Shaft.SLDPRT";
    var output = Path.IsPathRooted(requestedOutput) ? requestedOutput : Path.Combine(options.WorkspaceRoot, "parts", requestedOutput);
    var template = args.Value("--template") ?? options.PartTemplate
        ?? throw new FileNotFoundException("No SolidWorks part template was found. Pass --template <file.prtdot>.");
    return new PartBuilder(connection, log).CreateSteppedShaft(spec, output, template, args.Has("--overwrite"));
}

static object CreateTube(SwConnection connection, IAgentLog log, AgentOptions options, Arguments args)
{
    var spec = new TubeSpec
    {
        OuterDiameterMm = args.RequiredDouble("--outer-diameter"),
        InnerDiameterMm = args.RequiredDouble("--inner-diameter"),
        LengthMm = args.RequiredDouble("--length")
    };
    var requestedOutput = args.Value("--output") ?? "Tube.SLDPRT";
    var output = Path.IsPathRooted(requestedOutput) ? requestedOutput : Path.Combine(options.WorkspaceRoot, "parts", requestedOutput);
    var template = args.Value("--template") ?? options.PartTemplate
        ?? throw new FileNotFoundException("No SolidWorks part template was found. Pass --template <file.prtdot>.");
    return new PartBuilder(connection, log).CreateTube(spec, output, template, args.Has("--overwrite"));
}

static object CreateBearingAssembly(SwConnection connection, IAgentLog log, AgentOptions options, Arguments args)
{
    string PartPath(string option) => Path.GetFullPath(args.Value(option) ?? throw new ArgumentException($"Missing required option: {option}"));
    var outputName = args.Value("--output") ?? "TestAssembly.SLDASM";
    var output = Path.IsPathRooted(outputName) ? outputName : Path.Combine(options.WorkspaceRoot, "assemblies", outputName);
    var template = args.Value("--template") ?? options.AssemblyTemplate
        ?? throw new FileNotFoundException("No SolidWorks assembly template was found. Pass --template <file.asmdot>.");
    return new AssemblyBuilder(connection, log).CreateBearingTestAssembly(
        PartPath("--housing"), PartPath("--bearing"), PartPath("--shaft"), output, template, args.Has("--overwrite"));
}

static object SetIsometricView(SwConnection connection)
{
    connection.SetIsometricView();
    return new { view = "Isometric", zoomToFit = true };
}

static object ExecuteApi(SwConnection connection, AgentOptions options, Arguments args)
{
    Console.InputEncoding = System.Text.Encoding.UTF8;
    var json = args.Has("--plan-stdin") ? Console.In.ReadToEnd() : args.Value("--plan") ?? throw new ArgumentException("--plan or --plan-stdin is required");
    var plan = ApiPlan.Parse(json);
    var name = args.Value("--output") ?? throw new ArgumentException("--output is required");
    var output = Path.IsPathRooted(name) ? name : Path.Combine(options.WorkspaceRoot, "api", name);
    return new ApiExecutor(connection).Execute(plan, options, output, args.Has("--overwrite"));
}
static object CreatePlate(SwConnection connection, AgentOptions options, Arguments args)
{
    var holes = JsonSerializer.Deserialize<double[][]>(args.Value("--holes") ?? "[]") ?? [];
    var plan = PlateRecipe.Create(args.RequiredDouble("--width"),args.RequiredDouble("--height"),args.RequiredDouble("--thickness"),
        args.Has("--radius") ? args.RequiredDouble("--radius") : 0, holes,args.Value("--thread") ?? "M3x0.5");
    var name = args.Value("--output") ?? "Plate_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".SLDPRT";
    var output = Path.IsPathRooted(name) ? name : Path.Combine(options.WorkspaceRoot,"parts",name);
    return new ApiExecutor(connection).Execute(plan,options,output,args.Has("--overwrite"));
}

static object CreateGeneralDrawing(SwConnection connection, AgentOptions options, Arguments args)
{
    var name = args.Value("--output") ?? "Drawing_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".SLDDRW";
    var drawing = Path.IsPathRooted(name) ? name : Path.Combine(options.WorkspaceRoot, "drawings", name);
    var pdfName = args.Value("--pdf") ?? Path.ChangeExtension(Path.GetFileName(name), ".pdf");
    var pdf = Path.IsPathRooted(pdfName) ? pdfName : Path.Combine(options.WorkspaceRoot, "exports", pdfName);
    return new GeneralDrawingBuilder(connection).Create(args.Value("--model"), drawing, pdf,
        args.Value("--template") ?? options.DrawingTemplate ?? throw new FileNotFoundException("Drawing template missing"),
        args.Value("--paper") ?? "auto", args.Has("--scale") ? args.RequiredDouble("--scale") : null,
        args.Value("--views") ?? "standard", args.Value("--dimensions") ?? "auto", args.Has("--overwrite"));
}

static object CreateShaftDrawing(SwConnection connection, IAgentLog log, AgentOptions options, Arguments args)
{
    var model = Path.GetFullPath(args.Value("--model") ?? throw new ArgumentException("Missing required option: --model"));
    var drawingName = args.Value("--output") ?? "Shaft.SLDDRW";
    var drawing = Path.IsPathRooted(drawingName) ? drawingName : Path.Combine(options.WorkspaceRoot, "drawings", drawingName);
    var pdfName = args.Value("--pdf") ?? Path.ChangeExtension(Path.GetFileName(drawingName), ".pdf");
    var pdf = Path.IsPathRooted(pdfName) ? pdfName : Path.Combine(options.WorkspaceRoot, "exports", pdfName);
    var template = args.Value("--template") ?? options.DrawingTemplate
        ?? throw new FileNotFoundException("No SolidWorks drawing template was found. Pass --template <file.drwdot>.");
    return new DrawingBuilder(connection, log).CreateShaftDrawing(
        model, drawing, pdf, template,
        args.Has("--length") ? args.RequiredDouble("--length") : null,
        args.Has("--diameter") ? args.RequiredDouble("--diameter") : null,
        args.Has("--tolerance") ? args.RequiredDouble("--tolerance") : null,
        args.Has("--overwrite"));
}

static void PrintHuman(object result)
{
    if (result is SolidWorksAgent.Core.Models.DocumentInfo info)
    {
        Console.WriteLine("SolidWorks connected");
        Console.WriteLine($"Active document: {info.Title ?? "(none)"}");
        Console.WriteLine($"Document type: {info.Type}");
        if (info.Path is not null) Console.WriteLine($"Path: {info.Path}");
    }
    else Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
}

static void PrintHelp() => Console.WriteLine("""
SolidWorks AI Agent CLI

Usage:
  All CAD commands accept --solidworks-pid <pid> to target a specific running instance.
  swagent api search --query <English-keyword> [--interface <IFeatureManager>] [--offset <n>] [--json]
  swagent api describe --interface <API-interface-or-enum> [--member <name>] [--json]
  swagent api execute --plan-stdin --output <new-CAD-file> [--overwrite] [--json]
  swagent part create-plate --width <mm> --height <mm> --thickness <mm> [--radius <mm>] [--holes <JSON-xy-array>] [--thread M3x0.5] [--output <new.SLDPRT>] [--json]
  swagent connect [--json]
  swagent sketch list|inspect [--sketch <name>] [--json]
  swagent sketch fully-define --sketch <name> [--expected <fingerprint>] [--output <new.SLDPRT>] [--overwrite] [--json]
  swagent sketch add-dimension --sketch <name> --kind <length|distance|horizontal|vertical|diameter|radius> --entities <ids> [--value <mm>] [--json]
  swagent sketch add-relation --sketch <name> --kind <horizontal|vertical|coincident|concentric|parallel|perpendicular|tangent|equal|midpoint|collinear> --entities <ids> [--json]
  swagent chat check-connection [--solidworks-pid <pid>] [--json]
  swagent chat load-addin --path <SolidWorksAgent.ChatAddin.V5.comhost.dll> [--json]
  swagent chat unload-addin --path <SolidWorksAgent.ChatAddin.V5.comhost.dll> [--json]
  swagent info [--json]
  swagent part create-cylinder --diameter <mm> --length <mm>
      [--output <name.SLDPRT>] [--template <file.prtdot>] [--overwrite]
      [--json]
  swagent documents list [--json]
  swagent documents open-final --path <file.SLDDRW> [--json]
  swagent documents close --title <exact-title> --discard [--json]
  swagent documents verify-cleanup --path <final.SLDDRW> [--json]
  swagent part inspect [--model <part.SLDPRT>] [--json]
  swagent part create-shaft --diameters <d1,d2,...> --lengths <l1,l2,...>
      [--output <name.SLDPRT>] [--json]
  swagent part create-tube --outer-diameter <mm> --inner-diameter <mm> --length <mm>
      [--output <name.SLDPRT>] [--json]
  swagent assembly create-bearing-test --housing <part> --bearing <part> --shaft <part>
      [--output <name.SLDASM>] [--json]
  swagent assembly analyze [--json]
  swagent view isometric [--json]
  swagent view snapshot [--output <image.bmp>] [--json]
  swagent drawing create [--model <part.SLDPRT|assembly.SLDASM>] [--paper auto|A4|A3|A2|A1|A0]
      [--scale <ratio>] [--views standard|all] [--dimensions auto|model|none] [--output <drawing.SLDDRW>] [--pdf <drawing.pdf>] [--overwrite] [--json]
  swagent drawing create-shaft --model <shaft.SLDPRT>
      [--length <expected mm>] [--diameter <expected max mm>]
      [--tolerance <explicit symmetric mm>] [--output <drawing.SLDDRW>]
      [--pdf <drawing.pdf>] [--overwrite] [--json]
  swagent drawing inspect --drawing <drawing.SLDDRW> [--json]

Start SolidWorks and complete interactive authentication before running swagent.
Generated parts default to workspace/parts and existing files are never overwritten.
""");

static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
