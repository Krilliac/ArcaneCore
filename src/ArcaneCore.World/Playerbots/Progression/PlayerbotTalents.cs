using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Talents;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Talents;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Progression;

/// <summary>One talent rank to request: CMSG_LEARN_TALENT's talent id and zero-based rank.</summary>
internal readonly record struct PlayerbotTalentStep(uint TalentId, uint RankIndex, bool FromBuild);

/// <summary>
/// Which talent rank a bot learns next. Pure: the catalog, a "does the character know this spell" predicate and the free
/// points in, one step out, every candidate pre-checked with the server's own <see cref="TalentRules.EvaluateLearn"/> so the
/// bot never asks for what CMSG_LEARN_TALENT would refuse (the handler still decides).
/// <para>
/// First the build (<see cref="PlayerbotTalentBuild.Picks"/> in order: the next missing rank of the first unfinished pick).
/// When a pick does not resolve against the catalog (no talent at that page/row/column), the server would refuse it, or the
/// bot already had it refused, the build is abandoned for vmangos CombatBotBaseAI::LearnRandomTalents
/// (CombatBotBaseAI.cpp:2459): one tab, talents in tier order, each taken to its last rank before the next. vmangos picks the
/// tab and the order within a tier at random; here the tab is the one the character has invested most in (else the build's
/// first page, else one derived from the bot id) and a tier is walked by column, so a bot always continues where it left off.
/// When that tab has nothing left the other tabs follow in page order, so free points never stay unspent while a legal
/// talent exists. A finished build with points to spare (a points rate above 1) continues the same way.
/// </para>
/// </summary>
internal static class PlayerbotTalentPlanner
{
    public static PlayerbotTalentStep? Next(TalentCatalog catalog, Func<uint, bool> hasSpell, uint classMask, uint freePoints,
        PlayerbotTalentBuild build, uint botId, IReadOnlySet<(uint TalentId, uint RankIndex)>? refused = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(hasSpell);
        ArgumentNullException.ThrowIfNull(build);
        if (freePoints == 0)
        {
            return null;
        }

        IReadOnlyList<TalentTabRecord> pages = catalog.TabsForClassMask(classMask);
        if (pages.Count == 0)
        {
            return null;
        }

        foreach (PlayerbotTalentPick pick in build.Picks)
        {
            if (Resolve(catalog, pages, pick) is not { } talent)
            {
                break; // the catalog has no talent there: this build does not fit it
            }

            int target = Math.Min(pick.Ranks, talent.RankCount);
            int known = TalentRules.HighestKnownRank(talent, hasSpell);
            if (known >= target)
            {
                continue;
            }

            uint rank = (uint)known;
            if (Legal(catalog, hasSpell, talent.Id, rank, freePoints, classMask, refused))
            {
                return new PlayerbotTalentStep(talent.Id, rank, FromBuild: true);
            }

            break;
        }

        return Fallback(catalog, hasSpell, classMask, freePoints, pages, build, botId, refused);
    }

    /// <summary>The talent at a pick's page, row and column (null when the catalog has none there).</summary>
    public static TalentRecord? Resolve(TalentCatalog catalog, IReadOnlyList<TalentTabRecord> pages, PlayerbotTalentPick pick)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(pages);
        return pick.Page < pages.Count
            ? catalog.TalentsOfTab(pages[pick.Page].Id).FirstOrDefault(talent => talent.Row == pick.Row && talent.Column == pick.Column)
            : null;
    }

    private static PlayerbotTalentStep? Fallback(TalentCatalog catalog, Func<uint, bool> hasSpell, uint classMask, uint freePoints,
        IReadOnlyList<TalentTabRecord> pages, PlayerbotTalentBuild build, uint botId, IReadOnlySet<(uint, uint)>? refused)
    {
        int first = FallbackPage(catalog, hasSpell, pages, build, botId);
        for (int offset = 0; offset < pages.Count; offset++)
        {
            TalentTabRecord tab = pages[(first + offset) % pages.Count];
            foreach (TalentRecord talent in catalog.TalentsOfTab(tab.Id).OrderBy(t => t.Row).ThenBy(t => t.Column).ThenBy(t => t.Id))
            {
                int known = TalentRules.HighestKnownRank(talent, hasSpell);
                if (known < talent.RankCount && Legal(catalog, hasSpell, talent.Id, (uint)known, freePoints, classMask, refused))
                {
                    return new PlayerbotTalentStep(talent.Id, (uint)known, FromBuild: false);
                }
            }
        }

        return null;
    }

    private static int FallbackPage(TalentCatalog catalog, Func<uint, bool> hasSpell, IReadOnlyList<TalentTabRecord> pages,
        PlayerbotTalentBuild build, uint botId)
    {
        int best = -1;
        uint bestSpent = 0;
        for (int page = 0; page < pages.Count; page++)
        {
            uint spent = TalentRules.SpentInTab(catalog, pages[page].Id, hasSpell);
            if (spent > bestSpent)
            {
                best = page;
                bestSpent = spent;
            }
        }

        if (best >= 0)
        {
            return best;
        }

        return build.Picks.Count > 0 && build.Picks[0].Page < pages.Count ? build.Picks[0].Page : (int)(botId % (uint)pages.Count);
    }

    private static bool Legal(TalentCatalog catalog, Func<uint, bool> hasSpell, uint talentId, uint rank, uint freePoints,
        uint classMask, IReadOnlySet<(uint, uint)>? refused)
        => refused?.Contains((talentId, rank)) != true
            && TalentRules.EvaluateLearn(catalog, hasSpell, talentId, rank, freePoints, classMask).Outcome == TalentLearnOutcome.Ok;
}

/// <summary>
/// Talent spending for one managed player: out of combat, paced by the action budget, one rank per think through the real
/// CMSG_LEARN_TALENT handler while the talent service reports free points (vmangos CombatBotBaseAI::LearnPremadeSpecForClass
/// and LearnRandomTalents, CombatBotBaseAI.cpp:2407 and :2459, which call Player::LearnTalent directly). The handler sends no
/// reply either way, so a request whose rank did not appear is remembered as refused and not repeated until the level or the
/// free points change. Without a talent catalog (the talent feature inert) it does nothing.
/// </summary>
internal sealed class PlayerbotTalents(WorldSession session)
{
    private readonly HashSet<(uint TalentId, uint RankIndex)> _refused = [];
    private uint _refusedAtPoints;
    private byte _refusedAtLevel;

    /// <summary>The refusals remembered since the level or free points last changed (diagnostics and tests).</summary>
    internal IReadOnlyCollection<(uint TalentId, uint RankIndex)> Refused => _refused;

    /// <summary>
    /// Learn one rank when there are free points. The caller (<see cref="PlayerbotEquipment.Update"/>) has checked the bot is
    /// in the world, alive, out of combat and not casting, with budget left. Returns true when a request was sent.
    /// </summary>
    internal bool Update(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (session.Services.GetService<TalentFeature>()?.Service is not { } talents)
        {
            return false;
        }

        uint free = talents.FreePoints(player);
        if (free == 0)
        {
            return false;
        }

        if (free != _refusedAtPoints || player.Level != _refusedAtLevel)
        {
            _refused.Clear();
            _refusedAtPoints = free;
            _refusedAtLevel = player.Level;
        }

        uint botId = player.Guid.Low;
        PlayerbotTalentBuild build = PlayerbotTalentBuilds.Choose(player.Class, botId);
        Func<uint, bool> knows = spell => talents.HasSpell(player, spell);
        if (PlayerbotTalentPlanner.Next(talents.Catalog, knows, ClassMask(player), free, build, botId, _refused) is not { } step)
        {
            return false;
        }

        TalentRecord talent = talents.Catalog.ById(step.TalentId)!;
        int before = TalentRules.HighestKnownRank(talent, knows);
        if (!session.TryManagedAction(WorldOpcode.CmsgLearnTalent, Payload(step)))
        {
            return false;
        }

        if (TalentRules.HighestKnownRank(talent, knows) <= before)
        {
            _refused.Add((step.TalentId, step.RankIndex)); // refused by the handler: no reply packet exists to say why
        }

        return true;
    }

    /// <summary>vmangos Player::GetClassMask: 1 &lt;&lt; (class - 1).</summary>
    internal static uint ClassMask(Player player) => 1u << ((int)player.Class - 1);

    /// <summary>cmsg_learn_talent (build 5875, gtker/wow_messages): u32 talent id, u32 requested rank.</summary>
    internal static byte[] Payload(PlayerbotTalentStep step)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt32(step.TalentId);
        writer.WriteUInt32(step.RankIndex);
        return writer.ToArray();
    }
}
