using System.Reflection;

namespace ScriptDock;

/// <summary>
/// The product's identity as Directory.Build.props states it once (Product, Company, Copyright), read from
/// the assembly the build stamps with them, so no window, sentence or menu repeats it by hand.
/// </summary>
public static class AppInfo
{
    private static readonly Assembly Assembly = typeof(AppInfo).Assembly;

    public static string Product { get; } =
        Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "ScriptDock";

    public static string Publisher { get; } =
        Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "";

    public static string Copyright { get; } =
        Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";
}
