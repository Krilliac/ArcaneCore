using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Combat;
using Microsoft.Extensions.DependencyInjection;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// War Mode, initiating and helping, from the brain's side (<c>World:WarMode</c>, docs/areas/world-state.md): a player the bot has taken on that did not provoke it.
/// Two reasons put a player there: the help lane asked the bot to lend a hand against a flagged enemy attacker
/// (<see cref="PlayerbotEngageReason.Help"/>, also with War Mode off) and the hunt (<see cref="PlayerbotEngageReason.Grudge"/>): while War Mode is on
/// an idle bot picks the nearest flagged enemy-faction player or bot within <c>Social:WarMode:HuntRadiusYards</c> its war layer marks Rival and attacks it.
/// Either way the fight itself is <see cref="TryFightPlayer"/>'s; this decides whom, and lets go when the target enters an exempt area, dies or
/// runs out of reach. Memory only. World thread.
/// </summary>
internal sealed partial class PlayerbotBrain
{
    /// <summary>The hunt looks for a grudge target at most this often (milliseconds).</summary>
    internal const uint HuntScanMs = 2_000;

    /// <summary>A hunt follows its target this far past the hunt radius before it lets go.</summary>
    private const float HuntChaseMarginYards = 10f;

    private Player? _engaged;

    // The player the bot itself started on (a help, a grudge or an initiate), kept while that fight lasts: a fight the bot started is not chased into an exempt area (TryFightPlayer).
    private Player? _startedOn;
    private PlayerbotEngageReason _engagedReason;
    private uint _huntScanAtMs;
    private uint _initiateScanAtMs;
    private bool _initiateScanned;
    private bool _huntScanned;

    /// <summary>The player the bot has taken on by help or by the hunt (inspection and tests); null when none.</summary>
    internal Player? EngagedPlayer => _engaged;

    /// <summary>Why <see cref="EngagedPlayer"/> was taken on.</summary>
    internal PlayerbotEngageReason EngagedReason => _engagedReason;

    /// <summary>Take <paramref name="target"/> on for <paramref name="reason"/>; the next think's <see cref="TryFightPlayer"/> fights it.</summary>
    private void Engage(Player target, PlayerbotEngageReason reason)
    {
        _engaged = target;
        _startedOn = target;
        _engagedReason = reason;
    }

    /// <summary>
    /// The engaged player while it is still fair game: <see cref="PlayerbotHostility.MayEngage"/> for its reason (so a target that entered an
    /// exempt area, or War Mode switched off under a hunt, ends it), not set aside as unreachable and within reach. Otherwise the engagement ends
    /// and the fight against it is stopped.
    /// </summary>
    private Player? EngagedOpponent(Player player)
    {
        if (_engaged is not { } target) return null;
        float reach = _engagedReason == PlayerbotEngageReason.Initiate
            ? _options.Pvp.Initiate.RadiusYards + HuntChaseMarginYards
            : _engagedReason == PlayerbotEngageReason.Grudge
            ? _options.Social.WarMode.HuntRadiusYards + HuntChaseMarginYards
            : PvpPursuitYards;
        if (PlayerbotHostility.MayEngage(player, target, _engagedReason, null, _options.Social.WarMode.SharedSwitch(), _options.Pvp.Initiate)
            && _pvpFight?.IsSkipped(target) != true && Distance(player, target) <= reach)
            return target;

        _engaged = null;
        if (_pvpFight is { Target: { } fighting } ended && ReferenceEquals(fighting, target))
        {
            ended.Stop(player);
            AbandonRoute();
        }

        return null;
    }

    /// <summary>
    /// Whether the hunt looks for a target in this think: War Mode is on, nothing is engaged yet and the last look was at least <see cref="HuntScanMs"/> ago.
    /// With War Mode off this is one flag read.
    /// </summary>
    private bool HuntScanDue()
    {
        if (!_options.Social.WarMode.Enabled || _engaged is not null) return false;
        uint now = _session.World.NowMs;
        if (_huntScanned && unchecked((int)(now - _huntScanAtMs)) < HuntScanMs) return false;
        _huntScanned = true;
        _huntScanAtMs = now;
        return true;
    }

    /// <summary>
    /// The hunt: while War Mode is on and the bot is idle (alive, out of combat and duels, no defend post, no resurrection sickness, not in a group with a real
    /// player), it looks for the nearest visible player or bot of the other faction within the hunt radius that is PvP-flagged, outside the exempt areas and a
    /// Rival in its war layer, and takes it on (the think calls this when <see cref="HuntScanDue"/>). True when it did and the fight has begun.
    /// </summary>
    internal bool TryHunt(Player player, uint interval)
    {
        PlayerbotWarModeOptions war = _options.Social.WarMode;
        if (!war.Enabled || _engaged is not null || _defend is not null || !player.IsAlive || player.Combat.IsInCombat || player.Duel is not null) return false;
        if (!PlayerbotHelpRules.IsFreeGoal(_goal)) return false;
        if (Social is not { Enabled: true } recorder || !recorder.TryBot(player.Guid, out Guid botId, out _)) return false;
        if (PlayerbotSickness.Holder(_session, player) is { IsPermanent: false, Duration: > 0 } || InGroupWithRealPlayer(player)) return false;

        Player? best = null;
        float bestYards = war.HuntRadiusYards;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (!guid.IsPlayer || guid == player.Guid) continue;
            if (_session.World.FindOnlinePlayer(guid) is not { } other) continue;
            if (other.Team == player.Team || !PlayerbotHelpRules.IsPvpFlagged(other) || _pvpFight?.IsSkipped(other) == true) continue;
            float yards = Distance(player, other);
            if (yards > bestYards || (best is not null && yards >= bestYards)) continue;
            if (!recorder.Memory.TryGet(botId, other.Guid.Value, out PlayerbotRelation relation) || !recorder.Memory.IsRival(relation)) continue;
            if (!PlayerbotHostility.MayEngage(player, other, PlayerbotEngageReason.Grudge, null, war.SharedSwitch())) continue;
            best = other;
            bestYards = yards;
        }

        if (best is null) return false;
        Engage(best, PlayerbotEngageReason.Grudge);
        FightUnit(player, best, interval);
        return true;
    }

    /// <summary>
    /// Whether the initiate rule looks for a target in this think: <c>Pvp:Initiate:Enabled</c>, nothing engaged yet, and the last look at least
    /// <see cref="HuntScanMs"/> ago. With the rule off this is one flag read.
    /// </summary>
    private bool InitiateScanDue()
    {
        if (!_options.Pvp.Initiate.Enabled || _engaged is not null) return false;
        uint now = _session.World.NowMs;
        if (_initiateScanned && unchecked((int)(now - _initiateScanAtMs)) < HuntScanMs) return false;
        _initiateScanned = true;
        _initiateScanAtMs = now;
        return true;
    }

    /// <summary>
    /// World PvP (<c>World:Playerbots:Pvp:Initiate</c>, independent of War Mode): an idle bot (alive, out of combat and duels, a free goal, no defend post, no
    /// resurrection sickness, not in a group with a real player, at least <c>MinHealthPct</c> health) takes on the nearest visible player or bot of the other
    /// faction within <c>RadiusYards</c> that is PvP-flagged (so the combat hooks allow it), outside the exempt areas and inside the level window
    /// (<c>MaxLevelBelow</c> under the bot to <c>MaxLevelAbove</c> over it), unless that would leave it outnumbered: more living enemy-faction players and bots within
    /// the radius than living same-faction ones, the bot itself counted among its friends. Players the bot's sight does not reach are not counted. True when
    /// it did and the fight has begun (the think calls this when <see cref="InitiateScanDue"/>, after the war hunt).
    /// </summary>
    internal bool TryInitiate(Player player, uint interval)
    {
        PlayerbotPvpInitiateOptions rule = _options.Pvp.Initiate;
        if (!rule.Enabled || _engaged is not null || _defend is not null || !player.IsAlive || player.Combat.IsInCombat || player.Duel is not null) return false;
        if (!PlayerbotHelpRules.IsFreeGoal(_goal) || player.MaxHealth == 0 || player.Health * 100UL < (ulong)rule.MinHealthPct * player.MaxHealth) return false;
        if (PlayerbotSickness.Holder(_session, player) is { IsPermanent: false, Duration: > 0 } || InGroupWithRealPlayer(player)) return false;

        Player? best = null;
        float bestYards = rule.RadiusYards;
        int friends = 1, enemies = 0; // the bot counts as one of its own
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (!guid.IsPlayer || guid == player.Guid) continue;
            if (_session.World.FindOnlinePlayer(guid) is not { IsAlive: true } other || !ReferenceEquals(other.Map, player.Map)) continue;
            float yards = Distance(player, other);
            if (yards > rule.RadiusYards) continue;
            if (other.Team == player.Team)
            {
                friends++;
                continue;
            }

            enemies++;
            if (yards >= bestYards && best is not null) continue;
            if (other.Level + rule.MaxLevelBelow < player.Level || other.Level > player.Level + rule.MaxLevelAbove) continue;
            if (!PlayerbotHelpRules.IsPvpFlagged(other) || _pvpFight?.IsSkipped(other) == true) continue;
            if (!PlayerbotHostility.MayEngage(player, other, PlayerbotEngageReason.Initiate, null, null, rule)) continue;
            best = other;
            bestYards = yards;
        }

        if (best is null || enemies > friends) return false;
        Engage(best, PlayerbotEngageReason.Initiate);
        FightUnit(player, best, interval);
        return true;
    }

    /// <summary>Whether <paramref name="other"/> is the player the bot itself started on and it now stands in a War Mode exempt area (the bot lets go of it).</summary>
    private bool StartedAndExempt(Player other) => ReferenceEquals(other, _startedOn) && PvpAreaState.IsWarModeProtected(other);

    // A group with a real player in it follows that player's orders (or the group coordinator's), not the hunt. A member who is offline counts as real
    // unless the social recorder knows it as a bot.
    private bool InGroupWithRealPlayer(Player player)
    {
        if (_session.Services.GetService<ArcaneCore.World.Social.SocialFeature>()?.Context.Groups.GetGroup(player.Guid) is not { } group) return false;
        foreach (ArcaneCore.Game.Groups.GroupMemberSlot slot in group.Members)
        {
            if (slot.Guid == player.Guid) continue;
            if (_session.World.FindOnlinePlayer(slot.Guid) is { } member)
            {
                if (member.Session is not WorldSession { IsManaged: true }) return true;
            }
            else if (Social is not { } recorder || !recorder.TryBot(slot.Guid, out _, out _)) return true;
        }

        return false;
    }
}
