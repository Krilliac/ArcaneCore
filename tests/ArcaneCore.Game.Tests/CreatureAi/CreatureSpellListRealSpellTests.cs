using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// Spell lists through the real spell system: the list time replacing the Spell.dbc recovery in <c>SpellSystem.AddCooldown</c> (cmangos Creature::AddCooldown),
/// the dispel target rows against real auras, and a charm or a pet AI taking the list away (cmangos PetAI::UpdateAI runs no spell lists).
/// </summary>
public sealed class CreatureSpellListRealSpellTests
{
    private const uint ListId = 1000;
    private const uint ListedEntry = 9103;
    private const uint Bolt = 930_101;
    private const uint Sibling = 930_102;
    private const uint Dispel = 930_103;
    private const uint RootMagic = 930_104;
    private const uint RootCurse = 930_105;
    private const uint ListRepeatMs = 6000;

    /// <summary>The real caster with every list question passed on, recording what the creature asks to cast.</summary>
    private sealed class RecordingCaster(ICreatureSpellCaster inner) : ICreatureSpellCaster
    {
        public List<(Creature Caster, uint Spell, Unit? Target)> Requests { get; } = [];

        public ICreatureSpellCaster Inner => inner;

        public event Action<Unit, Unit, SpellInfo>? SpellHit
        {
            add => inner.SpellHit += value;
            remove => inner.SpellHit -= value;
        }

        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
        {
            Requests.Add((caster, spellId, target));
            return inner.Cast(caster, spellId, target, triggered);
        }

        public SpellInfo? FindSpell(uint spellId) => inner.FindSpell(spellId);

        public bool IsSpellReady(Creature caster, uint spellId) => inner.IsSpellReady(caster, spellId);

        public IReadOnlyList<SpellAuraHolder> GetAuras(Unit unit) => inner.GetAuras(unit);

        public bool IsImmuneToSpell(Unit target, SpellInfo spell) => inner.IsImmuneToSpell(target, spell);

        public bool IsCasting(Creature caster) => inner.IsCasting(caster);

        public bool HasAura(Unit unit, uint spellId) => inner.HasAura(unit, spellId);

        public void Interrupt(Creature caster) => inner.Interrupt(caster);

        public void OnCreatureRemoved(Creature creature) => inner.OnCreatureRemoved(creature);
    }

    private static SpellInfo Instant(uint id, uint recovery = 0, uint category = 0, uint categoryRecovery = 0)
        => Spell(id, Effect(SpellEffectName.SchoolDamage, 1, SpellImplicitTarget.UnitEnemy)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 100),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
            RecoveryTime = recovery,
            Category = category,
            CategoryRecoveryTime = categoryRecovery,
        };

    private static SpellInfo Root(uint id, uint dispelType)
        => Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModRoot)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 100),
            Dispel = dispelType,
            SpellVisual = 1,
        };

    private static SpellInfo DispelMagic()
        => Spell(Dispel, Effect(SpellEffectName.Dispel, 1, SpellImplicitTarget.UnitFriend, misc: 1)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 100),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static CreatureSpellListEntry Entry(uint spell, uint repeat = ListRepeatMs, uint initial = 0, CreatureSpellListFlags flags = CreatureSpellListFlags.None, uint target = CreatureSpellListTargets.Current)
        => new(1, spell, flags, target, 0, 100, 0, initial, initial, repeat, repeat);

    /// <summary>A listed wolf and a plain creature of its faction, a player, and the world and the spell system ticking together.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(CreatureSpellListEntry entry, params SpellInfo[] spells)
        {
            Kit = new SpellTestKit(spells);
            Caster = new RecordingCaster(new SpellSystemCreatureCaster(Kit.System));
            CreatureContent content = new(
                [
                    Template(configure: t => t.AIName = CreatureAiFactory.EventAIName) with { SpellList = ListId },
                    Template(ListedEntry),
                ],
                [Spawn(1, WolfEntry, 5, 0), Spawn(2, ListedEntry, 7, 0)], [], [], [],
                new CreatureAiContent([], []) { SpellLists = new CreatureSpellListCatalog([new CreatureSpellList(ListId, "test", 0, 0, [entry])]) });
            (_, Map, System) = CreateAiSystem(content, new CreatureAiServices { Spells = Caster }, world: Kit.World);
            (Player, _) = Kit.AddPlayer(1);
            Player.MaxHealth = 1_000_000; // the fights below outlast a level 1 character
            Player.Health = 1_000_000;
            Wolf = Assert.Single(System.Creatures, c => c.Template.Entry == WolfEntry);
            Friend = Assert.Single(System.Creatures, c => c.Template.Entry == ListedEntry);
        }

        public SpellTestKit Kit { get; }

        public RecordingCaster Caster { get; }

        public Map Map { get; }

        public CreatureMapSystem System { get; }

        public Player Player { get; }

        public Creature Wolf { get; }

        public Creature Friend { get; }

        public IEnumerable<uint> Spells => Caster.Requests.Where(r => ReferenceEquals(r.Caster, Wolf)).Select(r => r.Spell);

        public void Pull(Creature creature) => Map.Combat.DealDamage(Player, creature, 1, direct: false);

        public bool IsReady(uint spell) => Kit.System.IsSpellReady(Wolf, Kit.Store.Get(spell)!);

        public void Tick(uint ms)
        {
            for (uint done = 0; done < ms; done += 100)
            {
                Kit.World.RunTick(100);
                Kit.Advance(100, 100);
            }
        }

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void AListCast_StartsTheListTimeInPlaceOfTheSpellDbcTime_AndTheListRecastsOnThatOneTime()
    {
        using var rig = new Rig(Entry(Bolt), Instant(Bolt, recovery: 1000));

        rig.Pull(rig.Wolf);
        rig.Tick(300);
        Assert.Equal([Bolt], rig.Spells);

        rig.Tick(3000); // past the Spell.dbc second, inside the list's six
        Assert.False(rig.IsReady(Bolt));
        Assert.Equal([Bolt], rig.Spells);

        rig.Tick(4500); // past six seconds and one 1200 ms tick; a time applied twice would hold it until twelve
        Assert.Equal([Bolt, Bolt], rig.Spells);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TheCategoryFlag_SendsTheListTimeToTheCategory(bool categoryFlag, bool siblingReady)
    {
        CreatureSpellListFlags flags = categoryFlag ? CreatureSpellListFlags.CategoryCooldown : CreatureSpellListFlags.None;
        using var rig = new Rig(Entry(Bolt, flags: flags), Instant(Bolt, category: 77, categoryRecovery: 1000), Instant(Sibling, category: 77, categoryRecovery: 1000));

        rig.Pull(rig.Wolf);
        rig.Tick(300);
        Assert.Equal([Bolt], rig.Spells);

        rig.Tick(3000);
        Assert.False(rig.IsReady(Bolt));
        Assert.Equal(siblingReady, rig.IsReady(Sibling));
    }

    [Fact]
    public void ARepeatTimeOfZero_LeavesTheSpellDbcTime()
    {
        using var rig = new Rig(Entry(Bolt, repeat: 0), Instant(Bolt, recovery: 3000));

        rig.Pull(rig.Wolf);
        rig.Tick(300);
        Assert.Equal([Bolt], rig.Spells);

        rig.Tick(1000);
        Assert.False(rig.IsReady(Bolt));
    }

    [Fact]
    public void AScriptsCastOfAListSpell_TakesTheListTimeToo()
    {
        using var rig = new Rig(Entry(Bolt, initial: 600_000), Instant(Bolt, recovery: 1000));
        rig.Pull(rig.Wolf); // the list itself stays behind its initial cooldown

        Assert.Equal(CreatureCastResult.Ok, rig.System.CastSpell(rig.Wolf, Bolt, rig.Player, triggered: false));
        rig.Tick(3000);

        Assert.False(rig.IsReady(Bolt));
    }

    [Theory]
    [InlineData(CreatureSpellListTargets.DispellableFriendly, true, RootMagic, 1)]
    [InlineData(CreatureSpellListTargets.DispellableFriendlyNoSelf, true, RootMagic, 0)]
    [InlineData(CreatureSpellListTargets.DispellableFriendlyNoSelf, false, RootMagic, 2)]
    [InlineData(CreatureSpellListTargets.DispellableFriendly, false, RootCurse, 0)] // held, but not by anything the spell dispels
    public void TheDispelRows_TakeAHeldFriendWithAnAuraOfTheSpellsType(uint targetId, bool rootSelf, uint root, int expected)
    {
        using var rig = new Rig(Entry(Dispel, target: targetId), DispelMagic(), Root(RootMagic, 1), Root(RootCurse, 2));
        rig.Pull(rig.Wolf);
        rig.Pull(rig.Friend);
        rig.Tick(1500);
        Assert.Empty(rig.Spells); // nobody is held yet

        Creature held = rootSelf ? rig.Wolf : rig.Friend;
        Assert.Equal(CreatureCastResult.Ok, rig.Caster.Inner.AddAura(held, root, permanent: true));
        rig.Tick(1500);

        Unit? target = expected switch
        {
            1 => rig.Wolf,
            2 => rig.Friend,
            _ => null,
        };
        if (target is null)
        {
            Assert.Empty(rig.Spells);
        }
        else
        {
            Assert.Contains(rig.Caster.Requests, r => r.Spell == Dispel && ReferenceEquals(r.Target, target));
        }
    }

    private static byte[] PetAction(ObjectGuid pet, uint action, ActionType type, ObjectGuid target = default)
    {
        var w = new PacketWriter(20);
        w.WriteUInt64(pet.Value);
        w.WriteUInt32(ActionButton.Make(action, type).Packed);
        w.WriteUInt64(target.Value);
        return w.ToArray();
    }

    private static (PetTestKit Kit, RecordingCaster Caster, Player Player, Creature Mob) ListedMob()
    {
        CreatureTemplate template = Template(ListedEntry, b =>
        {
            b.Faction = 14;
            b.MinLevel = 10;
            b.MaxLevel = 10;
            b.MinLevelHealth = 500;
            b.MaxLevelHealth = 500;
            b.AIName = CreatureAiFactory.EventAIName;
        }) with { SpellList = ListId };
        var ai = new CreatureAiContent([], []) { SpellLists = new CreatureSpellListCatalog([new CreatureSpellList(ListId, "test", 0, 0, [Entry(Bolt, repeat: 1000)])]) };
        RecordingCaster? caster = null;
        var kit = new PetTestKit(
            [.. CharmPossessTests.ControlSpells(), Instant(Bolt)],
            extraTemplates: [template],
            creatureAi: ai,
            casterFactory: spells => caster = new RecordingCaster(new SpellSystemCreatureCaster(spells)));
        (Player player, _) = kit.AddPlayer(1, 5, 5);
        return (kit, caster!, player, kit.Creatures.SpawnTemporary(template, 8, 5, 83.5f, 0));
    }

    [Fact]
    public void ACharmedCreature_CastsNothingOfItsList_InAFight_AndGetsItBackWhenTheCharmEnds()
    {
        (PetTestKit kit, RecordingCaster caster, Player player, Creature mob) = ListedMob();
        using (kit)
        {
            kit.Map.Combat.DealDamage(player, mob, 1, direct: false);
            kit.Run(1500);
            Assert.Contains(caster.Requests, r => r.Spell == Bolt); // the list runs before the charm

            Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.CastSpell(player, CharmPossessTests.CharmSpell, SpellCastTargets.ForUnit(mob.Guid), triggered: true));
            Assert.IsType<PetAI>(mob.AI);
            Assert.Null(mob.SpellLists);

            // The charm ended its old fight; a new one has it attack on command, with the list's one second repeat long over.
            Creature enemy = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(PetTestKit.NpcCasterEntry)!, 6, 6, 83.5f, 0);
            kit.Controller.HandleAction(player, PetPackets.ReadAction(PetAction(mob.Guid, (uint)CommandState.Attack, ActionType.Command, enemy.Guid)));
            Assert.Same(enemy, mob.Combat.Victim);
            kit.Map.Combat.DealDamage(enemy, mob, 1, direct: false);
            caster.Requests.Clear();
            kit.Run(3000);
            Assert.True(mob.Combat.IsInCombat);
            Assert.DoesNotContain(caster.Requests, r => r.Spell == Bolt);

            kit.Spells.System.RemoveAuras(mob, CharmPossessTests.CharmSpell);
            Assert.IsType<CreatureEventAI>(mob.AI);
            Assert.Equal(ListId, mob.SpellLists?.List?.Id);
        }
    }

    [Fact]
    public void APetAi_TakesTheTemplatesListAway()
    {
        (PetTestKit kit, _, _, Creature mob) = ListedMob();
        using (kit)
        {
            Assert.Equal(ListId, mob.SpellLists?.List?.Id);

            kit.Service.AttachPetAi(mob);

            Assert.IsType<PetAI>(mob.AI);
            Assert.Null(mob.SpellLists);
        }
    }
}
