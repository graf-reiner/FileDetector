using System.Text;

namespace FileDetector.Services;

/// <summary>
/// Minimal rolling text log. A tray app fails invisibly, so every swallowed exception
/// must land here — this file is the only diagnostic surface after the fact.
/// </summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                Roll();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(AppPaths.LogFile, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    private static void Roll()
    {
        var info = new FileInfo(AppPaths.LogFile);
        if (!info.Exists || info.Length < MaxBytes) return;

        var archive = AppPaths.LogFile + ".1";
        if (File.Exists(archive)) File.Delete(archive);
        File.Move(AppPaths.LogFile, archive);
    }
}
