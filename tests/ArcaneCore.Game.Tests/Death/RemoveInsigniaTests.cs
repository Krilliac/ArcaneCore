using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>
/// Remove Insignia (22027, SPELL_EFFECT_SKIN_PLAYER_CORPSE): vmangos Spell::EffectSkinPlayerCorpse, Player::RemovedInsignia and the
/// HIGHGUID_CORPSE loot of Player::SendLoot. A battleground body (released first when it was not) becomes bones holding the insignia money,
/// the victim is told SMSG_PLAYER_SKINNED, and a looted-out window leaves the bones unlootable.
/// </summary>
public sealed class RemoveInsigniaTests
{
    private const long T = 1_700_000_000;
    private const uint WarsongGulch = 489;

    private static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(WarsongGulch, 0, MapType.Battleground, 0, 20, 0, -1, 0, 0, "Warsong Gulch", ""),
        ],
        [], [], [], []);

    /// <summary>urand's upper end: 150.</summary>
    private sealed class MaxRandom : Random
    {
        public override int Next(int minValue, int maxValue) => maxValue - 1;
    }

    private sealed class PlayerResolver : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid) => guid.IsEmpty ? null : reference.Guid == guid ? reference : reference.Map?.FindPlayer(guid);
    }

    private sealed class Rig : IDisposable
    {
        public required WorldRuntime World { get; init; }
        public required FixedDeathClock Clock { get; init; }
        public required Map Map { get; init; }
        public required LootService Loot { get; init; }
        public required SpellSystem Spells { get; init; }
        public required Player Looter { get; init; }
        public required FakeSession LooterSession { get; init; }
        public required Player Victim { get; init; }
        public required FakeSession VictimSession { get; init; }

        public void Dispose() => World.Dispose();
    }

    private static Rig CreateRig(uint mapId = WarsongGulch)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        WorldMaps.Of(world).Load(Content);
        var clock = new FixedDeathClock(T);
        DeathHooks.Register(world, new DeathHooks(new DeathOptions(), clock));
        Map map = world.GetMap(mapId);
        map.Combat.Hooks = new TestCombatHooks();
        var victimSession = new FakeSession(2);
        var looterSession = new FakeSession(3);
        Player victim = CombatTestKit.AddPlayer(world, 2, 10, 20, victimSession, mapId: mapId);
        Player looter = CombatTestKit.AddPlayer(world, 3, 11, 20, looterSession, mapId: mapId);
        victim.Level = 60;
        world.RunTick(1);
        map.Combat.Kill(null, victim);
        victim.UnitFlags |= UnitFlags.Skinnable; // the battleground marks its dead (BattleGround.cpp:1788)

        var loot = new LootService(new LootContent([], []));
        var insignia = new InsigniaLoot(loot, new MaxRandom());
        map.Combat.BonesRemoved += loot.RemoveSpecial;
        SpellInfo spell = SpellTestKit.Spell(InsigniaLoot.RemoveInsignia, SpellTestKit.Effect(SpellEffectName.SkinPlayerCorpse, 0, SpellImplicitTarget.None));
        var spells = new SpellSystem(new SpellStore([spell], [], []), () => 1000, units: new PlayerResolver());
        new InsigniaSpells(_ => insignia).Register(spells);
        victimSession.Clear();
        looterSession.Clear();
        return new Rig { World = world, Clock = clock, Map = map, Loot = loot, Spells = spells, Looter = looter, LooterSession = looterSession,
            Victim = victim, VictimSession = victimSession };
    }

    private static void Cast(Rig rig, SpellCastTargets targets)
        => rig.Spells.CastSpell(rig.Looter, InsigniaLoot.RemoveInsignia, targets, triggered: true);

    [Fact]
    public void AnUnreleasedBody_IsReleased_AndBecomesBonesHoldingTheInsigniaMoney()
    {
        using Rig rig = CreateRig();

        Cast(rig, SpellCastTargets.ForUnit(rig.Victim.Guid));

        Assert.Equal(DeathState.Dead, rig.Victim.Combat.DeathState); // "If not released spirit, do it !"
        Assert.Null(rig.Victim.Combat.Corpse);
        Corpse bones = Assert.Single(rig.Map.Combat.Bones);
        Assert.True(bones.IsBones);
        Assert.Equal(Corpse.FlagUnk2 | Corpse.FlagBones, bones.GetUInt32(UpdateFields.CorpseFieldFlags));
        Assert.Equal(Corpse.DynFlagLootable, bones.GetUInt32(UpdateFields.CorpseFieldDynamicFlags));
        Assert.Equal(rig.Victim.Guid, bones.Owner);
        Assert.Same(rig.Map, bones.Map);
        Assert.Equal([0], Assert.Single(SpellTestKit.Packets(rig.VictimSession, WorldOpcode.SmsgPlayerSkinned)));
        Assert.Single(SpellTestKit.Packets(rig.LooterSession, WorldOpcode.SmsgLootResponse));

        LootBag bag = rig.Loot.FindLoot(bones.Guid)!;
        Assert.Equal(LootSourceKind.Bones, bag.Kind);
        Assert.Equal(LootType.Insignia, bag.Type); // shown to the 1.12 client as pickpocketing loot (Player.cpp:7986-7988)
        Assert.Equal(InsigniaLoot.MoneyFor(60, 150, 1f), bag.Gold);
        Assert.True(bag.Gold > 0);
    }

    [Fact]
    public void TheInsigniaMoney_FollowsTheVmangosFormula()
    {
        // urand(50, 150) * 0.016 * (level / 5.76)^2.5: about 6 copper at level 10 for the low roll, a few silver at 60.
        Assert.Equal((uint)(50 * 0.016f * MathF.Pow(10 / 5.76f, 2.5f)), InsigniaLoot.MoneyFor(10, 50, 1f));
        Assert.Equal((uint)(150 * 0.016f * MathF.Pow(60 / 5.76f, 2.5f) * 2f), InsigniaLoot.MoneyFor(60, 150, 2f));
    }

    [Fact]
    public void TakingTheMoney_AndClosing_LeavesTheBonesUnlootable()
    {
        using Rig rig = CreateRig();
        Cast(rig, SpellCastTargets.ForUnit(rig.Victim.Guid));
        Corpse bones = Assert.Single(rig.Map.Combat.Bones);
        uint gold = rig.Loot.FindLoot(bones.Guid)!.Gold;
        uint before = rig.Looter.Money;

        Assert.True(rig.Loot.TakeMoney(rig.Looter));
        rig.Loot.Release(rig.Looter, bones.Guid);

        Assert.Equal(before + gold, rig.Looter.Money);
        Assert.Equal(0u, bones.GetUInt32(UpdateFields.CorpseFieldDynamicFlags));
        Assert.Null(rig.Loot.FindLoot(bones.Guid));
    }

    [Fact]
    public void LeftoverInsignia_CanBeOpenedAgain()
    {
        using Rig rig = CreateRig();
        Cast(rig, SpellCastTargets.ForUnit(rig.Victim.Guid));
        Corpse bones = Assert.Single(rig.Map.Combat.Bones);
        rig.Loot.Release(rig.Looter, bones.Guid);
        rig.LooterSession.Clear();

        Assert.Equal(LootResult.Ok, rig.Loot.Open(rig.Looter, bones.Guid));
        Assert.Single(SpellTestKit.Packets(rig.LooterSession, WorldOpcode.SmsgLootResponse));
    }

    [Fact]
    public void AReleasedGhost_IsSkinnedThroughItsCorpse()
    {
        using Rig rig = CreateRig();
        Assert.True(rig.Map.Combat.RepopPlayer(rig.Victim));
        Corpse corpse = rig.Victim.Combat.Corpse!;

        Cast(rig, new SpellCastTargets { Mask = SpellCastTargetFlags.CorpseEnemy, Corpse = corpse.Guid });

        Assert.Null(rig.Victim.Combat.Corpse);
        Assert.False(corpse.IsInWorld);
        Assert.Single(rig.Map.Combat.Bones);
    }

    [Fact]
    public void ABodyOutsideABattleground_IsNotSkinned()
    {
        using Rig rig = CreateRig(mapId: 0);
        Assert.True(rig.Map.Combat.RepopPlayer(rig.Victim)); // a body outside a battleground is not lootable
        Corpse corpse = rig.Victim.Combat.Corpse!;

        Cast(rig, new SpellCastTargets { Mask = SpellCastTargetFlags.CorpseEnemy, Corpse = corpse.Guid });

        Assert.Same(corpse, rig.Victim.Combat.Corpse);
        Assert.Empty(rig.Map.Combat.Bones);
        Assert.Empty(SpellTestKit.Packets(rig.VictimSession, WorldOpcode.SmsgPlayerSkinned));
    }

    [Fact]
    public void Bones_LeaveTheWorldAfterAnHour_WithTheirLoot()
    {
        using Rig rig = CreateRig();
        Cast(rig, SpellCastTargets.ForUnit(rig.Victim.Guid));
        Corpse bones = Assert.Single(rig.Map.Combat.Bones);

        rig.Clock.Now = T + MapCombat.BonesLifetimeSeconds - 1;
        rig.Map.Combat.Update(1);
        Assert.Single(rig.Map.Combat.Bones);

        rig.Clock.Now = T + MapCombat.BonesLifetimeSeconds;
        rig.Map.Combat.Update(1);
        Assert.Empty(rig.Map.Combat.Bones);
        Assert.False(bones.IsInWorld);
        Assert.Null(rig.Loot.FindLoot(bones.Guid));
    }
}
