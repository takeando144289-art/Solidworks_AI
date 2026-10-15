using System.Runtime.InteropServices;
using SolidWorksAgent.Core.Drawing;
using SolidWorksAgent.SwApi.Interop;

namespace SolidWorksAgent.SwApi.Drawing;

/// <summary>Model-driven drawings without topology, stage-count or physical-size restrictions.</summary>
public sealed partial class GeneralDrawingBuilder(SwConnection connection)
{
    public sealed record DimensionResult(string Name, double Value, string Unit, string View, bool Dangling, bool Overridden);
    public object Create(string? modelPath, string drawingPath, string pdfPath, string template,
        string paper = "auto", double? scale = null, string views = "standard", string dimensions = "auto", bool overwrite = false)
    {
        if (views is not ("standard" or "all")) throw new ArgumentException("views は standard または all です。");
        if (dimensions is not ("auto" or "model" or "none")) throw new ArgumentException("dimensions は auto/model/none です。");
        drawingPath = Output(drawingPath, ".SLDDRW", overwrite); pdfPath = Output(pdfPath, ".PDF", overwrite);
        if (!File.Exists(template)) throw new FileNotFoundException("図面テンプレートがありません。", template);
        using var scope = new ComScope();
        object source;
        if (modelPath is null) source = scope.Own(ComDispatch.Get(connection.Application, "ActiveDoc"));
        else
        {
            modelPath = Path.GetFullPath(modelPath);
            if (!File.Exists(modelPath)) throw new FileNotFoundException("モデルがありません。", modelPath);
            int type = Path.GetExtension(modelPath).Equals(".sldasm", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            source = scope.Own(connection.OpenDocument([modelPath, type, 1, "", 0, 0]));
        }
        int sourceType = Convert.ToInt32(ComDispatch.Call(source, "GetType"));
        if (sourceType is not (1 or 2)) throw new InvalidOperationException("部品またはアセンブリをアクティブにしてください。");
        var sourceTitle = Convert.ToString(ComDispatch.Call(source, "GetTitle"))!;
        var originalPath = Convert.ToString(ComDispatch.Call(source, "GetPathName"))!;
        var sourceDirty = Convert.ToBoolean(ComDispatch.Call(source, "GetSaveFlag"));
        bool snapshot = sourceDirty || string.IsNullOrEmpty(originalPath);
        modelPath = originalPath;
        if (snapshot)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(drawingPath)!);
            // Save a new copy for stable drawing references; never save over the user's source document.
            modelPath = Path.Combine(Path.GetDirectoryName(drawingPath)!, Path.GetFileNameWithoutExtension(drawingPath) + "_source_" + Guid.NewGuid().ToString("N")[..8] + (sourceType == 1 ? ".SLDPRT" : ".SLDASM"));
            int errors = Convert.ToInt32(ComDispatch.Call(source, "SaveAs3", modelPath, 0, 3)); // Silent | Copy
            if (errors != 0 || !File.Exists(modelPath)) throw new IOException($"作業内容の図面用コピーを保存できませんでした（{errors}）。");
            if (Convert.ToString(ComDispatch.Call(source, "GetPathName")) != originalPath || Convert.ToBoolean(ComDispatch.Call(source, "GetSaveFlag")) != sourceDirty)
                throw new InvalidOperationException("元モデルの保存状態が変わったため停止しました。元モデルを確認してください。");
            source = scope.Own(connection.OpenDocument([modelPath, sourceType, 1, "", 0, 0]));
        }
        var configuration = scope.Own(ComDispatch.Get(scope.Own(ComDispatch.Get(source, "ConfigurationManager")), "ActiveConfiguration"));
        var configurationName = Convert.ToString(ComDispatch.Get(configuration, "Name"))!;
        var modelViews = Items(ComDispatch.Call(source, "GetModelViewNames")).Select(Convert.ToString).ToArray();
        var drawing = scope.Own(connection.CreateDocument(template, 0, 0d, 0d));
        var sheet = scope.Own(ComDispatch.Call(drawing, "GetCurrentSheet"));
        var sheetName = Convert.ToString(ComDispatch.Call(sheet, "GetName"))!;
        if (Items(ComDispatch.Call(drawing, "GetSheetNames")).Length != 1) throw new InvalidOperationException("1シートの図面テンプレートを指定してください。");
        var extension = scope.Own(ComDispatch.Get(drawing, "Extension"));
        ComDispatch.Call(extension, "SetUserPreferenceInteger", 263, 0, 5); // MMGS
        if (!Convert.ToBoolean(ComDispatch.Call(drawing, "SetupSheet5", sheetName, 12, 13, 1d, 1d, false, "", .420, .297, "", true)))
            throw new InvalidOperationException("図面シートを設定できませんでした。");
        if (!Convert.ToBoolean(ComDispatch.Call(drawing, "Create3rdAngleViews2", modelPath))) throw new InvalidOperationException("三面図を作成できませんでした。");
        var modelViewList = ModelViews(scope, drawing);
        if (modelViewList.Count != 3) throw new InvalidOperationException("三面図のビュー数が不正です。");
        var labels = new List<string> { "FRONT", "TOP", "RIGHT" };
        void AddView(string english, string japanese, string label)
        {
            var viewName = modelViews.FirstOrDefault(n => n is not null && (n.Contains(english, StringComparison.OrdinalIgnoreCase) || n.Contains(japanese))) ?? "*" + english;
            modelViewList.Add(scope.Own(ComDispatch.Call(drawing, "CreateDrawViewFromModelView3", modelPath, viewName, .2, .15, 0d)));
            labels.Add(label);
        }
        AddView("Isometric", "等角", "ISOMETRIC");
        if (views == "all") { AddView("Back", "背面", "BACK"); AddView("Left", "左側面", "LEFT"); AddView("Bottom", "底面", "BOTTOM"); }
        for (int i = 0; i < modelViewList.Count; i++)
        {
            var v = modelViewList[i]; ComDispatch.Call(v, "RemoveAlignment");
            ComDispatch.Set(v, "ReferencedConfiguration", configurationName);
            ComDispatch.Set(v, "UseSheetScale", false); ComDispatch.Set(v, "ScaleDecimal", 1d);
            ComDispatch.Call(v, "SetDisplayMode3", false, labels[i] == "ISOMETRIC" ? 2 : 1, false, false);
        }
        Rebuild(drawing);
        var sizes = modelViewList.Select(v => { var b = Bounds(v); return new DrawingLayout.Size((b[2]-b[0])*1000, (b[3]-b[1])*1000); }).ToArray();
        var plan = DrawingLayout.Fit(sizes, paper, scale);
        ComDispatch.Call(sheet, "SetProperties2", 12, 13, plan.Scale, 1d, false, plan.WidthMm/1000, plan.HeightMm/1000, false);
        for (int i = 0; i < modelViewList.Count; i++)
        {
            var view = modelViewList[i]; ComDispatch.Set(view, "ScaleDecimal", plan.Scale);
            Rebuild(drawing);
            var b = Bounds(view); var p = Numbers(ComDispatch.Get(view, "Position")); var cell = plan.Cells[i];
            ComDispatch.Set(view, "Position", new[] { p[0] + cell.Xmm/1000 - (b[0]+b[2])/2, p[1] + cell.Ymm/1000 - (b[1]+b[3])/2 });
        }
        Rebuild(drawing);
        var warnings = new List<string>();
        if (dimensions != "none")
        {
            ComDispatch.Call(drawing, "ClearSelection2", true);
            // Importing every construction-sketch dimension can swamp the finished-part view.
            // Default part drawings dimension visible geometry; model import is explicitly selectable.
            if (dimensions == "model" || sourceType == 2)
                ComDispatch.Call(drawing, "InsertModelAnnotations3", 0, 32768 | 524288, true, true, false, true);
            if (dimensions == "auto" && sourceType == 1) AddGeometryDimensions(scope, drawing, modelViewList.Where((_,i) => labels[i] != "ISOMETRIC").ToArray(), warnings);
            ArrangeDimensions(scope, drawing, modelViewList);
        }
        Rebuild(drawing);
        for (int i = 0; i < modelViewList.Count; i++)
        {
            var b = Bounds(modelViewList[i]); var cell = plan.Cells[i];
            if (b[0] < .005 || b[1] < .030 || b[2] > plan.WidthMm/1000-.005 || b[3] > plan.HeightMm/1000-.010)
                throw new InvalidOperationException("ビューが用紙の外に出ています。出力を保存せず停止しました。");
            Note(scope, drawing, labels[i], cell.Xmm - 12, cell.Ymm - cell.HeightMm/2 + 5, 3);
        }
        Note(scope, drawing, Path.GetFileNameWithoutExtension(sourceTitle).Replace("*", "").Trim(), 14, plan.HeightMm-8, 4);
        var scaleText = plan.Scale >= 1 ? $"{plan.Scale:g}:1" : $"1:{1/plan.Scale:g}";
        Note(scope, drawing, $"UNITS: mm  |  THIRD ANGLE  |  SCALE {scaleText}  |  {plan.Paper}  |  SHEET 1/1", 14, 24, 3);
        Note(scope, drawing, "TOLERANCES / MATERIAL / FINISH: AS SPECIFIED IN MODEL OR DESIGN REQUIREMENTS", 14, 16, 2.5);
        var checks = ReadDimensions(scope, drawing);
        if (checks.Any(d => d.Dangling || d.Overridden)) throw new InvalidOperationException("関連付けが失われた寸法または数値上書き寸法があるため保存を停止しました。");
        if (dimensions != "none" && checks.Length == 0) warnings.Add("モデルに取り込める寸法がなく、関連付け可能な寸法も検出できませんでした。図面ビューは作成済みです。必要な寸法位置を指定してください。");
        warnings.Add("寸法は選択した方式の検出可能な外形・円形状、またはモデル寸法を対象にしています。公差・はめあい・断面位置など、未指定の製造条件は追加していません。");
        ComDispatch.Call(drawing, "ClearSelection2", true); Rebuild(drawing);
        Save(drawing, drawingPath, true, overwrite);
        // Verify persisted references and dimensions, not only the API's save return value.
        ComDispatch.Call(connection.Application, "CloseDoc", ComDispatch.Call(drawing, "GetTitle"));
        drawing = scope.Own(connection.OpenDocument([drawingPath, 3, 1, "", 0, 0])); Rebuild(drawing);
        var persistedViews = ModelViews(scope, drawing);
        var persisted = ReadDimensions(scope, drawing);
        if (persistedViews.Count != modelViewList.Count || persisted.Length != checks.Length || persisted.Any(d => d.Dangling || d.Overridden))
            throw new InvalidOperationException("保存後のビュー・寸法を検証できませんでした。");
        var beforeValues = checks.Select(d => d.Value).Order().ToArray(); var afterValues = persisted.Select(d => d.Value).Order().ToArray();
        if (beforeValues.Where((v,i) => Math.Abs(v-afterValues[i]) > 1e-6).Any()) throw new InvalidOperationException("保存後に寸法値が変化しました。");
        Save(drawing, pdfPath, false, overwrite);
        ComDispatch.Call(drawing, "ViewZoomtofit2");
        return new { DrawingPath = drawingPath, PdfPath = pdfPath, ModelPath = modelPath, SourcePath = originalPath,
            SourceSnapshotCreated = snapshot, SourceDocumentPreserved = true, Configuration = configurationName,
            SheetCount = 1, Paper = plan.Paper, Scale = scaleText, Views = labels, ViewSizesMm = sizes, Dimensions = persisted,
            Warnings = warnings, ManufacturingSpecificationsComplete = false, RebuildSucceeded = true };
    }

    private static object[] Items(object? value) => value is Array a ? a.Cast<object>().ToArray() : [];
    private static double[] Numbers(object? value) => value is Array a ? a.Cast<object>().Select(Convert.ToDouble).ToArray() : throw new InvalidOperationException("図形データがありません。");
    private static double[] Bounds(object view) => Numbers(ComDispatch.Call(view, "GetOutline"));
    private static string Name(object value) => Convert.ToString(ComDispatch.Get(value, "Name"))!;
    private static List<object> ModelViews(ComScope scope, object drawing)
    {
        var result = new List<object>(); var sheetView = scope.Own(ComDispatch.Call(drawing, "GetFirstView"));
        var view = ComDispatch.Call(sheetView, "GetNextView");
        while (view is not null) { result.Add(scope.Own(view)); view = ComDispatch.Call(view, "GetNextView"); }
        return result;
    }
    private static void Rebuild(object drawing) { if (!Convert.ToBoolean(ComDispatch.Call(drawing, "EditRebuild3"))) throw new InvalidOperationException("図面の再構築に失敗しました。"); }
    private static string Output(string path, string extension, bool overwrite)
    {
        path = Path.GetFullPath(path);
        if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("出力の拡張子は " + extension + " です。");
        if (!overwrite && File.Exists(path)) throw new IOException("既存ファイルの上書きには --overwrite が必要です：" + path);
        return path;
    }
    private static void Save(object drawing, string path, bool native, bool overwrite)
    {
        Output(path, native ? ".SLDDRW" : ".PDF", overwrite); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ComDispatch.Call(drawing, "ClearSelection2", true);
        int errors = Convert.ToInt32(ComDispatch.Call(drawing, "SaveAs3", path, 0, 1));
        if (errors != 0 || !File.Exists(path) || new FileInfo(path).Length == 0) throw new IOException($"図面を保存できませんでした（{errors}）：{path}");
        if (native && !string.Equals(Convert.ToString(ComDispatch.Call(drawing, "GetPathName")), path, StringComparison.OrdinalIgnoreCase)) throw new IOException("保存先が一致しません。");
    }
    private static void Note(ComScope scope, object drawing, string text, double x, double y, double height)
    {
        ComDispatch.Call(drawing, "ClearSelection2", true); ComDispatch.Call(drawing, "ActivateView", "");
        var note = scope.Own(ComDispatch.Call(drawing, "InsertNote", text)); var annotation = scope.Own(ComDispatch.Call(note, "GetAnnotation"));
        var format = scope.Own(ComDispatch.Call(annotation, "GetTextFormat", 0)); ComDispatch.Set(format, "CharHeight", height/1000);
        ComDispatch.Call(annotation, "SetTextFormat", 0, false, format); ComDispatch.Call(annotation, "SetPosition2", x/1000, y/1000, 0d);
    }
    private static DimensionResult[] ReadDimensions(ComScope scope, object drawing) => ModelViews(scope, drawing).SelectMany(view =>
        Items(ComDispatch.Call(view, "GetDisplayDimensions")).Select(display =>
        {
            scope.Own(display); var d = scope.Own(ComDispatch.Call(display, "GetDimension2", 0)); var a = scope.Own(ComDispatch.Call(display, "GetAnnotation"));
            var type = Convert.ToInt32(ComDispatch.Call(d, "GetType")); var value = Convert.ToDouble(ComDispatch.Get(d, "SystemValue"));
            return new DimensionResult(Convert.ToString(ComDispatch.Get(d, "FullName"))!, type == 0 ? value*1000 : type == 1 ? value*180/Math.PI : value,
                type == 0 ? "mm" : type == 1 ? "deg" : "scalar", Name(view), Convert.ToBoolean(ComDispatch.Call(a, "IsDangling")), Convert.ToBoolean(ComDispatch.Call(display, "GetOverride")));
        })).ToArray();
    private sealed class ComScope : IDisposable
    {
        private readonly List<object> values = [];
        public object Own(object? value) { if (value is null) throw new InvalidOperationException("SOLIDWORKSオブジェクトを取得できませんでした。"); values.Add(value); return value; }
        public void Dispose() { foreach (var value in values.AsEnumerable().Reverse()) if (Marshal.IsComObject(value)) try { Marshal.ReleaseComObject(value); } catch (InvalidComObjectException) { } }
    }
}
