using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// Each managed bot's trade ledger: per partner, fair deals, scams, the last scam and ninja times and the last counted gift day (P4 S3).
/// TEST-AUTHOR STUB: every member is the contract the tests pin; the bodies are the implementer's.
/// </summary>
internal sealed class PlayerbotTradeLedger(PlayerbotSocialTradingOptions options, PlayerbotWarModeOptions war, Func<long> clock)
{
    internal (PlayerbotSocialTradingOptions Options, PlayerbotWarModeOptions War, Func<long> Clock) Inputs => (options, war, clock);

    public void NoteFairDeal(Guid botId, int characterId, ulong other) { }
    public void NoteScam(Guid botId, int characterId, ulong other) { }
    public void NoteNinja(Guid botId, int characterId, ulong other) { }

    /// <summary>True the first time for this pair in the current UTC day (the gift counts), false after.</summary>
    public bool NoteGiftReceived(Guid botId, int characterId, ulong other) => false;

    public bool TryGet(Guid botId, ulong other, out PlayerbotTradeRecord record)
    {
        record = null!;
        return false;
    }

    /// <summary>A scam within ScamMemoryDays; with War Mode on and NeverForget the window is ignored at read time.</summary>
    public bool IsScammer(Guid botId, ulong other) => false;

    /// <summary>A ninja loot within NinjaMemoryDays; the same War Mode read rule.</summary>
    public bool IsNinja(Guid botId, ulong other) => false;

    public void Load(IEnumerable<PlayerbotTradeRecord> rows) { }
    public IReadOnlyList<PlayerbotTradeRecord> Snapshot(Guid botId) => [];
    public long Version(Guid botId) => 0;
    public void Forget(Guid botId) { }
}
