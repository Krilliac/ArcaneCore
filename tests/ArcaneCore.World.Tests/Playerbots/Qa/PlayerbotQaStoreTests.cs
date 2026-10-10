using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>The findings file (atomic, fail-closed) and the publish ledger inside it.</summary>
public sealed class PlayerbotQaStoreTests : IDisposable
{
    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    private static QaStoreFile Sample() => new()
    {
        SaltBase64 = Convert.ToBase64String([1, 2, 3, 4]),
        Findings = [QaData.View("aaaaaaaaaaaaaaaa", count: 7, subject: "quest 1656") with { Publication = new QaPublication(QaPublicationState.Published, 123), OccurrencesReported = 5 }],
        Bots = new Dictionary<string, ulong[]> { ["aaaaaaaaaaaaaaaa"] = [11UL, 22UL] },
        Ledger = new QaLedgerState([QaData.T0], [QaData.T0.AddMinutes(1), QaData.T0.AddMinutes(2)]),
    };

    [Fact]
    public void NoFile_LoadsNull_AndDoesNotDisablePersistence()
    {
        var log = new ListLogger();
        var store = new PlayerbotQaStore(_directory, log);

        Assert.Null(store.Load());

        Assert.Null(store.DisabledReason);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsFindingsBotsSaltAndLedger()
    {
        var store = new PlayerbotQaStore(_directory, new ListLogger());
        Assert.True(store.Save(Sample()));

        QaStoreFile loaded = new PlayerbotQaStore(_directory, new ListLogger()).Load()!;

        QaFindingView f = Assert.Single(loaded.Findings);
        Assert.Equal("aaaaaaaaaaaaaaaa", f.Id);
        Assert.Equal(7, f.Count);
        Assert.Equal(new QaPublication(QaPublicationState.Published, 123), f.Publication);
        Assert.Equal(5, f.OccurrencesReported);
        Assert.Equal([11UL, 22UL], loaded.Bots["aaaaaaaaaaaaaaaa"]);
        Assert.Equal(Convert.ToBase64String([1, 2, 3, 4]), loaded.SaltBase64);
        Assert.Equal([QaData.T0], loaded.Ledger.CreatesUtc);
        Assert.Equal(2, loaded.Ledger.CommentsUtc.Length);
    }

    [Fact]
    public void Save_LeavesNoTempFile_AndCreatesTheDirectory()
    {
        string nested = Path.Combine(_directory, "a", "b");
        var store = new PlayerbotQaStore(nested, new ListLogger());

        Assert.True(store.Save(Sample()));

        Assert.True(File.Exists(store.FilePath));
        Assert.Empty(Directory.GetFiles(nested, "*.tmp"));
    }

    [Fact]
    public void CorruptFile_DisablesPersistence_AndIsNeverOverwritten()
    {
        var log = new ListLogger();
        var store = new PlayerbotQaStore(_directory, log);
        File.WriteAllText(store.FilePath, "{ this is not json");

        Assert.Null(store.Load());
        Assert.NotNull(store.DisabledReason);
        Assert.False(store.Save(Sample()));

        Assert.Equal("{ this is not json", File.ReadAllText(store.FilePath));
        Assert.Single(log.At(LogLevel.Error));
    }

    [Fact]
    public void NewerFormat_DisablesPersistence_AndIsKeptUntouched()
    {
        var store = new PlayerbotQaStore(_directory, new ListLogger());
        const string newer = "{\"FormatVersion\": 99, \"Findings\": []}";
        File.WriteAllText(store.FilePath, newer);

        Assert.Null(store.Load());

        Assert.Contains("format version 99", store.DisabledReason, StringComparison.Ordinal);
        Assert.False(store.Save(Sample()));
        Assert.Equal(newer, File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void NullDocument_DisablesPersistence()
    {
        var store = new PlayerbotQaStore(_directory, new ListLogger());
        File.WriteAllText(store.FilePath, "null");

        Assert.Null(store.Load());
        Assert.NotNull(store.DisabledReason);
    }

    [Fact]
    public void UnwritableFolder_Save_ReturnsFalse_AndLogsOnce()
    {
        string blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "a file where the folder should be");
        var log = new ListLogger();
        var store = new PlayerbotQaStore(Path.Combine(blocker, "qa"), log);

        Assert.False(store.Save(Sample()));
        Assert.False(store.Save(Sample()));

        Assert.Single(log.At(LogLevel.Error));
        Assert.Null(store.DisabledReason); // a failed write is retried, not a reason to stop
    }

    // ----- the ledger -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Ledger_AllowsUpToTheMax_ThenBlocks_ThenForgetsAfterAnHour()
    {
        var ledger = new QaLedger();
        Assert.True(ledger.CanCreate(QaData.T0, 2));
        ledger.RecordCreate(QaData.T0);
        ledger.RecordCreate(QaData.T0.AddMinutes(10));

        Assert.False(ledger.CanCreate(QaData.T0.AddMinutes(30), 2));
        Assert.False(ledger.CanCreate(QaData.T0.AddMinutes(59), 2));
        Assert.True(ledger.CanCreate(QaData.T0.AddMinutes(60), 2)); // the first one is exactly an hour old
        Assert.False(ledger.CanCreate(QaData.T0.AddMinutes(60), 0));
    }

    [Fact]
    public void Ledger_CreatesAndCommentsAreCountedSeparately()
    {
        var ledger = new QaLedger();
        ledger.RecordCreate(QaData.T0);

        Assert.True(ledger.CanComment(QaData.T0, 1));
        ledger.RecordComment(QaData.T0);
        Assert.False(ledger.CanComment(QaData.T0, 1));
        Assert.False(ledger.CanCreate(QaData.T0, 1));
        Assert.True(ledger.CanCreate(QaData.T0, 2));
    }

    [Fact]
    public void Ledger_ExportRestore_KeepsTheCapAcrossARestart()
    {
        var before = new QaLedger();
        for (int i = 0; i < 5; i++) before.RecordCreate(QaData.T0.AddMinutes(i));
        before.RecordComment(QaData.T0);

        var after = new QaLedger();
        after.Restore(before.Export());

        Assert.False(after.CanCreate(QaData.T0.AddMinutes(10), 5));
        Assert.False(after.CanComment(QaData.T0.AddMinutes(10), 1));
        Assert.True(after.CanCreate(QaData.T0.AddMinutes(61), 5));
    }

    [Fact]
    public void Ledger_RestoreNull_Clears()
    {
        var ledger = new QaLedger();
        ledger.RecordCreate(QaData.T0);

        ledger.Restore(null);

        Assert.True(ledger.CanCreate(QaData.T0, 1));
    }
}
