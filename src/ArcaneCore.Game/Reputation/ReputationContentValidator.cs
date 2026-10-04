using System.Globalization;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Reputation;

namespace ArcaneCore.Game.Reputation;

/// <summary>The kill-reputation rows that survive validation, and one line per row dropped or suspicious.</summary>
public sealed record ReputationOnKillValidation(IReadOnlyList<ReputationOnKillEntry> Entries, IReadOnlyList<string> Warnings);

/// <summary>
/// Load-time validation of reputation content against the loaded Faction.dbc, so a reference to a faction that does not exist is reported
/// once at startup instead of being dropped silently at kill or reward time. Rules and messages follow vmangos:
/// <c>ObjectMgr::LoadReputationOnKill</c> skips a row whose faction does not exist (ObjectMgr.cpp:8935-8957) and the quest loader
/// reports the quest reputation columns (ObjectMgr.cpp:5702-5750, 6026-6039). Pure; no world state.
/// </summary>
public static class ReputationContentValidator
{
    /// <summary>
    /// Drop the rows with a nonzero faction missing from <paramref name="factions"/> (vmangos skips the whole row); a MaxStanding
    /// of 8 or more only never limits and is reported.
    /// </summary>
    public static ReputationOnKillValidation FilterOnKill(IEnumerable<ReputationOnKillEntry> entries, FactionCatalog factions)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(factions);
        var kept = new List<ReputationOnKillEntry>();
        var warnings = new List<string>();
        foreach (ReputationOnKillEntry entry in entries)
        {
            uint missing = entry.Faction1 != 0 && factions.Find(entry.Faction1) is null ? entry.Faction1
                : entry.Faction2 != 0 && factions.Find(entry.Faction2) is null ? entry.Faction2
                : 0;
            if (missing != 0)
            {
                warnings.Add(Format($"creature_onkill_reputation: creature {entry.CreatureEntry} uses faction {missing}, which is not in Faction.dbc; row skipped"));
                continue;
            }

            if ((entry.Faction1 != 0 && entry.MaxStanding1 >= ReputationContent.RankCount) || (entry.Faction2 != 0 && entry.MaxStanding2 >= ReputationContent.RankCount))
            {
                warnings.Add(Format($"creature_onkill_reputation: creature {entry.CreatureEntry} has a MaxStanding that is not a rank; it never limits the gain"));
            }

            kept.Add(entry);
        }

        return new ReputationOnKillValidation(kept, warnings);
    }

    /// <summary>The quest_template reputation columns that name a faction missing from Faction.dbc or that no player can satisfy.</summary>
    public static IReadOnlyList<string> ValidateQuests(IEnumerable<QuestTemplate> quests, FactionCatalog factions)
    {
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(factions);
        var warnings = new List<string>();
        foreach (QuestTemplate quest in quests)
        {
            (uint Faction, int Value)[] rewards =
            [
                (quest.RewRepFaction1, quest.RewRepValue1), (quest.RewRepFaction2, quest.RewRepValue2), (quest.RewRepFaction3, quest.RewRepValue3),
                (quest.RewRepFaction4, quest.RewRepValue4), (quest.RewRepFaction5, quest.RewRepValue5),
            ];
            for (int i = 0; i < rewards.Length; i++)
            {
                if (rewards[i].Faction == 0)
                {
                    continue;
                }

                if (rewards[i].Value == 0)
                {
                    warnings.Add(Format($"Quest {quest.Entry} has RewRepFaction{i + 1} = {rewards[i].Faction} but RewRepValue{i + 1} = 0, quest will not reward this reputation"));
                }

                if (factions.Find(rewards[i].Faction) is null)
                {
                    warnings.Add(Format($"Quest {quest.Entry} has RewRepFaction{i + 1} = {rewards[i].Faction} but the faction does not exist in Faction.dbc, quest will not reward reputation for it"));
                }
            }

            Check(warnings, factions, quest.Entry, "RepObjectiveFaction", quest.RepObjectiveFaction);
            Check(warnings, factions, quest.Entry, "RequiredMinRepFaction", quest.RequiredMinRepFaction);
            Check(warnings, factions, quest.Entry, "RequiredMaxRepFaction", quest.RequiredMaxRepFaction);
            if (quest.RequiredMinRepValue != 0 && quest.RequiredMinRepValue > ReputationMath.Cap)
            {
                warnings.Add(Format($"Quest {quest.Entry} has RequiredMinRepValue = {quest.RequiredMinRepValue} but max reputation is {ReputationMath.Cap}, quest can't be done"));
            }

            if (quest.RequiredMinRepValue != 0 && quest.RequiredMaxRepValue != 0 && quest.RequiredMaxRepValue <= quest.RequiredMinRepValue)
            {
                warnings.Add(Format($"Quest {quest.Entry} has RequiredMaxRepValue = {quest.RequiredMaxRepValue} and RequiredMinRepValue = {quest.RequiredMinRepValue}, quest can't be done"));
            }
        }

        return warnings;
    }

    private static void Check(List<string> warnings, FactionCatalog factions, uint quest, string column, uint faction)
    {
        if (faction != 0 && factions.Find(faction) is null)
        {
            warnings.Add(Format($"Quest {quest} has {column} = {faction} but the faction does not exist in Faction.dbc, quest can't be done"));
        }
    }

    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
