using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SolidWorksAgent.Core.Automation;

public sealed record ApiStep(string Id, string Target, string Interface, string Member, string Kind, JsonElement[] Args, string Expect = "success", JsonElement? Equal = null);
public sealed record ApiPlan(string Summary, string Document, ApiStep[] Steps, double[]? SizeMm = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static ApiPlan Parse(string json)
    {
        if (json.Length > 200_000) throw new ArgumentException("API操作案が大きすぎます。");
        var p = JsonSerializer.Deserialize<ApiPlan>(json, Json) ?? throw new ArgumentException("API操作案が空です。");
        if (string.IsNullOrWhiteSpace(p.Summary) || p.Summary.Length > 2000 || p.Document is not ("new-part" or "new-assembly" or "new-drawing" or "copy-active"))
            throw new ArgumentException("API操作案の説明またはドキュメント指定が不正です。");
        if (p.Steps is null || p.Steps.Length is < 1 or > 300) throw new ArgumentException("API操作は1～300ステップです。");
        if (p.SizeMm is not null && (p.SizeMm.Length != 3 || p.SizeMm.Any(v=>!double.IsFinite(v)||v<=0))) throw new ArgumentException("期待寸法は正の3軸寸法です。");
        var names = new HashSet<string> { "model", "sketch", "features", "extension", "plane", "math" };
        foreach (var s in p.Steps)
        {
            if (s is null || !Identifier(s.Id) || names.Contains(s.Id) || !Identifier(s.Interface) || !Identifier(s.Member)
                || s.Kind is not ("call" or "get" or "set") || s.Expect is not ("success" or "any" or "true" or "not-null") || s.Args is null || s.Args.Length > 64)
                throw new ArgumentException("APIステップが不正です。");
            ValidateReference(s.Target, names);
            ApiPolicy.Validate(s.Interface, s.Member);
            foreach (var arg in s.Args) ValidateValue(arg, names);
            if (s.Equal is JsonElement equal) ValidateValue(equal,names);
            names.Add(s.Id); names.Add(s.Id + "_out");
        }
        return p;
    }
    private static bool Identifier(string? s) => s is not null && Regex.IsMatch(s, "^[A-Za-z_][A-Za-z0-9_]{0,100}$");
    private static void ValidateReference(string? s, HashSet<string> names)
    {
        if (s is null || !Regex.IsMatch(s, @"^[A-Za-z_][A-Za-z0-9_]*(\[\d+\])*$") || !names.Contains(s.Split('[')[0]))
            throw new ArgumentException("未定義のAPI参照です：" + s);
    }
    private static void ValidateValue(JsonElement value, HashSet<string> names)
    {
        if (value.ValueKind == JsonValueKind.Number && !double.IsFinite(value.GetDouble())) throw new ArgumentException("数値が有限ではありません。");
        if (value.ValueKind == JsonValueKind.Array) { foreach (var v in value.EnumerateArray()) ValidateValue(v, names); }
        if (value.ValueKind != JsonValueKind.Object) return;
        var fields = value.EnumerateObject().ToArray();
        if (fields.Length != 1) throw new ArgumentException("API値のタグは1個です。");
        var f = fields[0];
        if (f.Name == "ref") ValidateReference(f.Value.GetString(), names);
        else if (f.Name is "mm" or "deg") { if (f.Value.ValueKind != JsonValueKind.Number || !double.IsFinite(f.Value.GetDouble())) throw new ArgumentException("寸法が不正です。"); }
        else if (f.Name == "enum") { if (f.Value.ValueKind != JsonValueKind.String || !Regex.IsMatch(f.Value.GetString()!, @"^\w+\.\w+$")) throw new ArgumentException("列挙値が不正です。"); }
        else throw new ArgumentException("未対応のAPI値タグです：" + f.Name);
    }
}

/// <summary>Document-local API execution. File lifecycle and application-wide execution belong to the host.</summary>
public static class ApiPolicy
{
    public static void Validate(string type, string member)
    {
        if (!type.StartsWith('I') || type is "ISldWorks" or "ISwAddin" || type.Contains("Macro", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Equation", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("このAPIはドキュメント操作の対象外です：" + type);
        // Macro execution, saving/export and application access bypass document ownership/overwrite checks.
        var forbidden = new[] { "Save", "Export", "Print", "Run", "Execute", "Macro", "Load", "OpenDoc", "CloseDoc", "Quit", "ExitApp", "SwApp", "Application", "GetModelDoc", "GetDocument", "ReplaceReferenced", "SetExternal", "PackAndGo", "DesignTable", "Equation", "EditPart", "MaterialPropertyName", "UserPreference", "Publish", "CheckIn", "CheckOut" };
        if (forbidden.Any(s => member.Contains(s, StringComparison.OrdinalIgnoreCase)) || member.StartsWith('I') && member.Length > 1 && char.IsUpper(member[1]))
            throw new ArgumentException("保存・外部実行・他文書への操作はホスト管理です：" + member);
    }
}
