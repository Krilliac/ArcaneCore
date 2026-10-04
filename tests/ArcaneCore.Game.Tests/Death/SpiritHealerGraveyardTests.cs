using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>
/// The spirit healer's resurrection finishes the way vmangos' SendSpiritResurrect does (NPCHandler.cpp:430-471): the ghost is
/// teleported to the graveyard nearest its corpse when that differs from the one nearest to where it stands, and the
/// resurrection sickness starts at <c>Death.SicknessLevel</c> (Player.cpp:4675-4697).
/// </summary>
public sealed class SpiritHealerGraveyardTests
{
    private const long T = 1_700_000_000;

    private static readonly MapContent Content = new(
        [new MapTemplate(0, 0, MapType.Common, 12, 0, 0, -1, 0, 0, "Eastern Kingdoms", "")],
        [new AreaTemplate(12, 0, 0, 0, 0, 0, "Elwynn", 0, 0)],
        [], [], []);

    private static readonly SpellInfo Sickness = SpellTestKit.Spell(SpiritHealerResurrection.ResurrectionSicknessSpell,
        SpellTestKit.Effect(SpellEffectName.ApplyAura, -75, aura: AuraType.Dummy)) with
    {
        Duration = new SpellDuration(600_000, 0, 600_000),
    };

    // G1 near the origin, G2 a thousand yards east; the corpse lies by G2, the ghost by G1.
    private static readonly GraveyardContent Graveyards = new(
        [new WorldSafeLoc(1, 0, 0, 0, 0, 0f, "G1"), new WorldSafeLoc(2, 0, 1000, 0, 0, 2.5f, "G2"), new WorldSafeLoc(3, 0, 2000, 0, 0, 0f, "G3 no facing")],
        [new GraveyardLink(1, 12, 0), new GraveyardLink(2, 12, 0), new GraveyardLink(3, 12, 0)]);

    private sealed class Rig : IDisposable
    {
        public Rig(int sicknessLevel = 11, byte level = 15)
        {
            World = TestWorld.CreateRuntime();
            DeathHooks.Register(World, new DeathHooks(new DeathOptions { SicknessLevel = sicknessLevel }, new FixedDeathClock(T)));
            WorldMaps.Of(World).Load(Content);
            Teleports = new TeleportService(World, _ => { }, _ => { });
            WorldGraveyards.Of(World).Load(Graveyards, null);
            DeathSeams.Of(World).TryRegisterGraveyards(new GraveyardRepopService(World, () => Teleports));
            Spells = new SpellSystem(new SpellStore([Sickness], [], []), () => 0u) { MapUpdateIntervalMs = 0 };
            Healer = new SpiritHealerResurrection(() => Spells, null, Saved.Add);
            Session = new FakeSession(7);
            Player = CombatTestKit.AddPlayer(World, 7, 990, 0, Session, level: level);
            World.RunTick(1);
        }

        public WorldRuntime World { get; }

        public TeleportService Teleports { get; }

        public SpellSystem Spells { get; }

        public SpiritHealerResurrection Healer { get; }

        public FakeSession Session { get; }

        public Player Player { get; }

        public List<Player> Saved { get; } = [];

        public MapCombat Combat => World.GetMap(0).Combat;

        /// <summary>Die at (<paramref name="corpseX"/>, 0), release, and stand as a ghost at (<paramref name="ghostX"/>, 0).</summary>
        public void DieAtAndStandAt(float corpseX, float ghostX)
        {
            Player.Relocate(corpseX, 0, 0, 1.25f, 0);
            Player.Health = 0;
            Combat.KillPlayer(Player);
            Assert.True(Combat.RepopPlayer(Player));
            Player.Relocate(ghostX, 0, 0, 1.25f, 0);
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void WhenTheCorpsesGraveyardIsAnother_TheResurrectedPlayerIsTeleportedThere_FacingTheSafeLocsFacing()
    {
        using var rig = new Rig();
        rig.DieAtAndStandAt(corpseX: 990, ghostX: 10);

        Assert.True(rig.Healer.ResurrectAtSpiritHealer(rig.Player));

        Assert.True(rig.Player.IsAlive);
        Assert.Equal(new TeleportDestination(0, 1000, 0, 0, 2.5f), rig.Teleports.DestinationOf(rig.Player));
        Assert.Equal([rig.Player], rig.Saved);
    }

    [Fact]
    public void ASafeLocWithoutAFacing_KeepsThePlayersOwn()
    {
        using var rig = new Rig();
        rig.DieAtAndStandAt(corpseX: 1990, ghostX: 10);

        Assert.True(rig.Healer.ResurrectAtSpiritHealer(rig.Player));

        Assert.Equal(new TeleportDestination(0, 2000, 0, 0, 1.25f), rig.Teleports.DestinationOf(rig.Player));
    }

    [Fact]
    public void WhenBothAreNearTheSameGraveyard_NobodyIsTeleported_AndTheVisibilityIsRefreshed()
    {
        using var rig = new Rig();
        rig.DieAtAndStandAt(corpseX: 990, ghostX: 1010);

        Assert.True(rig.Healer.ResurrectAtSpiritHealer(rig.Player));

        Assert.False(rig.Teleports.IsBeingTeleported(rig.Player));
        Assert.True(rig.Player.IsAlive);
    }

    [Fact]
    public void WithoutACorpse_ThePlayerStaysWhereItIs()
    {
        using var rig = new Rig();
        rig.DieAtAndStandAt(corpseX: 990, ghostX: 10);
        rig.Player.Combat.Corpse = null; // the body is gone already (ghost without a body)

        Assert.True(rig.Healer.ResurrectAtSpiritHealer(rig.Player));

        Assert.False(rig.Teleports.IsBeingTeleported(rig.Player));
    }

    [Fact]
    public void WithoutAGraveyardFeature_TheSpiritHealerStillResurrects_AsBefore()
    {
        using var rig = new Rig();
        rig.DieAtAndStandAt(corpseX: 990, ghostX: 10);
        WorldGraveyards.Of(rig.World).Replace(GraveyardCatalog.Empty);

        Assert.True(rig.Healer.ResurrectAtSpiritHealer(rig.Player));

        Assert.False(rig.Teleports.IsBeingTeleported(rig.Player));
        Assert.True(rig.Player.IsAlive);
    }

    [Theory]
    [InlineData(11, 15, 5)]   // level 15, start 11: (15 - 11 + 1) minutes, the shipped default
    [InlineData(14, 15, 2)]   // the start level can be raised
    [InlineData(61, 60, 0)]   // above the maximum player level: no sickness at any level
    [InlineData(15, 14, 0)]   // below the start level: none
    [InlineData(-10, 1, 10)]  // "full time (10 min) sickness at 1 level"
    public void TheSicknessStartsAtTheConfiguredLevel(int sicknessLevel, int level, int minutes)
    {
        using var rig = new Rig(sicknessLevel, (byte)level);
        rig.DieAtAndStandAt(corpseX: 990, ghostX: 990);

        Assert.True(rig.Healer.ResurrectAtSpiritHealer(rig.Player));

        SpellAuraHolder[] sickness = [.. rig.Spells.GetAuras(rig.Player).Where(h => h.Spell.Id == SpiritHealerResurrection.ResurrectionSicknessSpell)];
        if (minutes == 0)
        {
            Assert.Empty(sickness);
        }
        else
        {
            Assert.Equal(minutes * 60 * 1000, Assert.Single(sickness).MaxDuration);
        }
    }
}
