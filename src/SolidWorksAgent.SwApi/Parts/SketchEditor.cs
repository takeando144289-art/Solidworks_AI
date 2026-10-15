using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SolidWorksAgent.SwApi.Interop;

namespace SolidWorksAgent.SwApi.Parts;

/// <summary>Edits constraints on an existing part sketch; never recreates its geometry.</summary>
public sealed partial class SketchEditor(SwConnection connection)
{
    public sealed record Entity(string Id, string Kind, bool Construction, double[] GeometryMm, int Status);
    public sealed record DimensionInfo(string Name, int Type, double Value, string Unit, int DrivenState);
    public sealed record RelationInfo(int Type, int EntityCount);
    public sealed record SketchInfo(string Name, bool Is3D, int Status, bool FullyDefined, Entity[] Entities,
        DimensionInfo[] Dimensions, RelationInfo[] Relations, string Fingerprint);
    public static readonly IReadOnlyDictionary<string, int> RelationTypes = new Dictionary<string, int>
    {
        ["horizontal"] = 4, ["vertical"] = 5, ["tangent"] = 6, ["parallel"] = 7,
        ["perpendicular"] = 8, ["coincident"] = 9, ["concentric"] = 10,
        ["midpoint"] = 12, ["equal"] = 14, ["collinear"] = 27
    };
    public static readonly string[] DimensionKinds = ["length", "distance", "horizontal", "vertical", "diameter", "radius"];

    public object Inspect(string? sketchName = null)
    {
        using var scope = new Scope(connection.Application);
        var sketches = FindSketches(scope, scope.Model);
        if (!string.IsNullOrWhiteSpace(sketchName)) sketches = sketches.Where(f => Name(f) == sketchName).ToList();
        if (sketches.Count == 0) throw new InvalidOperationException("対象のスケッチがありません。部品とスケッチ名を確認してください。");
        return new { Document = Title(scope.Model), Modified = Convert.ToBoolean(ComDispatch.Call(scope.Model, "GetSaveFlag")), Sketches = sketches.Select(f => Snapshot(scope, f)).ToArray() };
    }

    public object FullyDefine(string sketchName, string? expected = null, string? output = null, bool overwrite = false) =>
        Edit(sketchName, expected, output, overwrite, true, (scope, feature, sketch, manager, before) =>
        {
            if (before.FullyDefined) return;
            // Select the document origin as both datums (marks 2 | 4), as required by FullyDefineSketch.
            var extension = scope.Own(ComDispatch.Get(scope.Model, "Extension"));
            object? origin = ComDispatch.Call(scope.Model, "FirstFeature");
            while (origin is not null && Convert.ToString(ComDispatch.Call(origin, "GetTypeName2")) != "OriginProfileFeature")
            { scope.Own(origin); origin = ComDispatch.Call(origin, "GetNextFeature"); }
            if (origin is null) throw new InvalidOperationException("原点を取得できませんでした。");
            scope.Own(origin);
            if (!Convert.ToBoolean(ComDispatch.Call(extension, "SelectByID2", "Point1@" + Name(origin), "EXTSKETCHPOINT", 0d, 0d, 0d, false, 6, new DispatchWrapper(null), 0)))
                throw new InvalidOperationException("寸法の基準原点を選択できませんでした。");
            // Geometric relations only (no Fix), plus editable driving dimensions.
            ComDispatch.Call(manager, "FullyDefineSketch", true, true, 1023, true, 1, new DispatchWrapper(null), 1, new DispatchWrapper(null), 0, 0);
        });

    public object AddDimension(string sketchName, string kind, string entities, double? valueMm = null,
        string? expected = null, string? output = null, bool overwrite = false)
    {
        if (!DimensionKinds.Contains(kind)) throw new ArgumentException("未対応の寸法種類です。");
        if (valueMm.HasValue && (!double.IsFinite(valueMm.Value) || valueMm <= 0)) throw new ArgumentException("寸法は正の有限数(mm)で指定してください。");
        return Edit(sketchName, expected, output, overwrite, false, (scope, feature, sketch, manager, before) =>
        {
            var selected = ResolveEntities(scope, sketch, entities);
            if (selected.Count is < 1 or > 2) throw new ArgumentException("寸法の対象は1〜2要素です。");
            var ids = entities.Split(',', StringSplitOptions.TrimEntries);
            var kinds = ids.Select(id => before.Entities.Single(e => e.Id == id).Kind).ToArray();
            if (kind is "diameter" or "radius" && (kinds.Length != 1 || kinds[0] is not ("circle" or "arc")))
                throw new ArgumentException("半径・直径寸法は円または円弧1個を指定してください。");
            if (kind == "length" && (kinds.Length != 1 || kinds[0] != "line")) throw new ArgumentException("長さ寸法は線分1本を指定してください。");
            foreach (var entity in selected) if (!Convert.ToBoolean(ComDispatch.Call(entity, "Select4", true, new DispatchWrapper(null))))
                throw new InvalidOperationException("寸法の対象を選択できませんでした。");
            var method = kind switch { "horizontal" => "AddHorizontalDimension2", "vertical" => "AddVerticalDimension2", "diameter" => "AddDiameterDimension2", "radius" => "AddRadialDimension2", _ => "AddDimension2" };
            var points = before.Entities.SelectMany(e => Coordinates(e)).ToArray();
            var x = points.Length == 0 ? 0.01 : points.Max(p => p[0]) / 1000d + 0.01;
            var y = points.Length == 0 ? 0.01 : points.Max(p => p[1]) / 1000d + 0.01;
            // Annotation coordinates are in model space, while inspection uses sketch coordinates.
            var transform = scope.Own(ComDispatch.Get(sketch, "ModelToSketchTransform"));
            var inverse = scope.Own(ComDispatch.Call(transform, "Inverse"));
            var math = scope.Own(ComDispatch.Call(connection.Application, "GetMathUtility"));
            var point = scope.Own(ComDispatch.Call(math, "CreatePoint", (object)new[] { x, y, 0d }));
            var modelPoint = scope.Own(ComDispatch.Call(point, "MultiplyTransform", inverse));
            var xyz = (Array)ComDispatch.Get(modelPoint, "ArrayData")!;
            var display = scope.Own(ComDispatch.Call(scope.Model, method, Convert.ToDouble(xyz.GetValue(0)), Convert.ToDouble(xyz.GetValue(1)), Convert.ToDouble(xyz.GetValue(2))));
            var dimension = scope.Own(ComDispatch.Call(display, "GetDimension2", 0));
            if (Convert.ToInt32(ComDispatch.Call(dimension, "GetType")) != 0)
                throw new InvalidOperationException("線寸法として追加できませんでした。平行な線分または点を指定してください。");
            if (Convert.ToInt32(ComDispatch.Get(dimension, "DrivenState")) != 2)
                throw new InvalidOperationException("駆動寸法を追加できませんでした。既存の寸法・拘束と競合しています。");
            if (valueMm.HasValue)
            {
                var current = Convert.ToDouble(ComDispatch.Get(dimension, "SystemValue"));
                if (Math.Abs(current * 1000d - valueMm.Value) > 0.001)
                    throw new InvalidOperationException("指定寸法が現在の形状と一致しません。形状を変えずに寸法を追加する操作なので取り消します。");
                var result = Convert.ToInt32(ComDispatch.Call(dimension, "SetSystemValue3", valueMm.Value / 1000d, 1, null));
                if (result != 0) throw new InvalidOperationException($"寸法値を設定できませんでした（{result}）。");
            }
        });
    }

    public object AddRelation(string sketchName, string kind, string entities, string? expected = null, string? output = null, bool overwrite = false)
    {
        if (!RelationTypes.TryGetValue(kind, out var type)) throw new ArgumentException("未対応の幾何拘束です。");
        return Edit(sketchName, expected, output, overwrite, false, (scope, feature, sketch, manager, before) =>
        {
            var selected = ResolveEntities(scope, sketch, entities);
            if (selected.Count != (kind is "horizontal" or "vertical" ? 1 : 2)) throw new ArgumentException("拘束の対象要素数が正しくありません。");
            var relations = scope.Own(ComDispatch.Get(sketch, "RelationManager"));
            scope.Own(ComDispatch.Call(relations, "AddRelation", (object)selected.Select(entity => new DispatchWrapper(entity)).ToArray(), type));
        });
    }

    private object Edit(string name, string? expected, string? output, bool overwrite, bool requireFullyDefined,
        Action<Scope, object, object, object, SketchInfo> change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var scope = new Scope(connection.Application);
        output = ValidateOutput(scope.Model, output, overwrite);
        var feature = FindSketches(scope, scope.Model).SingleOrDefault(f => Name(f) == name)
            ?? throw new InvalidOperationException("指定したスケッチがありません：" + name);
        var sketch = scope.Own(ComDispatch.Call(feature, "GetSpecificFeature2"));
        var before = Snapshot(scope, feature);
        var bodiesBefore = BodyGeometry(scope);
        if (before.Is3D || before.Entities.Any(e => e.Kind is not ("line" or "arc" or "circle" or "point")))
            throw new InvalidOperationException("現在は部品の2Dスケッチ（線分・円・円弧・点）に対応しています。");
        if (before.Status is not (2 or 3)) throw new InvalidOperationException("スケッチに既存の拘束エラーがあります。先に解消してください。");
        if (expected is not null && expected != before.Fingerprint) throw new InvalidOperationException("スケッチが確認時から変わっています。再確認してから実行してください。");
        var extension = scope.Own(ComDispatch.Get(scope.Model, "Extension"));
        var manager = scope.Own(ComDispatch.Get(scope.Model, "SketchManager"));
        var active = ComDispatch.Call(scope.Model, "GetActiveSketch2");
        bool entered = active is null;
        if (active is not null) { scope.Own(active); if (!Same(active, sketch)) throw new InvalidOperationException("別のスケッチを編集中です。編集を終了してから実行してください。"); }
        var preferences = new[] { 10, 100, 101, 642 }; // Input value, overdefined prompt/driven fallback, first-dimension scaling.
        var previous = preferences.Select(p => Convert.ToBoolean(ComDispatch.Call(connection.Application, "GetUserPreferenceToggle", p))).ToArray();
        var relationsManager = scope.Own(ComDispatch.Get(sketch, "RelationManager"));
        var originalRelations = Items(ComDispatch.Call(relationsManager, "GetRelations", 0)).Select(scope.Own).ToArray();
        bool closed = false, saving = false;
        try
        {
            if (entered)
            {
                ComDispatch.Call(scope.Model, "ClearSelection2", true);
                if (!Convert.ToBoolean(ComDispatch.Call(feature, "Select2", false, 0))) throw new InvalidOperationException("スケッチを選択できません。");
                ComDispatch.Call(scope.Model, "EditSketch");
                var editing = scope.Own(ComDispatch.Call(scope.Model, "GetActiveSketch2"));
                if (!Same(editing, sketch)) throw new InvalidOperationException("対象スケッチの編集を開始できません。");
            }
            for (var i = 0; i < preferences.Length; i++) ComDispatch.Call(connection.Application, "SetUserPreferenceToggle", preferences[i], false);
            ComDispatch.Call(scope.Model, "ClearSelection2", true);
            change(scope, feature, sketch, manager, before);
            if (!Convert.ToBoolean(ComDispatch.Call(scope.Model, "EditRebuild3"))) throw new InvalidOperationException("スケッチの再構築に失敗しました。");
            var after = Snapshot(scope, feature);
            CheckGeometry(before, after);
            if (after.Status is not (2 or 3)) throw new InvalidOperationException("拘束が競合するため取り消します。");
            if (requireFullyDefined && !after.FullyDefined) throw new InvalidOperationException("完全定義できませんでした。追加した寸法・拘束を取り消します。");
            if (!requireFullyDefined && after.Dimensions.Length <= before.Dimensions.Length && after.Relations.Length <= before.Relations.Length)
                throw new InvalidOperationException("寸法・拘束が追加されませんでした。既存の定義と重複している可能性があります。");
            if (entered) { ComDispatch.Call(manager, "InsertSketch", true); closed = true; }
            if (!Convert.ToBoolean(ComDispatch.Call(scope.Model, "EditRebuild3"))) throw new InvalidOperationException("変更後のモデルの再構築に失敗しました。");
            // Re-read after downstream features rebuild. Success is based on geometry and solver status, not API return code.
            after = Snapshot(scope, feature); CheckGeometry(before, after);
            CheckBodies(bodiesBefore, BodyGeometry(scope));
            if (after.Status is not (2 or 3) || (requireFullyDefined && !after.FullyDefined)) throw new InvalidOperationException("再構築後の拘束状態が不正です。");
            if (output is not null) { saving = true; Save(scope.Model, output, overwrite); }
            return new { Document = Title(scope.Model), Sketch = name, Saved = output is not null, Output = output,
                Message = output is null ? "寸法・拘束を追加しました。変更は開いているドキュメントに保持されています（未保存）。" : "寸法・拘束を追加して保存しました。",
                GeometryPreserved = true, Before = before, After = after,
                AddedDimensions = after.Dimensions.Length - before.Dimensions.Length, AddedRelations = after.Relations.Length - before.Relations.Length };
        }
        catch (Exception error)
        {
            if (saving) throw new IOException("寸法・拘束の追加は完了しましたが保存を検証できませんでした。出力ファイルを確認してください。" + error.Message, error);
            // External-process API undo grouping is not available in every SOLIDWORKS version.
            // Remove only additions identified against our retained original objects, then restore coordinates.
            if (Snapshot(scope, feature).Fingerprint != before.Fingerprint)
            {
                try
                {
                    if (closed) { ComDispatch.Call(feature, "Select2", false, 0); ComDispatch.Call(scope.Model, "EditSketch"); closed = false; }
                    Restore(scope, feature, sketch, before, originalRelations);
                }
                catch (Exception rollback) { throw new InvalidOperationException("変更の取り消しを検証できませんでした。ドキュメントは未保存です：" + rollback.Message + " 元のエラー：" + error.Message, error); }
                throw new InvalidOperationException("操作を取り消しました。" + error.Message, error);
            }
            throw;
        }
        finally
        {
            for (var i = 0; i < preferences.Length; i++) ComDispatch.Call(connection.Application, "SetUserPreferenceToggle", preferences[i], previous[i]);
            if (entered && !closed)
            {
                var editing = ComDispatch.Call(scope.Model, "GetActiveSketch2");
                if (editing is not null) { if (Same(editing, sketch)) ComDispatch.Call(manager, "InsertSketch", true); Release(editing); }
            }
            ComDispatch.Call(scope.Model, "ClearSelection2", true);
        }
    }

    private static void Restore(Scope scope, object feature, object sketch, SketchInfo before, object[] originalRelations)
    {
        ComDispatch.Call(scope.Model, "SetPickMode");
        var editing = ComDispatch.Call(scope.Model, "GetActiveSketch2");
        if (editing is null) { ComDispatch.Call(feature, "Select2", false, 0); ComDispatch.Call(scope.Model, "EditSketch"); }
        else scope.Own(editing);
        var extension = scope.Own(ComDispatch.Get(scope.Model, "Extension"));
        var additions = new List<object>();
        var display = ComDispatch.Call(feature, "GetFirstDisplayDimension");
        while (display is not null)
        {
            scope.Own(display); var dimension = scope.Own(ComDispatch.Call(display, "GetDimension2", 0));
            if (!before.Dimensions.Any(d => d.Name == Convert.ToString(ComDispatch.Get(dimension, "FullName")))) additions.Add(display);
            display = ComDispatch.Call(feature, "GetNextDisplayDimension", display);
        }
        foreach (var addition in additions)
        {
            var selectionName = Convert.ToString(ComDispatch.Call(addition, "GetNameForSelection"));
            ComDispatch.Call(scope.Model, "ClearSelection2", true);
            if (!Convert.ToBoolean(ComDispatch.Call(extension, "SelectByID2", selectionName, "DIMENSION", 0d, 0d, 0d, false, 0, new DispatchWrapper(null), 0)))
                throw new InvalidOperationException("追加寸法を選択できませんでした。");
            ComDispatch.Call(scope.Model, "EditDelete");
        }
        var manager = scope.Own(ComDispatch.Get(sketch, "RelationManager"));
        foreach (var relation in Items(ComDispatch.Call(manager, "GetRelations", 0)))
        {
            scope.Own(relation);
            if (!originalRelations.Any(original => Same(original, relation)) && !Convert.ToBoolean(ComDispatch.Call(manager, "DeleteRelation", relation)))
                throw new InvalidOperationException("追加拘束を削除できませんでした。");
        }
        foreach (var point in Items(ComDispatch.Call(sketch, "GetSketchPoints2")))
        {
            scope.Own(point); var original = before.Entities.Single(e => e.Id == Id(point, "p"));
            if (PointValues(point).Where((v, i) => Math.Abs(v - original.GeometryMm[i]) > 1e-8).Any())
                ComDispatch.Call(point, "SetCoords", original.GeometryMm[0] / 1000d, original.GeometryMm[1] / 1000d, original.GeometryMm[2] / 1000d);
        }
        foreach (var arc in Items(ComDispatch.Call(sketch, "GetSketchSegments")))
        {
            scope.Own(arc); var original = before.Entities.Single(e => e.Id == Id(arc, "s"));
            if (original.Kind is "circle" or "arc")
            {
                var radius = original.GeometryMm[original.Kind == "circle" ? 3 : 9];
                if (Math.Abs(Convert.ToDouble(ComDispatch.Call(arc, "GetRadius")) * 1000d - radius) > 1e-8)
                    if (!Convert.ToBoolean(ComDispatch.Call(arc, "SetRadius", radius / 1000d))) throw new InvalidOperationException("半径を復元できませんでした。");
            }
        }
        ComDispatch.Call(scope.Model, "EditRebuild3");
        var restored = Snapshot(scope, feature); CheckGeometry(before, restored);
        if (restored.Status != before.Status || !restored.Dimensions.OrderBy(d => d.Name).SequenceEqual(before.Dimensions.OrderBy(d => d.Name)) || restored.Relations.Length != before.Relations.Length)
            throw new InvalidOperationException("元の寸法・拘束状態を復元できませんでした。");
    }

    private static List<object> FindSketches(Scope scope, object model)
    {
        var result = new List<object>(); var seen = new HashSet<int>();
        void Walk(object? first, bool sub)
        {
            var current = first;
            while (current is not null)
            {
                scope.Own(current);
                var id = Convert.ToInt32(ComDispatch.Call(current, "GetID"));
                if (seen.Add(id))
                {
                    var type = Convert.ToString(ComDispatch.Call(current, "GetTypeName2"));
                    if (type is "ProfileFeature" or "3DProfileFeature") result.Add(current);
                    Walk(ComDispatch.Call(current, "GetFirstSubFeature"), true);
                }
                current = ComDispatch.Call(current, sub ? "GetNextSubFeature" : "GetNextFeature");
            }
        }
        Walk(ComDispatch.Call(model, "FirstFeature"), false);
        return result;
    }
    private static SketchInfo Snapshot(Scope scope, object feature)
    {
        var sketch = scope.Own(ComDispatch.Call(feature, "GetSpecificFeature2"));
        var entities = new List<Entity>();
        foreach (var segment in Items(ComDispatch.Call(sketch, "GetSketchSegments")))
        {
            scope.Own(segment); var type = Convert.ToInt32(ComDispatch.Call(segment, "GetType"));
            string kind = type switch { 0 => "line", 1 => Convert.ToInt32(ComDispatch.Call(segment, "IsCircle")) != 0 ? "circle" : "arc", _ => "unsupported:" + type };
            var geometry = new List<double>();
            if (type == 0 || kind == "arc") { geometry.AddRange(Point(scope, ComDispatch.Call(segment, "GetStartPoint2"))); geometry.AddRange(Point(scope, ComDispatch.Call(segment, "GetEndPoint2"))); }
            if (type == 1) { geometry.AddRange(Point(scope, ComDispatch.Call(segment, "GetCenterPoint2"))); geometry.Add(Convert.ToDouble(ComDispatch.Call(segment, "GetRadius")) * 1000d); geometry.Add(Convert.ToDouble(ComDispatch.Call(segment, "GetLength")) * 1000d); }
            entities.Add(new(Id(segment, "s"), kind, Convert.ToBoolean(ComDispatch.Get(segment, "ConstructionGeometry")), geometry.ToArray(), Convert.ToInt32(ComDispatch.Get(segment, "Status"))));
        }
        foreach (var point in Items(ComDispatch.Call(sketch, "GetSketchPoints2")))
        {
            scope.Own(point); entities.Add(new(Id(point, "p"), "point", false, PointValues(point), Convert.ToInt32(ComDispatch.Get(point, "Status"))));
        }
        var dimensions = new List<DimensionInfo>();
        var display = ComDispatch.Call(feature, "GetFirstDisplayDimension");
        while (display is not null)
        {
            scope.Own(display); var dimension = scope.Own(ComDispatch.Call(display, "GetDimension2", 0));
            var parameterType = Convert.ToInt32(ComDispatch.Call(dimension, "GetType"));
            var systemValue = Convert.ToDouble(ComDispatch.Get(dimension, "SystemValue"));
            dimensions.Add(new(Convert.ToString(ComDispatch.Get(dimension, "FullName"))!, parameterType,
                parameterType == 0 ? systemValue * 1000d : parameterType == 1 ? systemValue * 180d / Math.PI : systemValue,
                parameterType == 0 ? "mm" : parameterType == 1 ? "deg" : "scalar", Convert.ToInt32(ComDispatch.Get(dimension, "DrivenState"))));
            display = ComDispatch.Call(feature, "GetNextDisplayDimension", display);
        }
        var manager = scope.Own(ComDispatch.Get(sketch, "RelationManager"));
        var relations = Items(ComDispatch.Call(manager, "GetRelations", 0)).Select(r => { scope.Own(r); return new RelationInfo(Convert.ToInt32(ComDispatch.Call(r, "GetRelationType")), Convert.ToInt32(ComDispatch.Call(r, "GetEntitiesCount"))); }).ToArray();
        var status = Convert.ToInt32(ComDispatch.Call(sketch, "GetConstrainedStatus"));
        var sorted = entities.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Entities = sorted, Dimensions = dimensions.OrderBy(d => d.Name), Relations = relations.OrderBy(r => r.Type).ThenBy(r => r.EntityCount) }))));
        return new(Name(feature), Convert.ToBoolean(ComDispatch.Call(sketch, "Is3D")), status, status == 3, sorted, dimensions.ToArray(), relations, fingerprint);
    }
    private static List<object> ResolveEntities(Scope scope, object sketch, string text)
    {
        var requested = text.Split(',', StringSplitOptions.TrimEntries);
        if (requested.Length is < 1 or > 2 || requested.Distinct().Count() != requested.Length) throw new ArgumentException("要素IDを重複なく1〜2個指定してください。");
        var all = Items(ComDispatch.Call(sketch, "GetSketchSegments")).Select(o => (Object: scope.Own(o), Id: Id(o, "s")))
            .Concat(Items(ComDispatch.Call(sketch, "GetSketchPoints2")).Select(o => (Object: scope.Own(o), Id: Id(o, "p")))).ToArray();
        return requested.Select(id => all.SingleOrDefault(e => e.Id == id).Object ?? throw new ArgumentException("要素IDが見つかりません。sketch inspectで確認してください：" + id)).ToList();
    }
    private static void CheckGeometry(SketchInfo before, SketchInfo after)
    {
        foreach (var original in before.Dimensions)
        {
            var current = after.Dimensions.SingleOrDefault(d => d.Name == original.Name);
            if (current is null || current.Type != original.Type || current.DrivenState != original.DrivenState || Math.Abs(current.Value - original.Value) > .001)
                throw new InvalidOperationException("既存の寸法が変化したため取り消します。");
        }
        if (before.Entities.Length != after.Entities.Length) throw new InvalidOperationException("スケッチの要素数が変化しました。");
        for (var i = 0; i < before.Entities.Length; i++)
        {
            var a = before.Entities[i]; var b = after.Entities[i];
            if (a.Id != b.Id || a.Kind != b.Kind || a.Construction != b.Construction || a.GeometryMm.Length != b.GeometryMm.Length
                || a.GeometryMm.Where((v, j) => !double.IsFinite(b.GeometryMm[j]) || Math.Abs(v - b.GeometryMm[j]) > 0.001).Any())
                throw new InvalidOperationException("寸法・拘束の追加によって形状が変化したため取り消します。");
        }
    }
    private static double[][] BodyGeometry(Scope scope) => Items(ComDispatch.Call(scope.Model, "GetBodies2", -1, false))
        .Select(body => { scope.Own(body); return ((Array)ComDispatch.Call(body, "GetBodyBox")!).Cast<object>().Select(v => Convert.ToDouble(v) * 1000d).ToArray(); }).ToArray();
    private static void CheckBodies(double[][] before, double[][] after)
    {
        if (before.Length != after.Length || before.Where((a, i) => a.Length != after[i].Length || a.Where((v, j) => !double.IsFinite(after[i][j]) || Math.Abs(v - after[i][j]) > .001).Any()).Any())
            throw new InvalidOperationException("再構築後のボディ形状が変わったため取り消します。");
    }
    private static IEnumerable<double[]> Coordinates(Entity entity)
    { for (var i = 0; i < (entity.Kind == "arc" ? 9 : entity.Kind == "line" ? 6 : Math.Min(3, entity.GeometryMm.Length)); i += 3) yield return entity.GeometryMm.Skip(i).Take(3).ToArray(); }
    private static double[] Point(Scope scope, object? point) => PointValues(scope.Own(point));
    private static double[] PointValues(object point) => new[] { "X", "Y", "Z" }.Select(p => Convert.ToDouble(ComDispatch.Get(point, p)) * 1000d).ToArray();
    private static string Id(object entity, string prefix) => prefix + ":" + string.Join(":", ((Array)ComDispatch.Call(entity, "GetID")!).Cast<object>().Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)));
    private static string Name(object feature) => Convert.ToString(ComDispatch.Get(feature, "Name"))!;
    private static string Title(object model) => Convert.ToString(ComDispatch.Call(model, "GetTitle"))!;
    private static object[] Items(object? array) => array is Array a ? a.Cast<object>().Where(v => v is not null).ToArray() : [];
    private static bool Same(object a, object b)
    { var x = Marshal.GetIUnknownForObject(a); var y = Marshal.GetIUnknownForObject(b); try { return x == y; } finally { Marshal.Release(x); Marshal.Release(y); } }
    private static void Release(object o) { if (Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
    private static string? ValidateOutput(object model, string? output, bool overwrite)
    {
        if (output is null) return null;
        output = Path.GetFullPath(output);
        if (!output.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("保存先は.SLDPRTで指定してください。");
        if (File.Exists(output) && !overwrite) throw new IOException("既存ファイルの上書きには --overwrite が必要です。");
        return output;
    }
    private static void Save(object model, string output, bool overwrite)
    {
        ValidateOutput(model, output, overwrite); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var errors = Convert.ToInt32(ComDispatch.Call(model, "SaveAs3", output, 0, 1));
        if (errors != 0 || !File.Exists(output) || Convert.ToBoolean(ComDispatch.Call(model, "GetSaveFlag")))
            throw new IOException($"部品を保存できませんでした（保存エラー {errors}）。");
        if (!string.Equals(Convert.ToString(ComDispatch.Call(model, "GetPathName")), output, StringComparison.OrdinalIgnoreCase)) throw new IOException("保存先を確認できませんでした。");
    }
    private sealed class Scope : IDisposable
    {
        private readonly List<object> _objects = [];
        public object Model { get; }
        public Scope(object app)
        {
            Model = Own(ComDispatch.Get(app, "ActiveDoc"));
            if (Convert.ToInt32(ComDispatch.Call(Model, "GetType")) != 1) { Dispose(); throw new InvalidOperationException("部品ドキュメントをアクティブにしてください。"); }
        }
        public object Own(object? o) { if (o is null) throw new InvalidOperationException("SOLIDWORKSから必要なオブジェクトを取得できませんでした。"); _objects.Add(o); return o; }
        public void Dispose() { for (var i = _objects.Count - 1; i >= 0; i--) if (Marshal.IsComObject(_objects[i])) try { Marshal.ReleaseComObject(_objects[i]); } catch (InvalidComObjectException) { } }
    }
}
