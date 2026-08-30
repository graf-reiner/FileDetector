using System.Text;
using System.Text.Json;
using FileDetector.Core;

namespace FileDetector.Services;

public sealed record HistoryEntry
{
    public DateTime WhenUtc { get; init; }
    public string FolderPath { get; init; } = string.Empty;
    public ChangeKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? OldName { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public bool WasCatchUp { get; init; }
}

/// <summary>
/// Append-only JSONL event log. One line per change so a partially written tail costs at most
/// the last event rather than the whole file.
/// </summary>
public sealed class HistoryStore
{
    private const long RotateBytes = 5 * 1024 * 1024;
    private const int ReadLimit = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly object _gate = new();

    public void Append(ChangeBatch batch)
    {
        try
        {
            lock (_gate)
            {
                AppPaths.EnsureCreated();
                Rotate();

                var sb = new StringBuilder();
                foreach (var change in batch.Changes)
                {
                    var entry = new HistoryEntry
                    {
                        WhenUtc = batch.CreatedUtc,
                        FolderPath = batch.FolderPath,
                        Kind = change.Kind,
                        Name = change.Name,
                        OldName = change.OldName,
                        IsDirectory = change.IsDirectory,
                        Size = change.Size,
                        WasCatchUp = batch.IsCatchUp,
                    };
                    sb.AppendLine(JsonSerializer.Serialize(entry, JsonOptions));
                }

                File.AppendAllText(AppPaths.HistoryFile, sb.ToString(), new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            Log.Error("failed to append history", ex);
        }
    }

    /// <summary>Most recent first, capped so the history window cannot be sunk by a huge log.</summary>
    public IReadOnlyList<HistoryEntry> ReadRecent(int max = ReadLimit)
    {
        var entries = new List<HistoryEntry>();
        lock (_gate)
        {
            foreach (var file in new[] { AppPaths.HistoryFile, AppPaths.HistoryArchiveFile })
            {
                if (!File.Exists(file)) continue;
                try
                {
                    foreach (var line in ReadLinesReversed(file))
                    {
                        if (entries.Count >= max) return entries;
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var entry = JsonSerializer.Deserialize<HistoryEntry>(line, JsonOptions);
                            if (entry is not null) entries.Add(entry);
                        }
                        catch (JsonException)
                        {
                            // Skip a torn line rather than losing the whole log.
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"failed to read history file '{file}'", ex);
                }
            }
        }
        return entries;
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var file in new[] { AppPaths.HistoryFile, AppPaths.HistoryArchiveFile })
            {
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                }
                catch (Exception ex)
                {
                    Log.Error($"failed to clear history file '{file}'", ex);
                }
            }
        }
    }

    private static IEnumerable<string> ReadLinesReversed(string path)
    {
        var lines = File.ReadAllLines(path);
        for (var i = lines.Length - 1; i >= 0; i--) yield return lines[i];
    }

    private static void Rotate()
    {
        var info = new FileInfo(AppPaths.HistoryFile);
        if (!info.Exists || info.Length < RotateBytes) return;

        if (File.Exists(AppPaths.HistoryArchiveFile)) File.Delete(AppPaths.HistoryArchiveFile);
        File.Move(AppPaths.HistoryFile, AppPaths.HistoryArchiveFile);
    }
}
