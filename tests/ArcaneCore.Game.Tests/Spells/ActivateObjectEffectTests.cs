using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class ActivateObjectEffectTests
{
    private const uint Door = 990001;
    private const uint Goober = 990002;

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            SpellInfo[] spells = [.. Enumerable.Range(1, 18).Select(action => Spell(990100u + (uint)action,
                Effect(SpellEffectName.ActivateObject, 0, SpellImplicitTarget.GameObject, misc: action)) with
                { RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0 })];
            Kit = new SpellTestKit(spells);
            var map = Kit.World.GetMap(0);
            Objects = new GameObjectMapSystem(map, new GameObjectContent(
                [GameObjectTestKit.GoTemplate(Door, GameObjectType.Door), GameObjectTestKit.GoTemplate(Goober, GameObjectType.Goober)],
                [], [], [], []));
            map.AddUpdater(Objects);
            (Caster, Session) = Kit.AddPlayer(1);
        }

        public SpellTestKit Kit { get; }
        public GameObjectMapSystem Objects { get; }
        public Player Caster { get; }
        public FakeSession Session { get; }

        public GameObject Spawn(uint entry) => Objects.Summon(entry, 2, 0, Caster.Z, 0)!;

        public SpellCastResult Cast(int action, GameObject go)
        {
            var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = go.Guid };
            return Kit.System.CastSpell(Caster, 990100u + (uint)action, targets, triggered: true);
        }

        public void Dispose() => Kit.Dispose();
    }

    private static SpellInfo ScriptSpell(uint id, int action, SpellImplicitTarget targetA,
        SpellImplicitTarget targetB = SpellImplicitTarget.None, float radius = 0)
        => Spell(id, Effect(SpellEffectName.ActivateObject, 0, targetA, misc: action, targetB: targetB)
            with { Radius = radius }) with { RangeIndex = 4, Range = new SpellRange(0, 30) };

    private static ulong[] SpellGoHits(FakeSession session)
    {
        var reader = new PacketReader(Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgSpellGo).Payload);
        reader.ReadPackedGuid();
        reader.ReadPackedGuid();
        reader.ReadUInt32();
        reader.ReadUInt16();
        int count = reader.ReadByte();
        var hits = new ulong[count];
        for (int i = 0; i < count; i++) hits[i] = reader.ReadUInt64();
        return hits;
    }

    [Fact]
    public void ScriptNearCaster_UsesTheNearestListedObject_OrTheExplicitListedObject()
    {
        const uint spell = 990201;
        using var rig = new Rig();
        rig.Kit.System.Store = new SpellStore([ScriptSpell(spell, 6, (SpellImplicitTarget)40)], [], [],
            [new SpellStore.ScriptTarget(spell, 0, Door, 0)]);
        GameObject near = rig.Objects.Summon(Door, 3, 0, rig.Caster.Z, 0)!;
        GameObject far = rig.Objects.Summon(Door, 12, 0, rig.Caster.Z, 0)!;
        near.Flags |= GameObjectFlags.Locked;
        far.Flags |= GameObjectFlags.Locked;

        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Caster, spell, SpellCastTargets.ForSelf(), triggered: true));
        Assert.False(near.Flags.HasFlag(GameObjectFlags.Locked));
        Assert.True(far.Flags.HasFlag(GameObjectFlags.Locked));

        var explicitFar = new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = far.Guid };
        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Caster, spell, explicitFar, triggered: true));
        Assert.False(far.Flags.HasFlag(GameObjectFlags.Locked));
    }

    [Fact]
    public void ScriptObjectAreas_UseTheSourceOrDestinationAndEveryListedObjectInRange()
    {
        const uint sourceSpell = 990202, destSpell = 990203;
        using var rig = new Rig();
        rig.Kit.System.Store = new SpellStore(
            [ScriptSpell(sourceSpell, 7, SpellImplicitTarget.LocationCasterSrc, (SpellImplicitTarget)51, 10),
             ScriptSpell(destSpell, 6, SpellImplicitTarget.LocationCasterDest, (SpellImplicitTarget)52, 10)],
            [], [],
            [new SpellStore.ScriptTarget(sourceSpell, 0, Goober, 0),
             new SpellStore.ScriptTarget(destSpell, 0, Goober, 0)]);
        GameObject first = rig.Objects.Summon(Goober, 3, 0, rig.Caster.Z, 0)!;
        GameObject second = rig.Objects.Summon(Goober, 7, 0, rig.Caster.Z, 0)!;
        GameObject destination = rig.Objects.Summon(Goober, 25, 0, rig.Caster.Z, 0)!;
        destination.Flags |= GameObjectFlags.Locked;

        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Caster, sourceSpell, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(first.Flags.HasFlag(GameObjectFlags.Locked));
        Assert.True(second.Flags.HasFlag(GameObjectFlags.Locked));
        Assert.True(destination.Flags.HasFlag(GameObjectFlags.Locked));
        ulong[] sourceHits = SpellGoHits(rig.Session);
        Assert.Contains(first.Guid.Value, sourceHits);
        Assert.Contains(second.Guid.Value, sourceHits);
        Assert.DoesNotContain(destination.Guid.Value, sourceHits);
        Assert.DoesNotContain(rig.Caster.Guid.Value, sourceHits); // the unit only carries the object effect internally

        rig.Session.Clear();
        var atDestination = new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = (25, 0, rig.Caster.Z) };
        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Caster, destSpell, atDestination, triggered: true));
        Assert.False(destination.Flags.HasFlag(GameObjectFlags.Locked));
        Assert.True(first.Flags.HasFlag(GameObjectFlags.Locked));
        Assert.True(second.Flags.HasFlag(GameObjectFlags.Locked));
        Assert.Contains(destination.Guid.Value, SpellGoHits(rig.Session));
    }

    [Fact]
    public void ScriptObjectRows_RespectEachEffectsInverseMask()
    {
        const uint spell = 990204;
        using var rig = new Rig();
        SpellInfo twoEffects = Spell(spell,
            Effect(SpellEffectName.ActivateObject, 0, (SpellImplicitTarget)40, misc: 7),
            Effect(SpellEffectName.ActivateObject, 0, (SpellImplicitTarget)40, misc: 6)) with
        { RangeIndex = 4, Range = new SpellRange(0, 30) };
        rig.Kit.System.Store = new SpellStore([twoEffects], [], [],
            [new SpellStore.ScriptTarget(spell, 0, Door, 0b10),
             new SpellStore.ScriptTarget(spell, 0, Goober, 0b01)]);
        GameObject door = rig.Spawn(Door);
        GameObject goober = rig.Spawn(Goober);
        goober.Flags |= GameObjectFlags.Locked;

        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Caster, spell, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(door.Flags.HasFlag(GameObjectFlags.Locked));
        Assert.False(goober.Flags.HasFlag(GameObjectFlags.Locked));
    }

    [Fact]
    public void OnyxiaEruption_AnimatesEveryListedObjectWithoutUsingIt()
    {
        const uint spell = 17731;
        using var rig = new Rig();
        // Imported 17731: effect 0 damages the destination area; effect 1 animates listed objects there.
        SpellInfo eruption = Spell(spell,
            Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.LocationCasterDest,
                targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtDestLoc) with { Radius = 10 },
            Effect(SpellEffectName.ActivateObject, 0, SpellImplicitTarget.LocationCasterDest,
                misc: 5, targetB: (SpellImplicitTarget)52) with { Radius = 10 }) with
        { RangeIndex = 4, Range = new SpellRange(0, 30) };
        rig.Kit.System.Store = new SpellStore(
            [eruption],
            [], [], [new SpellStore.ScriptTarget(spell, 0, Goober, 0)]);
        GameObject first = rig.Objects.Summon(Goober, 3, 0, rig.Caster.Z, 0)!;
        GameObject second = rig.Objects.Summon(Goober, 7, 0, rig.Caster.Z, 0)!;
        rig.Kit.World.RunTick(0);
        rig.Session.Clear();

        var destination = new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = (5, 0, rig.Caster.Z) };
        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Caster, spell, destination, triggered: true));

        Assert.Equal(2, rig.Session.Sent.Count(p => p.Opcode == WorldOpcode.SmsgGameobjectCustomAnim));
        Assert.Equal(GameObjectLootState.Ready, first.LootState);
        Assert.Equal(GameObjectLootState.Ready, second.LootState);
    }

    [Fact]
    public void UnlockAndLock_ChangeTheTargetObjectsFlag()
    {
        using var rig = new Rig();
        GameObject go = rig.Spawn(Door);
        go.Flags |= GameObjectFlags.Locked;
        Assert.Equal(SpellCastResult.CastOk, rig.Cast(6, go));
        Assert.False(go.Flags.HasFlag(GameObjectFlags.Locked));

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(7, go));
        Assert.True(go.Flags.HasFlag(GameObjectFlags.Locked));
    }

    [Fact]
    public void OpenCloseDestroyAndRebuild_UseTheDoorStateMachine()
    {
        using var rig = new Rig();
        GameObject first = rig.Spawn(Door);
        Assert.Equal(GameObjectState.Ready, first.State);
        rig.Cast(8, first);
        Assert.Equal(GameObjectState.Active, first.State);
        rig.Cast(10, first);
        Assert.Equal(GameObjectState.Ready, first.State);
        Assert.Equal(GameObjectLootState.JustDeactivated, first.LootState);

        GameObject second = rig.Spawn(Door);
        second.Flags |= GameObjectFlags.Locked;
        rig.Cast(9, second);
        Assert.Equal(GameObjectState.Active, second.State);
        Assert.False(second.Flags.HasFlag(GameObjectFlags.Locked));
        rig.Cast(18, second);
        Assert.Equal(GameObjectState.Ready, second.State);
        Assert.True(second.Flags.HasFlag(GameObjectFlags.Locked));

        GameObject third = rig.Spawn(Door);
        rig.Cast(12, third);
        Assert.Equal(GameObjectState.ActiveAlternative, third.State);
        rig.Cast(13, third);
        Assert.Equal(GameObjectState.Ready, third.State);
    }

    [Fact]
    public void AnimationInertActiveAndDespawn_AffectOnlyTheNamedObject()
    {
        using var rig = new Rig();
        GameObject go = rig.Spawn(Goober);
        GameObject other = rig.Spawn(Door);
        rig.Kit.World.RunTick(0);
        rig.Session.Clear();

        rig.Cast(1, go);
        Assert.Contains(rig.Session.Sent, packet => packet.Opcode == WorldOpcode.SmsgGameobjectCustomAnim);
        rig.Cast(16, go);
        Assert.True(go.Flags.HasFlag(GameObjectFlags.NoInteract));
        rig.Cast(17, go);
        Assert.False(go.Flags.HasFlag(GameObjectFlags.NoInteract));
        rig.Cast(15, go);
        Assert.False(go.IsSpawned);
        Assert.True(other.IsSpawned);
    }

    [Theory]
    [InlineData(1, 0u)]
    [InlineData(2, 1u)]
    [InlineData(3, 2u)]
    [InlineData(4, 3u)]
    public void CustomAnimationActions_SendTheirOwnAnimationId(int action, uint animation)
    {
        using var rig = new Rig();
        GameObject go = rig.Spawn(Goober);
        rig.Kit.World.RunTick(0);
        rig.Session.Clear();

        rig.Cast(action, go);

        byte[] expected = GameObjectPackets.CustomAnim(go.Guid, animation);
        Assert.Contains(rig.Session.Sent, packet => packet.Opcode == WorldOpcode.SmsgGameobjectCustomAnim
            && packet.Payload.SequenceEqual(expected));
    }

    [Fact]
    public void Disturb_UsesTheGooberTarget()
    {
        using var rig = new Rig();
        GameObject go = rig.Spawn(Goober);

        rig.Cast(5, go);

        Assert.Equal(GameObjectLootState.Activated, go.LootState);
        Assert.Equal(GameObjectState.Active, go.State);
    }

    [Fact]
    public void CloseOnAnActivatedGoober_LeavesItForTheDeactivationPass()
    {
        using var rig = new Rig();
        GameObject go = rig.Spawn(Goober);
        rig.Cast(5, go);

        rig.Cast(10, go);
        Assert.Equal(GameObjectLootState.JustDeactivated, go.LootState);
        rig.Kit.World.RunTick(1);
        Assert.False(go.IsSpawned);
    }

    private sealed class SpellTakingAi : IGameObjectAi
    {
        public int Calls { get; private set; }
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;
        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs) { }
        public bool OnActivateBySpell(GameObjectMapSystem objects, GameObject go, Unit caster, uint spellId, uint action)
        {
            Calls++;
            return true;
        }
    }

    [Fact]
    public void ObjectScriptCanHandleActivationBeforeTheGenericAction()
    {
        using var rig = new Rig();
        GameObject go = rig.Spawn(Goober);
        var ai = new SpellTakingAi();
        rig.Objects.RegisterAi(Goober, ai);

        rig.Cast(16, go);

        Assert.Equal(1, ai.Calls);
        Assert.False(go.Flags.HasFlag(GameObjectFlags.NoInteract));
    }

    [Fact]
    public void SummonTemplar_MakesTheStoneInertAndSpawnsTheDatabaseCreatureForOneMinute()
    {
        const uint spell = 24744, templar = 15209;
        SpellInfo summon = Spell(spell,
            Effect(SpellEffectName.ActivateObject, 0, (SpellImplicitTarget)40, misc: 16)) with
        { RangeIndex = 4, Range = new SpellRange(0, 30) };
        using var kit = new SpellTestKit(summon);
        kit.System.Store = new SpellStore([summon], [], [], [new SpellStore.ScriptTarget(spell, 0, Goober, 0)]);
        var map = kit.World.GetMap(0);
        var creatures = new CreatureMapSystem(map, Content([Template(templar, b => b.Faction = 35)], []),
            new CreatureOptions { AggroRate = 0 }, random: new Random(1));
        map.AddUpdater(creatures);
        var objects = new GameObjectMapSystem(map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(Goober, GameObjectType.Goober)], [], [], [], []));
        map.AddUpdater(objects);
        (Player caster, _) = kit.AddPlayer(1);
        GameObject stone = objects.Summon(Goober, 2, 0, caster.Z, 0)!;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, spell, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(stone.Flags.HasFlag(GameObjectFlags.NoInteract));
        Creature result = Assert.Single(creatures.Creatures, c => c.Entry == templar);
        Assert.Equal(stone.X, result.X);
        Assert.Equal(stone.Y, result.Y);
        kit.World.RunTick(59_999);
        Assert.True(result.IsInWorld);
        kit.World.RunTick(2);
        Assert.False(result.IsInWorld);
    }
}
