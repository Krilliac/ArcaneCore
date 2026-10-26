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
}
