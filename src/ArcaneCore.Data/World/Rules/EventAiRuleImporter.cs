using ArcaneCore.Data.Content;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.Rules;
using ArcaneCore.Kernel.Rules.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.World.Rules;

/// <summary>How many rows <see cref="EventAiRuleImporter.ImportAsync"/> wrote (and removed from its own id band first).</summary>
public sealed record EventAiRuleImportResult(int RulesWritten, int ActionsWritten, int TextsWritten, int RulesRemoved, int ActionsRemoved, int TextsRemoved);

/// <summary>
/// The database side of the EventAI converter: reads the imported EventAI tables into a <see cref="CreatureAiContent"/> and writes a
/// <see cref="EventAiConversion"/> into <c>rule</c>, <c>rule_action</c> and <c>rule_text</c>. The converter's rows live at ids at or above
/// <see cref="CreatureRuleIds.ConvertedRuleIdBase"/>; a run removes exactly its sub-band (2^30 up to 2^30 + 2^26 - 1) first and writes the new rows in the same transaction,
/// so running it twice leaves the same rows and a row outside the sub-band (authored, shipped or another converter's content) is never read, changed or removed.
/// Owned by the caller's thread; the context is used in place (not thread-safe).
/// </summary>
public static class EventAiRuleImporter
{
    private const int Batch = 2000;

    /// <summary>Reads <c>creature_ai_scripts</c>, <c>creature_ai_texts</c>, <c>broadcast_text</c> and <c>creature_ai_summons</c> as the loader does.</summary>
    public static async Task<CreatureAiContent> LoadSourceAsync(WorldDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        List<CreatureAiScriptRow> scripts = await db.Set<CreatureAiScriptRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureAiTextRow> texts = await db.Set<CreatureAiTextRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<BroadcastTextRow> broadcastTexts = await db.Set<BroadcastTextRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureAiSummonRow> summons = await db.Set<CreatureAiSummonRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return new CreatureAiContent(
            scripts.Select(CreatureAiDataModule.ToEvent),
            texts.Select(CreatureAiDataModule.ToText),
            new BroadcastTextCatalog(broadcastTexts.Select(EfCreatureDataStore.ToBroadcastText)),
            summons.Select(s => new CreatureAiSummon(s.Id, s.X, s.Y, s.Z, s.Orientation, s.SpawnTimeSeconds)));
    }

    /// <summary>Replaces the converter's id sub-band (2^30 up to 2^30 + 2^26 - 1, RuleIdBands.EventAi) with <paramref name="conversion"/> in one transaction.</summary>
    public static async Task<EventAiRuleImportResult> ImportAsync(WorldDbContext db, EventAiConversion conversion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(conversion);
        const int first = RuleIdBands.EventAi;
        const int end = RuleIdBands.EventAi + RuleIdBands.SubBandSize;
        RuleRow[] rules = [.. conversion.Content.Rules.OrderBy(r => r.Id).Select(ToRow)];
        RuleActionRow[] actions = [.. conversion.Content.Actions.OrderBy(a => a.OwnerId).ThenBy(a => a.Seq).Select(ToRow)];
        RuleTextRow[] texts = [.. conversion.Content.Texts.OrderBy(t => t.Id).Select(ToRow)];

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        int actionsRemoved = await db.Set<RuleActionRow>().Where(a => a.RuleId >= first && a.RuleId < end).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        int rulesRemoved = await db.Set<RuleRow>().Where(r => r.Id >= first && r.Id < end).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        int textsRemoved = await db.Set<RuleTextRow>().Where(t => t.Id >= first && t.Id < end).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await AddAsync(db, texts, cancellationToken).ConfigureAwait(false);
        await AddAsync(db, rules, cancellationToken).ConfigureAwait(false);
        await AddAsync(db, actions, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return new EventAiRuleImportResult(rules.Length, actions.Length, texts.Length, rulesRemoved, actionsRemoved, textsRemoved);
    }

    private static async Task AddAsync<T>(WorldDbContext db, T[] rows, CancellationToken cancellationToken)
        where T : class
    {
        for (int offset = 0; offset < rows.Length; offset += Batch)
        {
            db.Set<T>().AddRange(rows.Skip(offset).Take(Batch));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
    }

    private static RuleRow ToRow(RuleRecord r) => new()
    {
        Id = r.Id,
        Host = r.Host,
        OwnerKind = r.OwnerKind,
        OwnerId = r.OwnerId,
        EventType = r.EventType,
        EventParam1 = r.EventParam1,
        EventParam2 = r.EventParam2,
        EventParam3 = r.EventParam3,
        EventParam4 = r.EventParam4,
        PhaseMask = r.PhaseMask,
        FilterId = r.FilterId,
        ChancePct = r.ChancePct,
        ConditionId = r.ConditionId,
        CooldownMinMs = r.CooldownMinMs,
        CooldownMaxMs = r.CooldownMaxMs,
        Priority = r.Priority,
        Flags = r.Flags,
        Enabled = (byte)(r.Enabled ? 1 : 0),
        Comment = r.Comment,
    };

    private static RuleActionRow ToRow(RuleActionRecord a) => new()
    {
        RuleId = a.OwnerId,
        Seq = a.Seq,
        ActionType = a.ActionType,
        Param1 = a.Param1,
        Param2 = a.Param2,
        Param3 = a.Param3,
        Param4 = a.Param4,
        Param5 = a.Param5,
        Param6 = a.Param6,
        TargetType = a.TargetType,
        TargetParam1 = a.TargetParam1,
        TargetParam2 = a.TargetParam2,
        TargetParam3 = a.TargetParam3,
        DelayMs = a.DelayMs,
        Flags = a.Flags,
    };

    private static RuleTextRow ToRow(RuleTextRecord t) => new()
    {
        Id = t.Id,
        ContentDefault = t.ContentDefault,
        Sound = t.Sound,
        Type = t.Type,
        Language = t.Language,
        Emote = t.Emote,
        BroadcastTextId = t.BroadcastTextId,
        Comment = t.Comment,
    };
}
