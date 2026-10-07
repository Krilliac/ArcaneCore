using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// Kill-driven instance binding and reset time (vmangos Unit.cpp:1253-1263 -> Map::BindToInstanceOrRaid,
/// Map.cpp:3526-3545): raids bind permanently only through a flagged creature; a normal dungeon
/// never binds permanently and instead pushes its reset time to respawn + 2 h.
/// </summary>
public sealed class BindCreditTests
{
    private const long TwoHours = 2 * 60 * 60;

    /// <summary>A creature that is not in any creature system; with <paramref name="clock"/> its respawn time is that clock plus the delay.</summary>
    private static Creature NewCreature(uint counter, uint extraFlags, long respawnDelaySeconds = 0, CreatureMapSystem? clock = null)
    {
        var template = new CreatureTemplate { Entry = 11502, Name = "Boss", Faction = 14, ExtraFlags = extraFlags, MinLevel = 63, MaxLevel = 63 };
        return new Creature(counter, template, null, CreatureContent.Empty, new Random(1)) { RespawnAtMs = (clock?.ClockMs ?? 0) + (respawnDelaySeconds * 1000) };
    }

    [Fact]
    public void InstanceBindCreatureInANormalDungeon_BindsNobodyPermanently()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1);
        Assert.True(f.EnterDungeon(a));
        Map map = a.Map!;

        map.Combat.Kill(a, NewCreature(1, InstanceManager.CreatureFlagExtraInstanceBind));

        InstanceBind? bind = f.Manager.GetPlayerBind(a.Guid, Dungeon);
        Assert.NotNull(bind);
        Assert.False(bind!.Value.Permanent); // vmangos Map.cpp:3530-3535: PermBind is raid-only
        Assert.Empty(f.Sent(a, WorldOpcode.SmsgInstanceSaveCreated));
        Assert.True(f.Manager.FindSave(map.InstanceId)!.CanReset);
    }

    [Fact]
    public void RaidBossKilledByAUnitThatTheResolverMapsToAPlayer_StillBinds_AndAnUnresolvedKillerDoesNot()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        f.RaidGroup(a, b);
        Assert.True(f.EnterRaid(a));
        Assert.True(f.EnterRaid(b));
        Map map = a.Map!;
        var pet = new Creature(77, new CreatureTemplate { Entry = 416, Name = "Imp", Faction = 1, MinLevel = 60, MaxLevel = 60 }, null, CreatureContent.Empty, new Random(1));
        f.Manager.KillCreditResolver = new OwnerResolver(a, pet);

        map.Combat.Kill(null, NewCreature(1, InstanceManager.CreatureFlagExtraInstanceBind)); // unresolved: nobody
        Assert.Null(f.Manager.GetPlayerBind(a.Guid, Raid)); // grouped: only the group is bound, not permanently

        map.Combat.Kill(pet, NewCreature(2, InstanceManager.CreatureFlagExtraInstanceBind)); // pet -> owner a
        Assert.True(f.Manager.GetPlayerBind(a.Guid, Raid)!.Value.Permanent);
        Assert.True(f.Manager.GetPlayerBind(b.Guid, Raid)!.Value.Permanent);
    }

    [Fact]
    public void NormalDungeonKill_PushesResetTimeToRespawnPlusTwoHours_AndPersistsIt()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1);
        Assert.True(f.EnterDungeon(a));
        Map map = a.Map!;
        InstanceSave save = f.Manager.FindSave(map.InstanceId)!;
        Assert.Equal(Start + TwoHours, save.ResetTime);
        f.Persistence.Calls.Clear();

        map.Combat.Kill(a, NewCreature(1, 0, respawnDelaySeconds: 86_400, CreaturesOf(map)));

        Assert.Equal(Start + 86_400 + TwoHours, save.ResetTime);
        Assert.Contains($"save {save.InstanceId}", f.Persistence.Calls); // re-persisted

        // A shorter respawn never moves the time backwards.
        map.Combat.Kill(a, NewCreature(2, 0, respawnDelaySeconds: 300, CreaturesOf(map)));
        Assert.Equal(Start + 86_400 + TwoHours, save.ResetTime);

        // The instance is empty at the old two hour mark but is not reset any more.
        Assert.True(f.LeaveToContinent(a));
        f.Now = Start + TwoHours + 1;
        f.Manager.UpdateSchedule();
        f.Tick();
        Assert.True(f.Manager.IsSaveLive(Dungeon, map.InstanceId));

        f.Now = Start + 86_400 + TwoHours + 1;
        f.Manager.UpdateSchedule();
        f.Tick();
        Assert.False(f.Manager.IsSaveLive(Dungeon, map.InstanceId));
    }

    [Fact]
    public void ResetExtendsOnKillsOff_KeepsTheFixedTwoHours()
    {
        using var f = new InstanceFixture(new InstanceOptions { ResetExtendsOnKills = false });
        Player a = f.AddPlayer(1);
        Assert.True(f.EnterDungeon(a));
        Map map = a.Map!;
        map.Combat.Kill(a, NewCreature(1, 0, respawnDelaySeconds: 86_400, CreaturesOf(map)));
        Assert.Equal(Start + TwoHours, f.Manager.FindSave(map.InstanceId)!.ResetTime);
    }

    [Fact]
    public void RaidKill_NeverMovesTheGlobalResetTime()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        f.RaidGroup(a, b);
        Assert.True(f.EnterRaid(a));
        InstanceSave save = f.Manager.FindSave(a.Map!.InstanceId)!;
        long before = save.ResetTime;

        a.Map!.Combat.Kill(a, NewCreature(1, 0, respawnDelaySeconds: 10_000_000));
        Assert.Equal(before, save.ResetTime);
    }

    /// <summary>The instance map's creature system, attached on first use (the fixture's world has none of its own).</summary>
    private static CreatureMapSystem CreaturesOf(Map map)
    {
        if (map.FindUpdater<CreatureMapSystem>() is { } existing)
        {
            return existing;
        }

        var system = new CreatureMapSystem(map, CreatureContent.Empty, random: new Random(1));
        map.AddUpdater(system);
        return system;
    }

    [Fact]
    public void RaidBossKilledByAPet_WithTheDefaultResolver_CreditsItsOwner()
    {
        // vmangos Unit.cpp:1255: playerKiller = GetCharmerOrOwnerPlayerOrPlayerItself().
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        f.RaidGroup(a, b);
        Assert.True(f.EnterRaid(a));
        Assert.True(f.EnterRaid(b));
        Map map = a.Map!;
        Creature pet = CreaturesOf(map).SpawnTemporary(
            new CreatureTemplate { Entry = 416, Name = "Imp", Faction = 1, MinLevel = 60, MaxLevel = 60 }, a.X, a.Y, a.Z, 0);
        pet.SetOwnerGuid(a.Guid);

        map.Combat.Kill(pet, NewCreature(1, InstanceManager.CreatureFlagExtraInstanceBind));

        Assert.True(f.Manager.GetPlayerBind(a.Guid, Raid)!.Value.Permanent);
        Assert.True(f.Manager.GetPlayerBind(b.Guid, Raid)!.Value.Permanent);
    }

    [Fact]
    public void NormalDungeonKill_OfACreatureWithoutARespawnTimer_CountsItsCorpseTime()
    {
        // vmangos Creature::GetRespawnTimeEx (Creature.cpp:3305-3314): a respawn time that is not ahead
        // gives now + respawn delay + the corpse time left; the dungeon then resets 2 h after that.
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1);
        Assert.True(f.EnterDungeon(a));
        Map map = a.Map!;
        InstanceSave save = f.Manager.FindSave(map.InstanceId)!;
        Creature creature = CreaturesOf(map).SpawnTemporary(
            new CreatureTemplate { Entry = 11502, Name = "Summoned", Faction = 14, MinLevel = 20, MaxLevel = 20 }, a.X + 2, a.Y, a.Z, 0);

        map.Combat.Kill(a, creature);

        Assert.Equal(0u, creature.RespawnDelaySeconds); // no spawn row: the respawn time is the moment of death
        Assert.Equal(Start + 300 + TwoHours, save.ResetTime); // the 5-minute corpse of a normal creature
    }

    [Fact]
    public void NormalDungeonKill_MeasuresTheRespawnTimeAgainstTheMapClock()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1);
        Assert.True(f.EnterDungeon(a));
        Map map = a.Map!;
        CreatureMapSystem creatures = CreaturesOf(map);
        f.Tick(5_000); // the map clock is no longer 0
        InstanceSave save = f.Manager.FindSave(map.InstanceId)!;
        Creature boss = NewCreature(1, 0);
        boss.RespawnAtMs = creatures.ClockMs + (3_600 * 1000L); // back in an hour

        map.Combat.Kill(a, boss);

        Assert.Equal(Start + 3_600 + TwoHours, save.ResetTime);
    }

    private sealed class OwnerResolver(Player owner, Unit pet) : IKillCreditResolver
    {
        public Player? Resolve(Unit? killer, Creature victim) => killer is Player p ? p : ReferenceEquals(killer, pet) ? owner : null;
    }
}
