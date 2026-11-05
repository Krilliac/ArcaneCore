using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Diagnostics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.GridTerrain;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;
using CombatRandom = ArcaneCore.Game.Tests.CombatCore.ScriptedRandom;

namespace ArcaneCore.Game.Tests.Diagnostics;

/// <summary>
/// The game-side producers of the GM stream (<see cref="GmTraceTap"/>, the cast-failure and proc-roll hooks) driven through the real send
/// paths: packets made by the real builders go out through <see cref="SpellSystem.SendToSet"/>, <see cref="CombatPackets.SendToSet"/> or
/// <see cref="Map.BroadcastToObservers(WorldObject, WorldOpcode, ReadOnlySpan{byte})"/>, casts through <see cref="SpellSystem.HandleCastRequest"/>,
/// procs through a melee swing. The sink is a recording fake (the stream itself lives in the World layer), so these tests pin what each
/// producer decodes and the subject it reports; the scope test of the subject is the World layer's (GmStreamScopeTests).
/// </summary>
public sealed class GmTraceTapTests
{
    /// <summary>A sink following the given channels that formats and records every line it is offered.</summary>
    internal sealed class RecordingTraceSink(params string[] followed) : IGmTraceSink
    {
        private readonly HashSet<string> _followed = new(followed, StringComparer.OrdinalIgnoreCase);

        public List<(string Channel, GmTraceSubject Subject, string Line)> Lines { get; } = [];

        public int HasSubscribersCalls { get; private set; }

        public int PublishCalls { get; private set; }

        /// <summary>When true, Publish counts but never calls the formatter (allocation measurements).</summary>
        public bool SkipFormat { get; set; }

        public bool IsActive => _followed.Count > 0;

        public bool HasSubscribers(string channel)
        {
            HasSubscribersCalls++;
            return _followed.Contains(channel);
        }

        public int Publish<TState>(string channel, in GmTraceSubject subject, TState state, Func<TState, string> format)
        {
            PublishCalls++;
            if (SkipFormat)
            {
                return 0;
            }

            Lines.Add((channel, subject, format(state)));
            return 1;
        }

        public string Single(string channel) => Assert.Single(Lines, l => l.Channel == channel).Line;
    }

    /// <summary>A sink whose Publish throws: tracing must never break the broadcast it watches.</summary>
    private sealed class ThrowingTraceSink : IGmTraceSink
    {
        public bool IsActive => true;

        public bool HasSubscribers(string channel) => true;

        public int Publish<TState>(string channel, in GmTraceSubject subject, TState state, Func<TState, string> format)
            => throw new InvalidOperationException("sink failure");
    }

    private static (SpellTestKit Kit, Player A, FakeSession SessionA, Player B, FakeSession SessionB) TwoPlayers(params SpellInfo[] extra)
    {
        var kit = new SpellTestKit(extra);
        (Player a, FakeSession sa) = kit.AddPlayer(1);
        (Player b, FakeSession sb) = kit.AddPlayer(2, 5, 0);
        kit.World.RunTick(0);
        sa.Clear();
        sb.Clear();
        return (kit, a, sa, b, sb);
    }

    // --- damage ----------------------------------------------------------------------------------------------

    [Fact]
    public void SpellDamageLog_SentToTheSet_TracesSourceTargetSpellAmountSchoolAbsorbResistAndCrit()
    {
        (SpellTestKit kit, Player a, _, Player b, FakeSession sb) = TwoPlayers();
        using SpellTestKit owned = kit;
        var sink = new RecordingTraceSink(GmStreamChannels.Damage);
        kit.World.GmTrace = sink;

        SpellSystem.SendToSet(a, WorldOpcode.SmsgSpellnonmeleedamagelog,
            SpellPackets.BuildSpellNonMeleeDamageLog(b.Guid, a.Guid, 133, 412, SpellSchool.Fire, absorbed: 30, resisted: 25, blocked: 0, hitInfo: SpellSystem.SpellHitTypeCrit),
            includeSelf: true);

        (string channel, GmTraceSubject subject, string line) = Assert.Single(sink.Lines);
        Assert.Equal(GmStreamChannels.Damage, channel);
        Assert.Equal("P1(Player:1) -> P2(Player:2) spell 133 412 Fire (absorb 30, resist 25, block 0) CRIT", line);
        Assert.Equal((a.Guid, b.Guid, true), (subject.Actor, subject.Other, subject.HasPosition));
        Assert.Equal((a.X, a.Y, 0u), (subject.X, subject.Y, subject.MapId));
        Assert.Contains(sb.Sent, p => p.Opcode == WorldOpcode.SmsgSpellnonmeleedamagelog); // the broadcast itself is unchanged
    }

    [Fact]
    public void SpellDamageLog_WithoutTheCritBit_HasNoCritMarker_AndAPeriodicLeechSaysPeriodic()
    {
        (SpellTestKit kit, Player a, _, Player b, _) = TwoPlayers();
        using SpellTestKit owned = kit;
        var sink = new RecordingTraceSink(GmStreamChannels.Damage);
        kit.World.GmTrace = sink;

        SpellSystem.SendToSet(a, WorldOpcode.SmsgSpellnonmeleedamagelog,
            SpellPackets.BuildSpellNonMeleeDamageLog(b.Guid, a.Guid, 5138, 40, SpellSchool.Shadow, periodic: true), includeSelf: true);

        Assert.Equal("P1(Player:1) -> P2(Player:2) periodic spell 5138 40 Shadow (absorb 0, resist 0, block 0)", sink.Single(GmStreamChannels.Damage));
    }

    [Fact]
    public void HealLog_WithCrit_AndPeriodicDamageAndHealTicks_AreTraced()
    {
        (SpellTestKit kit, Player a, _, Player b, _) = TwoPlayers();
        using SpellTestKit owned = kit;
        var sink = new RecordingTraceSink(GmStreamChannels.Damage);
        kit.World.GmTrace = sink;

        SpellSystem.SendToSet(a, WorldOpcode.SmsgSpellheallog, SpellPackets.BuildSpellHealLog(b.Guid, a.Guid, 2054, 380, critical: true), includeSelf: true);
        SpellSystem.SendToSet(b, WorldOpcode.SmsgPeriodicauralog,
            SpellPackets.BuildPeriodicAuraLog(b.Guid, a.Guid, 589, new PeriodicLogEntry(AuraType.PeriodicDamage, 61, (uint)SpellSchool.Shadow, Absorbed: 9, Resisted: 4)), includeSelf: true);
        SpellSystem.SendToSet(b, WorldOpcode.SmsgPeriodicauralog,
            SpellPackets.BuildPeriodicAuraLog(b.Guid, a.Guid, 774, new PeriodicLogEntry(AuraType.PeriodicHeal, 33, 0)), includeSelf: true);
        SpellSystem.SendToSet(b, WorldOpcode.SmsgPeriodicauralog, // energize ticks carry no damage line
            SpellPackets.BuildPeriodicAuraLog(b.Guid, a.Guid, 5677, new PeriodicLogEntry(AuraType.PeriodicEnergize, 10, (uint)PowerType.Mana)), includeSelf: true);

        Assert.Equal(
            [
                "P1(Player:1) -> P2(Player:2) heal spell 2054 380 CRIT",
                "P1(Player:1) -> P2(Player:2) periodic spell 589 61 Shadow (absorb 9, resist 4, block 0)",
                "P1(Player:1) -> P2(Player:2) periodic heal spell 774 33",
            ],
            sink.Lines.Select(l => l.Line));
        Assert.All(sink.Lines, l => Assert.Equal((a.Guid, b.Guid), (l.Subject.Actor, l.Subject.Other)));
        Assert.Equal((b.X, b.Y), (sink.Lines[1].Subject.X, sink.Lines[1].Subject.Y)); // a tick is placed at the unit that sent it
    }

    [Fact]
    public void MeleeSwing_SentThroughCombatPackets_TracesAmountSchoolAbsorbResistBlockAndOutcome()
    {
        (SpellTestKit kit, Player a, _, Player b, _) = TwoPlayers();
        using SpellTestKit owned = kit;
        var sink = new RecordingTraceSink(GmStreamChannels.Damage);
        kit.World.GmTrace = sink;

        CombatPackets.SendToSet(a, WorldOpcode.SmsgAttackerstateupdate, CombatPackets.AttackerStateUpdate(
            HitInfo.AffectsVictim | HitInfo.Block, a.Guid, b.Guid, 37, [new SubDamage(0, 37, 6, 3)], VictimState.Blocks, blocked: 12));
        CombatPackets.SendToSet(a, WorldOpcode.SmsgAttackerstateupdate, CombatPackets.AttackerStateUpdate(
            HitInfo.AffectsVictim | HitInfo.CriticalHit, a.Guid, b.Guid, 90, [new SubDamage(0, 90, 0, 0)], VictimState.Normal, blocked: 0, meleeSpellId: 78));

        Assert.Equal(
            [
                "P1(Player:1) -> P2(Player:2) melee 37 Physical (absorb 6, resist 3, block 12) Blocks",
                "P1(Player:1) -> P2(Player:2) melee via spell 78 90 Physical (absorb 0, resist 0, block 0) CRIT",
            ],
            sink.Lines.Select(l => l.Line));
        Assert.Equal((a.Guid, b.Guid), (sink.Lines[0].Subject.Actor, sink.Lines[0].Subject.Other));
    }

    [Fact]
    public void ACreatureSource_IsNamedByKindAndCounter_WhenItIsNotACreatureTemplate()
    {
        (SpellTestKit kit, Player a, _, _, _) = TwoPlayers();
        using SpellTestKit owned = kit;
        var unit = new TestUnit(77, 3, 3);
        a.Map!.AddObject(unit);
        var sink = new RecordingTraceSink(GmStreamChannels.Damage);
        kit.World.GmTrace = sink;

        SpellSystem.SendToSet(unit, WorldOpcode.SmsgSpellnonmeleedamagelog,
            SpellPackets.BuildSpellNonMeleeDamageLog(a.Guid, unit.Guid, 9, 5, SpellSchool.Nature), includeSelf: false);

        Assert.Equal("Creature:77 -> P1(Player:1) spell 9 5 Nature (absorb 0, resist 0, block 0)", sink.Single(GmStreamChannels.Damage));
    }

    // --- spells ----------------------------------------------------------------------------------------------

    [Fact]
    public void SpellStartGoMissAndFailedOther_AreTraced_WithHitAndMissTargetsAndReasons()
    {
        (SpellTestKit kit, Player a, _, Player b, _) = TwoPlayers();
        using SpellTestKit owned = kit;
        var unit = new TestUnit(5, 2, 2);
        a.Map!.AddObject(unit);
        var sink = new RecordingTraceSink(GmStreamChannels.Spells);
        kit.World.GmTrace = sink;

        SpellSystem.SendToSet(a, WorldOpcode.SmsgSpellStart,
            SpellPackets.BuildSpellStart(a.Guid, a.Guid, 133, SpellCastFlags.None, 3500, SpellCastTargets.ForUnit(b.Guid)), includeSelf: true);
        SpellSystem.SendToSet(a, WorldOpcode.SmsgSpellGo,
            SpellPackets.BuildSpellGo(a.Guid, a.Guid, 133, SpellCastFlags.None, [b.Guid], [(unit.Guid, SpellMissInfo.Resist), (a.Guid, SpellMissInfo.Reflect)], SpellCastTargets.ForUnit(b.Guid)),
            includeSelf: true);
        SpellSystem.SendToSet(a, WorldOpcode.SmsgSpelllogmiss, SpellPackets.BuildSpellLogMiss(133, a.Guid, b.Guid, SpellMissInfo.Immune), includeSelf: true);
        SpellSystem.SendToSet(a, WorldOpcode.SmsgSpellFailedOther, SpellPackets.BuildSpellFailedOther(a.Guid, 133), includeSelf: true);

        Assert.Equal(
            [
                "cast start spell 133 by P1(Player:1) on P2(Player:2), cast 3500 ms",
                "go spell 133 by P1(Player:1) hit: P2(Player:2); miss: Creature:5 (Resist), P1(Player:1) (Reflect)",
                "miss spell 133 P1(Player:1) -> P2(Player:2): Immune",
                "interrupted spell 133 by P1(Player:1)",
            ],
            sink.Lines.Select(l => l.Line));
        Assert.Equal((a.Guid, b.Guid), (sink.Lines[0].Subject.Actor, sink.Lines[0].Subject.Other));
        Assert.Equal((a.Guid, b.Guid), (sink.Lines[1].Subject.Actor, sink.Lines[1].Subject.Other)); // the first hit is the other party
        Assert.Equal((a.Guid, ObjectGuid.Empty), (sink.Lines[3].Subject.Actor, sink.Lines[3].Subject.Other));
    }

    [Fact]
    public void ASpellGoWithOnlyMisses_NamesTheFirstMissAsTheOtherParty()
    {
        (SpellTestKit kit, Player a, _, Player b, _) = TwoPlayers();
        using SpellTestKit owned = kit;
        var sink = new RecordingTraceSink(GmStreamChannels.Spells);
        kit.World.GmTrace = sink;

        SpellSystem.SendToSet(a, WorldOpcode.SmsgSpellGo,
            SpellPackets.BuildSpellGo(a.Guid, a.Guid, 116, SpellCastFlags.None, [], [(b.Guid, SpellMissInfo.Miss)], SpellCastTargets.ForUnit(b.Guid)), includeSelf: true);

        (_, GmTraceSubject subject, string line) = Assert.Single(sink.Lines);
        Assert.Equal("go spell 116 by P1(Player:1) miss: P2(Player:2) (Miss)", line);
        Assert.Equal(b.Guid, subject.Other);
    }

    [Fact]
    public void ACastThatFailsForPower_TracesTheFailureWithItsReason_ThroughTheRealCastPath()
    {
        (SpellTestKit kit, Player a, FakeSession sa, Player b, _) = TwoPlayers();
        using SpellTestKit owned = kit;
        kit.Spellbook.Teach(a, CastBolt);
        SpellSystem.SetPower(a, PowerType.Rage, 10);
        var sink = new RecordingTraceSink(GmStreamChannels.Spells);
        kit.World.GmTrace = sink;

        Assert.Equal(SpellCastResult.NoPower, kit.System.HandleCastRequest(a, CastBolt, SpellCastTargets.ForUnit(b.Guid)));

        Assert.Equal($"fail spell {CastBolt} \"Spell {CastBolt}\" by P1(Player:1): NoPower", sink.Single(GmStreamChannels.Spells));
        Assert.Equal((byte)SpellCastResult.NoPower, Packets(sa, WorldOpcode.SmsgCastResult).Single()[5]); // the client still gets its result
    }

    [Fact]
    public void ATriggeredCastFailure_IsTracedAndMarked_ThoughTheClientIsNeverTold()
    {
        (SpellTestKit kit, Player a, FakeSession sa, _, _) = TwoPlayers();
        using SpellTestKit owned = kit;
        var sink = new RecordingTraceSink(GmStreamChannels.Spells);
        kit.World.GmTrace = sink;

        SpellSystem.SendCastResult(a, kit.Store.Get(CastBolt)!, SpellCastResult.OutOfRange, triggered: true);
        SpellSystem.SendCastResult(a, kit.Store.Get(CastBolt)!, SpellCastResult.CastOk, triggered: false); // success is not a failure line

        Assert.Equal($"fail spell {CastBolt} \"Spell {CastBolt}\" by P1(Player:1): OutOfRange (triggered)", sink.Single(GmStreamChannels.Spells));
        Assert.Single(Packets(sa, WorldOpcode.SmsgCastResult)); // only the CastOk one
    }

    [Fact]
    public void AMovementInterrupt_SendsSpellFailedOther_WhichTheSpellsChannelTraces()
    {
        (SpellTestKit kit, Player a, _, Player b, _) = TwoPlayers();
        using SpellTestKit owned = kit;
        kit.Spellbook.Teach(a, CastBolt);
        SpellSystem.SetPower(a, PowerType.Rage, 100);
        var sink = new RecordingTraceSink(GmStreamChannels.Spells);
        kit.World.GmTrace = sink;

        kit.System.HandleCastRequest(a, CastBolt, SpellCastTargets.ForUnit(b.Guid));
        a.Relocate(2, 0, a.Z, 0, kit.Now);
        kit.Advance(100);

        Assert.Contains(sink.Lines, l => l.Line == $"cast start spell {CastBolt} by P1(Player:1) on P2(Player:2), cast 2000 ms");
        Assert.Contains(sink.Lines, l => l.Line == $"interrupted spell {CastBolt} by P1(Player:1)");
        Assert.Contains(sink.Lines, l => l.Line.StartsWith($"fail spell {CastBolt} \"Spell {CastBolt}\" by P1(Player:1): Interrupted", StringComparison.Ordinal));
    }

    // --- moves -----------------------------------------------------------------------------------------------

    [Fact]
    public void AMovementRelayAndAMonsterMove_TracePositionAndSpeed()
    {
        (SpellTestKit kit, Player a, _, _, FakeSession sb) = TwoPlayers();
        using SpellTestKit owned = kit;
        var unit = new TestUnit(9, 4, 6, 1.5f);
        a.Map!.AddObject(unit);
        kit.World.RunTick(0);
        var sink = new RecordingTraceSink(GmStreamChannels.Moves);
        kit.World.GmTrace = sink;
        a.RunSpeed = 9.1f;

        a.Map.BroadcastToObservers(a, WorldOpcode.MsgMoveHeartbeat, [1, 2, 3]);
        a.Map.BroadcastToObservers(unit, WorldOpcode.SmsgMonsterMove, [4, 5, 6]);

        Assert.Equal(
            [
                $"P1(Player:1) MSG_MOVE_HEARTBEAT at ({a.X:0.0}, {a.Y:0.0}, {a.Z:0.0}) o 0.00 run 9.10 walk 2.50 swim 4.72",
                "Creature:9 SMSG_MONSTER_MOVE at (4.0, 6.0, 1.5) o 0.00 run 7.00 walk 2.50 swim 4.72",
            ],
            sink.Lines.Select(l => l.Line));
        Assert.Equal((unit.Guid, ObjectGuid.Empty, 4f, 6f), (sink.Lines[1].Subject.Actor, sink.Lines[1].Subject.Other, sink.Lines[1].Subject.X, sink.Lines[1].Subject.Y));
        Assert.Contains(sb.Sent, p => p.Opcode == WorldOpcode.MsgMoveHeartbeat);
    }

    // --- procs -----------------------------------------------------------------------------------------------

    private const uint HalfChanceAura = 992_001;
    private const uint HalfChanceBuff = 992_002;

    /// <summary>A <see cref="Random"/> seeded like the kit's that logs every draw (method and value), so two runs can be compared draw by draw.</summary>
    private sealed class LoggingRandom(int seed) : Random(seed)
    {
        public List<string> Draws { get; } = [];

        public override double NextDouble()
        {
            double value = base.NextDouble();
            Draws.Add("d:" + value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            return value;
        }

        public override int Next() => Log(base.Next());

        public override int Next(int maxValue) => Log(base.Next(maxValue));

        public override int Next(int minValue, int maxValue) => Log(base.Next(minValue, maxValue));

        public override float NextSingle()
        {
            float value = base.NextSingle();
            Draws.Add("f:" + value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            return value;
        }

        private int Log(int value)
        {
            Draws.Add("i:" + value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return value;
        }
    }

    private static SpellTestKit ProcKit() => new(
        Spell(HalfChanceAura, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ProcTriggerSpell, trigger: HalfChanceBuff)) with
        {
            Duration = new SpellDuration(-1, 0, -1),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
            ProcFlags = ProcFlags.DealMeleeSwing,
            ProcChance = 50,
        },
        RuleTestKit.Grant(HalfChanceBuff, AuraType.ModResistance, 5, misc: 1) with { Duration = new SpellDuration(10000, 0, 10000) });

    /// <summary>Swing <paramref name="swings"/> times with a 50% on-swing proc aura; returns whether each swing procced and the RNG draws.</summary>
    private static (List<bool> Procced, List<string> Draws, RecordingTraceSink? Sink, Player Attacker) Swing(int swings, RecordingTraceSink? sink, int seed = 4242)
    {
        using SpellTestKit kit = ProcKit();
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        MapCombat combat = attacker.Map!.Combat;
        combat.SpellMitigation = kit.System;
        combat.Random = new CombatRandom { DefaultInt = 9999 }; // every swing an ordinary hit
        attacker.SetFloat(UpdateFields.UnitFieldMindamage, 1);
        attacker.SetFloat(UpdateFields.UnitFieldMaxdamage, 1);
        victim.Health = victim.MaxHealth = 100_000;
        RuleTestKit.Apply(kit, attacker, HalfChanceAura);
        kit.Now++;
        var random = new LoggingRandom(seed);
        kit.System.Random = random;
        kit.World.GmTrace = sink;

        var procced = new List<bool>();
        for (int i = 0; i < swings; i++)
        {
            combat.AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack);
            procced.Add(kit.System.HasAura(attacker, HalfChanceBuff));
            kit.System.RemoveAuras(attacker, HalfChanceBuff);
            kit.Now += 10;
        }

        return (procced, random.Draws, sink, attacker);
    }

    [Fact]
    public void AProcRoll_IsTracedWithChanceRollAndResult_MatchingWhatTheSwingDid()
    {
        (List<bool> procced, List<string> draws, RecordingTraceSink? sink, _) = Swing(12, new RecordingTraceSink(GmStreamChannels.Procs));

        List<string> lines = [.. sink!.Lines.Where(l => l.Channel == GmStreamChannels.Procs).Select(l => l.Line)];
        Assert.Equal(12, lines.Count); // one roll per swing
        Assert.Contains(true, procced);
        Assert.Contains(false, procced); // seed 4242 gives both outcomes in 12 rolls
        List<double> rolls = [.. draws.Where(d => d.StartsWith("d:", StringComparison.Ordinal)).Select(d => double.Parse(d[2..], System.Globalization.CultureInfo.InvariantCulture) * 100d)];
        Assert.Equal(12, rolls.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            string expected = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"P1(Player:1) aura {HalfChanceAura} \"Spell {HalfChanceAura}\" on ");
            Assert.StartsWith(expected, lines[i], StringComparison.Ordinal);
            Assert.Contains("(attacker) chance 50.00% roll " + rolls[i].ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), lines[i], StringComparison.Ordinal);
            Assert.EndsWith(procced[i] ? "-> proc" : "-> no proc", lines[i], StringComparison.Ordinal);
            Assert.Equal(rolls[i] < 50d, procced[i]);
        }

        Assert.All(sink.Lines, l => Assert.Equal((new ObjectGuid(1), new ObjectGuid(2)), (l.Subject.Actor, l.Subject.Other)));
    }

    [Fact]
    public void TheProcRngDrawsAndOutcomes_AreTheSame_WithTheStreamFollowedOrNot()
    {
        (List<bool> withStream, List<string> drawsWith, RecordingTraceSink? sink, _) = Swing(40, new RecordingTraceSink(GmStreamChannels.Procs, GmStreamChannels.Damage));
        (List<bool> withoutSink, List<string> drawsWithout, _, _) = Swing(40, null);
        (List<bool> otherChannel, List<string> drawsOther, RecordingTraceSink? moves, _) = Swing(40, new RecordingTraceSink(GmStreamChannels.Moves));

        Assert.Equal(40, sink!.Lines.Count(l => l.Channel == GmStreamChannels.Procs)); // the traced run really traced
        Assert.NotEmpty(drawsWithout);
        Assert.Equal(drawsWithout, drawsWith);
        Assert.Equal(withoutSink, withStream);
        Assert.Equal(drawsWithout, drawsOther);
        Assert.Equal(withoutSink, otherChannel);
        Assert.Equal(0, moves!.PublishCalls);
    }

    // --- zero cost -------------------------------------------------------------------------------------------

    private static (WorldOpcode Opcode, byte[] Payload)[] EveryTappedPacket(Player a, Player b) =>
    [
        (WorldOpcode.SmsgSpellnonmeleedamagelog, SpellPackets.BuildSpellNonMeleeDamageLog(b.Guid, a.Guid, 133, 1, SpellSchool.Fire)),
        (WorldOpcode.SmsgAttackerstateupdate, CombatPackets.AttackerStateUpdate(HitInfo.AffectsVictim, a.Guid, b.Guid, 1, [new SubDamage(0, 1, 0, 0)], VictimState.Normal, 0)),
        (WorldOpcode.SmsgSpellheallog, SpellPackets.BuildSpellHealLog(b.Guid, a.Guid, 2054, 1)),
        (WorldOpcode.SmsgPeriodicauralog, SpellPackets.BuildPeriodicAuraLog(b.Guid, a.Guid, 589, new PeriodicLogEntry(AuraType.PeriodicDamage, 1, 5))),
        (WorldOpcode.SmsgSpellStart, SpellPackets.BuildSpellStart(a.Guid, a.Guid, 133, SpellCastFlags.None, 1, SpellCastTargets.ForUnit(b.Guid))),
        (WorldOpcode.SmsgSpellGo, SpellPackets.BuildSpellGo(a.Guid, a.Guid, 133, SpellCastFlags.None, [b.Guid], [], SpellCastTargets.ForUnit(b.Guid))),
        (WorldOpcode.SmsgSpelllogmiss, SpellPackets.BuildSpellLogMiss(133, a.Guid, b.Guid, SpellMissInfo.Miss)),
        (WorldOpcode.SmsgSpellFailedOther, SpellPackets.BuildSpellFailedOther(a.Guid, 133)),
        (WorldOpcode.MsgMoveHeartbeat, [0]),
        (WorldOpcode.SmsgMonsterMove, [0]),
        (WorldOpcode.SmsgBuyFailed, [0]), // no channel at all
    ];

    [Fact]
    public void WithNoSinkOrNoFollowedChannel_NoProducerPublishes()
    {
        (SpellTestKit kit, Player a, _, Player b, FakeSession sb) = TwoPlayers();
        using SpellTestKit owned = kit;
        kit.Spellbook.Teach(a, CastBolt);
        var idle = new RecordingTraceSink(); // attached, but follows nothing: IsActive false

        foreach (IGmTraceSink? sink in new IGmTraceSink?[] { null, idle })
        {
            kit.World.GmTrace = sink;
            foreach ((WorldOpcode opcode, byte[] payload) in EveryTappedPacket(a, b))
            {
                a.Map!.BroadcastToObservers(a, opcode, payload);
            }

            SpellSystem.SetPower(a, PowerType.Rage, 0);
            Assert.Equal(SpellCastResult.NoPower, kit.System.HandleCastRequest(a, CastBolt, SpellCastTargets.ForUnit(b.Guid)));
        }

        Assert.Equal(0, idle.PublishCalls);
        Assert.Equal(0, idle.HasSubscribersCalls); // the map's IsActive guard stops before any channel lookup
        Assert.Equal(2 * 11, sb.Sent.Count(p => p.Opcode != WorldOpcode.SmsgCastResult)); // every broadcast still reached the observer
    }

    [Fact]
    public void WithOnlyAnotherChannelFollowed_TheTapLooksUpTheChannelButNeverDecodesOrPublishes()
    {
        (SpellTestKit kit, Player a, _, Player b, _) = TwoPlayers();
        using SpellTestKit owned = kit;
        kit.Spellbook.Teach(a, CastBolt);
        var sink = new RecordingTraceSink(GmStreamChannels.AntiCheat);
        kit.World.GmTrace = sink;

        foreach ((WorldOpcode opcode, byte[] payload) in EveryTappedPacket(a, b))
        {
            a.Map!.BroadcastToObservers(a, opcode, payload);
        }

        SpellSystem.SetPower(a, PowerType.Rage, 0);
        kit.System.HandleCastRequest(a, CastBolt, SpellCastTargets.ForUnit(b.Guid));

        Assert.Equal(0, sink.PublishCalls);
        Assert.Equal(11, sink.HasSubscribersCalls); // one lookup per tapped opcode (10) and one for the cast failure; none for SMSG_BUY_FAILED
    }

    [Fact]
    public void AShortOrTruncatedPayload_IsSkipped_AndAThrowingSinkNeverBreaksTheBroadcast()
    {
        (SpellTestKit kit, Player a, _, Player b, FakeSession sb) = TwoPlayers();
        using SpellTestKit owned = kit;
        var sink = new RecordingTraceSink(GmStreamChannels.Damage, GmStreamChannels.Spells);
        kit.World.GmTrace = sink;

        foreach ((WorldOpcode opcode, byte[] payload) in EveryTappedPacket(a, b).Take(8))
        {
            a.Map!.BroadcastToObservers(a, opcode, payload.AsSpan(0, payload.Length / 2)); // cut inside the fields the tap reads (a trailing byte it skips would still decode)
            a.Map.BroadcastToObservers(a, opcode, []);
        }

        Assert.Equal(0, sink.PublishCalls);

        kit.World.GmTrace = new ThrowingTraceSink();
        sb.Clear();
        a.Map!.BroadcastToObservers(a, WorldOpcode.SmsgSpellnonmeleedamagelog, EveryTappedPacket(a, b)[0].Payload);
        Assert.Single(sb.Sent, p => p.Opcode == WorldOpcode.SmsgSpellnonmeleedamagelog);
    }

    [Fact]
    public void TheTapAndTheGuard_AllocateNothing_WhenNobodyFollowsTheChannel()
    {
        (SpellTestKit kit, Player a, _, Player b, _) = TwoPlayers();
        using SpellTestKit owned = kit;
        var sink = new RecordingTraceSink(GmStreamChannels.AntiCheat);
        kit.World.GmTrace = sink;
        (WorldOpcode Opcode, byte[] Payload)[] packets = EveryTappedPacket(a, b);
        Map map = a.Map!;
        WorldRuntime world = kit.World;

        void Run(int rounds)
        {
            for (int r = 0; r < rounds; r++)
            {
                foreach ((WorldOpcode opcode, byte[] payload) in packets)
                {
                    GmTraceTap.OnBroadcast(sink, map, a, opcode, payload);
                }

                GmTraceGuard.Wants(world, GmStreamChannels.Damage, out _);
                GmTraceGuard.Wants(world, GmStreamChannels.Procs, out _);
                GmTraceGuard.Wants(null, GmStreamChannels.Procs, out _);
            }
        }

        Run(50); // JIT and static tables
        long before = GC.GetAllocatedBytesForCurrentThread();
        Run(1000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(0, sink.PublishCalls);
    }

    [Fact]
    public void Wants_IsFalseWithoutAWorldASinkOrAFollower_AndHandsOutTheSinkOnlyWhenFollowed()
    {
        using SpellTestKit kit = new();
        var sink = new RecordingTraceSink(GmStreamChannels.Procs);

        Assert.False(GmTraceGuard.Wants(null, GmStreamChannels.Procs, out IGmTraceSink? none));
        Assert.Null(none);
        Assert.False(GmTraceGuard.Wants(kit.World, GmStreamChannels.Procs, out none));
        kit.World.GmTrace = new RecordingTraceSink();
        Assert.False(GmTraceGuard.Wants(kit.World, GmStreamChannels.Procs, out none));
        kit.World.GmTrace = sink;
        Assert.False(GmTraceGuard.Wants(kit.World, GmStreamChannels.Damage, out none));
        Assert.Null(none);
        Assert.True(GmTraceGuard.Wants(kit.World, "PROCS", out IGmTraceSink? got));
        Assert.Same(sink, got);
    }

    [Fact]
    public void TheChannelCatalog_ListsFiveChannels_AndOnlyMovesRefusesAll()
    {
        Assert.Equal(["damage", "procs", "spells", "moves", "anticheat"], GmStreamChannels.All.Select(c => c.Name));
        Assert.Equal(["moves"], GmStreamChannels.All.Where(c => !c.AllowsAll).Select(c => c.Name));
        Assert.Equal("anticheat", GmStreamChannels.Find("AntiCheat")?.Name);
        Assert.Null(GmStreamChannels.Find("proc"));
    }

    // --- melee spells (the spell-owned SMSG_ATTACKERSTATEUPDATE), environmental damage, game-object targets ----

    private const uint NextSwingStrike = 993_001;

    /// <summary>A Heroic-Strike-shaped next-melee spell (Attributes 0x4, melee damage class) of <paramref name="school"/>, as MeleeSpellLoopTests builds it.</summary>
    private static SpellInfo NextSwingSpell(SpellSchool school) => Spell(NextSwingStrike, Effect(SpellEffectName.SchoolDamage, 11, SpellImplicitTarget.UnitEnemy)) with
    {
        Attributes = (SpellAttributes)0x4 | SpellAttributes.IsAbility,
        RangeIndex = SpellConstants.RangeIndexCombat,
        Range = new SpellRange(0, 5),
        PowerType = (int)PowerType.Rage,
        ManaCost = 15,
        DamageClass = SpellDamageClass.Melee,
        School = school,
    };

    /// <summary>Queue the next-swing spell and let the main-hand swing cast it: the spell system sends its own SMSG_ATTACKERSTATEUPDATE (SpellSystem.MeleeSpellPackets).</summary>
    private static (List<(string Channel, GmTraceSubject Subject, string Line)> Lines, byte[] Packet) SwingNextMeleeSpell(SpellSchool school)
    {
        using var kit = new SpellTestKit(NextSwingSpell(school));
        (Player caster, FakeSession casterSession) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 3, 0, race: Race.Orc);
        target.Map!.Combat.TogglePvp(target, true);
        kit.Spellbook.Teach(caster, NextSwingStrike);
        caster.SetUInt32(UpdateFields.UnitFieldMaxpower1 + 1, 1000);
        SpellSystem.SetPower(caster, PowerType.Rage, 100);
        CombatEnvironment.Register(kit.World, new CombatEnvironment(new CombatOptions(), null, new SpellSystemMeleeHooks(kit.System)));
        kit.World.RunTick(0);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, NextSwingStrike, SpellCastTargets.ForUnit(target.Guid)));
        caster.Map!.Combat.Attack(caster, target);
        casterSession.Clear();
        var sink = new RecordingTraceSink(GmStreamChannels.Damage);
        kit.World.GmTrace = sink;

        kit.World.RunTick(50); // the first swing is due at once and casts the queued spell

        byte[] packet = Assert.Single(Packets(casterSession, WorldOpcode.SmsgAttackerstateupdate)); // the spell-owned packet went out, no white swing
        return (sink.Lines, packet);
    }

    [Fact]
    public void ANextMeleeSpellOfThePhysicalSchool_IsTracedAsPhysical_ThroughTheRealMeleeSpellPacket()
    {
        // The sub-damage school is a school index on the wire (vmangos Combat.cpp:203, cmangos-classic Unit.cpp:6112: GetFirstSchoolInMask);
        // a producer writing the mask (Normal's mask is 1) made this line, and the client, read "Holy".
        (string channel, GmTraceSubject subject, string line) = Assert.Single(SwingNextMeleeSpell(SpellSchool.Normal).Lines, l => l.Line.Contains(" melee via spell ", StringComparison.Ordinal)); // the spell's own damage log is a second line

        Assert.Equal(GmStreamChannels.Damage, channel);
        Assert.Equal($"P1(Player:1) -> P2(Player:2) melee via spell {NextSwingStrike} 11 Physical (absorb 0, resist 0, block 0)", line);
        Assert.Equal((new ObjectGuid(1), new ObjectGuid(2)), (subject.Actor, subject.Other));
    }

    [Fact]
    public void ANextMeleeSpellOfAMagicSchool_IsTracedWithThatSchool_ThroughTheRealMeleeSpellPacket()
    {
        // Fire's mask is 4, which read as an index would be "Frost".
        string line = Assert.Single(SwingNextMeleeSpell(SpellSchool.Fire).Lines, l => l.Line.Contains(" melee via spell ", StringComparison.Ordinal)).Line;

        Assert.StartsWith($"P1(Player:1) -> P2(Player:2) melee via spell {NextSwingStrike} ", line, StringComparison.Ordinal);
        Assert.Contains(" Fire (absorb ", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Frost", line, StringComparison.Ordinal);
    }

    /// <summary>Environmental mitigation with fixed absorb and resist for fire and nature damage.</summary>
    private sealed class FixedMitigation(uint absorb, int resist) : IEnvironmentalDamageMitigation
    {
        public bool IsImmune(Player player, EnvironmentalSchool school) => false;

        public EnvironmentalMitigation Calculate(Player player, EnvironmentalSchool school, uint damage) => new(absorb, resist);
    }

    [Fact]
    public void EnvironmentalDamage_ThroughItsRealSendPath_TracesTypeAmountAbsorbAndResist()
    {
        (SpellTestKit kit, Player a, _, _, FakeSession sb) = TwoPlayers();
        using SpellTestKit owned = kit;
        a.Health = a.MaxHealth = 1000;
        var sink = new RecordingTraceSink(GmStreamChannels.Damage);
        kit.World.GmTrace = sink;

        uint lava = EnvironmentalDamage.Apply(a, EnvironmentalDamageType.Lava, 40, new FixedMitigation(7, 5)); // 40 - 7 absorbed - 5 resisted
        uint fall = EnvironmentalDamage.Apply(a, EnvironmentalDamageType.FallToVoid, 12, new FixedMitigation(7, 5)); // physical: never mitigated, logged as Fall

        Assert.Equal((28u, 12u), (lava, fall));
        Assert.Equal(
            [
                "P1(Player:1) environmental Lava 28 (absorb 7, resist 5)",
                "P1(Player:1) environmental Fall 12 (absorb 0, resist 0)",
            ],
            sink.Lines.Select(l => l.Line));
        Assert.All(sink.Lines, l => Assert.Equal((a.Guid, ObjectGuid.Empty, true), (l.Subject.Actor, l.Subject.Other, l.Subject.HasPosition)));
        Assert.Equal(2, sb.Sent.Count(p => p.Opcode == WorldOpcode.SmsgEnvironmentaldamagelog)); // the observer still got both logs
    }

    [Fact]
    public void ASpellStartOnAGameObject_NamesTheObjectAsTheTarget()
    {
        (SpellTestKit kit, Player a, _, _, _) = TwoPlayers();
        using SpellTestKit owned = kit;
        var sink = new RecordingTraceSink(GmStreamChannels.Spells);
        kit.World.GmTrace = sink;
        ObjectGuid chest = ObjectGuid.WithEntry(HighGuid.GameObject, 2843, 321);
        var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = chest };

        SpellSystem.SendToSet(a, WorldOpcode.SmsgSpellStart, SpellPackets.BuildSpellStart(a.Guid, a.Guid, 1804, SpellCastFlags.None, 5000, targets), includeSelf: true);

        (_, GmTraceSubject subject, string line) = Assert.Single(sink.Lines);
        Assert.Equal($"cast start spell 1804 by P1(Player:1) on GameObject:{chest.Counter}, cast 5000 ms", line);
        Assert.Equal(321u, chest.Counter);
        Assert.Equal((a.Guid, chest), (subject.Actor, subject.Other));
    }
}
