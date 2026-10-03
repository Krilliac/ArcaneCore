using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Locomotion;

/// <summary>
/// Spell help for the liquid rules, which run from movement and map code that cannot reach <see cref="SpellSystem"/> (a feature of
/// the world daemon). The daemon registers the implementation with <see cref="LocomotionEnvironment.RegisterSpellBridge"/>.
/// </summary>
public interface IEnvironmentSpellBridge
{
    /// <summary>vmangos Unit::RemoveAurasWithInterruptFlags.</summary>
    void RemoveAurasWithInterruptFlags(Unit unit, SpellAuraInterruptFlags flags);

    /// <summary>vmangos Unit::InterruptSpellsWithChannelFlags.</summary>
    void InterruptChannelsWithFlags(Unit unit, SpellAuraInterruptFlags flags);
}

/// <summary>
/// Where a player is in the liquids and what that does (vmangos Player::UpdateTerainEnvironmentFlags, Player.cpp:20353-20420,
/// and Player::SetEnvironmentFlags, :831-868).
/// </summary>
public static class LiquidEnvironment
{
    /// <summary>
    /// Ask the terrain where the player stands and set the environment flags (vmangos asks at z + 0.01 with every liquid type):
    /// no liquid clears all liquid flags; otherwise Liquid is set, then Underwater (fully submerged and the surface above the
    /// head: z + collision height), InWater (water or ocean, in or under the surface), InMagma and InSlime (also within a tenth of
    /// a yard above the surface), HighSea (a deep water cell, at any depth) and HighLiquid (deep enough to swim: z + three
    /// quarters of the collision height). The LiquidType.dbc remap and its liquid spells (an area spell on entering a liquid) need
    /// the client data and are not applied; the liquid kind is the one the .map file stores. WMO liquid is not queried.
    /// </summary>
    public static void UpdateTerrainFlags(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Map is not { } map)
        {
            return;
        }

        float x = player.X;
        float y = player.Y;
        float z = player.Z;
        LocomotionState state = player.Locomotion;
        state.LastEnvironmentSample = (map, x, y, z);

        LiquidStatus res = map.GetLiquidStatus(x, y, z + 0.01f, LiquidTypeFlags.AllLiquids, out LiquidData liquid);
        if (res == LiquidStatus.NoWater)
        {
            SetFlags(player, EnvironmentFlags.MaskLiquidFlags, false);
            return;
        }

        SetFlags(player, EnvironmentFlags.Liquid, true);

        const LiquidStatus inOrUnder = LiquidStatus.UnderWater | LiquidStatus.InWater;
        const LiquidStatus nearSurface = inOrUnder | LiquidStatus.WaterWalk;
        float collisionHeight = state.CollisionHeight;
        float minSwimDepth = collisionHeight * 0.75f;

        if ((liquid.TypeFlags & LiquidTypeFlags.AllLiquids) != 0)
        {
            SetFlags(player, EnvironmentFlags.Underwater, (res & LiquidStatus.UnderWater) != 0 && liquid.Level > z + collisionHeight);
        }

        if ((liquid.TypeFlags & (LiquidTypeFlags.Water | LiquidTypeFlags.Ocean)) != 0)
        {
            SetFlags(player, EnvironmentFlags.InWater, (res & inOrUnder) != 0);
        }

        if ((liquid.TypeFlags & LiquidTypeFlags.Magma) != 0)
        {
            SetFlags(player, EnvironmentFlags.InMagma, (res & nearSurface) != 0);
        }

        if ((liquid.TypeFlags & LiquidTypeFlags.Slime) != 0)
        {
            SetFlags(player, EnvironmentFlags.InSlime, (res & nearSurface) != 0);
        }

        SetFlags(player, EnvironmentFlags.HighSea, (liquid.TypeFlags & LiquidTypeFlags.DeepWater) != 0);
        SetFlags(player, EnvironmentFlags.HighLiquid, (res & inOrUnder) != 0 && liquid.Level > z + minSwimDepth);
    }

    /// <summary>
    /// vmangos Player::SetEnvironmentFlags: change flags, and when they changed (a flag set that already is in the wanted state is
    /// ignored): entering or leaving a swimmable liquid removes the auras and channels that need land or water (the
    /// under-water and above-water interrupt flags); the high sea starts or restores the fatigue timer, going under the surface
    /// starts the breath timer (if the player can lose breath) or restores it, and a hazardous liquid starts or restores the
    /// lava timer. The threat tables of swimming mobs (HostileRefManager::updateThreatTables) belong to the creature AI and are
    /// not touched.
    /// </summary>
    public static void SetFlags(Player player, EnvironmentFlags flags, bool apply)
    {
        LocomotionState state = player.Locomotion;
        if (((state.Environment & flags) != 0) == apply)
        {
            return;
        }

        state.Environment = apply ? state.Environment | flags : state.Environment & ~flags;

        if ((flags & EnvironmentFlags.HighLiquid) != 0 && LocomotionEnvironment.SpellBridgeFor(player.Map) is { } bridge)
        {
            SpellAuraInterruptFlags cancel = apply ? SpellAuraInterruptFlags.UnderWaterCancels : SpellAuraInterruptFlags.AboveWaterCancels;
            bridge.InterruptChannelsWithFlags(player, cancel);
            bridge.RemoveAurasWithInterruptFlags(player, cancel);
        }

        if ((flags & EnvironmentFlags.HighSea) != 0)
        {
            state.MirrorTimers[(int)MirrorTimerType.Fatigue].SetScale(apply ? -1 : 10);
        }

        if ((flags & EnvironmentFlags.Underwater) != 0)
        {
            state.MirrorTimers[(int)MirrorTimerType.Breath].SetScale(apply && WaterBreathingInterval(player) != 0 ? -1 : 10);
        }

        if ((flags & EnvironmentFlags.MaskLiquidHazard) != 0)
        {
            state.MirrorTimers[(int)MirrorTimerType.Environmental].SetScale((state.Environment & EnvironmentFlags.MaskLiquidHazard) != 0 ? -1 : 10);
        }
    }

    /// <summary>vmangos Player::GetWaterBreathingInterval: the breath time in milliseconds, 0 for a player who does not lose breath.</summary>
    public static uint WaterBreathingInterval(Player player)
        => (uint)(LocomotionEnvironment.For(player.Map).Options.MirrorTimerBreathMaxSec * 1000u * player.Locomotion.BreathingMultiplier);

    /// <summary>
    /// vmangos Player::SetWaterBreathingIntervalMultiplier: change how long the breath lasts; the breath timer follows (restoring
    /// when the player cannot lose breath any more).
    /// </summary>
    public static void SetBreathingMultiplier(Player player, float multiplier)
    {
        LocomotionState state = player.Locomotion;
        if (state.BreathingMultiplier == multiplier)
        {
            return;
        }

        state.BreathingMultiplier = multiplier;
        MirrorTimer breath = state.MirrorTimers[(int)MirrorTimerType.Breath];
        uint interval = WaterBreathingInterval(player);
        if (interval != 0)
        {
            breath.SetDuration(interval);
            breath.SetScale((state.Environment & EnvironmentFlags.Underwater) != 0 ? -1 : 10);
        }
        else
        {
            breath.SetScale(10);
        }
    }
}
