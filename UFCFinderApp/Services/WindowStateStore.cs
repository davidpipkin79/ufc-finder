using System.Text.Json;
using System.Windows;

namespace UFCFinderApp.Services;

/// <summary>Saved window geometry.</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// Remembers where the window was and puts it back on the next run.
///
/// A saved position can point at a monitor that is no longer attached, or at a resolution
/// that no longer exists, so whatever is restored is always checked against the current
/// desktop and re-centred when it would otherwise open off-screen.
/// </summary>
public static class WindowStateStore
{
    /// <summary>How much of the window must be on a screen for the position to be trusted.</summary>
    private const double MinVisibleWidth = 160;
    private const double MinVisibleHeight = 80;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UFCFinder",
        "window.json");

    /// <summary>Applies the saved placement, or a safe default, to the window.</summary>
    public static void Restore(Window window)
    {
        var saved = Load();

        var desktop = VisibleDesktop();
        var work = SystemParameters.WorkArea;

        var width = saved?.Width ?? window.Width;
        var height = saved?.Height ?? window.Height;
        var left = saved?.Left ?? double.NaN;
        var top = saved?.Top ?? double.NaN;

        width = Clamp(width, window.MinWidth, Math.Max(window.MinWidth, desktop.Width));
        height = Clamp(height, window.MinHeight, Math.Max(window.MinHeight, desktop.Height));

        if (!IsFinite(left) || !IsFinite(top) || !HasVisibleArea(left, top, width, height, desktop))
        {
            // Stale or missing coordinates: centre on the primary work area instead.
            width = Math.Min(width, Math.Max(window.MinWidth, work.Width));
            height = Math.Min(height, Math.Max(window.MinHeight, work.Height));

            left = work.Left + Math.Max(0, (work.Width - width) / 2);
            top = work.Top + Math.Max(0, (work.Height - height) / 2);
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = left;
        window.Top = top;
        window.Width = width;
        window.Height = height;

        if (saved?.Maximized == true)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    public static void Save(Window window)
    {
        try
        {
            // When maximised or minimised, the normal bounds live in RestoreBounds.
            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.Width, window.Height)
                : window.RestoreBounds;

            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;

            var placement = new WindowPlacement
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = window.WindowState == WindowState.Maximized
            };

            var folder = Path.GetDirectoryName(SettingsPath);

            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(placement, JsonOptions));
        }
        catch
        {
            // Window geometry is a convenience; it must never block shutdown.
        }
    }

    private static WindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;

            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(SettingsPath));

            if (placement is null || placement.Width <= 0 || placement.Height <= 0) return null;

            return placement;
        }
        catch
        {
            return null;
        }
    }

    private static Rect VisibleDesktop() => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

    private static bool HasVisibleArea(double left, double top, double width, double height, Rect desktop)
    {
        if (desktop.Width <= 0 || desktop.Height <= 0) return false;

        var overlap = Rect.Intersect(new Rect(left, top, width, height), desktop);

        return !overlap.IsEmpty
               && overlap.Width >= MinVisibleWidth
               && overlap.Height >= MinVisibleHeight;
    }

    private static double Clamp(double value, double min, double max)
    {
        if (!IsFinite(value)) return min;

        if (max < min) max = min;

        return Math.Min(Math.Max(value, min), max);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
