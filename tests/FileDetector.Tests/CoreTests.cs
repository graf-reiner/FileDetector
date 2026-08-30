using FileDetector.Core;
using Xunit;

namespace FileDetector.Tests;

public class IgnoreMatcherTests
{
    [Theory]
    [InlineData("draft.tmp", true)]
    [InlineData("movie.mp4.crdownload", true)]
    [InlineData("~$report.docx", true)]
    [InlineData("Thumbs.db", true)]
    [InlineData("THUMBS.DB", true)]
    [InlineData("invoice.pdf", false)]
    [InlineData("notes.txt", false)]
    [InlineData("tmp", false)]
    public void DefaultPatternsMatchTempFilesOnly(string name, bool expected)
    {
        var matcher = new IgnoreMatcher(IgnoreMatcher.DefaultPatterns);
        Assert.Equal(expected, matcher.IsIgnored(name));
    }

    [Fact]
    public void QuestionMarkMatchesExactlyOneCharacter()
    {
        var matcher = new IgnoreMatcher(new[] { "log?.txt" });
        Assert.True(matcher.IsIgnored("log1.txt"));
        Assert.False(matcher.IsIgnored("log.txt"));
        Assert.False(matcher.IsIgnored("log12.txt"));
    }

    [Fact]
    public void EmptyAndWhitespacePatternsAreDropped()
    {
        var matcher = new IgnoreMatcher(new[] { "  ", string.Empty, "*.bak" });
        Assert.Single(matcher.Patterns);
        Assert.True(matcher.IsIgnored("db.bak"));
        Assert.False(matcher.IsIgnored("anything"));
    }

    [Fact]
    public void PatternsAreAnchored()
    {
        var matcher = new IgnoreMatcher(new[] { "*.tmp" });
        Assert.False(matcher.IsIgnored("file.tmp.pdf"));
    }
}

public class FolderDifferTests
{
    private static FolderSnapshot Snapshot(string path, params FolderEntry[] entries) =>
        new() { Path = path, CapturedUtc = DateTime.UtcNow, Entries = entries.ToList() };

    private static FolderEntry File(string name, long size = 10) =>
        new(name, false, size, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

    private static FolderEntry Dir(string name) =>
        new(name, true, 0, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void ReportsAddedItems()
    {
        var before = Snapshot(@"C:\watch", File("a.txt"));
        var after = Snapshot(@"C:\watch", File("a.txt"), File("b.txt"), Dir("New Folder"));

        var changes = FolderDiffer.Diff(before, after);

        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(ChangeKind.Added, c.Kind));
        Assert.Contains(changes, c => c.Name == "b.txt" && !c.IsDirectory);
        Assert.Contains(changes, c => c.Name == "New Folder" && c.IsDirectory);
    }

    [Fact]
    public void ReportsDeletedItems()
    {
        var before = Snapshot(@"C:\watch", File("a.txt"), File("b.txt"));
        var after = Snapshot(@"C:\watch", File("a.txt"));

        var changes = FolderDiffer.Diff(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(ChangeKind.Deleted, change.Kind);
        Assert.Equal("b.txt", change.Name);
    }

    [Fact]
    public void AnOfflineRenameLooksLikeAnAddPlusADelete()
    {
        var before = Snapshot(@"C:\watch", File("old.txt"));
        var after = Snapshot(@"C:\watch", File("new.txt"));

        var changes = FolderDiffer.Diff(before, after);

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.Kind == ChangeKind.Added && c.Name == "new.txt");
        Assert.Contains(changes, c => c.Kind == ChangeKind.Deleted && c.Name == "old.txt");
    }

    [Fact]
    public void SizeOrTimestampChangeIsModified()
    {
        var before = Snapshot(@"C:\watch", File("a.txt", size: 10));
        var after = Snapshot(@"C:\watch", File("a.txt", size: 20));

        var changes = FolderDiffer.Diff(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(ChangeKind.Modified, change.Kind);
        Assert.Equal(20, change.Size);
    }

    [Fact]
    public void ModifiedIsSuppressedWhenNotRequested()
    {
        var before = Snapshot(@"C:\watch", File("a.txt", size: 10));
        var after = Snapshot(@"C:\watch", File("a.txt", size: 20));

        Assert.Empty(FolderDiffer.Diff(before, after, includeModified: false));
    }

    [Fact]
    public void DirectoryTimestampChurnIsNotReported()
    {
        var before = Snapshot(@"C:\watch", new FolderEntry("sub", true, 0, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var after = Snapshot(@"C:\watch", new FolderEntry("sub", true, 0, new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Empty(FolderDiffer.Diff(before, after));
    }

    [Fact]
    public void NameComparisonIgnoresCase()
    {
        var before = Snapshot(@"C:\watch", File("Report.PDF"));
        var after = Snapshot(@"C:\watch", File("report.pdf"));

        Assert.Empty(FolderDiffer.Diff(before, after));
    }

    [Fact]
    public void EmptyBaselineReportsEverythingAsAdded()
    {
        var before = FolderSnapshot.Empty(@"C:\watch");
        var after = Snapshot(@"C:\watch", File("a.txt"), File("b.txt"));

        var changes = FolderDiffer.Diff(before, after);

        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(ChangeKind.Added, c.Kind));
    }

    [Fact]
    public void ChangesAreSortedByKindThenFoldersFirst()
    {
        var before = Snapshot(@"C:\watch", File("gone.txt"));
        var after = Snapshot(@"C:\watch", File("zeta.txt"), Dir("alpha"));

        var changes = FolderDiffer.Diff(before, after);

        Assert.Equal("alpha", changes[0].Name);      // Added, directory first
        Assert.Equal("zeta.txt", changes[1].Name);   // Added, file
        Assert.Equal("gone.txt", changes[2].Name);   // Deleted last
    }
}

public class ChangeAggregatorTests
{
    private const string Folder = @"C:\watch";
    private static readonly DateTime T0 = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    private static ChangeAggregator NewAggregator() =>
        new(Folder, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));

    private static FileChange Change(ChangeKind kind, string name, string? oldName = null, bool isDir = false, long size = 0) =>
        new() { Kind = kind, FolderPath = Folder, Name = name, OldName = oldName, IsDirectory = isDir, Size = size, TimestampUtc = T0 };

    [Fact]
    public void NothingIsEmittedBeforeTheQuietPeriodElapses()
    {
        var aggregator = NewAggregator();
        aggregator.Add(Change(ChangeKind.Added, "a.txt"), T0);

        Assert.False(aggregator.TryFlush(T0.AddSeconds(1), out _));
        Assert.True(aggregator.HasPending);
    }

    [Fact]
    public void OneBatchIsEmittedOnceTheFolderIsQuiet()
    {
        var aggregator = NewAggregator();
        aggregator.Add(Change(ChangeKind.Added, "a.txt"), T0);
        aggregator.Add(Change(ChangeKind.Added, "b.txt"), T0.AddSeconds(1));

        Assert.False(aggregator.TryFlush(T0.AddSeconds(2), out _));   // quiet timer restarted by the second event
        Assert.True(aggregator.TryFlush(T0.AddSeconds(3), out var batch));

        Assert.Equal(2, batch.Count);
        Assert.False(aggregator.HasPending);
    }

    [Fact]
    public void AContinuousStreamStillFlushesAtTheMaximumWait()
    {
        var aggregator = NewAggregator();
        for (var i = 0; i < 40; i++)
        {
            aggregator.Add(Change(ChangeKind.Added, $"f{i}.txt"), T0.AddSeconds(i * 0.5));
        }

        // Never quiet, but the 10s cap has passed.
        Assert.True(aggregator.TryFlush(T0.AddSeconds(11), out var batch));
        Assert.Equal(40, batch.Count);
    }

    [Fact]
    public void ABurstOfTwoHundredFilesBecomesOneBatch()
    {
        var aggregator = NewAggregator();
        for (var i = 0; i < 200; i++)
        {
            aggregator.Add(Change(ChangeKind.Added, $"invoice_{i:D3}.pdf"), T0.AddMilliseconds(i * 5));
        }

        Assert.True(aggregator.TryFlush(T0.AddSeconds(4), out var batch));
        Assert.Equal(200, batch.Count);
        Assert.Equal(200, batch.CountOf(ChangeKind.Added));
    }

    [Fact]
    public void CreatedThenDeletedReportsNothing()
    {
        var aggregator = NewAggregator();
        aggregator.Add(Change(ChangeKind.Added, "scratch.txt"), T0);
        aggregator.Add(Change(ChangeKind.Deleted, "scratch.txt"), T0.AddSeconds(0.5));

        Assert.False(aggregator.TryFlush(T0.AddSeconds(5), out _));
        Assert.False(aggregator.HasPending);
    }

    [Fact]
    public void CreatedThenRenamedIsOneAddUnderTheFinalName()
    {
        var result = ChangeAggregator.Coalesce(new[]
        {
            Change(ChangeKind.Added, "tmpfile"),
            Change(ChangeKind.Renamed, "final.zip", oldName: "tmpfile"),
        }, Folder);

        var change = Assert.Single(result);
        Assert.Equal(ChangeKind.Added, change.Kind);
        Assert.Equal("final.zip", change.Name);
    }

    [Fact]
    public void RenamedWithAnUnknownOriginStaysARename()
    {
        var result = ChangeAggregator.Coalesce(new[]
        {
            Change(ChangeKind.Renamed, "new.txt", oldName: "old.txt"),
        }, Folder);

        var change = Assert.Single(result);
        Assert.Equal(ChangeKind.Renamed, change.Kind);
        Assert.Equal("old.txt", change.OldName);
        Assert.Equal("new.txt", change.Name);
    }

    [Fact]
    public void ChainedRenamesCollapseToOriginalAndFinalName()
    {
        var result = ChangeAggregator.Coalesce(new[]
        {
            Change(ChangeKind.Renamed, "middle.txt", oldName: "first.txt"),
            Change(ChangeKind.Renamed, "last.txt", oldName: "middle.txt"),
        }, Folder);

        var change = Assert.Single(result);
        Assert.Equal(ChangeKind.Renamed, change.Kind);
        Assert.Equal("first.txt", change.OldName);
        Assert.Equal("last.txt", change.Name);
    }

    [Fact]
    public void RenamedThenDeletedReportsTheOriginalNameAsDeleted()
    {
        var result = ChangeAggregator.Coalesce(new[]
        {
            Change(ChangeKind.Renamed, "new.txt", oldName: "old.txt"),
            Change(ChangeKind.Deleted, "new.txt"),
        }, Folder);

        var change = Assert.Single(result);
        Assert.Equal(ChangeKind.Deleted, change.Kind);
        Assert.Equal("old.txt", change.Name);
    }

    [Fact]
    public void DuplicateEventsCollapseAndKeepTheNewestMetadata()
    {
        var result = ChangeAggregator.Coalesce(new[]
        {
            Change(ChangeKind.Added, "big.iso", size: 100),
            Change(ChangeKind.Modified, "big.iso", size: 5000),
            Change(ChangeKind.Modified, "big.iso", size: 9000),
        }, Folder);

        var change = Assert.Single(result);
        Assert.Equal(ChangeKind.Added, change.Kind);
        Assert.Equal(9000, change.Size);
    }

    [Fact]
    public void DeletedThenRecreatedIsReportedAsAdded()
    {
        var result = ChangeAggregator.Coalesce(new[]
        {
            Change(ChangeKind.Deleted, "data.csv"),
            Change(ChangeKind.Added, "data.csv", size: 42),
        }, Folder);

        var change = Assert.Single(result);
        Assert.Equal(ChangeKind.Added, change.Kind);
        Assert.Equal(42, change.Size);
    }

    [Fact]
    public void NameMatchingIgnoresCase()
    {
        var result = ChangeAggregator.Coalesce(new[]
        {
            Change(ChangeKind.Added, "Report.pdf"),
            Change(ChangeKind.Deleted, "REPORT.PDF"),
        }, Folder);

        Assert.Empty(result);
    }

    [Fact]
    public void ForceFlushReturnsNullWhenEverythingCancelsOut()
    {
        var aggregator = NewAggregator();
        aggregator.Add(Change(ChangeKind.Added, "scratch.txt"), T0);
        aggregator.Add(Change(ChangeKind.Deleted, "scratch.txt"), T0);

        Assert.Null(aggregator.ForceFlush(T0));
    }

    [Fact]
    public void DiscardDropsPendingEvents()
    {
        var aggregator = NewAggregator();
        aggregator.Add(Change(ChangeKind.Added, "a.txt"), T0);
        aggregator.Discard();

        Assert.False(aggregator.HasPending);
        Assert.False(aggregator.TryFlush(T0.AddSeconds(10), out _));
    }
}

public class ChangeBatchTests
{
    [Fact]
    public void SummarizeCountsEachKind()
    {
        var batch = new ChangeBatch
        {
            FolderPath = @"C:\Dropbox\Intake",
            Changes = new[]
            {
                new FileChange { Kind = ChangeKind.Added, FolderPath = @"C:\Dropbox\Intake", Name = "a.txt" },
                new FileChange { Kind = ChangeKind.Added, FolderPath = @"C:\Dropbox\Intake", Name = "b.txt" },
                new FileChange { Kind = ChangeKind.Deleted, FolderPath = @"C:\Dropbox\Intake", Name = "c.txt" },
            },
        };

        Assert.Equal("2 new, 1 deleted in Intake", batch.Summarize());
        Assert.Equal("Intake", batch.FolderName);
    }

    [Fact]
    public void FolderNameFallsBackToTheFullPathForADriveRoot()
    {
        var batch = new ChangeBatch { FolderPath = @"D:\", Changes = Array.Empty<FileChange>() };
        Assert.Equal(@"D:\", batch.FolderName);
    }
}
