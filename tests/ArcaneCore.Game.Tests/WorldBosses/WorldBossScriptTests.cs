using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts.WorldBosses;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.WorldBosses;

/// <summary>The open-world bosses of <see cref="WorldBossScripts"/> on a continent map (no instance script).</summary>
public sealed class WorldBossScriptTests
{
    private sealed class Field(uint entry, params uint[] extraEntries) : IDisposable
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();
        public FakeCaster Caster { get; } = new();
        public Map Map { get; private set; } = null!;
        public CreatureMapSystem Creatures { get; private set; } = null!;
        public Creature Boss { get; private set; } = null!;
        public Player Tank { get; private set; } = null!;

        public void Start(bool aggro = true)
        {
            WorldMaps.Of(World).Load(new MapContent([new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", "")], [], [], [], []));
            Map = World.GetMap(1);
            CreatureTemplate[] templates = [Template(entry), .. extraEntries.Select(e => Template(e))];
            Creatures = new CreatureMapSystem(Map, Content(templates, []), new CreatureOptions { AggroRate = 0, RespawnPacifyMs = 0 },
                random: new Random(1), aiServices: new CreatureAiServices { Spells = Caster, Hostility = new AlwaysHostile() });
            Map.AddUpdater(Creatures);
            Tank = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(), 1);
            Tank.Relocate(0, 0, 450, 0, 0);
            World.AddPlayer(Tank);
            World.RunTick(0);
            Boss = Creatures.SpawnTemporary(Template(entry), 1, 0, 450, 0);
            if (aggro) Boss.AI!.AttackStart(Tank);
        }

        public Creature Spawn(uint other) => Creatures.SpawnTemporary(Template(other), 2, 0, 450, 0);

        public void SetHealthPct(uint pct)
        {
            Boss.MaxHealth = 10000;
            Boss.Health = 100 * pct;
        }

        public int CountOf(uint spell) => Caster.Casts.Count(c => c.Spell == spell);

        public void Dispose() => World.Dispose();
    }

    [Theory]
    [InlineData(14887u, typeof(YsondreAI))]
    [InlineData(14888u, typeof(LethonAI))]
    [InlineData(14889u, typeof(EmerissAI))]
    [InlineData(14890u, typeof(TaerarAI))]
    [InlineData(15261u, typeof(SpiritShadeAI))]
    [InlineData(6109u, typeof(AzuregosAI))]
    [InlineData(12397u, typeof(KazzakAI))]
    public void OpenWorldBoss_GetsItsScript(uint entry, Type ai)
    {
        using var field = new Field(entry);
        field.Start(aggro: false);
        Assert.IsType(ai, field.Boss.AI);
    }

    [Fact]
    public void EventAiAdds_AreNotShadowed()
    {
        foreach (uint add in new uint[] { 15224, 15260, 15302 })
        {
            using var field = new Field(add);
            field.Start(aggro: false);
            Assert.Null(WorldBossScripts.Create(field.Boss));
        }
    }

    [Theory]
    [InlineData(14887u)]
    [InlineData(14888u)]
    [InlineData(14889u)]
    [InlineData(14890u)]
    public void Dragon_SharedNightmareMechanics(uint entry)
    {
        using var field = new Field(entry);
        field.Start();
        Assert.Contains(field.Caster.Casts, c => c.Spell == DragonOfNightmareAI.SpellMarkOfNature && c.Triggered);
        field.Boss.AI!.OnUpdate(1);
        Assert.Equal(1, field.CountOf(DragonOfNightmareAI.SpellAuraOfNature));
        field.Boss.AI.OnUpdate(10000);
        Assert.Equal(1, field.CountOf(DragonOfNightmareAI.SpellNoxiousBreath));
        Assert.Equal(1, field.CountOf(DragonOfNightmareAI.SpellTailSweep));
        Assert.Equal(0, field.CountOf(DragonOfNightmareAI.SpellSeepingFogLeft));
        field.Boss.AI.OnUpdate(10000);
        Assert.Equal(1, field.CountOf(DragonOfNightmareAI.SpellSeepingFogLeft));
        Assert.Equal(1, field.CountOf(DragonOfNightmareAI.SpellSeepingFogRight));
    }

    [Fact]
    public void Dragon_EvadeRemovesMarkOfNature()
    {
        using var field = new Field(14889);
        field.Start();
        field.Boss.AI!.OnEvade();
        Assert.Contains(field.Caster.RemovedAuras, r => r.Spell == DragonOfNightmareAI.SpellMarkOfNature);
    }

    [Fact]
    public void Emeriss_CorruptionOnceAtEachQuarter()
    {
        using var field = new Field(14889);
        field.Start();
        field.SetHealthPct(76);
        field.Boss.AI!.OnUpdate(1);
        Assert.Equal(0, field.CountOf(EmerissAI.SpellCorruptionOfTheEarth));
        field.SetHealthPct(74);
        field.Boss.AI.OnUpdate(1);
        field.Boss.AI.OnUpdate(1);
        Assert.Equal(1, field.CountOf(EmerissAI.SpellCorruptionOfTheEarth));
        field.SetHealthPct(10);
        field.Boss.AI.OnUpdate(1);
        field.Boss.AI.OnUpdate(1);
        field.Boss.AI.OnUpdate(1);
        field.Boss.AI.OnUpdate(1);
        Assert.Equal(3, field.CountOf(EmerissAI.SpellCorruptionOfTheEarth));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 3)]
    [InlineData(8, 6)]
    [InlineData(19, 14)]
    [InlineData(20, 15)]
    [InlineData(40, 15)]
    public void Ysondre_DruidCount(int players, int druids) => Assert.Equal(druids, YsondreAI.DruidCount(players));

    [Fact]
    public void Ysondre_SummonsDruidsThatAttack()
    {
        using var field = new Field(14887, YsondreAI.NpcDruidSpirit);
        field.Start();
        field.SetHealthPct(74);
        field.Boss.AI!.OnUpdate(1);
        Creature[] druids = [.. field.Creatures.CreaturesOfEntryInRange(field.Boss, YsondreAI.NpcDruidSpirit, 10)];
        Assert.Equal(3, druids.Length);
        Assert.All(druids, d => Assert.Same(field.Tank, d.Combat.Victim));
    }

    [Fact]
    public void Taerar_BanishesUntilThreeShadesDie()
    {
        using var field = new Field(14890, TaerarAI.NpcShadeOfTaerar);
        field.Start();
        var ai = Assert.IsType<TaerarAI>(field.Boss.AI);
        field.SetHealthPct(74);
        ai.OnUpdate(1);
        Assert.True(ai.Banished);
        Assert.Equal(1, field.CountOf(TaerarAI.SpellSelfStun));
        Assert.Equal(1, field.CountOf(TaerarAI.SpellShadeFront));
        Assert.NotEqual(UnitFlags.None, field.Boss.UnitFlags & UnitFlags.NotSelectable);
        for (int i = 0; i < 3; i++) ai.OnSummonedCreatureJustDied(field.Spawn(TaerarAI.NpcShadeOfTaerar));
        Assert.False(ai.Banished);
        Assert.Equal(UnitFlags.None, field.Boss.UnitFlags & UnitFlags.NotSelectable);
        Assert.Contains(field.Caster.RemovedAuras, r => r.Spell == TaerarAI.SpellSelfStun);
    }

    [Fact]
    public void Taerar_BanishTimesOutAfterTwoMinutes()
    {
        using var field = new Field(14890);
        field.Start();
        var ai = Assert.IsType<TaerarAI>(field.Boss.AI);
        field.SetHealthPct(74);
        ai.OnUpdate(1);
        ai.OnUpdate(119000);
        Assert.True(ai.Banished);
        ai.OnUpdate(1000);
        Assert.False(ai.Banished);
    }

    [Fact]
    public void Lethon_DrawSpiritLeavesAShadeThatOffersToHim()
    {
        using var field = new Field(14888, SpiritShadeAI.Entry);
        field.Start();
        field.Tank.DisplayId = 59;
        field.Boss.AI!.OnSpellHitTarget(field.Tank, new SpellInfo { Id = LethonAI.SpellDrawSpirit });
        Creature shade = Assert.Single(field.Creatures.CreaturesOfEntryInRange(field.Boss, SpiritShadeAI.Entry, 10));
        Assert.Equal(59u, shade.DisplayId);
        var shadeAi = Assert.IsType<SpiritShadeAI>(shade.AI);
        Assert.Same(field.Boss, shadeAi.Lethon);
        shadeAi.OnUpdate(2000);
        Assert.Equal(0, field.CountOf(SpiritShadeAI.SpellDarkOffering));
        shadeAi.OnUpdate(500);
        Assert.Contains(field.Caster.Casts, c => c.Spell == SpiritShadeAI.SpellDarkOffering && ReferenceEquals(c.Target, field.Boss));
    }

    [Fact]
    public void Azuregos_GossipOffInCombat_AndMarksKilledPlayers()
    {
        using var field = new Field(6109);
        field.Start(aggro: false);
        field.Boss.NpcFlags = (uint)NpcFlags.Gossip;
        field.Boss.AI!.AttackStart(field.Tank);
        Assert.Equal(0u, field.Boss.NpcFlags & (uint)NpcFlags.Gossip);
        Assert.Contains(field.Caster.Casts, c => c.Spell == AzuregosAI.SpellMarkOfFrostAura);
        field.Boss.AI.OnKilledUnit(field.Tank);
        Assert.Contains(field.Caster.UnitCasts, c => c.Spell == AzuregosAI.SpellMarkOfFrostPlayer && ReferenceEquals(c.Caster, field.Tank));
        field.Boss.AI.OnEvade();
        Assert.Equal((uint)NpcFlags.Gossip, field.Boss.NpcFlags & (uint)NpcFlags.Gossip);
    }

    [Fact]
    public void Azuregos_CleavesEverySevenSeconds()
    {
        using var field = new Field(6109);
        field.Start();
        field.Boss.AI!.OnUpdate(7000);
        Assert.Equal(1, field.CountOf(AzuregosAI.SpellCleave));
        field.Boss.AI.OnUpdate(7000);
        Assert.Equal(2, field.CountOf(AzuregosAI.SpellCleave));
    }

    [Fact]
    public void Kazzak_CapturesSoul_AndGoesBerserkAtThreeMinutes()
    {
        using var field = new Field(12397);
        field.Start();
        var ai = Assert.IsType<KazzakAI>(field.Boss.AI);
        Assert.Contains(field.Caster.Casts, c => c.Spell == KazzakAI.SpellCaptureSoul && c.Triggered);
        ai.OnUpdate(7000);
        Assert.Equal(1, field.CountOf(KazzakAI.SpellCleave));
        Assert.False(ai.Berserk);
        for (int i = 0; i < 173; i++) ai.OnUpdate(1000);
        Assert.True(ai.Berserk);
        Assert.Equal(1, field.CountOf(KazzakAI.SpellBerserk));
        int volleys = field.CountOf(KazzakAI.SpellShadowVolley);
        ai.OnUpdate(3000); // berserk volleys come every 1-3 s
        Assert.Equal(volleys + 1, field.CountOf(KazzakAI.SpellShadowVolley));
    }

    [Fact]
    public void Kazzak_MarkExplodesWhenTheTargetRunsDry()
    {
        using var field = new Field(12397);
        field.Start();
        var ai = Assert.IsType<KazzakAI>(field.Boss.AI);
        field.Tank.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        field.Tank.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000);
        field.Tank.SetUInt32(UpdateFields.UnitFieldPower1, 500);
        ai.OnUpdate(25000);
        Assert.Contains(field.Caster.Casts, c => c.Spell == KazzakAI.SpellMarkOfKazzak && ReferenceEquals(c.Target, field.Tank));
        field.Caster.Auras.Add((field.Tank, KazzakAI.SpellMarkOfKazzak));
        ai.OnUpdate(1);
        Assert.DoesNotContain(field.Caster.UnitCasts, c => c.Spell == KazzakAI.SpellMarkOfKazzakExplode);
        field.Tank.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        ai.OnUpdate(1);
        Assert.Contains(field.Caster.UnitCasts, c => c.Spell == KazzakAI.SpellMarkOfKazzakExplode && ReferenceEquals(c.Caster, field.Tank));
        Assert.Contains(field.Caster.RemovedAuras, r => r.Spell == KazzakAI.SpellMarkOfKazzak);
    }
}
