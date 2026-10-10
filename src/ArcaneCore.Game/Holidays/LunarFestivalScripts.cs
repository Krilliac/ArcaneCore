using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Holidays;

/// <summary>One Lunar Festival firework (vmangos npcs_special.cpp:1008-1043, FireworkStruct and the Fireworks table).</summary>
public sealed record FireworkKind(uint NpcEntry, uint[] Spells, bool IsCluster, bool IsLarge);

/// <summary>vmangos npcs_special.cpp:994-1043: the firework creatures players launch and the spells each one casts.</summary>
public static class FireworkCatalog
{
    public const uint NpcLuckyCluster = 15918;
    public const uint NpcFireworkCredit = 15893, NpcClusterCredit = 15894;
    public const uint SpellLunarFortune = 26522;

    public static readonly IReadOnlyList<FireworkKind> Fireworks =
    [
        new(15872, [26357, 26303, 26302, 26300, 26301], true, false), // Blue Firework Cluster
        new(15873, [26360, 26308, 26307, 26306, 26305], true, false), // Red Firework Cluster
        new(15874, [26358, 26312, 26311, 26310, 26309], true, false), // Green Firework Cluster
        new(15875, [26359, 26316, 26315, 26314, 26313], true, false), // Purple Firework Cluster
        new(15876, [26361, 26320, 26319, 26318, 26317], true, false), // White Firework Cluster
        new(15877, [26362, 26324, 26323, 26322, 26321], true, false), // Yellow Firework Cluster
        new(15879, [26344], false, false), // Small Blue Rocket
        new(15880, [26345], false, false), // Small Green Rocket
        new(15881, [26346], false, false), // Small Purple Rocket
        new(15882, [26347], false, false), // Small Red Rocket
        new(15883, [26349], false, false), // Small Yellow Rocket
        new(15884, [26348], false, false), // Small White Rocket
        new(15885, [26351], false, true), // Large Blue Rocket
        new(15886, [26352], false, true), // Large Green Rocket
        new(15887, [26353], false, true), // Large Purple Rocket
        new(15888, [26354], false, true), // Large Red Rocket
        new(15889, [26355], false, true), // Large White Rocket
        new(15890, [26356], false, true), // Large Yellow Rocket
        new(15911, [26487, 26486, 26485, 26484, 26483], true, true), // Large Blue Firework Cluster
        new(15912, [26495, 26494, 26493, 26492, 26491], true, true), // Large Green Firework Cluster
        new(15913, [26500, 26499, 26498, 26497, 26496], true, true), // Large Purple Firework Cluster
        new(15914, [26505, 26504, 26503, 26502, 26501], true, true), // Large Red Firework Cluster
        new(15915, [26510, 26509, 26508, 26507, 26506], true, true), // Large White Firework Cluster
        new(15916, [26515, 26514, 26513, 26512, 26511], true, true), // Large Yellow Firework Cluster
        new(15918, [26487, 26509, 26508, 26484, 26483], true, true), // Lucky Rocket Cluster
    ];

    public static FireworkKind? Find(uint entry) => Fireworks.FirstOrDefault(f => f.NpcEntry == entry);

    /// <summary>The offsets of the five bursts of a large cluster (npcs_special.cpp:1088-1123).</summary>
    public static readonly (float X, float Y, float Z)[] LargeClusterOffsets = [(0, 0, 3), (0, 3, 7.5f), (5.25f, -1.5f, 7.5f), (-5.25f, -1.5f, 7.5f), (0, 0, 12)];

    /// <summary>The offsets of the five bursts of a normal cluster (npcs_special.cpp:1124-1162).</summary>
    public static readonly (float X, float Y, float Z)[] ClusterOffsets = [(0, 0, 8), (3.5f, -1, 5), (0, 2, 5), (0, 0, 2), (-3.5f, -1, 5)];
}

/// <summary>
/// npc_pats_firework_guyAI (vmangos npcs_special.cpp:1045-1210): a launched firework goes off on its first update, a cluster in five bursts
/// around its launch point, a rocket three yards up; the Lucky Rocket Cluster adds Lunar Fortune three seconds later. The player who launched
/// it gets the firework or cluster credit, and a firework launched at Omen's cluster launcher counts toward his return
/// (<see cref="OmenController.OnFireworkLaunch"/>).
/// </summary>
public sealed class FireworkGuyAi(Creature creature, OmenController? omen) : CreatureAI(creature)
{
    private readonly FireworkKind? _kind = FireworkCatalog.Find(creature.Entry);
    private bool _done;
    private int _fortuneMs = -1;

    public override void OnUpdate(uint diffMs)
    {
        if (_fortuneMs >= 0)
        {
            _fortuneMs -= (int)diffMs;
            if (_fortuneMs < 0) DoCast(Me, FireworkCatalog.SpellLunarFortune, triggered: true);
        }

        if (_kind is null || _done || System is not { } system) return;
        _done = true;
        float x = Me.X, y = Me.Y, z = Me.Z;
        if (_kind.IsCluster)
        {
            (float X, float Y, float Z)[] offsets = _kind.IsLarge ? FireworkCatalog.LargeClusterOffsets : FireworkCatalog.ClusterOffsets;
            for (int i = 0; i < 5; i++)
            {
                system.NearTeleport(Me, x + offsets[i].X, y + offsets[i].Y, z + offsets[i].Z, 0);
                DoCast(Me, _kind.Spells[i], triggered: true);
            }
        }
        else
        {
            // Non Cluster Rockets are always z + 3.0f!
            system.NearTeleport(Me, x, y, z + 3, 0);
            DoCast(Me, _kind.Spells[0], triggered: true);
        }

        if (_kind.NpcEntry == FireworkCatalog.NpcLuckyCluster) _fortuneMs = 3000;

        if (Me.Summon is { } summon && Me.Map?.FindObject(summon.Owner) is Player launcher)
            system.KilledMonsterCredit(launcher, _kind.IsCluster ? FireworkCatalog.NpcClusterCredit : FireworkCatalog.NpcFireworkCredit, default);

        omen?.OnFireworkLaunch(Me, x, y, z);
    }
}

/// <summary>
/// boss_omen's world state (vmangos src/scripts/kalimdor/moonglade/boss_omen.cpp, OmenData and boss_omenAI::OnFireworkLaunch / JustDied):
/// twenty fireworks launched at the Moonglade cluster launchers bring Omen up from the lake, unless he is alive or died less than fifteen minutes
/// ago. Omen's fight is ClassicDB's EventAI (Cleave, Starfall, the Elune's Candle phase); this adds only what boss_omen does around it.
/// </summary>
public sealed class OmenController
{
    public const uint NpcOmen = 15467;
    public const uint SpellElunesCandle = 26374, SpellOmensMoonlight = 26392, SpellSelfDamage = 26544;
    public const uint SoundCharacterSplash = 1097, SoundHydraSpecialAggro = 8460;
    public const int FireworksToSummon = 20;
    public const long RespawnSeconds = 15 * 60;

    /// <summary>
    /// The Moonglade cluster launchers: vmangos GO_OMEN_CLUSTER_LAUNCHER 180874 (INTERACTION_DISTANCE 5) has no ClassicDB spawn; ClassicDB
    /// places Cluster Launcher 180859 on the shore instead (guids 89636 and 89637, 31 and 34 yards from the summon point), so both count.
    /// </summary>
    public static readonly uint[] OmenLaunchers = [180874, 180859];
    public const float LauncherRange = 5f, MoongladeRange = 100f;

    public static readonly (float X, float Y, float Z, float O) Summon = (7560.01f, -2838.36f, 449.575f, 4.01426f);
    public static readonly (float X, float Y, float Z, float O) Home = (7542.5f, -2870.67f, 459.498f, 1.13905f);
    internal static readonly (float X, float Y, float Z)[] Path = [(7553.95f, -2848.48f, 454.56f), (7549.98f, -2855.14f, 456.968f), (Home.X, Home.Y, Home.Z)];

    public int FireworksCount { get; internal set; }
    public long NextRespawnUnix { get; internal set; }
    public Creature? Omen { get; private set; }
    public bool OmenAlive => Omen is { IsAlive: true };

    internal Func<long> NowUnix { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private int _soundMs = -1, _walkMs = -1, _pathIndex = -1;
    private bool _deathHandled;

    /// <summary>Whether a firework at this place was launched at one of Omen's cluster launchers.</summary>
    public static bool AtOmenLauncher(GameObjectMapSystem? objects, int mapId, float x, float y, float z)
    {
        if (mapId != 1 || objects is null) return false;
        float dx = x - Summon.X, dy = y - Summon.Y;
        if (dx * dx + dy * dy > MoongladeRange * MoongladeRange) return false;
        return objects.GameObjects.Any(g => g.IsSpawned && OmenLaunchers.Contains(g.Entry)
            && (g.X - x) * (g.X - x) + (g.Y - y) * (g.Y - y) + (g.Z - z) * (g.Z - z) <= LauncherRange * LauncherRange);
    }

    /// <summary>boss_omenAI::OnFireworkLaunch.</summary>
    public void OnFireworkLaunch(Creature firework, float x, float y, float z)
    {
        if (firework.Map is not { } map || !AtOmenLauncher(map.FindUpdater<GameObjectMapSystem>(), (int)map.MapId, x, y, z)) return;
        if (!CountLaunch()) return;
        if (firework.System?.SummonAt(firework, NpcOmen, Summon.X, Summon.Y, Summon.Z, Summon.O, null, 2 * 60 * 60 * 1000) is not { } omen) return;
        Summoned(omen);
    }

    /// <summary>The counting half of OnFireworkLaunch: true when Omen is due to rise now.</summary>
    internal bool CountLaunch()
    {
        if (OmenAlive) return false;
        ++FireworksCount;
        // vmangos also starts GAME_EVENT_MINIONS_OF_OMEN (43) from the third launch; ClassicDB has no such event (nor Minion of Omen spawns).
        return FireworksCount >= FireworksToSummon && NextRespawnUnix < NowUnix();
    }

    internal void Summoned(Creature omen)
    {
        FireworksCount = 0;
        Omen = omen;
        _deathHandled = false;
        omen.System?.SetHomePosition(omen, Home.X, Home.Y, Home.Z, Home.O);
        omen.System?.SetDefaultRandomMovement(omen, 10f);
        _soundMs = 800;
        _walkMs = 4000;
        _pathIndex = -1;
    }

    /// <summary>The timed parts of the summon (the splash at 0.8 s, the walk to the shore at 4 s), and boss_omenAI::JustDied.</summary>
    public void Update(uint diffMs)
    {
        if (Omen is not { } omen) return;
        if (omen.Map is null)
        {
            Omen = null; // OnRemoveFromWorld: GAME_EVENT_MINIONS_OF_OMEN would stop here (see CountLaunch)
            return;
        }

        if (!omen.IsAlive)
        {
            if (_deathHandled) return;
            _deathHandled = true;
            omen.System?.CastSpell(omen, SpellOmensMoonlight, omen, triggered: true);
            NextRespawnUnix = NowUnix() + RespawnSeconds;
            omen.System?.ForcedDespawn(omen, 5 * 60 * 1000);
            return;
        }

        if (_soundMs >= 0 && (_soundMs -= (int)diffMs) < 0)
        {
            foreach (uint sound in new[] { SoundCharacterSplash, SoundHydraSpecialAggro })
                omen.Map.BroadcastToObservers(omen, WorldOpcode.SmsgPlayObjectSound, Fishing.FishingPackets.PlayObjectSound(sound, omen.Guid));
        }

        if (_walkMs >= 0 && (_walkMs -= (int)diffMs) < 0 && !omen.Combat.IsInCombat)
            Advance(omen, 0);

        FollowPath(omen);
    }

    /// <summary>
    /// boss_omenAI::MovementInform: point 1 then 2 then home, then the default wander resumes. Omen keeps ClassicDB's EventAI, which does
    /// not forward movement informs, so arrival is seen by distance instead.
    /// </summary>
    private void FollowPath(Creature omen)
    {
        if (_pathIndex < 0 || _pathIndex >= Path.Length || omen.Combat.IsInCombat) return;
        (float px, float py, float pz) = Path[_pathIndex];
        float dx = omen.X - px, dy = omen.Y - py, dz = omen.Z - pz;
        if (dx * dx + dy * dy + dz * dz > 1.5f * 1.5f) return;
        if (_pathIndex + 1 < Path.Length) Advance(omen, _pathIndex + 1);
        else _pathIndex = Path.Length;
    }

    private void Advance(Creature omen, int index)
    {
        _pathIndex = index;
        omen.Motion.MovePoint((uint)(index + 1), Path[index].X, Path[index].Y, Path[index].Z, run: true);
    }

    /// <summary>boss_omenAI::SpellHit: Elune's Candle makes Omen hurt himself.</summary>
    public void OnSpellHit(Unit target, uint spellId)
    {
        if (spellId == SpellElunesCandle && target is Creature { Entry: NpcOmen, IsAlive: true } omen)
            omen.System?.CastSpell(omen, SpellSelfDamage, omen, triggered: true);
    }
}

/// <summary>
/// go_lunar_festival_firecracker (vmangos src/scripts/world/go_scripts.cpp:366-420): the placed Firecrackers 180763/180764 go off after 30-60
/// seconds, any firecracker 0-2 seconds after it is used; then it despawns for its respawn time.
/// </summary>
public sealed class FirecrackerAi(Random? random = null) : IGameObjectAi
{
    public static readonly uint[] Entries = [180763, 180764, 180870, 180871, 180872, 180873];
    private readonly Random _random = random ?? Random.Shared;
    private readonly Dictionary<ObjectGuid, long> _despawnInMs = [];
    private readonly HashSet<ObjectGuid> _seen = [];

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
    {
        _despawnInMs[go.Guid] = _random.Next(0, 3) * 1000L;
        return true;
    }

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
        if (!go.IsSpawned)
        {
            _seen.Remove(go.Guid);
            return;
        }

        if (_seen.Add(go.Guid) && go.Entry is 180763 or 180764) _despawnInMs[go.Guid] = _random.Next(30, 61) * 1000L;
        if (!_despawnInMs.TryGetValue(go.Guid, out long left)) return;
        left -= diffMs;
        if (left > 0)
        {
            _despawnInMs[go.Guid] = left;
            return;
        }

        _despawnInMs.Remove(go.Guid);
        objects.DespawnForRespawn(go);
    }
}

/// <summary>go_firework_rocket (vmangos go_scripts.cpp:320-364): a show rocket despawns at once, so its firework plays straight away.</summary>
public sealed class FireworkRocketAi : IGameObjectAi
{
    public static readonly uint[] Entries = [180851, 180854, 180855, 180856, 180857, 180858, 180860, 180861, 180862, 180863, 180864, 180865];

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
        if (go.IsSpawned) objects.Despawn(go);
    }
}
