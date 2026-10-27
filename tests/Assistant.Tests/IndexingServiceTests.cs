using Assistant.Core.Indexing;
using Assistant.Tests.Fakes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Assistant.Tests;

public sealed class IndexingServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lka-svc-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _dbPath;
    private readonly CapturingLogger<IndexingService> _log = new();
    private readonly FakeTimeProvider _time = new();

    public IndexingServiceTests()
    {
        _root = Path.Combine(_dir, "docs");
        _dbPath = Path.Combine(_dir, "meta.db");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private IndexingService Create(bool enabled = true, int intervalMinutes = 0, string? dbPath = null, string? root = null, ILogger<FileIndexer>? indexerLog = null)
    {
        var db = new MetadataDatabase(dbPath ?? _dbPath);
        var options = new IndexingOptions { Enabled = enabled, IntervalMinutes = intervalMinutes, Root = root ?? _root, DatabasePath = db.Path };
        return new IndexingService(db, new FileIndexer(db, indexerLog ?? NullLogger<FileIndexer>.Instance), Options.Create(options), _log, _time);
    }

    private long FileCount()
    {
        using var c = new MetadataDatabase(_dbPath).Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM files";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Polls until <paramref name="condition"/> holds, advancing the fake clock each round so a pending delay fires.</summary>
    private async Task Eventually(Func<bool> condition, bool advanceClock = false)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition was not reached in time");
            if (advanceClock) _time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Disabled_service_does_nothing_and_says_so()
    {
        var service = Create(enabled: false);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        Assert.False(File.Exists(_dbPath));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("disabled"));
    }

    [Fact]
    public async Task Interval_zero_indexes_once_and_finishes()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.md"), "hello");
        var service = Create(intervalMinutes: 0);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        Assert.Equal(1, FileCount());
        Assert.Contains(_log.Entries, e => e.Message.StartsWith("Indexed ") && e.Message.Contains("1 added"));
    }

    [Fact]
    public async Task Unusable_database_location_is_logged_and_indexing_stays_off()
    {
        var blocker = Path.Combine(_dir, "blocker");
        await File.WriteAllTextAsync(blocker, "a file where a directory is needed");
        var service = Create(dbPath: Path.Combine(blocker, "meta.db"));

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("indexing is off"));
    }

    [Fact]
    public async Task Failed_scan_is_logged_and_retried_on_the_next_interval()
    {
        var missingRoot = Path.Combine(_dir, "later");
        var service = Create(intervalMinutes: 1, root: missingRoot);

        await service.StartAsync(CancellationToken.None);
        await Eventually(() => _log.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("will retry")));
        Assert.False(service.ExecuteTask!.IsCompleted);

        Directory.CreateDirectory(missingRoot);
        await File.WriteAllTextAsync(Path.Combine(missingRoot, "a.md"), "hello");
        await Eventually(() => File.Exists(_dbPath) && FileCount() == 1, advanceClock: true);

        await service.StopAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteTask!);
    }

    [Fact]
    public async Task Cancellation_during_a_scan_ends_quietly()
    {
        // The locked file makes the indexer log a warning; cancelling there means the very next file's check throws.
        var locked = Path.Combine(_root, "a-locked.md");
        await File.WriteAllTextAsync(locked, "x");
        await File.WriteAllTextAsync(Path.Combine(_root, "b.md"), "x");
        await File.WriteAllTextAsync(Path.Combine(_root, "c.md"), "x");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cts = new CancellationTokenSource();
        var indexerLog = new CapturingLogger<FileIndexer> { OnLog = (level, _) => { if (level == LogLevel.Warning) cts.Cancel(); } };
        var service = Create(intervalMinutes: 1, indexerLog: indexerLog);

        await service.StartAsync(cts.Token);
        await service.ExecuteTask!;

        Assert.DoesNotContain(_log.Entries, e => e.Level == LogLevel.Error || e.Message.StartsWith("Indexed "));
    }

    [Fact]
    public async Task Without_an_injected_clock_the_system_clock_paces_the_rescans()
    {
        var db = new MetadataDatabase(_dbPath);
        var options = new IndexingOptions { IntervalMinutes = 1, Root = _root, DatabasePath = db.Path };
        var service = new IndexingService(db, new FileIndexer(db, NullLogger<FileIndexer>.Instance), Options.Create(options), _log);

        await service.StartAsync(CancellationToken.None);
        await Eventually(() => _log.Entries.Any(e => e.Message.StartsWith("Indexed ")));
        await service.StopAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteTask!);
    }
}
