using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Interrupts;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Druid;

/// <summary>
/// ENVIRONMENT_FLAG_HIGH_LIQUID edge handling (D:\refs\vmangos\src\game\Objects\Player.cpp:848-855 and 20353-20419):
/// auras with AURA_INTERRUPT_UNDER_WATER_CANCELS (0x80) go on entering deep liquid, ABOVE_WATER_CANCELS (0x100) on
/// leaving it. Spell ids in comments are classic-db rows the synthetic spells imitate: 783 Travel Form
/// (AuraInterruptFlags 0x80), 1066 Aquatic Form (0x100).
/// </summary>
public class LiquidAuraInterruptTests
{
    private const uint TravelLike = 9001;
    private const uint AquaLike = 9002;
    private const uint ControlBuff = 9003;
    private const uint ChannelUnderWater = 9004;

    private sealed class FakeProbe : ILiquidProbe
    {
        public bool High { get; set; }

        public int Calls { get; private set; }

        public bool IsHighLiquid(Map map, Player player)
        {
            Calls++;
            return High;
        }
    }

    private static SpellInfo Buff(uint id, uint interruptFlags) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            AuraInterruptFlags = (SpellAuraInterruptFlags)interruptFlags,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
            SpellVisual = 1,
        };

    private sealed class Rig : IDisposable
    {
        public Rig(bool startsHigh = false)
        {
            Kit = new SpellTestKit(
                Buff(TravelLike, AuraInterruptMasks.UnderWaterCancels),
                Buff(AquaLike, AuraInterruptMasks.AboveWaterCancels),
                Buff(ControlBuff, 0),
                SpellTestKit.Spell(ChannelUnderWater, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
                {
                    AttributesEx = SpellAttributesEx.IsChanneled,
                    Duration = new SpellDuration(5000, 0, 5000),
                    ChannelInterruptFlags = (SpellAuraInterruptFlags)AuraInterruptMasks.UnderWaterCancels,
                    SpellVisual = 1,
                });
            Probe = new FakeProbe { High = startsHigh };
            (Player, _) = Kit.AddPlayer(1, 10, 10);
            Updater = new LiquidAuraInterruptUpdater(() => Kit.System, Probe);
            Kit.World.GetMap(0).AddUpdater(Updater);
        }

        public SpellTestKit Kit { get; }

        public FakeProbe Probe { get; }

        public LiquidAuraInterruptUpdater Updater { get; }

        public Player Player { get; }

        public void Apply(uint spell) =>
            Assert.Equal(SpellCastResult.CastOk, Kit.System.CastSpell(Player, spell, SpellCastTargets.ForSelf(), triggered: true));

        /// <summary>Move the player (so the updater probes it again) and run one world tick.</summary>
        public void MoveAndTick(float dx = 1)
        {
            Player.SetPosition(Player.X + dx, Player.Y, Player.Z, 0);
            Kit.World.RunTick(100);
        }

        public bool Has(uint spell) => Kit.System.HasAura(Player, spell);

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void EnteringHighLiquid_RemovesTheUnderWaterCancelsAura_AndKeepsOthers()
    {
        using var rig = new Rig();
        rig.MoveAndTick(); // first sighting records "dry"
        rig.Apply(TravelLike);
        rig.Apply(AquaLike);
        rig.Apply(ControlBuff);

        rig.Probe.High = true;
        rig.MoveAndTick();

        Assert.False(rig.Has(TravelLike));
        Assert.True(rig.Has(AquaLike));
        Assert.True(rig.Has(ControlBuff));
    }

    [Fact]
    public void LeavingHighLiquid_RemovesTheAboveWaterCancelsAura()
    {
        using var rig = new Rig(startsHigh: true);
        rig.MoveAndTick(); // first sighting records "in water"
        rig.Apply(AquaLike);
        rig.Apply(TravelLike);

        rig.Probe.High = false;
        rig.MoveAndTick();

        Assert.False(rig.Has(AquaLike));
        Assert.True(rig.Has(TravelLike));
    }

    [Fact]
    public void WithoutAChange_NothingIsRemoved_EvenInWater()
    {
        using var rig = new Rig(startsHigh: true);
        rig.MoveAndTick();
        rig.Apply(TravelLike); // applied while already in water: vmangos only reacts to the edge

        rig.MoveAndTick();
        rig.MoveAndTick();

        Assert.True(rig.Has(TravelLike));
    }

    [Fact]
    public void RemovalIsEdgeTriggered_ARecastInWaterSurvivesTheNextTick()
    {
        using var rig = new Rig();
        rig.MoveAndTick();
        rig.Probe.High = true;
        rig.Apply(TravelLike);
        rig.MoveAndTick();
        Assert.False(rig.Has(TravelLike));

        rig.Apply(TravelLike);
        rig.MoveAndTick();

        Assert.True(rig.Has(TravelLike));
    }

    [Fact]
    public void AStationaryPlayerIsNotProbedAgain()
    {
        using var rig = new Rig();
        rig.MoveAndTick();
        int callsAfterFirst = rig.Probe.Calls;

        rig.Kit.World.RunTick(100);
        rig.Kit.World.RunTick(100);

        Assert.Equal(1, callsAfterFirst);
        Assert.Equal(callsAfterFirst, rig.Probe.Calls);
    }

    [Fact]
    public void EnteringHighLiquid_InterruptsAChannelWithTheUnderWaterFlag()
    {
        using var rig = new Rig();
        rig.MoveAndTick();
        rig.Kit.Spellbook.Teach(rig.Player, ChannelUnderWater);
        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.HandleCastRequest(rig.Player, ChannelUnderWater, SpellCastTargets.ForSelf()));
        Assert.Equal(ChannelUnderWater, rig.Player.GetUInt32(UpdateFields.UnitChannelSpell));

        rig.Probe.High = true;
        rig.MoveAndTick();

        Assert.Equal(0u, rig.Player.GetUInt32(UpdateFields.UnitChannelSpell));
    }

    [Theory]
    [InlineData(LiquidStatus.InWater, 11.51f, 10f, true)]
    [InlineData(LiquidStatus.UnderWater, 20f, 10f, true)]
    [InlineData(LiquidStatus.InWater, 11.5f, 10f, false)] // level must exceed z + 0.75 * 2.0
    [InlineData(LiquidStatus.InWater, 10.5f, 10f, false)] // shallow
    [InlineData(LiquidStatus.AboveWater, 20f, 10f, false)]
    [InlineData(LiquidStatus.WaterWalk, 20f, 10f, false)]
    [InlineData(LiquidStatus.NoWater, 20f, 10f, false)]
    public void TerrainProbeRule_MatchesHighLiquid(LiquidStatus status, float level, float z, bool expected)
    {
        Assert.Equal(expected, TerrainLiquidProbe.IsHighLiquid(status, level, z));
    }

    [Fact]
    public void RemoveAurasWithInterruptFlags_RemovesMatchingSpellsExceptTheNamedOne()
    {
        using var rig = new Rig();
        rig.Apply(TravelLike);
        rig.Apply(AquaLike);
        rig.Apply(ControlBuff);

        rig.Kit.System.RemoveAurasWithInterruptFlags(rig.Player, AuraInterruptMasks.UnderWaterCancels | AuraInterruptMasks.AboveWaterCancels, exceptSpellId: AquaLike);

        Assert.False(rig.Has(TravelLike));
        Assert.True(rig.Has(AquaLike));
        Assert.True(rig.Has(ControlBuff));
    }

    [Fact]
    public void AuraInterruptMasks_MatchVmangos()
    {
        Assert.Equal(0x80u, AuraInterruptMasks.UnderWaterCancels);
        Assert.Equal(0x100u, AuraInterruptMasks.AboveWaterCancels);
        Assert.Equal(0x8000u, AuraInterruptMasks.ShapeshiftingCancels);
        Assert.Equal(0x4u, AuraInterruptMasks.ActionCancels);
        Assert.Equal(0x10000u, AuraInterruptMasks.ActionCancelsLate);
    }
}
