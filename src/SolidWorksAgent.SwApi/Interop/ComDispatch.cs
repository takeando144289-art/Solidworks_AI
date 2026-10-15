using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Globalization;

namespace SolidWorksAgent.SwApi.Interop;

internal static class ComDispatch
{
    private const string ProgId = "SldWorks.Application";

    public static object GetRunningApplication()
    {
        var hr = CLSIDFromProgID(ProgId, out var clsid);
        Marshal.ThrowExceptionForHR(hr);
        hr = GetActiveObject(ref clsid, IntPtr.Zero, out var application);
        Marshal.ThrowExceptionForHR(hr);
        return application;
    }

    public static object CreateApplication()
    {
        var type = Type.GetTypeFromProgID(ProgId, throwOnError: true)
            ?? throw new InvalidOperationException("SolidWorks COM registration was not found.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("SolidWorks could not be started.");
    }

    public static object GetRunningApplication(int processId)
    {
        Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out var table));
        IEnumMoniker? enumerator = null;
        IBindCtx? bindContext = null;
        try
        {
            table.EnumRunning(out enumerator);
            Marshal.ThrowExceptionForHR(CreateBindCtx(0, out bindContext));
            enumerator.Reset();
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                try
                {
                    moniker.GetDisplayName(bindContext, null, out var displayName);
                    if (displayName.EndsWith($"SolidWorks_PID_{processId}", StringComparison.OrdinalIgnoreCase))
                    {
                        table.GetObject(moniker, out var application);
                        return application;
                    }
                }
                finally { Release(moniker); }
            }
            throw new COMException($"SolidWorks PID {processId} is not registered in the Running Object Table.", unchecked((int)0x800401E3));
        }
        finally
        {
            Release(bindContext);
            Release(enumerator);
            Release(table);
        }
    }

    public static IReadOnlyList<string> GetRunningSolidWorksMonikers()
    {
        Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out var table));
        IEnumMoniker? enumerator = null;
        IBindCtx? bindContext = null;
        var names = new List<string>();
        try
        {
            table.EnumRunning(out enumerator);
            Marshal.ThrowExceptionForHR(CreateBindCtx(0, out bindContext));
            enumerator.Reset();
            var monikers = new IMoniker[1];
            while (enumerator.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                try
                {
                    moniker.GetDisplayName(bindContext, null, out var displayName);
                    if (displayName.Contains("SolidWorks", StringComparison.OrdinalIgnoreCase)) names.Add(displayName);
                }
                finally { Release(moniker); }
            }
            return names;
        }
        finally
        {
            Release(bindContext);
            Release(enumerator);
            Release(table);
        }
    }

    public static object? Get(object target, string name, params object?[] indexes) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, indexes);

    public static void Set(object target, string name, object? value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value]);

    public static object? Call(object target, string name, params object?[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

    public static object? CallWithByRef(object target, string name, object?[] args, params int[] byRefIndexes)
    {
        var modifier = new ParameterModifier(args.Length);
        foreach (var index in byRefIndexes) modifier[index] = true;
        return target.GetType().InvokeMember(
            name,
            BindingFlags.InvokeMethod,
            null,
            target,
            args,
            [modifier],
            CultureInfo.InvariantCulture,
            null);
    }

    public static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string lpszProgID, out Guid lpclsid);

    [DllImport("oleaut32.dll")]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable pprot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);
}
