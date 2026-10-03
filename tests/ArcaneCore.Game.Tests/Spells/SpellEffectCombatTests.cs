using System.Buffers.Binary;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Weapon, leech, dispel, interrupt, summon and area-aura effects; hit/crit/resist rules; pushback and channel interrupts.</summary>
public sealed class SpellEffectCombatTests
{
    private const uint WeaponStrike = 900400;
    private const uint WeaponPercent = 900401;
    private const uint Normalized = 900402;
    private const uint Leech = 900403;
    private const uint Dispel = 900404;
    private const uint MagicDebuff = 900405;
    private const uint MagicBuff = 900406;
    private const uint Kick = 900407;
    private const uint SilenceableBolt = 900408;
    private const uint Summon = 900409;
    private const uint PartyAura = 900410;
    private const uint PushbackBolt = 900411;
    private const uint FragileBolt = 900412;
    private const uint DelayChannel = 900413;
    private const uint BreakChannel = 900414;
    private const uint FragileAura = 900415;
    private const uint Environmental = 900416;
    private const uint MagicBolt = 900417;

    [Fact]
    public void WeaponDamage_AddsTheFlatBonus_ThePercentScalesTheTotal_AndNormalizedUsesTheNormalizedSpeed()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.SetFloat(UpdateFields.UnitFieldMindamage, 10);
        caster.SetFloat(UpdateFields.UnitFieldMaxdamage, 10);
        caster.SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2000);
        caster.SetInt32(UpdateFields.UnitFieldAttackPower, 140);

        kit.System.CastSpell(caster, WeaponStrike, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.Equal(45u, target.Health); // 10 + 5

        target.Health = 60;
        kit.System.CastSpell(caster, WeaponPercent, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.Equal(36u, target.Health); // (10 + 6) × 150%

        target.Health = 60;
        kit.System.CastSpell(caster, Normalized, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.Equal(44u, target.Health); // 10 + (2.4 - 2.0) × 140 / 14 + 2
        Assert.Equal(14f, kit.System.WeaponDamageRoll(caster, WeaponAttackType.BaseAttack, normalized: true), 3);
    }

    [Fact]
    public void HealthLeech_HealsTheCasterByTheDamageDealtTimesTheMultiple()
    {
        using var kit = Kit();
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.Health = 10;
        target.Health = 8; // only 8 can be dealt

        kit.System.CastSpell(caster, Leech, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(0u, target.Health);
        Assert.Equal(14u, caster.Health); // 8 × 0.5
        Assert.Single(Packets(session, WorldOpcode.SmsgSpellheallog));
    }

    [Fact]
    public void EnvironmentalDamage_NeverCrits()
    {
        using var kit = Kit();
        kit.System.CombatRules = new FixedRules { Crit = true };
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(caster, Environmental, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(50u, target.Health);
    }

    [Fact]
    public void Dispel_RemovesHarmfulMagicFromFriends_BeneficialFromEnemies_AndNothingToDispelFailsTheCast()
    {
        using var kit = Kit();
        var relations = new FakeRelations();
        kit.System.Relations = relations;
        (Player caster, _) = kit.AddPlayer(1);
        (Player friend, _) = kit.AddPlayer(2, 2);
        (Player enemy, _) = kit.AddPlayer(3, 3);
        relations.Hostile.Add(enemy.Guid);
        kit.Spellbook.Teach(caster, Dispel);
        kit.System.CastSpell(enemy, MagicDebuff, SpellCastTargets.ForUnit(friend.Guid), triggered: true);
        kit.System.CastSpell(friend, MagicBuff, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(enemy, MagicBuff, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Single(kit.System.DispellableAuras(caster, friend, 1));
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Dispel, SpellCastTargets.ForUnit(friend.Guid)));
        Assert.False(kit.System.HasAura(friend, MagicDebuff));
        Assert.True(kit.System.HasAura(friend, MagicBuff));

        kit.Advance(1500);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Dispel, SpellCastTargets.ForUnit(enemy.Guid)));
        Assert.False(kit.System.HasAura(enemy, MagicBuff));

        kit.Advance(1500);
        Assert.Equal(SpellCastResult.NothingToDispel, kit.System.HandleCastRequest(caster, Dispel, SpellCastTargets.ForUnit(friend.Guid)));
    }

    [Fact]
    public void InterruptCast_CancelsASilenceableCast_LocksTheSchool_AndTellsTheVictim()
    {
        using var kit = Kit();
        (Player kicker, _) = kit.AddPlayer(1);
        (Player victim, FakeSession victimSession) = kit.AddPlayer(2, 2);
        kit.Spellbook.Teach(victim, SilenceableBolt, MagicBolt);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(victim, SilenceableBolt, SpellCastTargets.ForUnit(kicker.Guid)));
        victimSession.Clear();

        kit.System.CastSpell(kicker, Kick, SpellCastTargets.ForUnit(victim.Guid), triggered: true);

        Assert.Null(kit.System.GetState(victim.Guid)!.CurrentCast);
        Assert.True(kit.System.IsSchoolLocked(victim, SpellSchool.Fire));
        Assert.False(kit.System.IsSchoolLocked(victim, SpellSchool.Frost));
        byte[] cooldown = Assert.Single(Packets(victimSession, WorldOpcode.SmsgSpellCooldown));
        Assert.Equal(SilenceableBolt, BinaryPrimitives.ReadUInt32LittleEndian(cooldown.AsSpan(8)));
        Assert.Equal(4000u, BinaryPrimitives.ReadUInt32LittleEndian(cooldown.AsSpan(12)));

        kit.Advance(1500);
        Assert.Equal(SpellCastResult.NotReady, kit.System.HandleCastRequest(victim, SilenceableBolt, SpellCastTargets.ForUnit(kicker.Guid)));
        kit.Advance(2500);
        Assert.False(kit.System.IsSchoolLocked(victim, SpellSchool.Fire));
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(victim, SilenceableBolt, SpellCastTargets.ForUnit(kicker.Guid)));
    }

    [Fact]
    public void InterruptCast_IgnoresCastsWithoutSilencePrevention_AndInstantCasts()
    {
        using var kit = Kit();
        (Player kicker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        kit.Spellbook.Teach(victim, MagicBolt);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(victim, MagicBolt, SpellCastTargets.ForUnit(kicker.Guid)));

        kit.System.CastSpell(kicker, Kick, SpellCastTargets.ForUnit(victim.Guid), triggered: true);

        Assert.NotNull(kit.System.GetState(victim.Guid)!.CurrentCast);
        Assert.False(kit.System.IsSchoolLocked(victim, SpellSchool.Frost));
    }

    [Fact]
    public void Summon_GoesThroughTheSink_AtTheDestination_AndWithoutASinkIsReportedOnly()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Summon, SpellCastTargets.ForSelf(), triggered: true));

        var sink = new FakeSummons();
        kit.System.Summons = sink;
        kit.System.CastSpell(caster, Summon, new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = (3, 4, 5) }, triggered: true);
        (Unit summoner, uint entry, float x, float y, float z, int duration) = Assert.Single(sink.Calls);
        Assert.Same(caster, summoner);
        Assert.Equal((416u, 3f, 4f, 5f, 60_000), (entry, x, y, z, duration));

        sink.Fail = true;
        kit.System.CastSpell(caster, Summon, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(2, sink.Calls.Count);
        Assert.Equal((caster.X, caster.Y), (sink.Calls[1].X, sink.Calls[1].Y));
    }

    [Fact]
    public void PartyAreaAura_SpreadsToMembersInRange_AndLeavesWithRangePartyAndCaster()
    {
        using var kit = Kit();
        var groups = new FakeGroups();
        kit.System.Groups = groups;
        (Player caster, _) = kit.AddPlayer(1);
        (Player near, _) = kit.AddPlayer(2, 10);
        (Player far, _) = kit.AddPlayer(3, 40);
        (Player stranger, _) = kit.AddPlayer(4, 5);
        groups.Parties.Add([caster.Guid, near.Guid, far.Guid]);

        kit.System.CastSpell(caster, PartyAura, SpellCastTargets.ForSelf(), triggered: true);
        SpellAuraHolder source = Assert.Single(kit.System.GetAuras(caster));
        Assert.True(source.IsAreaSource);
        Assert.True(source.IsSaveable);
        kit.Advance(100);

        SpellAuraHolder child = Assert.Single(kit.System.GetAuras(near));
        Assert.Same(source, child.AreaParent);
        Assert.Equal(caster.Guid, child.CasterGuid);
        Assert.False(child.IsSaveable);
        Assert.Same(caster, kit.System.ResolveAuraActor(child));
        Assert.Empty(kit.System.GetAuras(far));
        Assert.Empty(kit.System.GetAuras(stranger));

        // In range again → gets it; out of range → loses it.
        far.Relocate(20, 0, far.Z, 0, kit.Now);
        kit.Advance(100);
        Assert.Single(kit.System.GetAuras(far));
        far.Relocate(40, 0, far.Z, 0, kit.Now);
        kit.Advance(100);
        Assert.Empty(kit.System.GetAuras(far));

        // Leaving the party removes it.
        groups.Parties[0].Remove(near.Guid);
        kit.Advance(100);
        Assert.Empty(kit.System.GetAuras(near));
        groups.Parties[0].Add(near.Guid);
        kit.Advance(100);
        child = Assert.Single(kit.System.GetAuras(near));

        // The caster logs out: the source goes, and so does every child.
        kit.System.RemoveUnit(caster);
        kit.Advance(100);
        Assert.True(child.IsRemoved);
        Assert.Empty(kit.System.GetAuras(near));
    }

    [Fact]
    public void PartyAreaAura_RemovingTheSource_RemovesChildrenImmediately()
    {
        using var kit = Kit();
        var groups = new FakeGroups();
        kit.System.Groups = groups;
        (Player caster, _) = kit.AddPlayer(1);
        (Player member, _) = kit.AddPlayer(2, 5);
        groups.Parties.Add([caster.Guid, member.Guid]);
        kit.System.CastSpell(caster, PartyAura, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(100);
        Assert.Single(kit.System.GetAuras(member));

        kit.System.RemoveAuras(caster, PartyAura);

        Assert.Empty(kit.System.GetAuras(member));
        Assert.Empty(kit.System.CaptureState(member, 0).Auras);
    }

    [Fact]
    public void Misses_SendSpellLogMiss_AndDealNoDamage()
    {
        using var kit = Kit();
        kit.System.CombatRules = new FixedRules { Miss = SpellMissInfo.Resist };
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(caster, MagicBolt, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(60u, target.Health);
        byte[] miss = Assert.Single(Packets(session, WorldOpcode.SmsgSpelllogmiss));
        Assert.Equal(MagicBolt, BinaryPrimitives.ReadUInt32LittleEndian(miss));
        Assert.Equal((byte)SpellMissInfo.Resist, miss[^1]);
        Assert.Empty(Packets(session, WorldOpcode.SmsgSpellnonmeleedamagelog));
    }

    [Fact]
    public void Crit_MultipliesDamage_FlagsTheLog_AndResistAndArmorReduceIt()
    {
        using var kit = Kit();
        var rules = new FixedRules { Crit = true, Multiplier = 1.5f };
        kit.System.CombatRules = rules;
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);

        SpellDamageResult crit = kit.System.DealDirectDamage(caster, target, kit.Store.Get(MagicBolt)!, 20, allowCrit: true);
        Assert.Equal(new SpellDamageResult(30, 0, true), crit);
        byte[] log = Assert.Single(Packets(session, WorldOpcode.SmsgSpellnonmeleedamagelog));
        Assert.Equal(SpellSystem.SpellHitTypeCrit, BinaryPrimitives.ReadUInt32LittleEndian(log.AsSpan(log.Length - 5)) & SpellSystem.SpellHitTypeCrit);

        rules.Crit = false;
        rules.ResistFraction = 0.25f;
        rules.Armor = 4;
        target.Health = 60;
        Assert.Equal(new SpellDamageResult(12, 4, false), kit.System.DealDirectDamage(caster, target, kit.Store.Get(MagicBolt)!, 20, allowCrit: true));
        Assert.Equal(48u, target.Health);
    }

    [Fact]
    public void PeriodicDamage_IsPartiallyResisted_AndLogsTheResist()
    {
        using var kit = Kit();
        kit.System.CombatRules = new FixedRules { ResistFraction = 0.5f };
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, FakeSession session) = kit.AddPlayer(2, 2);
        kit.System.CastSpell(caster, DotSpell, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        session.Clear();

        kit.Advance(3000);

        Assert.Equal(58u, target.Health); // 4 - 2
        Assert.Single(Packets(session, WorldOpcode.SmsgPeriodicauralog));
    }

    [Fact]
    public void DirectDamage_PushesBackACast_ByTheDecayingDelay_CappedAtTheCastTime_AndPeriodicDamageDoesNot()
    {
        using var kit = Kit();
        (Player attacker, _) = kit.AddPlayer(1);
        (Player caster, FakeSession session) = kit.AddPlayer(2, 2);
        kit.Spellbook.Teach(caster, PushbackBolt);
        kit.System.HandleCastRequest(caster, PushbackBolt, SpellCastTargets.ForUnit(attacker.Guid));
        SpellCast cast = kit.System.GetState(caster.Guid)!.CurrentCast!;
        kit.Advance(1000);
        session.Clear();

        kit.System.OnDamageTaken(caster, attacker, 5, periodic: false);
        Assert.Equal(2000, cast.Timer); // first pushback 1000 ms (vmangos Spell::GetNextDelayAtDamageMsTime)
        Assert.Equal(1, cast.PushbackCount);

        kit.System.OnDamageTaken(caster, attacker, 5, periodic: false);
        Assert.Equal(2000, cast.Timer); // second would be 800 ms: capped at the full cast time
        kit.System.OnDamageTaken(caster, attacker, 5, periodic: true);
        kit.System.OnDamageTaken(caster, caster, 5, periodic: false); // self damage
        kit.System.OnDamageTaken(caster, attacker, 0, periodic: false);
        Assert.Equal(2000, cast.Timer);
        List<byte[]> delayed = Packets(session, WorldOpcode.SmsgSpellDelayed);
        Assert.Equal([1000u, 0u], delayed.Select(p => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(p.Length - 4))));
    }

    [Fact]
    public void DamageCancels_InterruptsTheCast_AndDamageBreaksFragileAuras()
    {
        using var kit = Kit();
        (Player attacker, _) = kit.AddPlayer(1);
        (Player caster, _) = kit.AddPlayer(2, 2);
        kit.Spellbook.Teach(caster, FragileBolt);
        kit.System.CastSpell(caster, FragileAura, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.HandleCastRequest(caster, FragileBolt, SpellCastTargets.ForUnit(attacker.Guid));

        kit.System.CastSpell(attacker, MagicBolt, SpellCastTargets.ForUnit(caster.Guid), triggered: true);

        Assert.Null(kit.System.GetState(caster.Guid)!.CurrentCast);
        Assert.False(kit.System.HasAura(caster, FragileAura));
    }

    [Fact]
    public void ChannelDelay_ShortensTheChannelAndItsAura_AndChannelDamageFlagInterrupts()
    {
        using var kit = Kit();
        (Player attacker, _) = kit.AddPlayer(1);
        (Player caster, FakeSession session) = kit.AddPlayer(2, 2);
        kit.Spellbook.Teach(caster, DelayChannel, BreakChannel);
        kit.System.HandleCastRequest(caster, DelayChannel, SpellCastTargets.ForSelf());
        SpellCast channel = kit.System.GetState(caster.Guid)!.CurrentCast!;
        Assert.Equal(SpellCastState.Casting, channel.State);
        session.Clear();

        kit.System.OnDamageTaken(caster, attacker, 5, periodic: true); // DoTs never delay a channel (Unit.cpp:900-906)
        Assert.Equal(8000, channel.Timer);
        Assert.Empty(Packets(session, WorldOpcode.MsgChannelUpdate));

        kit.System.OnDamageTaken(caster, attacker, 5, periodic: false);
        Assert.Equal(7000, channel.Timer); // first pushback 1000 ms
        Assert.Equal(7000, kit.System.GetAuras(caster).Single(h => h.Spell.Id == DelayChannel).Duration);
        Assert.Single(Packets(session, WorldOpcode.MsgChannelUpdate));
        kit.System.OnDamageTaken(caster, attacker, 5, periodic: false);
        Assert.Equal(6200, channel.Timer); // second pushback 800 ms

        kit.Advance(1500);
        kit.System.HandleCastRequest(caster, BreakChannel, SpellCastTargets.ForSelf());
        Assert.Equal(BreakChannel, kit.System.GetState(caster.Guid)!.CurrentCast!.Spell.Id);
        kit.System.OnDamageTaken(caster, attacker, 5, periodic: false);
        Assert.Null(kit.System.GetState(caster.Guid)!.CurrentCast);
    }

    [Fact]
    public void MeleeDamage_RaisesTheMapCombatDamageEvent()
    {
        using var kit = Kit();
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        MapCombat combat = attacker.Map!.FindUpdater<MapCombat>()!;
        var events = new List<(Unit, Unit, uint, bool, bool)>();
        combat.DamageDealt += (a, v, d, direct, melee) => events.Add((a, v, d, direct, melee));

        combat.DealDamage(attacker, victim, 7);
        combat.DealDamage(attacker, victim, 3, direct: false, meleeDamage: false);
        combat.DealDamage(attacker, victim, 500); // lethal: no event

        Assert.Equal([(attacker, victim, 7u, true, true), (attacker, victim, 3u, false, false)], events);
    }

    [Theory]
    [InlineData(60, 60, 96f)]
    [InlineData(60, 62, 94f)]
    [InlineData(60, 63, 87f)]  // player target: 94 - 7
    [InlineData(60, 50, 106f)] // clamped later to 99
    public void VanillaMagicHitChance_FollowsTheLevelDifference(int casterLevel, int targetLevel, float expected)
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.Level = (byte)casterLevel;
        target.Level = (byte)targetLevel;

        Assert.Equal(expected, VanillaSpellCombatRules.MagicHitChance(caster, target));
    }

    [Fact]
    public void VanillaRules_ResistArmorAndCritFollowTheirInputs()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.Level = 60;
        target.SetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire, 200);
        var rules = new VanillaSpellCombatRules();
        SpellInfo magic = kit.Store.Get(MagicBolt)! with { School = SpellSchool.Fire };

        Assert.Equal(0.5f, VanillaSpellCombatRules.AverageResistFraction(caster, target, SpellSchool.Fire), 3);
        Assert.Equal(0f, VanillaSpellCombatRules.AverageResistFraction(caster, target, SpellSchool.Frost));
        uint resisted = rules.RollPartialResist(kit.System, caster, target, magic, 100);
        Assert.Equal(50u, resisted); // exactly on a quarter step
        Assert.Equal(50u, rules.RollPartialResist(kit.System, caster, target, magic with { DamageClass = SpellDamageClass.None }, 100)); // the school decides, not the damage class
        Assert.True(VanillaSpellCombatRules.IsBinary(kit.Store.Get(StunSpell)! with { DamageClass = SpellDamageClass.Magic, School = SpellSchool.Frost })); // binary needs a magic non-physical spell (SpellMgr.cpp:3350-3356)
        Assert.False(VanillaSpellCombatRules.IsBinary(magic));

        SpellInfo physical = magic with { School = SpellSchool.Normal, DamageClass = SpellDamageClass.Melee };
        Assert.Equal(100u, rules.ApplyArmor(caster, target, physical, 100)); // no armor
        target.SetUInt32(UpdateFields.UnitFieldResistances, 2000);
        Assert.True(rules.ApplyArmor(caster, target, physical, 100) < 100);
        Assert.Equal(100u, rules.ApplyArmor(caster, target, magic, 100));

        Assert.Equal(5f, rules.CritChance(kit.System, caster, magic));
        Assert.Equal(0f, rules.CritChance(kit.System, caster, magic with { DamageClass = SpellDamageClass.None }));
        Assert.False(rules.RollCrit(kit.System, caster, target, magic with { AttributesEx2 = SpellAttributesEx2.CantCrit }));
        Assert.Equal(1.5f, rules.CritMultiplier(magic));
        Assert.Equal(2f, rules.CritMultiplier(physical));
        Assert.Equal(SpellMissInfo.None, rules.RollHit(kit.System, caster, target, magic with { DamageClass = SpellDamageClass.None }));
        Assert.Equal(SpellMissInfo.None, rules.RollHit(kit.System, caster, caster, magic));
    }

    [Fact]
    public void VanillaMagicHits_MissAboutAsOftenAsTheHitChanceSays()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.Level = 60;
        target.Level = 63; // 87% hit
        var rules = new VanillaSpellCombatRules();
        SpellInfo magic = kit.Store.Get(MagicBolt)!;

        int resisted = Enumerable.Range(0, 4000).Count(_ => rules.RollHit(kit.System, caster, target, magic) == SpellMissInfo.Resist);

        Assert.InRange(resisted, 400, 640); // 13% of 4000 = 520
    }

    private static SpellTestKit Kit()
    {
        static SpellInfo Ranged(SpellInfo spell) => spell with { RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0 };
        return new SpellTestKit(
            Ranged(Spell(WeaponStrike, Effect(SpellEffectName.WeaponDamage, 5, SpellImplicitTarget.UnitEnemy)) with { DamageClass = SpellDamageClass.Melee }),
            Ranged(Spell(WeaponPercent,
                Effect(SpellEffectName.WeaponDamageNoschool, 6, SpellImplicitTarget.UnitEnemy),
                Effect(SpellEffectName.WeaponPercentDamage, 150, SpellImplicitTarget.UnitEnemy)) with { DamageClass = SpellDamageClass.Melee }),
            Ranged(Spell(Normalized, Effect(SpellEffectName.NormalizedWeaponDmg, 2, SpellImplicitTarget.UnitEnemy)) with { DamageClass = SpellDamageClass.Melee }),
            Ranged(Spell(Leech, Effect(SpellEffectName.HealthLeech, 20, SpellImplicitTarget.UnitEnemy) with { MultipleValue = 0.5f })
                with { School = SpellSchool.Shadow, DamageClass = SpellDamageClass.Magic }),
            Ranged(Spell(Environmental, Effect(SpellEffectName.EnvironmentalDamage, 10, SpellImplicitTarget.UnitEnemy))),
            Ranged(Spell(Dispel, Effect(SpellEffectName.Dispel, 1, SpellImplicitTarget.Unit, misc: 1))),
            Ranged(Spell(MagicDebuff, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Dummy))
                with { Dispel = 1, Duration = new SpellDuration(30_000, 0, 30_000), SpellVisual = 1 }),
            Ranged(Spell(MagicBuff, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy))
                with { Dispel = 1, Duration = new SpellDuration(30_000, 0, 30_000), SpellVisual = 1 }),
            Ranged(Spell(Kick, Effect(SpellEffectName.InterruptCast, 0, SpellImplicitTarget.UnitEnemy)) with { Duration = new SpellDuration(4000, 0, 4000) }),
            Ranged(Spell(SilenceableBolt, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy))
                with { School = SpellSchool.Fire, CastTime = new SpellCastTime(3000, 0, 0), PreventionType = 1, DamageClass = SpellDamageClass.Magic, InterruptFlags = SpellInterruptFlags.DamagePushback }),
            Ranged(Spell(MagicBolt, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy))
                with { School = SpellSchool.Frost, CastTime = new SpellCastTime(3000, 0, 0), DamageClass = SpellDamageClass.Magic }),
            Spell(Summon, Effect(SpellEffectName.Summon, 0, misc: 416)) with { Duration = new SpellDuration(60_000, 0, 60_000) },
            Spell(PartyAura, Effect(SpellEffectName.ApplyAreaAuraParty, 0, aura: AuraType.Dummy) with { Radius = 30 })
                with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1, StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            Ranged(Spell(PushbackBolt, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy))
                with { CastTime = new SpellCastTime(2000, 0, 0), InterruptFlags = SpellInterruptFlags.DamagePushback }),
            Ranged(Spell(FragileBolt, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy))
                with { CastTime = new SpellCastTime(2000, 0, 0), InterruptFlags = SpellInterruptFlags.DamageCancels }),
            Spell(FragileAura, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy))
                with { AuraInterruptFlags = SpellAuraInterruptFlags.Damage, Duration = new SpellDuration(30_000, 0, 30_000), SpellVisual = 1, StartRecoveryCategory = 0 },
            Spell(DelayChannel, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            {
                AttributesEx = SpellAttributesEx.IsChanneled, Duration = new SpellDuration(8000, 0, 8000), SpellVisual = 1,
                ChannelInterruptFlags = (SpellAuraInterruptFlags)SpellChannelInterruptFlags.Delay, StartRecoveryCategory = 0, StartRecoveryTime = 0,
            },
            Spell(BreakChannel, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            {
                AttributesEx = SpellAttributesEx.IsChanneled, Duration = new SpellDuration(8000, 0, 8000), SpellVisual = 1,
                ChannelInterruptFlags = (SpellAuraInterruptFlags)SpellChannelInterruptFlags.Damage, StartRecoveryCategory = 0, StartRecoveryTime = 0,
            });
    }
}
