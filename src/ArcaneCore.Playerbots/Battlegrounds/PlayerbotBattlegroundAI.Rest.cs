using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Battlegrounds;

/// <summary>
/// Eating and drinking between fights in a match. vmangos BattleBotAI::UpdateAI (BattleBotAI.cpp:849-853) calls DrinkAndEat
/// (:173-229) whenever the bot is out of combat: with no victim, not mounted, not carrying a Warsong Gulch flag, it stops and drinks
/// below full mana and eats below full health until the aura ends. Without it a caster carried its empty mana from one fight into the
/// next for the whole match (bot QA #813, Warsong Gulch: a priest below 10% mana for 45 s of one fight, again 85 s later in the next).
/// The battle bots of vmangos cast the food and drink spells for free; these bots use what their bags hold (PlayerbotConsumables, as the
/// group AI does between pulls), from <see cref="RestBelowPct"/> until full or <see cref="RestMaxMs"/>.
/// </summary>
internal sealed partial class PlayerbotBattlegroundAI
{
    /// <summary>A bot starts eating or drinking below this share of its health or mana (the group AI's threshold between pulls).</summary>
    internal const uint RestBelowPct = Groups.PlayerbotGroupAI.RestBelowPct;

    /// <summary>A rest ends after this long at the latest (one food or drink lasts up to 30 s in 1.12; the next think may start another).</summary>
    internal const uint RestMaxMs = 30_000;

    private PlayerbotConsumableKind _restKind;
    private long _restUntilMs = -1;

    /// <summary>The rests the bot started in its matches (inspection and tests).</summary>
    internal int Rests { get; private set; }

    /// <summary>
    /// One think with no enemy to fight: go on with a rest that is running, or start one when the bot needs it and can (true: the think is
    /// spent resting). A flag carrier never stops (BattleBotAI.cpp:191-194).
    /// </summary>
    private bool RestBetweenFights(Player player, bool carrier)
    {
        if (_restUntilMs >= 0)
        {
            if (!carrier && ContinueRest(player)) return true;
            EndRest(player);
            return false;
        }

        if (carrier || player.Combat.IsInCombat || ArcaneCore.Game.Locomotion.MountService.IsMounted(player)
            || _session.Services.GetService<SpellFeature>() is not { } spells
            || spells.System.GetState(player.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting }
            || PlayerbotConsumables.IsEatingOrAboutTo(player, spells.System)
            || !PlayerbotConsumables.TryFindRecovery(player, spells.System, RestBelowPct, RestBelowPct, out PlayerbotConsumable consumable))
            return false;

        Activity = "resting";
        _route = null;
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0)
        {
            PlayerbotMovementControl.Stop(_session, player);
            return true;
        }

        if (player.StandState != StandState.Sit)
        {
            Act(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes((uint)StandState.Sit));
            return true;
        }

        if (!Act(WorldOpcode.CmsgUseItem, PlayerbotNavigation.UseItemPayload(consumable.Item.BagSlot, consumable.Item.Slot))) return true;
        _restKind = consumable.Kind;
        _restUntilMs = Now + RestMaxMs;
        Rests++;
        return true;
    }

    /// <summary>A rest goes on while the food or drink aura runs, the bot still needs it, nothing hit it and the time is not up.</summary>
    private bool ContinueRest(Player player)
    {
        if (player.Combat.IsInCombat || Now >= _restUntilMs || !PlayerbotConsumables.NeedsRecovery(player, _restKind)
            || _session.Services.GetService<SpellFeature>() is not { } spells || !PlayerbotConsumables.IsEatingOrAboutTo(player, spells.System))
            return false;

        Activity = "resting";
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(_session, player);
        return true;
    }

    /// <summary>The rest is over (or an enemy came): stand up, which also ends a food or drink still running (STANDING_CANCELS).</summary>
    private void EndRest(Player player)
    {
        if (_restUntilMs < 0) return;
        _restUntilMs = -1;
        if (player.IsAlive && player.StandState == StandState.Sit)
            Act(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes((uint)StandState.Stand));
    }
}
