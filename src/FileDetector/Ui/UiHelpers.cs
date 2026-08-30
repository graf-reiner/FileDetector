using System.Diagnostics;
using System.Reflection;
using FileDetector.Core;
using FileDetector.Services;

namespace FileDetector.Ui;

/// <summary>Tray and window icons, loaded from embedded resources so single-file publish works.</summary>
public static class IconProvider
{
    private static Icon? _app;
    private static Icon? _paused;

    public static Icon App => _app ??= Load("FileDetector.app.ico") ?? SystemIcons.Application;

    public static Icon Paused => _paused ??= Load("FileDetector.app-paused.ico") ?? App;

    private static Icon? Load(string resourceName)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            return stream is null ? null : new Icon(stream);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not load icon '{resourceName}': {ex.Message}");
            return null;
        }
    }
}

/// <summary>Explorer integration and the small formatting helpers the forms share.</summary>
public static class ShellHelper
{
    public static void OpenFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        try
        {
            if (!Directory.Exists(folderPath))
            {
                MessageBox.Show($"The folder no longer exists:{Environment.NewLine}{folderPath}",
                    "FileDetector", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folderPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"could not open folder '{folderPath}'", ex);
        }
    }

    /// <summary>Opens Explorer with the item selected, falling back to its parent folder.</summary>
    public static void RevealItem(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                var parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent)) OpenFolder(parent!);
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"could not reveal '{fullPath}'", ex);
        }
    }

    public static string FormatSize(long bytes, bool isDirectory)
    {
        if (isDirectory) return string.Empty;
        if (bytes <= 0) return "0 B";

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    public static string KindLabel(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => "Added",
        ChangeKind.Renamed => "Renamed",
        ChangeKind.Modified => "Changed",
        ChangeKind.Deleted => "Deleted",
        _ => kind.ToString(),
    };

    public static string TypeLabel(bool isDirectory, string name) =>
        isDirectory ? "Folder" : (Path.GetExtension(name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? $"{ext} file" : "File");
}
