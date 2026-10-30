using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Zones;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>
/// zzOLDUnstable Power 24660, the Zandalarian Hero Charm (vmangos SpellEffects.cpp:3832-3834, RemoveAuraHolderFromStack, Unit.cpp:3672-3686): the
/// target loses one stack of Unstable Power 24659; the aura goes when its last stack does.
/// </summary>
[SpellScript(24660)]
public sealed class UnstablePowerScript : ISpellScript
{
    public const uint UnstablePower = 24659;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        SpellAuraHolder? holder = context.System.GetAuras(context.Target).FirstOrDefault(h => h.Spell.Id == UnstablePower && !h.IsRemoved);
        if (holder is not null && context.System.ModAuraStackAmount(holder, -1))
        {
            context.System.RemoveAuras(context.Target, UnstablePower);
        }
    }
}

/// <summary>
/// EPL PvP "A Game of Towers": Tower Capture Test (DND) 30882 (vmangos SpellEffects.cpp:4413-4427): every living friendly player within
/// <see cref="Range"/> yards of the caster, for whom outdoor PvP is active (alive, not a GM, not invisible or stealthed, PvP desired or a PvP
/// realm, not on a taxi; Player::IsOutdoorPvPActive, Player.cpp:6720-6724), casts Tower Capture 31929 on itself.
/// </summary>
[SpellScript(30882)]
public sealed class TowerCaptureTestScript : ISpellScript
{
    public const float Range = 100f; // VISIBILITY_DISTANCE_NORMAL, ObjectDefines.h:38
    public const uint TowerCapture = 31929;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit caster = context.Caster;
        if (caster.Map is not { } map)
        {
            return;
        }

        bool pvpRealm = WorldStateHooks.For(map.World).Zones.PvpRealmMode != PvpRealmMode.Normal;
        foreach (Player player in map.World.OnlinePlayers.ToArray())
        {
            if (!ReferenceEquals(player.Map, map) || WorldDummyHelpers.Dist(caster, player) > Range
                || !context.System.Relations.IsFriendly(caster, player) || !OutdoorPvPActive(context.System, player, pvpRealm))
            {
                continue;
            }

            context.System.CastSpell(player, TowerCapture, SpellCastTargets.ForSelf(), triggered: true);
        }
    }

    internal static bool OutdoorPvPActive(SpellSystem system, Player player, bool pvpRealm)
        => player.IsAlive && !player.IsGameMaster && (player.UnitFlags & UnitFlags.TaxiFlight) == 0
            && ((player.Flags & PlayerFlags.PvpDesired) != 0 || pvpRealm)
            && !system.GetAuras(player).Any(h => !h.IsRemoved && (h.HasAura(AuraType.ModStealth) || h.HasAura(AuraType.ModInvisibility)));
}
