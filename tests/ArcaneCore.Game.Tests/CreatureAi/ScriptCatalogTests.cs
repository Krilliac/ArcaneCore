using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Instances.Scripts.Deadmines;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi;

public sealed class ScriptCatalogTests
{
    private static CreatureTemplate T(uint entry, string script = "", string ai = "", bool civilian = false)
        => new() { Entry = entry, Name = "T" + entry, ScriptName = script, AIName = ai, Civilian = civilian };

    private static ScriptResolution Resolve(CreatureTemplate t, uint map, CreatureAiFactory? factory = null, CreatureMapSystem? live = null)
        => new ScriptCatalog(factory ?? new CreatureAiFactory(), new CreatureContent([t], [], [], [], [])).Resolve(t, map, live);

    [Fact]
    public void DungeonBossAi_IsResolvedByMapAndEntry()
    {
        ScriptResolution r = Resolve(T(10503, "boss_jandice_barov"), 289);
        Assert.Equal(ScriptKind.DungeonBossAi, r.Kind);
        Assert.Equal("JandiceBarovAI", r.Implementation);
        Assert.True(r.ScriptNameResolved);
        Assert.False(Resolve(T(10503, "boss_jandice_barov"), 0).ScriptNameResolved);
    }

    [Fact]
    public void NamedScript_IsResolvedByScriptName()
    {
        var factory = new CreatureAiFactory();
        factory.Register("my_script", c => new AggressorAI(c));
        ScriptResolution r = Resolve(T(5, "my_script"), 0, factory);
        Assert.Equal(ScriptKind.ScriptName, r.Kind);
        Assert.True(r.ScriptNameResolved);
    }

    [Fact]
    public void EntryScript_ReportsItsTypeName()
    {
        ScriptResolution r = Resolve(T(RuulSnowhoofAI.Entry, "npc_ruul_snowhoof"), 1);
        Assert.Equal(ScriptKind.EntryScript, r.Kind);
        Assert.Equal(nameof(RuulSnowhoofAI), r.Implementation);
    }

    [Theory]
    [InlineData("EventAI", ScriptKind.EventAI)]
    [InlineData("SmartAI", ScriptKind.SmartAI)]
    [InlineData("NullAI", ScriptKind.BuiltInAi)]
    [InlineData("NoSuchAI", ScriptKind.UnknownAiName)]
    [InlineData("", ScriptKind.Default)]
    public void AiName_SelectsTheFallbackKind(string ai, ScriptKind kind)
    {
        ScriptResolution r = Resolve(T(6, ai: ai), 0);
        Assert.Equal(kind, r.Kind);
        Assert.True(r.ScriptNameResolved);
    }

    [Fact]
    public void Default_ASpiritHealerWithoutAnAiName_ResolvesToNullCreatureAi()
    {
        CreatureTemplate healer = T(6491) with { NpcFlags = (uint)ArcaneCore.Game.Npc.NpcFlags.SpiritHealer };
        ScriptResolution r = Resolve(healer, 0);
        Assert.Equal(ScriptKind.Default, r.Kind);
        Assert.Equal(nameof(NullCreatureAI), r.Implementation);
    }

    [Fact]
    public void Default_DistinguishesCivilianFromAggressor()
    {
        Assert.Equal("ReactorAI", Resolve(T(7, civilian: true), 0).Implementation);
        Assert.Equal("AggressorAI", Resolve(T(7), 0).Implementation);
    }

    [Fact]
    public void UnresolvedScriptName_IsMissing_EvenOnAnInstanceMap()
    {
        ScriptResolution plain = Resolve(T(8, "boss_nothing", "EventAI"), 0);
        Assert.Equal(ScriptKind.EventAI, plain.Kind);
        Assert.True(plain.ScriptNameMissing);
        Assert.True(Resolve(T(8, "boss_nothing"), 36).ScriptNameMissing);
    }

    [Fact]
    public void InstanceScriptedEntry_IsResolvedFromTheTable()
    {
        ScriptResolution r = Resolve(T(646, "boss_mr_smite"), 36);
        Assert.Equal(ScriptKind.InstanceScriptAi, r.Kind);
        Assert.True(r.ScriptNameResolved);
        Assert.True(Resolve(T(646, "boss_mr_smite"), 0).ScriptNameMissing); // another map: not the Deadmines
    }

    [Theory]
    [InlineData(229u, 10339u, "GythAI")]
    [InlineData(229u, 9816u, "PyroguardEmberseerAI")]
    [InlineData(229u, 9568u, "OverlordWyrmthalakAI")]
    [InlineData(289u, 1853u, "DarkmasterGandlingAI")]
    [InlineData(289u, 10503u, "JandiceBarovAI")]
    [InlineData(289u, 10498u, "SpectralTutorAI")]
    [InlineData(329u, 10384u, "SpectralGhostlyCitizenAI")]
    [InlineData(329u, 10385u, "SpectralGhostlyCitizenAI")]
    [InlineData(329u, 10436u, "BaronessAnastariAI")]
    [InlineData(329u, 10438u, "MalekiThePallidAI")]
    [InlineData(329u, 10997u, "CannonMasterWilleyAI")]
    [InlineData(329u, 10812u, "DathrohanBalnazzarAI")]
    public void DungeonBossAis_PinsEveryMapEntryPair(uint map, uint entry, string type)
    {
        Assert.Equal(type, ArcaneCore.Game.Instances.Scripts.DungeonBossAis.Describe(map, entry));
        Assert.Equal(type, Resolve(T(entry, "boss_x"), map).Implementation);
        Assert.Null(ArcaneCore.Game.Instances.Scripts.DungeonBossAis.Describe(map == 289 ? 329u : 289u, entry));
    }

    [Theory]
    [InlineData(6109u, "boss_azuregos", 1u, ScriptKind.WorldBossAi)]
    [InlineData(12397u, "boss_kazzak", 0u, ScriptKind.WorldBossAi)]
    [InlineData(11583u, "boss_nefarian", 469u, ScriptKind.InstanceScriptAi)]
    [InlineData(15989u, "boss_sapphiron", 533u, ScriptKind.InstanceScriptAi)]
    public void OpenWorldAndSummonedBosses_Resolve(uint entry, string script, uint map, ScriptKind kind)
    {
        ScriptResolution r = Resolve(T(entry, script), map);
        Assert.Equal(kind, r.Kind);
        Assert.True(r.ScriptNameResolved);
    }

    [Fact]
    public void MapEntryAi_IsSeenThroughALiveMap()
    {
        using var run = new Instances.DungeonScriptTestKit(map => new DeadminesInstance(map), [646], [646], []);
        ScriptResolution r = Resolve(T(646, "boss_mr_smite"), 36, live: run.Creatures);
        Assert.Equal(ScriptKind.MapEntryAi, r.Kind);
        Assert.Equal(nameof(MrSmiteAi), r.Implementation);
    }
}
