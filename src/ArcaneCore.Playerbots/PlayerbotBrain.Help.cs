using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Groups;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots;

/// <summary>What a bot that lends a hand to another character has been asked to do (<see cref="PlayerbotBrain.RequestHelp"/>).</summary>
internal enum PlayerbotHelpKind : byte
{
    /// <summary>Nothing pending.</summary>
    None,

    /// <summary>Join the fight of a player or bot in trouble: attack its attacker through the assist lane (a healer class heals the victim first, <see cref="PlayerbotBrain.TryHealTagger"/>).</summary>
    Assist,

    /// <summary>Resurrect a dead player or bot (its corpse in reach) with the class resurrection spell.</summary>
    Resurrect,

    /// <summary>Cast the class buff it casts on groupmates on a stranger.</summary>
    Buff,
}

/// <summary>
/// The brain's side of helping a stranger (docs/areas/playbots-social.md, Helping). The social pass (world thread) decides who to help and files
/// one request; the next think of an idle bot (never one in a fight, resting or recovering) carries it out before the goal ladder, through the
/// ordinary paths: an assist is the lane <see cref="DropIfSomeoneElses"/> uses (<see cref="PlayerbotContention.BeginAssist"/>, then the attack and the
/// heal of <see cref="TryHealTagger"/>), a resurrection or a buff is one CMSG_CAST_SPELL. A request nobody carried out within
/// <see cref="HelpWaitMs"/> lapses. Memory: three fields.
/// </summary>
internal sealed partial class PlayerbotBrain
{
    /// <summary>How long (milliseconds) a request waits for a think that can carry it out.</summary>
    internal const uint HelpWaitMs = 20_000;

    private PlayerbotHelpKind _helpKind;
    private ObjectGuid _helpTarget;
    private uint _helpUntilMs;

    // The attacker an assist took on and whom it was for: the help is recorded once the bot has really hurt the attacker (it is on the attacker's threat list).
    private Creature? _assistAttacker;
    private ObjectGuid _assistVictim;
    private uint _assistUntilMs;

    private const uint AssistCreditMs = 60_000;

    private void CheckAssistCredit(Player player)
    {
        if (_assistAttacker is not { } attacker) return;
        if (attacker.IsInWorld && attacker.Combat.HasThreatList && attacker.Combat.Threat.GetThreat(player) > 0f)
        {
            if (player.Map?.FindPlayer(_assistVictim) is { } victim) Social?.NoteHelp(player, victim);
            _assistAttacker = null;
        }
        else if (!attacker.IsAlive || !attacker.IsInWorld || unchecked((int)(_assistUntilMs - _session.World.NowMs)) <= 0)
        {
            _assistAttacker = null;
        }
    }

    /// <summary>The request waiting for a think, or <see cref="PlayerbotHelpKind.None"/>.</summary>
    internal PlayerbotHelpKind PendingHelp => _helpKind;

    /// <summary>The kind of the latest request a think carried out (inspection and tests).</summary>
    internal PlayerbotHelpKind LastHelpKind { get; private set; }

    /// <summary>The character the latest carried-out request was for.</summary>
    internal ObjectGuid LastHelpTarget { get; private set; }

    /// <summary>Requests carried out so far.</summary>
    internal int HelpsDone { get; private set; }

    /// <summary>
    /// File a request to help <paramref name="target"/>. False when one is already pending (one at a time) or the request is empty. The caller has
    /// judged the bot free and the target in need; the think checks the facts again before it acts.
    /// </summary>
    internal bool RequestHelp(PlayerbotHelpKind kind, ObjectGuid target)
    {
        if (_helpKind != PlayerbotHelpKind.None || kind == PlayerbotHelpKind.None || target.IsEmpty) return false;
        _helpKind = kind;
        _helpTarget = target;
        _helpUntilMs = unchecked(_session.World.NowMs + HelpWaitMs);
        return true;
    }

    private void ClearHelp()
    {
        _helpKind = PlayerbotHelpKind.None;
        _helpTarget = default;
    }

    private void DoneHelp(PlayerbotHelpKind kind, ObjectGuid target)
    {
        LastHelpKind = kind;
        LastHelpTarget = target;
        HelpsDone++;
        ClearHelp();
    }

    /// <summary>The think's step for a pending request: 0 nothing to do, 1 it used the think, 2 the bot took the attacker as its target (the caller goes on to the target step).</summary>
    private int TakeHelpErrand(Player player)
    {
        if (unchecked((int)(_helpUntilMs - _session.World.NowMs)) <= 0 || !player.IsAlive)
        {
            ClearHelp();
            return 0;
        }

        ObjectGuid targetGuid = _helpTarget;
        Player? target = player.Map?.FindPlayer(targetGuid);
        if (target is null || !ReferenceEquals(target.Map, player.Map))
        {
            ClearHelp();
            return 0;
        }

        switch (_helpKind)
        {
            case PlayerbotHelpKind.Assist:
            {
                if (!target.IsAlive || !target.Combat.IsInCombat || target.Team != player.Team)
                {
                    ClearHelp();
                    return 0;
                }

                bool mayFlag = PlayerbotHelpRules.MayFlag(player, _options.Social.WarMode.Enabled);
                int groupSize = _session.Services.GetService<ArcaneCore.World.Social.SocialFeature>()?.Context.Groups.GetGroup(player.Guid)?.MemberCount ?? 1;
                if (PlayerbotHelpRules.AttackerOf(player, target, mayFlag, groupSize) is not { } attacker)
                {
                    ClearHelp();
                    return 0;
                }

                // The same state DropIfSomeoneElses leaves when it decides to help a tagger: the mob is held as an assist-only target.
                _contention.ReleaseClaim();
                _contention.BeginAssist(attacker.Guid, target.Guid);
                _target = attacker;
                TargetEntry = attacker.Entry;
                AbandonRoute();
                _attacking = false;
                _lootOpened = false;
                _goal = PlayerbotGoalKind.Combat;
                _assistAttacker = attacker;
                _assistVictim = target.Guid;
                _assistUntilMs = unchecked(_session.World.NowMs + AssistCreditMs);
                DoneHelp(PlayerbotHelpKind.Assist, targetGuid);
                return 2;
            }

            case PlayerbotHelpKind.Resurrect:
                return CastOnStranger(player, target, PlayerbotHelpKind.Resurrect);
            case PlayerbotHelpKind.Buff:
                return CastOnStranger(player, target, PlayerbotHelpKind.Buff);
            default:
                ClearHelp();
                return 0;
        }
    }

    // A resurrection or a buff: stop, then one cast. A bot that cannot cast it now (no spell, out of reach, refused lately) drops the request.
    private int CastOnStranger(Player player, Player target, PlayerbotHelpKind kind)
    {
        // The body to reach: a released ghost's is its corpse, not the unit (which stands at the graveyard).
        WorldObject body = target;
        if (kind == PlayerbotHelpKind.Resurrect && (target.Flags & PlayerFlags.Ghost) != 0 && target.Combat.Corpse is { IsInWorld: true } ghostCorpse && ReferenceEquals(ghostCorpse.Map, player.Map))
            body = ghostCorpse;
        if (player.Combat.IsInCombat || Distance(player, body) > PlayerbotHelpRules.CastYards)
        {
            ClearHelp();
            return 0;
        }

        SpellInfo? spell;
        Corpse? corpse = null;
        if (kind == PlayerbotHelpKind.Resurrect)
        {
            if (target.IsAlive || ArcaneCore.Game.Death.Resurrection.ResurrectionRequests.IsRequested(target)
                || PlayerbotGroupAI.FindResurrectionSpell(_session, player) is not { } rez)
            {
                ClearHelp();
                return 0;
            }

            spell = rez;
            // An unreleased body is the player's unit; once it released, the cast goes at its corpse (as a 1.12 client does).
            if ((target.Flags & PlayerFlags.Ghost) != 0)
            {
                corpse = target.Combat.Corpse;
                if (corpse is null || !corpse.IsInWorld || !ReferenceEquals(corpse.Map, player.Map) || Distance(player, corpse) > PlayerbotHelpRules.CastYards)
                {
                    ClearHelp();
                    return 0;
                }
            }
        }
        else
        {
            string? name = PlayerbotHelpRules.BuffOf(player.Class);
            spell = name is null || !target.IsAlive ? null : _combatSpells.Abilities(player)[name];
            if (spell is null || !_combatSpells.CanCastNow(player, spell, target))
            {
                ClearHelp();
                return 0;
            }
        }

        if (!PlayerbotMovementControl.Stop(_session, player)) return 1; // still moving: try again next think
        _route = null;
        bool cast = corpse is not null ? _combatSpells.CastAtCorpse(player, spell, corpse) : _combatSpells.CastAt(player, spell, target);
        if (cast)
        {
            Social?.NoteHelp(player, target, kind == PlayerbotHelpKind.Resurrect ? PlayerbotThanksKind.Resurrect : PlayerbotThanksKind.Buff);
            DoneHelp(kind, target.Guid);
        }
        else
        {
            ClearHelp();
        }

        return 1;
    }
}
