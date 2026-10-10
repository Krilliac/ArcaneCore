using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// The Dragons of Nightmare rotation: vmangos src/game/HardcodedEvents.cpp:160-332 (DragonsOfNightmare, event 66) with
/// GetAI_boss_dragon_of_nightmare (scripts/world/dragons_of_nightmare/boss_dragon_of_nightmare.cpp), which turns each spawn into the dragon
/// its VAR_PERM_n names. The four portal spawns are those of the vmangos world database (brotalnia/database world_full_14_june_2021,
/// creature guids 52350, 52359, 52364, 52357, all in game_event_creature 66); ClassicDB z2815 has no spawn of 14887-14890 at all, so
/// without this the dragons never appear.
/// <para>
/// While the event is active the four dragons stand at their portals. Once all four are dead the event waits
/// <see cref="NightmareDragonsState.DefaultStopDelay"/> updates (time to loot the last one), then rolls the next spawn 4-7 days ahead, shuffles
/// which dragon stands at which portal, and removes the corpses. An inactive event starts when the spawn time has passed. The state is
/// saved in characters table world_nightmare_dragons whenever it changes, as sObjectMgr.SetSavedVariable(..., true) does.
/// </para>
/// Deviations: the update runs every <see cref="UpdateIntervalMs"/> (vmangos updates its hardcoded events on the game-event timer, 20 s
/// while the Scourge invasion is enabled); a dragon is placed when its portal's grid is loaded (it is a script spawn, which leaves the map
/// with its grid and comes back at the next update with the grid loaded); Emeriss's Duskwood waypoint path (movement type 2) is not carried.
/// </summary>
public sealed class NightmareDragonsFeature(IServiceScopeFactory scopes, ILogger<NightmareDragonsFeature>? logger = null) : IWorldFeature
{
    public const uint UpdateIntervalMs = 20_000;
    public const uint Ysondre = 14887, Lethon = 14888, Emeriss = 14889, Taerar = 14890;
    private const long Day = 24 * 60 * 60;

    /// <summary>One portal: the vmangos spawn (map, position) and the dragon that stands there unpermuted (the VAR_PERM_n it reads).</summary>
    public readonly record struct Portal(uint DefaultEntry, uint MapId, float X, float Y, float Z, float Orientation, string Name);

    /// <summary>VAR_PERM_1..4 in GetDrakeVar order: Ysondre, Lethon, Emeriss, Taerar.</summary>
    public static readonly Portal[] Portals =
    [
        new(Ysondre, 0, 869.667f, -3974.87f, 145.827f, 3.473f, "Seradane, The Hinterlands"),
        new(Lethon, 1, -2872.66f, 1884.25f, 52.7336f, 2.6529f, "Dream Bough, Feralas"),
        new(Emeriss, 0, -10428.8f, -392.176f, 43.7411f, 0.932375f, "Twilight Grove, Duskwood"),
        new(Taerar, 1, 3301.05f, -3732.57f, 173.544f, 2.9147f, "Bough Shadow, Ashenvale"),
    ];

    /// <summary>What an update needs to know of one portal's dragon.</summary>
    public enum DragonStatus { Absent, Alive, Dead }

    private readonly Creature?[] _dragons = new Creature?[Portals.Length];
    private WorldRuntime? _world;
    private uint _sinceUpdateMs;

    internal NightmareDragonsState State { get; set; } = NightmareDragonsState.Initial;

    internal Func<long> NowUnix { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    internal Random Random { get; set; } = new();

    /// <summary>The dragon status of a portal (tests; null: the live world).</summary>
    internal Func<int, DragonStatus>? StatusOverride { get; set; }

    /// <summary>Place the dragon <c>entry</c> at a portal if its grid is loaded (tests; null: the live world).</summary>
    internal Action<int, uint>? SpawnOverride { get; set; }

    /// <summary>Remove a portal's dragon or corpse, GameEventUnspawn when the event stops (tests; null: the live world).</summary>
    internal Action<int>? DespawnOverride { get; set; }

    private DragonStatus StatusOf(int portal) => StatusOverride?.Invoke(portal) ?? LiveStatus(portal);

    private void Spawn(int portal, uint entry)
    {
        if (SpawnOverride is { } spawn) spawn(portal, entry);
        else LiveSpawn(portal, entry);
    }

    private void Despawn(int portal)
    {
        if (DespawnOverride is { } despawn) despawn(portal);
        else LiveDespawn(portal);
    }

    public void Attach(WorldRuntime world)
    {
        _world = world;
        Load();
        world.WorldTick += diff =>
        {
            _sinceUpdateMs += diff;
            if (_sinceUpdateMs < UpdateIntervalMs) return;
            _sinceUpdateMs = 0;
            Update();
        };
    }

    /// <summary>The dragon entry of a portal: its VAR_PERM_n, or the portal's own dragon when nothing is saved (permEntry 0).</summary>
    public uint EntryAt(int portal) => State.Permutation[portal] is var perm and not 0 ? perm : Portals[portal].DefaultEntry;

    /// <summary>DragonsOfNightmare::Update.</summary>
    internal void Update()
    {
        NightmareDragonsState s = State;
        if (s.Active)
        {
            byte killed = s.KilledMask;
            int alive = 0;
            for (int i = 0; i < Portals.Length; i++)
            {
                if ((killed & (1 << i)) != 0) continue;
                switch (StatusOf(i))
                {
                    case DragonStatus.Dead:
                        killed |= (byte)(1 << i); // SaveCreatureRespawnTime(max): a dead dragon stays dead while the event runs
                        break;
                    case DragonStatus.Absent:
                        Spawn(i, EntryAt(i)); // a living dragon whose grid unloaded (or a restart): back once the grid is loaded
                        ++alive;
                        break;
                    default:
                        ++alive;
                        break;
                }
            }

            if (killed != s.KilledMask) Save(s = s with { KilledMask = killed });
            if (alive > 0) return;

            if (s.RequiredUpdates == 0)
            {
                // urand(4 days, 7 days), then PermutateDragons and StopEvent.
                long respawn = NowUnix() + Random.NextInt64(4 * Day, (7 * Day) + 1);
                for (int i = 0; i < Portals.Length; i++) Despawn(i);
                Save(s with
                {
                    RespawnUnix = respawn,
                    RequiredUpdates = NightmareDragonsState.DefaultStopDelay,
                    Permutation = Permutate(),
                    Active = false,
                    KilledMask = 0,
                });
                logger?.LogInformation("Dragons of Nightmare: all four slain; next spawn at {Respawn:u}", DateTimeOffset.FromUnixTimeSeconds(respawn));
            }
            else
            {
                Save(s with { RequiredUpdates = s.RequiredUpdates - 1 });
            }
        }
        else if (s.RespawnUnix < NowUnix())
        {
            // StartEvent(66): the dragons appear.
            Save(s = s with { Active = true, KilledMask = 0 });
            for (int i = 0; i < Portals.Length; i++) Spawn(i, EntryAt(i));
            logger?.LogInformation("Dragons of Nightmare: {Dragons}", string.Join(", ", Portals.Select((p, i) => $"{EntryAt(i)} at {p.Name}")));
        }
    }

    /// <summary>PermutateDragons: { Lethon, Emeriss, Ysondre, Taerar } shuffled into VAR_PERM_1..4.</summary>
    internal uint[] Permutate()
    {
        uint[] permutation = [Lethon, Emeriss, Ysondre, Taerar];
        Random.Shuffle(permutation);
        return permutation;
    }

    internal void Load()
    {
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            if (scope.ServiceProvider.GetService<INightmareDragonsStore>()?.LoadAsync().GetAwaiter().GetResult() is { } saved)
                State = saved;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "could not load the Dragons of Nightmare state");
        }
    }

    private void Save(NightmareDragonsState state)
    {
        State = state;
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            scope.ServiceProvider.GetService<INightmareDragonsStore>()?.SaveAsync(state).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "could not save the Dragons of Nightmare state");
        }
    }

    private CreatureMapSystem? CreaturesOf(int portal)
        => _world?.FindMap(Portals[portal].MapId)?.FindUpdater<CreatureMapSystem>();

    private DragonStatus LiveStatus(int portal)
    {
        if (_dragons[portal] is not { IsInWorld: true } dragon)
        {
            _dragons[portal] = null;
            return DragonStatus.Absent;
        }

        return dragon.IsAlive ? DragonStatus.Alive : DragonStatus.Dead;
    }

    private void LiveSpawn(int portal, uint entry)
    {
        Portal p = Portals[portal];
        if (_dragons[portal] is { IsInWorld: true } || CreaturesOf(portal) is not { } creatures || !creatures.IsGridLoadedAt(p.X, p.Y)) return;
        _dragons[portal] = creatures.SummonForInstance(entry, p.X, p.Y, p.Z, p.Orientation);
    }

    private void LiveDespawn(int portal)
    {
        if (_dragons[portal] is { IsInWorld: true } dragon) CreaturesOf(portal)?.ForcedDespawn(dragon, 0);
        _dragons[portal] = null;
    }
}
