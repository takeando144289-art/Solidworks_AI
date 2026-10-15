using System.Reflection;
using SolidWorksAgent.Core.Automation;

namespace SolidWorksAgent.SwApi.Automation;

public sealed class ApiCatalog
{
    private readonly System.Reflection.Assembly api;
    private readonly System.Reflection.Assembly constants;
    public ApiCatalog()
    {
        var roots = new[] { AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"SOLIDWORKS Corp\SOLIDWORKS\api\redist"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Dassault Systemes\SOLIDWORKS 3DEXPERIENCE R2026x\SOLIDWORKS\api\redist") };
        string Locate(string file) => roots.Select(r => Path.Combine(r, file)).FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("SOLIDWORKS APIメタデータが見つかりません：" + file);
        api = System.Reflection.Assembly.LoadFrom(Locate("SolidWorks.Interop.sldworks.dll"));
        constants = System.Reflection.Assembly.LoadFrom(Locate("SolidWorks.Interop.swconst.dll"));
    }
    public Type Type(string name) => api.GetType("SolidWorks.Interop.sldworks." + name) is { IsInterface: true } t ? t : throw new ArgumentException("SOLIDWORKSインターフェースがありません：" + name);
    public int EnumValue(string text)
    {
        var parts = text.Split('.'); var t = constants.GetType("SolidWorks.Interop.swconst." + parts[0]);
        if (parts.Length != 2 || t is null || !t.IsEnum || !Enum.IsDefined(t, parts[1])) throw new ArgumentException("SOLIDWORKS列挙値がありません：" + text);
        return Convert.ToInt32(Enum.Parse(t, parts[1]));
    }
    public MethodInfo Method(ApiStep step)
    {
        ApiPolicy.Validate(step.Interface, step.Member);
        var name = step.Kind == "call" ? step.Member : (step.Kind == "get" ? "get_" : "set_") + step.Member;
        var matches = Type(step.Interface).GetMethods().Where(m => m.Name == name && m.GetParameters().Length == step.Args.Length).ToArray();
        if (matches.Length != 1) throw new ArgumentException($"{step.Interface}.{step.Member}: 引数の個数・種類を api describe で確認してください。");
        var method = matches[0];
        if (method.GetParameters().Any(p => p.ParameterType.IsPointer || p.ParameterType == typeof(IntPtr))) throw new ArgumentException("生ポインターAPIには対応していません。");
        return method;
    }
    public object Search(string query, string? type = null, int offset = 0)
    {
        if (query.Length > 100 || offset < 0) throw new ArgumentException("検索条件が不正です。");
        var types = type is null ? api.GetTypes().Where(t => t.IsInterface && t.Name.StartsWith('I')) : [Type(type)];
        var matches = types.SelectMany(t => t.GetMethods().Where(m => !m.IsSpecialName).Select(m => (t,m)))
            .Where(x => (x.t.Name + "." + x.m.Name).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new { Total = matches.Length, Offset = offset, NextOffset = offset + 30 < matches.Length ? (int?)(offset + 30) : null,
            Members = matches.Skip(offset).Take(30).Select(x => DescribeMethod(x.t,x.m)).ToArray() };
    }
    public object Describe(string type, string? member = null)
    {
        var e = constants.GetType("SolidWorks.Interop.swconst." + type);
        if (e is { IsEnum:true }) return new { Type = type, Values = Enum.GetNames(e).Where(n=>member is null || n.Contains(member,StringComparison.OrdinalIgnoreCase)).Take(100).ToDictionary(n=>n,n=>Convert.ToInt32(Enum.Parse(e,n))) };
        var t = Type(type); var members = t.GetMethods().Where(m => member is null || m.Name.Replace("get_", "").Replace("set_", "").Equals(member, StringComparison.Ordinal)).ToArray();
        return new { Type = type, Total = members.Length, Members = members.Take(80).Select(m=>DescribeMethod(t,m)).ToArray() };
    }
    private static object DescribeMethod(Type t, MethodInfo m)
    {
        var member = m.Name.Replace("get_", "").Replace("set_", ""); string? blocked = null;
        try { ApiPolicy.Validate(t.Name,member); } catch(ArgumentException ex) { blocked = ex.Message; }
        return new { Interface = t.Name, Member = member, Kind = m.Name.StartsWith("get_") ? "get" : m.Name.StartsWith("set_") ? "set" : "call",
            ReturnType = m.ReturnType.Name, Parameters = m.GetParameters().Select(p => new { p.Name, Type=p.ParameterType.Name, ByRef=p.ParameterType.IsByRef }).ToArray(),
            Available = blocked is null, Reason = blocked,
            Help = $"https://help.solidworks.com/2026/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.{t.Name}~{member}.html" };
    }
}
