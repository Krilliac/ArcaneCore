using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>Breath, fatigue and lava (vmangos Player::UpdateMirrorTimers and OnMirrorTimerExpirationPulse, Player.cpp:942-1062).</summary>
public sealed class HazardPulseTests
{
    private const uint WaterBreathing = 980001;   // aura 82
    private const uint UnendingBreath = 980002;   // aura 155, +300%
    private const float Submerged = 90f;

    private static SpellInfo Perm(uint id, params SpellEffectInfo[] effects) => Spell(id, effects) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
    };

    private static HazardKit Kit(LiquidTypeFlags? liquid) => new(
        liquid,
        Perm(WaterBreathing, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.WaterBreathing)),
        Perm(UnendingBreath, Effect(SpellEffectName.ApplyAura, 300, aura: AuraType.ModWaterBreathing)));

    private static (MirrorTimerType Type, uint Remaining, uint Duration, int Scale, byte Paused, uint Spell)[] Starts(FakeSession session)
        => [.. session.Sent.Where(p => p.Opcode == WorldOpcode.SmsgStartMirrorTimer).Select(p =>
        {
            var r = new PacketReader(p.Payload);
            return ((MirrorTimerType)r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadInt32(), r.ReadByte(), r.ReadUInt32());
        })];

    private static MirrorTimerType[] Stops(FakeSession session)
        => [.. session.Sent.Where(p => p.Opcode == WorldOpcode.SmsgStopMirrorTimer).Select(p => (MirrorTimerType)BitConverter.ToUInt32(p.Payload))];

    private static byte[][] Logs(FakeSession session) => [.. session.Sent.Where(p => p.Opcode == WorldOpcode.SmsgEnvironmentaldamagelog).Select(p => p.Payload)];

    // --- breath ----------------------------------------------------------------------------

    [Fact]
    public void GoingUnderwater_StartsTheBreathBar_AndItPulsesDrowningDamage()
    {
        using HazardKit kit = Kit(LiquidTypeFlags.Water);
        Player player = kit.AddPlayer(1, 110f, out FakeSession session);
        kit.MoveTo(player, Submerged);
        kit.Tick(50);

        // SMSG_START_MIRROR_TIMER: type 1 (breath), 60000 remaining of 60000, scale -1, not paused, no spell.
        Assert.Equal([(MirrorTimerType.Breath, 60000u, 60000u, -1, (byte)0, 0u)], Starts(session));
        Assert.Equal(new byte[] { 1, 0, 0, 0, 0x60, 0xea, 0, 0, 0x60, 0xea, 0, 0, 0xff, 0xff, 0xff, 0xff, 0, 0, 0, 0, 0 }, session.Sent.First(p => p.Opcode == WorldOpcode.SmsgStartMirrorTimer).Payload);

        kit.Random.Ints.Enqueue(7);               // urand(0, level - 1)
        kit.Tick(60000);                          // the bar runs out

        byte[] log = Assert.Single(Logs(session));
        Assert.Equal((1, 3000u / 5 + 7), (log[8], BitConverter.ToUInt32(log, 9))); // DAMAGE_DROWNING, maxHealth / 5 + 7
        Assert.Equal(3000u - 607, player.Health);

        kit.Random.Ints.Enqueue(0);
        kit.Tick(1999);
        Assert.Single(Logs(session));             // nothing before two seconds are up
        kit.Tick(1);
        Assert.Equal(2, Logs(session).Length);    // then again every two seconds
    }

    [Fact]
    public void Surfacing_RestartsTheBarRegenerating_AndStopsItWhenFull()
    {
        using HazardKit kit = Kit(LiquidTypeFlags.Water);
        Player player = kit.AddPlayer(1, 110f, out FakeSession session);
        kit.MoveTo(player, Submerged);
        kit.Tick(50);                             // the bar starts
        kit.Tick(30000);                          // half of it is used up
        session.Clear();

        kit.MoveTo(player, 110f);
        kit.Tick(50);

        (MirrorTimerType type, _, uint duration, int scale, _, _) = Assert.Single(Starts(session));
        Assert.Equal((MirrorTimerType.Breath, 60000u, 10), (type, duration, scale)); // a full update with the regenerating scale
        session.Clear();

        kit.Tick(3000);                           // 30000 ms of the breath comes back in 3 seconds
        Assert.Equal([MirrorTimerType.Breath], Stops(session));
        Assert.Empty(Logs(session));
    }

    [Fact]
    public void WaterBreathing_MeansTheBreathNeverStarts_AndRemovingItBringsTheBarBack()
    {
        using HazardKit kit = Kit(LiquidTypeFlags.Water);
        Player player = kit.AddPlayer(1, 110f, out FakeSession session);
        kit.Kit.System.CastSpell(player, WaterBreathing, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(0.0f, player.Locomotion.BreathingMultiplier);

        kit.MoveTo(player, Submerged);
        kit.Tick(70000);

        Assert.Empty(Starts(session));
        Assert.Empty(Logs(session));

        kit.Kit.System.RemoveAuras(player, WaterBreathing);
        Assert.Equal(1.0f, player.Locomotion.BreathingMultiplier);
        kit.Tick(50);

        Assert.Equal(MirrorTimerType.Breath, Assert.Single(Starts(session)).Type);
    }

    [Fact]
    public void UnendingBreath_MultipliesTheBreathTime()
    {
        using HazardKit kit = Kit(LiquidTypeFlags.Water);
        Player player = kit.AddPlayer(1, 110f, out FakeSession session);
        kit.Kit.System.CastSpell(player, UnendingBreath, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(4.0f, player.Locomotion.BreathingMultiplier);

        kit.MoveTo(player, Submerged);
        kit.Tick(50);

        Assert.Equal(240000u, Assert.Single(Starts(session)).Duration); // 60 s x (100 + 300) / 100
    }

    [Fact]
    public void WaterBreathing_UnderwaterAlready_StopsTheBreathBar()
    {
        using HazardKit kit = Kit(LiquidTypeFlags.Water);
        Player player = kit.AddPlayer(1, 110f, out FakeSession session);
        kit.MoveTo(player, Submerged);
        kit.Tick(50);
        session.Clear();

        kit.Kit.System.CastSpell(player, WaterBreathing, SpellCastTargets.ForSelf(), triggered: true);
        kit.Tick(50);

        // The multiplier 0 makes the bar regenerate from what little was used and stop; it never comes back while the aura lasts.
        Assert.Equal([MirrorTimerType.Breath], Stops(session));
        kit.Tick(70000);
        Assert.Empty(Logs(session));
        Assert.Empty(Starts(session));
    }

    // --- fatigue ---------------------------------------------------------------------------

    [Fact]
    public void DeepWater_StartsTheFatigueBar_AndExhaustsAfterAMinute()
    {
        using HazardKit kit = Kit(LiquidTypeFlags.Water | LiquidTypeFlags.DeepWater);
        Player player = kit.AddPlayer(1, 110f, out FakeSession session); // on the surface of the deep sea
        kit.Tick(50);

        Assert.True(player.Locomotion.MirrorTimers[(int)MirrorTimerType.Fatigue].IsActive); // started when the sea was entered
        Assert.Equal(60000u, player.Locomotion.MirrorTimers[(int)MirrorTimerType.Fatigue].Duration);

        kit.Random.Ints.Enqueue(5);
        kit.Tick(59950);                          // 50 + 59950 = a minute

        byte[] log = Assert.Single(Logs(session));
        Assert.Equal((0, 3000u / 5 + 5), (log[8], BitConverter.ToUInt32(log, 9))); // DAMAGE_EXHAUSTED
        Assert.Equal(3000u - 605, player.Health);
    }

    [Fact]
    public void AGhostThatKeepsSwimming_IsSentToTheGraveyardByTheFatiguePulse()
    {
        using HazardKit kit = Kit(LiquidTypeFlags.Water | LiquidTypeFlags.DeepWater);
        Player player = kit.AddPlayer(1, 110f, out FakeSession session);
        kit.Tick(50);
        kit.Map.Combat.Kill(null, player);
        kit.Map.Combat.RepopPlayer(player);
        int before = kit.Hooks.GraveyardRepops;
        Assert.True((player.Flags & PlayerFlags.Ghost) != 0);

        kit.Tick(60100);

        Assert.True(kit.Hooks.GraveyardRepops > before);
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgEnvironmentaldamagelog); // a ghost takes no damage
    }

    [Fact]
    public void ADeadPlayer_LosesItsTimers()
    {
        using HazardKit kit = Kit(LiquidTypeFlags.Water);
        Player player = kit.AddPlayer(1, 110f, out FakeSession session);
        kit.MoveTo(player, Submerged);
        kit.Tick(50);
        session.Clear();

        player.Map!.Combat.Kill(null, player);
        kit.Tick(50);

        Assert.Equal([MirrorTimerType.Breath], Stops(session));
    }

    // --- lava and slime --------------------------------------------------------------------

    [Fact]
    public void Lava_BurnsOnceASecond_ThenEveryTwoSeconds_AndTheEnvironmentalBarIsNeverSent()
    {
        using HazardKit kit = Kit(LiquidTypeFlags.Magma);
        Player player = kit.AddPlayer(1, 99.95f, out FakeSession session); // standing on the lava
        Assert.True(player.Locomotion.Environment.HasFlag(EnvironmentFlags.InMagma));

        kit.Random.Ints.Enqueue(607);             // urand(605, 610)
        kit.Tick(1050);                           // the timer starts on the first update and expires a second later

        byte[] log = Assert.Single(Logs(session));
        Assert.Equal((3, 607u), (log[8], BitConverter.ToUInt32(log, 9))); // DAMAGE_LAVA
        Assert.Empty(Starts(session));            // MirrorTimer::ENVIRONMENTAL is server only
        Assert.Equal(3000u - 607, player.Health);

        kit.Random.Ints.Enqueue(610);
        kit.Tick(2000);
        Assert.Equal(2, Logs(session).Length);
        Assert.Equal(610u, BitConverter.ToUInt32(Logs(session)[1], 9));
    }

    [Fact]
    public void LavaDamage_FollowsTheConfiguredRange()
    {
        using HazardKit kit = Kit(LiquidTypeFlags.Magma);
        LocomotionEnvironment.Register(kit.Kit.World, new LocomotionEnvironment(new LocomotionOptions { EnvironmentalDamageMin = 100, EnvironmentalDamageMax = 120, MirrorTimerEnvironmentalMaxSec = 3 }));
        Player player = kit.AddPlayer(1, 99.95f, out FakeSession session);

        kit.Random.Ints.Enqueue(1000);            // clamped into the range
        kit.Tick(3050);

        Assert.Equal(120u, BitConverter.ToUInt32(Assert.Single(Logs(session)), 9));
        Assert.NotNull(player);
    }

    [Fact]
    public void Slime_DoesNothing_UnlessTheDeviationIsSwitchedOn()
    {
        using (HazardKit kit = Kit(LiquidTypeFlags.Slime))
        {
            Player player = kit.AddPlayer(1, 99.95f, out FakeSession session);
            Assert.True(player.Locomotion.Environment.HasFlag(EnvironmentFlags.InSlime));
            kit.Tick(5000);
            Assert.Empty(Logs(session)); // vmangos and mangos-classic: DAMAGE_SLIME is defined but never applied
            Assert.Equal(3000u, player.Health);
        }

        using HazardKit deviating = Kit(LiquidTypeFlags.Slime);
        LocomotionEnvironment.Register(deviating.Kit.World, new LocomotionEnvironment(new LocomotionOptions { SlimeDamage = true }));
        Player slimed = deviating.AddPlayer(1, 99.95f, out FakeSession slimedSession);
        deviating.Random.Ints.Enqueue(606);
        deviating.Tick(1050);

        byte[] log = Assert.Single(Logs(slimedSession));
        Assert.Equal((4, 606u), (log[8], BitConverter.ToUInt32(log, 9))); // DAMAGE_SLIME
        Assert.NotNull(slimed);
    }

    [Fact]
    public void LeavingTheLava_StopsTheBurning()
    {
        using HazardKit kit = Kit(LiquidTypeFlags.Magma);
        Player player = kit.AddPlayer(1, 99.95f, out FakeSession session);
        kit.Tick(50);

        kit.MoveTo(player, 130f);
        kit.Tick(10000);

        Assert.Empty(Logs(session));
        Assert.False(player.Locomotion.Environment.HasFlag(EnvironmentFlags.InMagma));
    }

    // --- game masters ----------------------------------------------------------------------

    [Fact]
    public void AGameMastersTimers_StandStill()
    {
        using HazardKit kit = Kit(LiquidTypeFlags.Water);
        Player gm = kit.AddPlayer(1, 110f, out FakeSession session, AccountSecurity.GameMaster);
        gm.SetGameMaster(true);
        kit.MoveTo(gm, Submerged);
        kit.Tick(50);

        (MirrorTimerType type, _, _, _, byte paused, _) = Assert.Single(Starts(session));
        Assert.Equal((MirrorTimerType.Breath, (byte)1), (type, paused)); // the bar is shown paused

        kit.Tick(70000);
        Assert.Empty(Logs(session));
        Assert.Equal(3000u, gm.Health);

        gm.SetGameMaster(false);
        kit.Random.Ints.Enqueue(0);
        kit.Tick(60000);
        Assert.Single(Logs(session));             // thawed: the minute runs from where it stood
    }
}
