using System.Reflection;

namespace UFCFinderApp;

/// <summary>Product branding and version, in one place.</summary>
public static class AppInfo
{
    public const string Company = "Pipkinsoft";
    public const string Product = "UFC Event Host Finder";

    /// <summary>
    /// Major.minor of the assembly. That version comes from Directory.Build.props, which the
    /// installer reads too, so the title bar, the dialogs and the MSI can never disagree.
    /// </summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>Compact form for the header badge.</summary>
    public static string VersionLabel { get; } = "v" + Version;

    /// <summary>Window title and the caption on every dialog.</summary>
    public static string Caption { get; } = Company + " — " + Product + " " + Version;

    public static string WindowTitle { get; } = Product + " " + Version + " — " + Company;

    public const string Tagline = Company + " · Unauthorized showing & advertising detection";

    private static string ReadVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;

        return version is null ? "0.0" : $"{version.Major}.{version.Minor}";
    }
}
