using SolidWorksAgent.SwApi.Interop;

namespace SolidWorksAgent.SwApi.Parts;

public sealed partial class SketchEditor
{
    // Opt-in integration verification. All created documents belong to this command's session.
    public object Verify(string template, string output)
    {
        output = Path.GetFullPath(output);
        if (File.Exists(output)) throw new IOException("検証ファイルが既にあります。別名を指定してください。");
        using var session = connection.BeginDocumentSession(Path.GetDirectoryName(output)!);
        var report = new List<object>();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        void Stage(string value) => File.AppendAllText(Path.Combine(Path.GetDirectoryName(output)!, "verification-progress.log"), DateTimeOffset.Now + " " + value + "\n");
        foreach (var shape in new[] { "circle", "rectangle", "line", "solid" })
        {
            Stage(shape);
            var model = connection.CreateDocument(template, 0, 0d, 0d) ?? throw new InvalidOperationException("検証部品を作成できません。");
            try
            {
                using var scope = new Scope(connection.Application);
                object? plane = ComDispatch.Call(model, "FirstFeature");
                while (plane is not null && Convert.ToString(ComDispatch.Call(plane, "GetTypeName2")) != "RefPlane")
                { var next = ComDispatch.Call(plane, "GetNextFeature"); Release(plane); plane = next; }
                if (plane is null) throw new InvalidOperationException("基準平面がありません。");
                scope.Own(plane); ComDispatch.Call(plane, "Select2", false, 0);
                var manager = scope.Own(ComDispatch.Get(model, "SketchManager"));
                ComDispatch.Call(manager, "InsertSketch", true);
                // Bypass automatic relations deliberately so the test starts unconstrained.
                ComDispatch.Set(manager, "AddToDB", true);
                try
                {
                    if (shape is "circle" or "solid") scope.Own(ComDispatch.Call(manager, "CreateCircleByRadius", .008, .006, 0d, .005));
                    else if (shape == "rectangle")
                    {
                        foreach (var line in new[] { new[] { .002, .003, .022, .003 }, new[] { .022, .003, .022, .013 }, new[] { .022, .013, .002, .013 }, new[] { .002, .013, .002, .003 } })
                            scope.Own(ComDispatch.Call(manager, "CreateLine", line[0], line[1], 0d, line[2], line[3], 0d));
                    }
                    else scope.Own(ComDispatch.Call(manager, "CreateLine", .001, .002, 0d, .021, .002, 0d));
                }
                finally { ComDispatch.Set(manager, "AddToDB", false); }
                ComDispatch.Call(manager, "InsertSketch", true);
                var feature = FindSketches(scope, model).Single();
                if (shape == "solid")
                {
                    ComDispatch.Call(feature, "Select2", false, 0);
                    var featureManager = scope.Own(ComDispatch.Get(model, "FeatureManager"));
                    scope.Own(ComDispatch.Call(featureManager, "FeatureExtrusion2", true, false, false, 0, 0, .01, 0d,
                        false, false, false, false, 0d, 0d, false, false, false, false, true, true, true, 0, 0d, false));
                    if (!Convert.ToBoolean(ComDispatch.Call(model, "EditRebuild3")) || BodyGeometry(scope).Length != 1) throw new Exception("Solid fixture missing");
                }
                var before = Snapshot(scope, feature);
                if (before.FullyDefined || before.Dimensions.Length != 0) throw new Exception("Fixture is not an undimensioned sketch.");
                if (shape == "line")
                {
                    var line = before.Entities.Single(e => e.Kind == "line");
                    Stage("reject wrong dimension");
                    try { AddDimension(before.Name, "length", line.Id, 99, before.Fingerprint); throw new Exception("Wrong dimension accepted"); }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("操作を取り消しました")) { }
                    var rolledBack = Snapshot(scope, feature); CheckGeometry(before, rolledBack);
                    if (rolledBack.Dimensions.Length != 0 || rolledBack.Relations.Length != before.Relations.Length) throw new Exception("Failed dimension was not rolled back");
                    Stage("reject moving relation");
                    try { AddRelation(before.Name, "vertical", line.Id); throw new Exception("Geometry-changing relation accepted"); }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("操作を取り消しました")) { }
                    var relationRollback = Snapshot(scope, feature); CheckGeometry(before, relationRollback);
                    Stage("add horizontal relation");
                    AddRelation(before.Name, "horizontal", line.Id);
                    var related = Snapshot(scope, feature);
                    Stage("add length");
                    AddDimension(before.Name, "length", line.Id, 20, related.Fingerprint);
                    var dimensioned = Snapshot(scope, feature);
                    if (dimensioned.Dimensions.Length != 1 || dimensioned.Dimensions[0].DrivenState != 2) throw new Exception("Driving dimension missing.");
                    try { AddDimension(before.Name, "length", line.Id, 20, "stale-fingerprint"); throw new Exception("Stale plan was accepted"); }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("確認時から")) { }
                    var preserved = Snapshot(scope, feature); CheckGeometry(dimensioned, preserved);
                    if (preserved.Fingerprint != dimensioned.Fingerprint) throw new Exception("Stale plan changed sketch.");
                }
                Stage("fully define " + shape);
                report.Add(FullyDefine(before.Name, output: shape == "solid" ? output : null));
                var after = Snapshot(scope, feature);
                if (!after.FullyDefined || after.Dimensions.Length == 0 || after.Dimensions.Any(d => d.DrivenState != 2)) throw new Exception("Fully defined sketch with driving dimensions was not created.");
                CheckGeometry(before, after);
            }
            catch (Exception ex) { throw new InvalidOperationException(shape + ": " + ex.ToString(), ex); }
            finally { Release(model); }
        }
        var cleanup = session.Complete([output]);
        return new { Passed = true, Cases = report, FinalOutput = output, Cleanup = cleanup };
    }
}
