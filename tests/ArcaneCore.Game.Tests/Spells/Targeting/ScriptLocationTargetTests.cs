using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells.Targeting;

/// <summary>
/// TARGET_LOCATION_SCRIPT_NEAR_CASTER (46; vmangos Spell::CheckScriptTargeting, Spell.cpp:446-600): the nearest spell_script_target object within range
/// is the destination and, with target A 46, the effect's target.
/// </summary>
public sealed class ScriptLocationTargetTests
{
    private const uint Firework = 990301;
    private const uint Firecracker = 990501;
    private const uint Fountain = 990502;
    private const uint Marker = 990601;
    private const int Target46 = 46;

    private static SpellInfo FireworkSpell(uint id = Firework) => Spell(id, Effect(SpellEffectName.Dummy, 0, (SpellImplicitTarget)Target46))
        with { RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private sealed class Rig : IDisposable
    {
        public Rig(params SpellStore.ScriptTarget[] rows)
        {
            Kit = new SpellTestKit([FireworkSpell()]);
            var map = Kit.World.GetMap(0);
            Objects = new GameObjectMapSystem(map, new GameObjectContent(
                [GameObjectTestKit.GoTemplate(Firecracker, GameObjectType.Goober), GameObjectTestKit.GoTemplate(Fountain, GameObjectType.Goober)],
                [], [], [], []));
            map.AddUpdater(Objects);
            (Caster, Session) = Kit.AddPlayer(1);
            Kit.System.Store = new SpellStore([FireworkSpell()], [], [], rows);
        }

        public SpellTestKit Kit { get; }
        public GameObjectMapSystem Objects { get; }
        public Player Caster { get; }
        public FakeSession Session { get; }

        public GameObject Spawn(uint entry, float x) => Objects.Summon(entry, x, 0, Caster.Z, 0)!;

        public SpellCastResult Cast(uint spell = Firework) => Kit.System.CastSpell(Caster, spell, SpellCastTargets.ForSelf(), triggered: true);

        public ulong[] SpellGoHits()
        {
            var reader = new PacketReader(Assert.Single(Session.Sent, p => p.Opcode == WorldOpcode.SmsgSpellGo).Payload);
            reader.ReadPackedGuid();
            reader.ReadPackedGuid();
            reader.ReadUInt32();
            reader.ReadUInt16();
            int count = reader.ReadByte();
            var hits = new ulong[count];
            for (int i = 0; i < count; i++) hits[i] = reader.ReadUInt64();
            return hits;
        }

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void ARowOfTypeGameObject_LandsOnTheNearestListedObject_NotAnUnlistedOrFartherOne()
    {
        using var rig = new Rig(new SpellStore.ScriptTarget(Firework, 0, Firecracker, 0));
        GameObject far = rig.Spawn(Firecracker, 20);
        GameObject near = rig.Spawn(Firecracker, 4);
        GameObject unlisted = rig.Spawn(Fountain, 2);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast());

        ulong[] hits = rig.SpellGoHits();
        Assert.Contains(near.Guid.Value, hits);
        Assert.DoesNotContain(far.Guid.Value, hits);
        Assert.DoesNotContain(unlisted.Guid.Value, hits);
    }

    [Fact]
    public void AListedObjectBeyondTheSpellRange_IsNotSelected()
    {
        using var rig = new Rig(new SpellStore.ScriptTarget(Firework, 0, Firecracker, 0));
        GameObject outside = rig.Spawn(Firecracker, 60);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast());

        Assert.DoesNotContain(outside.Guid.Value, rig.SpellGoHits());
    }

    [Fact]
    public void ARowOfTypeCreature_LandsOnTheNearestListedCreature()
    {
        const uint proxy = 16398;
        using var kit = new PetTestKit([FireworkSpell()], extraTemplates: [CreatureTestKit.Template(proxy, b => b.Faction = 35)]);
        kit.Spells.System.Store = new SpellStore(kit.Spells.Store.All, [], [], [new SpellStore.ScriptTarget(Firework, 1, proxy, 0)]);
        List<Unit> hit = [];
        kit.Spells.System.SpellHit += (_, target, spell) =>
        {
            if (spell.Id == Firework) hit.Add(target);
        };
        (Player caster, _) = kit.AddPlayer(1);
        Creature near = kit.Creatures.SummonForInstance(proxy, 5f, 0f, caster.Z, 0f)!;
        Creature far = kit.Creatures.SummonForInstance(proxy, 20f, 0f, caster.Z, 0f)!;
        Creature unlisted = kit.Creatures.SummonForInstance(PetTestKit.WildEntry, 2f, 0f, caster.Z, 0f)!;

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(caster, Firework));

        Assert.Same(near, Assert.Single(hit));
        Assert.DoesNotContain(far, hit);
        Assert.DoesNotContain(unlisted, hit);
    }

    [Fact]
    public void ASpellWithNoRows_FailsWithBadTargets()
    {
        using var rig = new Rig();
        rig.Spawn(Firecracker, 4);

        Assert.Equal(SpellCastResult.BadTargets, rig.Cast());
    }

    [Fact]
    public void ASpellSpecificSelector_KeepsPrecedence_AndNeedsNoRows()
    {
        // Destroy Egg (BlackwingLairTargetModule) owns its target 46; the generic selector never runs for it, rows or not.
        using var rig = new Rig();
        GameObject near = rig.Spawn(Firecracker, 3);
        bool ran = false;
        rig.Kit.System.RegisterSpellTargetSelector(Firework, (SpellImplicitTarget)Target46, (_, cast, _, _) =>
        {
            ran = true;
            return [(cast.Caster, 1f)];
        });

        Assert.Equal(SpellCastResult.CastOk, rig.Cast());

        Assert.True(ran);
        Assert.DoesNotContain(near.Guid.Value, rig.SpellGoHits());
    }

    [Theory]
    [InlineData(11)]
    [InlineData(46)]
    [InlineData(58)]
    [InlineData(62)]
    public void TheTargetsOfThisArea_AreSelectable(int target)
    {
        using var rig = new Rig();
        SpellInfo spell = Spell(Marker, Effect(SpellEffectName.Dummy, 0, (SpellImplicitTarget)target));

        Assert.True(rig.Kit.System.CanSelectImplicitTarget(spell, spell.Effects[0]));
    }
}
