using ArcaneCore.Kernel.Diagnostics;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Diagnostics;

/// <summary>Each daemon's crash journal is its own, so the Realm and the World never consume each other's terminating-crash records.</summary>
[Collection(DiagnosticsCollection.Name)]
public sealed class CrashJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-crashjournal-" + Guid.NewGuid().ToString("N"));

    public CrashJournalTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static Exception Thrown(string message)
    {
        try { throw new InvalidOperationException(message); } catch (Exception ex) { return ex; }
    }

    [Fact]
    public void PathFor_KeysTheFileByProcessName()
    {
        Assert.Equal(Path.Combine(_directory, "crash-faults.ArcaneCore.World.jsonl"), CrashJournalPaths.PathFor(_directory, "ArcaneCore.World"));
        Assert.NotEqual(CrashJournalPaths.PathFor(_directory, "ArcaneCore.World"), CrashJournalPaths.PathFor(_directory, "ArcaneCore.Realm"));
        Assert.DoesNotContain('/', Path.GetFileName(CrashJournalPaths.PathFor(_directory, "a/b")));
    }

    [Fact]
    public void TwoDaemons_WithTheirOwnJournals_DoNotConsumeEachOthersRecords()
    {
        string worldJournal = CrashJournalPaths.PathFor(_directory, "ArcaneCore.World");
        string realmJournal = CrashJournalPaths.PathFor(_directory, "ArcaneCore.Realm");
        var worldCrashing = new FaultLedger();
        worldCrashing.ConfigureCrashJournal(worldJournal, "ArcaneCore.World");
        worldCrashing.Record("unhandled", Thrown("world crash"), terminating: true);

        var realmRestart = new FaultLedger();
        realmRestart.ConfigureCrashJournal(realmJournal, "ArcaneCore.Realm");
        Assert.Equal(0, realmRestart.ReplayCrashJournal());
        Assert.True(File.Exists(worldJournal));

        var worldRestart = new FaultLedger();
        worldRestart.ConfigureCrashJournal(worldJournal, "ArcaneCore.World");
        Assert.Equal(1, worldRestart.ReplayCrashJournal());
        Assert.False(File.Exists(worldJournal));
    }

    [Fact]
    public void ASharedFile_IsConsumedOnlyByTheProcessThatWroteTheRecord()
    {
        string shared = CrashJournalPaths.LegacyPath(_directory);
        var world = new FaultLedger();
        world.ConfigureCrashJournal(shared, "ArcaneCore.World");
        world.Record("unhandled", Thrown("world crash"), terminating: true);

        var realm = new FaultLedger();
        realm.ConfigureCrashJournal(shared, "ArcaneCore.Realm");
        Assert.Equal(0, realm.ReplayCrashJournal());
        Assert.True(File.Exists(shared));

        var worldRestart = new FaultLedger();
        worldRestart.ConfigureCrashJournal(shared, "ArcaneCore.World");
        Assert.Equal(1, worldRestart.ReplayCrashJournal());
        Assert.False(File.Exists(shared));
    }

    [Fact]
    public void LegacyMigration_ReplaysOwnRecords_KeepsOthers_AndDiscardsUnattributedOnes()
    {
        string legacy = CrashJournalPaths.LegacyPath(_directory);
        var old = new FaultLedger();
        old.ConfigureCrashJournal(legacy); // an older build: no owner in the record
        old.Record("unhandled", Thrown("old build crash"), terminating: true);
        var world = new FaultLedger();
        world.ConfigureCrashJournal(legacy, "ArcaneCore.World");
        world.Record("unhandled", Thrown("world crash"), terminating: true);
        var realm = new FaultLedger();
        realm.ConfigureCrashJournal(legacy, "ArcaneCore.Realm");
        realm.Record("unhandled", Thrown("realm crash"), terminating: true);

        var worldRestart = new FaultLedger();
        (int replayed, int dropped) = worldRestart.MigrateLegacyCrashJournal(legacy, "ArcaneCore.World");
        Assert.Equal(1, replayed);
        Assert.Equal(1, dropped);
        Assert.True(File.Exists(legacy)); // the Realm's record is still there

        var realmRestart = new FaultLedger();
        Assert.Equal((1, 0), realmRestart.MigrateLegacyCrashJournal(legacy, "ArcaneCore.Realm"));
        Assert.False(File.Exists(legacy));
    }

    /// <summary>The first call throws, every later one reads the system clock.</summary>
    private sealed class FirstCallThrowsTime : TimeProvider
    {
        private int _calls;

        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref _calls) == 1 ? throw new InvalidOperationException("clock failed") : DateTimeOffset.UtcNow;
    }

    [Fact]
    public void AThrowingRecord_DoesNotDropTheRecordsAfterIt_AndIsNotCounted()
    {
        string journal = CrashJournalPaths.PathFor(_directory, "ArcaneCore.World");
        // The first record has no AtUtc, so replaying it reads the (throwing) clock; the second names its time.
        File.WriteAllLines(journal,
        [
            "{\"Id\":\"aaaaaaaaaaaaaaaa\",\"Site\":\"unhandled\",\"ExceptionType\":\"System.Exception\",\"Frames\":[],\"Message\":\"first\",\"SampleStack\":\"\",\"SampleChain\":\"\",\"Process\":\"ArcaneCore.World\"}",
            "{\"Id\":\"bbbbbbbbbbbbbbbb\",\"Site\":\"unhandled\",\"ExceptionType\":\"System.Exception\",\"Frames\":[],\"Message\":\"second\",\"SampleStack\":\"\",\"SampleChain\":\"\",\"AtUtc\":\"2026-01-01T00:00:00+00:00\",\"Process\":\"ArcaneCore.World\"}",
        ]);
        var ledger = new FaultLedger(new FaultLedgerLimits(), new FirstCallThrowsTime());
        ledger.ConfigureCrashJournal(journal, "ArcaneCore.World");

        int replayed = ledger.ReplayCrashJournal();

        Assert.Equal(1, replayed);
        Assert.False(File.Exists(journal));
        Assert.Contains(ledger.Snapshot(), f => f.Id == "bbbbbbbbbbbbbbbb");
        Assert.Contains(ledger.Snapshot(), f => f.Site == "diagnostics.crash-journal-replay");
    }

    [Fact]
    public void ConfigureAndReplay_UsesThePerProcessFile_ConsumesTheLegacyJournal_AndWarnsAboutDiscardedRecords()
    {
        string legacy = CrashJournalPaths.LegacyPath(_directory);
        var old = new FaultLedger();
        old.ConfigureCrashJournal(legacy); // an older build: no writer in the record
        old.Record("unhandled", Thrown("old build crash"), terminating: true);
        var world = new FaultLedger();
        world.ConfigureCrashJournal(legacy, "ArcaneCore.World");
        world.Record("unhandled", Thrown("world crash"), terminating: true);
        var current = new FaultLedger();
        current.ConfigureCrashJournal(CrashJournalPaths.PathFor(_directory, "ArcaneCore.World"), "ArcaneCore.World");
        current.Record("unhandled", Thrown("current crash"), terminating: true);

        var restarted = new FaultLedger();
        var log = new CapturingLogger();
        int replayed = CrashJournalPaths.ConfigureAndReplay(restarted, _directory, "ArcaneCore.World", log);

        Assert.Equal(2, replayed); // the per-process file's record plus the legacy file's own record
        Assert.False(File.Exists(legacy));
        Assert.False(File.Exists(CrashJournalPaths.PathFor(_directory, "ArcaneCore.World")));
        Assert.Contains(log.Entries, e => e.StartsWith("Warning: Diagnostics: discarded 1 unattributed", StringComparison.Ordinal) && e.Contains("will not reach QA", StringComparison.Ordinal));
        Assert.Contains(log.Entries, e => e.StartsWith("Warning: Diagnostics: replayed 2 prior terminating crash fault(s)", StringComparison.Ordinal));

        // The ledger now writes to the per-process file.
        restarted.Record("unhandled", Thrown("later crash"), terminating: true);
        Assert.True(File.Exists(CrashJournalPaths.PathFor(_directory, "ArcaneCore.World")));
        Assert.False(File.Exists(legacy));
    }
}
