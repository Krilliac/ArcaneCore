using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Locomotion;

/// <summary>SMSG_START_MIRROR_TIMER / SMSG_STOP_MIRROR_TIMER (vmangos Server/Packets/Misc.cpp:669-700; gtker smsg_start_mirror_timer.wowm).</summary>
public static class MirrorTimerPackets
{
    /// <summary>u32 timer type, u32 remaining ms, u32 duration ms, i32 scale, u8 paused, u32 spell id.</summary>
    public static byte[] BuildStart(MirrorTimerType type, uint remaining, uint duration, int scale, bool paused, uint spellId)
    {
        var writer = new PacketWriter(21);
        writer.WriteUInt32((uint)type);
        writer.WriteUInt32(remaining);
        writer.WriteUInt32(duration);
        writer.WriteInt32(scale);
        writer.WriteByte(paused ? (byte)1 : (byte)0);
        writer.WriteUInt32(spellId);
        return writer.ToArray();
    }

    /// <summary>u32 timer type.</summary>
    public static byte[] BuildStop(MirrorTimerType type)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32((uint)type);
        return writer.ToArray();
    }
}

/// <summary>
/// The per-map tick of the liquid rules (vmangos Player::Update: <c>UpdateMirrorTimers(diff)</c> first, Player.cpp:1117; the liquid flags
/// are re-evaluated whenever the position changes, SetPosition, :5988). Every player in the map: the liquid flags when its position
/// or map changed since they were last asked (login, teleport and spell relocation have no movement packet), then the mirror timers.
/// <para>
/// A game master's timers stand still (FreezeMirrorTimers, :2634,2656). A dead player's timers stop, except the fatigue of a
/// ghost, which sends the ghost to the graveyard when it pulses. Not delivered: the feign death timer (the hunter lane's state), the
/// Spirit of Redemption form that drops every timer, and timers that follow an aura (a liquid with a spell in LiquidType.dbc).
/// Runs after combat (order 10) so that combat stays every map's first updater; a pulse that kills is therefore handled by the
/// next tick's combat update instead of the same one.
/// </para>
/// </summary>
[DefaultMapUpdater(Order = 10)]
internal sealed class MapEnvironment : IMapUpdater
{
    private readonly WorldRuntime _world;

    public MapEnvironment(Map map, WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(map);
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public void Update(Map map, uint diffMs)
    {
        foreach (Player player in map.Players.ToArray())
        {
            LocomotionState state = player.Locomotion;
            if (state.LastEnvironmentSample is not { } sample || !ReferenceEquals(sample.Map, map)
                || sample.X != player.X || sample.Y != player.Y || sample.Z != player.Z)
            {
                LiquidEnvironment.UpdateTerrainFlags(player);
            }

            UpdateMirrorTimers(map, player, diffMs);
        }
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
        // A player that leaves has no liquid to be in and no timer to run; the next map starts from nothing.
        if (LocomotionStates.TryGet(player, out LocomotionState? state))
        {
            state.Environment = EnvironmentFlags.None;
            state.LastEnvironmentSample = null;
            foreach (MirrorTimer timer in state.MirrorTimers)
            {
                timer.Stop();
                timer.FetchStatus();
            }
        }
    }

    /// <summary>vmangos Player::UpdateMirrorTimers.</summary>
    private void UpdateMirrorTimers(Map map, Player player, uint diff)
    {
        LocomotionState state = player.Locomotion;
        LocomotionOptions options = LocomotionEnvironment.For(_world).Options;
        bool gm = player.IsGameMaster;
        foreach (MirrorTimer timer in state.MirrorTimers)
        {
            if (timer.SpellId == 0 && timer.IsFrozen != gm)
            {
                timer.SetFrozen(gm);
            }

            MirrorTimerType type = timer.Type;
            bool active = timer.IsActive;
            if (active || CheckActivation(player, type))
            {
                if (CheckDeactivation(player, type))
                {
                    timer.Stop();
                }
                else if (active)
                {
                    if (!timer.Update(diff))
                    {
                        OnExpirationPulse(map, player, type, options);
                    }
                }
                else
                {
                    timer.Start(MaxDuration(player, type, options));
                }
            }
        }

        SendMirrorTimers(player);
    }

    private static bool CheckActivation(Player player, MirrorTimerType type)
    {
        EnvironmentFlags flags = player.Locomotion.Environment;
        return type switch
        {
            MirrorTimerType.Fatigue => (flags & EnvironmentFlags.HighSea) != 0 && (player.UnitFlags & UnitFlags.TaxiFlight) == 0,
            MirrorTimerType.Breath => (flags & EnvironmentFlags.Underwater) != 0 && LiquidEnvironment.WaterBreathingInterval(player) != 0,
            MirrorTimerType.Environmental => (flags & EnvironmentFlags.MaskLiquidHazard) != 0,
            _ => false,
        };
    }

    private static bool CheckDeactivation(Player player, MirrorTimerType type)
    {
        EnvironmentFlags flags = player.Locomotion.Environment;
        bool liquid = (flags & EnvironmentFlags.Liquid) != 0;
        return type switch
        {
            MirrorTimerType.Fatigue => !liquid || (!player.IsAlive && (player.Flags & PlayerFlags.Ghost) == 0),
            MirrorTimerType.Breath => !liquid || !player.IsAlive,
            MirrorTimerType.FeignDeath => true,
            MirrorTimerType.Environmental => !liquid || !player.IsAlive,
            _ => false,
        };
    }

    private static uint MaxDuration(Player player, MirrorTimerType type, LocomotionOptions options) => type switch
    {
        MirrorTimerType.Fatigue => options.MirrorTimerFatigueMaxSec * 1000u,
        MirrorTimerType.Breath => LiquidEnvironment.WaterBreathingInterval(player),
        MirrorTimerType.Environmental => options.MirrorTimerEnvironmentalMaxSec * 1000u,
        _ => 0,
    };

    /// <summary>vmangos Player::OnMirrorTimerExpirationPulse (Player.cpp:983-1062).</summary>
    private void OnExpirationPulse(Map map, Player player, MirrorTimerType type, LocomotionOptions options)
    {
        ICombatRandom random = map.Combat.Random;
        switch (type)
        {
            case MirrorTimerType.Fatigue:
                if (player.IsAlive)
                {
                    EnvironmentalDamage.Apply(_world, player, EnvironmentalDamageType.Exhausted, SuffocationDamage(player, random));
                }
                else if ((player.Flags & PlayerFlags.Ghost) != 0)
                {
                    map.Combat.Hooks.RepopAtGraveyard(player);
                }

                break;
            case MirrorTimerType.Breath:
                // "TODO: Check this formula" (vmangos): a fifth of the health plus up to level - 1.
                EnvironmentalDamage.Apply(_world, player, EnvironmentalDamageType.Drowning, SuffocationDamage(player, random));
                break;
            case MirrorTimerType.Environmental:
                if ((player.Locomotion.Environment & EnvironmentFlags.InMagma) != 0)
                {
                    EnvironmentalDamage.Apply(_world, player, EnvironmentalDamageType.Lava, (uint)random.Next((int)options.EnvironmentalDamageMin, (int)options.EnvironmentalDamageMax));
                }
                else if (options.SlimeDamage && (player.Locomotion.Environment & EnvironmentFlags.InSlime) != 0)
                {
                    EnvironmentalDamage.Apply(_world, player, EnvironmentalDamageType.Slime, (uint)random.Next((int)options.EnvironmentalDamageMin, (int)options.EnvironmentalDamageMax));
                }

                break;
        }
    }

    /// <summary>GetMaxHealth() / 5 + urand(0, level - 1).</summary>
    private static uint SuffocationDamage(Player player, ICombatRandom random)
        => (player.MaxHealth / 5) + (uint)random.Next(0, Math.Max(0, player.Level - 1));

    /// <summary>vmangos Player::SendMirrorTimers: only the client's timers; a pause is always a full resend (client UI bug).</summary>
    private static void SendMirrorTimers(Player player)
    {
        foreach (MirrorTimer timer in player.Locomotion.MirrorTimers)
        {
            if (timer.Type > MirrorTimerType.FeignDeath)
            {
                return;
            }

            switch (timer.FetchStatus())
            {
                case MirrorTimerStatus.FullUpdate:
                    SendStart(player, timer);
                    break;
                case MirrorTimerStatus.StatusUpdate:
                    if (!timer.IsActive)
                    {
                        player.Session.Send(WorldOpcode.SmsgStopMirrorTimer, MirrorTimerPackets.BuildStop(timer.Type));
                    }
                    else
                    {
                        SendStart(player, timer);
                    }

                    break;
            }
        }
    }

    private static void SendStart(Player player, MirrorTimer timer)
        => player.Session.Send(
            WorldOpcode.SmsgStartMirrorTimer,
            MirrorTimerPackets.BuildStart(timer.Type, timer.Remaining, timer.Duration, timer.Scale, timer.IsFrozen, timer.SpellId));
}

/// <summary>The liquid flags follow the client's movement: evaluated after every accepted movement block (vmangos SetPosition, Player.cpp:5988).</summary>
[MovementObserver(Order = 30)]
public sealed class LiquidFlagsObserver : IClientMovementObserver
{
    public void AfterApply(MovementObserverContext context, in MovementInfo previous)
        => LiquidEnvironment.UpdateTerrainFlags(context.Player);
}
