using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// S08b: the melee swing and the spell system, against vmangos Unit::AttackerStateUpdate (Unit.cpp:2235-2257: no swing
/// while casting a non-melee spell, a queued next-swing spell is cast by the main-hand swing in place of the white
/// hit) and Unit::AttackStop (Unit.cpp:4604: the queued spell is interrupted).
/// </summary>
public sealed class MeleeSpellLoopTests
{
    private const uint HeroicStrike = 930001;   // Attributes 0x4 (the classic-db Heroic Strike shape)

    private static SpellInfo Strike(bool twoDamageEffects = false, bool zeroDamage = false)
    {
        SpellEffectInfo[] effects = zeroDamage
            ? [Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy)]
            : twoDamageEffects
            ? [Effect(SpellEffectName.SchoolDamage, 11, SpellImplicitTarget.UnitEnemy), Effect(SpellEffectName.SchoolDamage, 7, SpellImplicitTarget.UnitEnemy)]
            : [Effect(SpellEffectName.SchoolDamage, 11, SpellImplicitTarget.UnitEnemy)];
        SpellInfo spell = Spell(HeroicStrike, effects) with
        {
            Attributes = (SpellAttributes)0x4 | SpellAttributes.IsAbility,
            RangeIndex = SpellConstants.RangeIndexCombat,
            Range = new SpellRange(0, 5),
            PowerType = (int)PowerType.Rage,
            ManaCost = 15,
            DamageClass = SpellDamageClass.Melee,
        };
        return spell;
    }

    private sealed class Rig : IDisposable
    {
        public Rig(bool linked = true, CombatOptions? options = null, bool twoDamageEffects = false, bool zeroDamage = false)
        {
            Kit = new SpellTestKit(Strike(twoDamageEffects, zeroDamage));
            (Caster, CasterSession) = Kit.AddPlayer(1);
            (Target, _) = Kit.AddPlayer(2, 3, 0);
            Kit.Spellbook.Teach(Caster, HeroicStrike, CastBolt);
            Caster.SetUInt32(UpdateFields.UnitFieldMaxpower1 + 1, 1000);
            SpellSystem.SetPower(Caster, PowerType.Rage, 100);
            if (linked)
            {
                CombatEnvironment.Register(Kit.World, new CombatEnvironment(options ?? new CombatOptions(), null, new SpellSystemMeleeHooks(Kit.System)));
            }

            Kit.World.RunTick(0);
            CasterSession.Clear();
        }

        public SpellTestKit Kit { get; }

        public Player Caster { get; }

        public FakeSession CasterSession { get; }

        public Player Target { get; }

        public MapCombat Combat => Caster.Map!.Combat;

        public SpellCastResult QueueHeroicStrike() => Kit.System.HandleCastRequest(Caster, HeroicStrike, SpellCastTargets.ForUnit(Target.Guid));

        public List<byte[]> AttackerStatePackets => Packets(CasterSession, WorldOpcode.SmsgAttackerstateupdate);

        public int WhiteSwings => AttackerStatePackets.Count(packet => ReadAttackerState(packet).SpellId == 0);

        public int QueuedSpellSwings => AttackerStatePackets.Count(packet => ReadAttackerState(packet).SpellId == HeroicStrike);

        public void Tick(uint ms) => Kit.World.RunTick(ms);

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void QueuedNextSwingSpell_IsCastByTheSwing_InPlaceOfTheWhiteHit()
    {
        using var rig = new Rig();
        Assert.Equal(SpellCastResult.CastOk, rig.QueueHeroicStrike());
        rig.Combat.Attack(rig.Caster, rig.Target);
        rig.CasterSession.Clear();

        rig.Tick(50);   // the first swing is due at once

        Assert.Equal(49u, rig.Target.Health);                                 // the spell's 11, no white damage on top
        Assert.Equal(85u, SpellSystem.GetPower(rig.Caster, PowerType.Rage)); // 15 taken at the swing, not at the press
        Assert.Null(rig.Kit.System.GetState(rig.Caster.Guid)?.MeleeCast);
        Assert.Equal(0, rig.WhiteSwings);                                     // the skipped white hit remains absent
        Assert.Equal(1, rig.QueuedSpellSwings);                               // spell-owned attacker state carries Heroic Strike
        var packet = ReadAttackerState(Assert.Single(rig.AttackerStatePackets));
        Assert.Equal(HeroicStrike, packet.SpellId);
        Assert.True(((HitInfo)packet.HitInfo).HasFlag(HitInfo.NoAction));
        Assert.Equal(11u, packet.TotalDamage);
        Assert.Equal(11u, packet.SubDamage);
        Assert.Equal(1u, packet.SubDamageSchool);                         // SpellSchool.Normal -> SPELL_SCHOOL_MASK_NORMAL
        Assert.Contains(WorldOpcode.SmsgSpellGo, Opcodes(rig.CasterSession));

        rig.Tick(2000);   // the next swing is a normal white one again
        Assert.Equal(1, rig.WhiteSwings);
        Assert.Equal(1, rig.QueuedSpellSwings);                               // no duplicate spell packet
    }

    [Fact]
    public void QueuedNextSwingSpell_TwoDamageEffects_EmitsOneAggregatedPacket()
    {
        using var rig = new Rig(twoDamageEffects: true);
        Assert.Equal(SpellCastResult.CastOk, rig.QueueHeroicStrike());
        rig.Combat.Attack(rig.Caster, rig.Target);
        rig.CasterSession.Clear();

        rig.Tick(50);

        var packet = ReadAttackerState(Assert.Single(rig.AttackerStatePackets));
        Assert.Equal(HeroicStrike, packet.SpellId);
        Assert.Equal(18u, packet.TotalDamage);
        Assert.Equal(18u, packet.SubDamage);
        Assert.Equal(1, packet.SubDamageCount); // vmangos coalesces one school for a spell
        Assert.Equal(1u, packet.SubDamageSchool);
        Assert.True(((HitInfo)packet.HitInfo).HasFlag(HitInfo.NoAction));
        Assert.Equal(42u, rig.Target.Health);
        Assert.Equal(2, Packets(rig.CasterSession, WorldOpcode.SmsgSpellnonmeleedamagelog).Count);
    }

    [Fact]
    public void QueuedSpell_WaitsWhileTheTargetIsOutOfReach_ThenFiresOnTheFirstSwingInRange()
    {
        using var rig = new Rig();
        rig.Combat.Attack(rig.Caster, rig.Target);
        rig.Tick(50);   // the white opening swing
        Assert.Equal(1, rig.WhiteSwings);
        rig.QueueHeroicStrike();
        rig.CasterSession.Clear();
        uint rage = SpellSystem.GetPower(rig.Caster, PowerType.Rage);

        float z = rig.Target.Z;
        rig.Target.Relocate(40, 0, z, 0, 1);
        rig.Tick(2100);
        Assert.NotNull(rig.Kit.System.GetState(rig.Caster.Guid)!.MeleeCast);   // out of reach: no swing, nothing fired
        Assert.Equal(rage, SpellSystem.GetPower(rig.Caster, PowerType.Rage));

        rig.Target.Relocate(3, 0, z, 0, 2);
        uint health = rig.Target.Health;
        rig.Tick(200);

        Assert.Null(rig.Kit.System.GetState(rig.Caster.Guid)?.MeleeCast);
        Assert.Equal(health - 11, rig.Target.Health);
    }

    [Fact]
    public void QueuedSpellThatCannotBePaid_IsDropped_AndTheWhiteHitIsLostToo()
    {
        // vmangos: the cast failed and left the slot, `!spell` returns before the white swing (Unit.cpp:2255-2257).
        using var rig = new Rig();
        rig.QueueHeroicStrike();
        SpellSystem.SetPower(rig.Caster, PowerType.Rage, 5);
        rig.Combat.Attack(rig.Caster, rig.Target);
        rig.CasterSession.Clear();

        rig.Tick(50);

        Assert.Null(rig.Kit.System.GetState(rig.Caster.Guid)?.MeleeCast);
        Assert.Equal(60u, rig.Target.Health);
        Assert.Equal(0, rig.WhiteSwings);
        Assert.Equal((byte)SpellCastResult.NoPower, Packets(rig.CasterSession, WorldOpcode.SmsgCastResult).Single()[5]);
    }

    [Fact]
    public void OffHandSwing_NeverFiresTheQueuedSpell()
    {
        using var rig = new Rig();
        rig.QueueHeroicStrike();

        MeleeDamageInfo? info = rig.Combat.AttackerStateUpdate(rig.Caster, rig.Target, WeaponAttackType.OffAttack);

        Assert.NotNull(info);
        Assert.NotNull(rig.Kit.System.GetState(rig.Caster.Guid)!.MeleeCast);
    }

    [Fact]
    public void StoppingTheAttack_InterruptsTheQueuedSpell()
    {
        using var rig = new Rig();
        rig.Combat.Attack(rig.Caster, rig.Target);
        rig.QueueHeroicStrike();
        rig.CasterSession.Clear();

        Assert.True(rig.Combat.AttackStop(rig.Caster));

        Assert.Null(rig.Kit.System.GetState(rig.Caster.Guid)?.MeleeCast);
        Assert.Equal((byte)SpellCastResult.Interrupted, Packets(rig.CasterSession, WorldOpcode.SmsgCastResult).Single()[5]);
    }

    [Fact]
    public void SwitchingTheVictim_InterruptsTheQueuedSpell()
    {
        using var rig = new Rig();
        (Player other, _) = rig.Kit.AddPlayer(3, 0, 3);
        rig.Combat.Attack(rig.Caster, rig.Target);
        rig.QueueHeroicStrike();

        rig.Combat.Attack(rig.Caster, other);

        Assert.Null(rig.Kit.System.GetState(rig.Caster.Guid)?.MeleeCast);
    }

    [Fact]
    public void CombatStop_InterruptsTheQueuedSpell()
    {
        using var rig = new Rig();
        rig.Combat.Attack(rig.Caster, rig.Target);
        rig.QueueHeroicStrike();

        rig.Combat.CombatStop(rig.Caster);

        Assert.Null(rig.Kit.System.GetState(rig.Caster.Guid)?.MeleeCast);
    }

    [Fact]
    public void CastingANonMeleeSpell_LosesTheSwing_ButTheTimerRestarts()
    {
        // The pre-wave-4 behaviour, now the deviation Combat:CastingConsumesSwing; the retail default is in CastingSwingTests.
        using var rig = new Rig(options: new CombatOptions { CastingConsumesSwing = true });
        rig.Kit.System.HandleCastRequest(rig.Caster, CastBolt, SpellCastTargets.ForUnit(rig.Target.Guid));
        rig.Combat.Attack(rig.Caster, rig.Target);
        rig.CasterSession.Clear();

        rig.Tick(50);
        Assert.Equal(0, rig.WhiteSwings);                                     // no swing while the bolt is on the cast bar
        Assert.Equal(2000u, rig.Caster.Combat.GetAttackTimer(WeaponAttackType.BaseAttack));   // lost, not delayed

        rig.Kit.Advance(2000);   // the bolt lands
        rig.Tick(2000);
        Assert.Equal(1, rig.WhiteSwings);
    }

    [Fact]
    public void CastingANonMeleeSpell_DoesNotBlockTheSwing_WhenTheOptionIsOff()
    {
        using var rig = new Rig(options: new CombatOptions { MeleeCastingBlocksSwing = false });
        rig.Kit.System.HandleCastRequest(rig.Caster, CastBolt, SpellCastTargets.ForUnit(rig.Target.Guid));
        rig.Combat.Attack(rig.Caster, rig.Target);
        rig.CasterSession.Clear();

        rig.Tick(50);

        Assert.Equal(1, rig.WhiteSwings);
    }

    [Fact]
    public void CastingANonMeleeSpell_BlocksTheQueuedSpellToo_UntilTheCastEnds()
    {
        using var rig = new Rig();
        rig.QueueHeroicStrike();
        rig.Kit.Advance(1500);
        rig.Kit.System.HandleCastRequest(rig.Caster, CastBolt, SpellCastTargets.ForUnit(rig.Target.Guid));
        rig.Combat.Attack(rig.Caster, rig.Target);

        rig.Tick(50);
        Assert.NotNull(rig.Kit.System.GetState(rig.Caster.Guid)!.MeleeCast);   // Unit.cpp:2240-2241 returns before the melee-spell branch
        Assert.Equal(60u, rig.Target.Health);
    }

    [Fact]
    public void WithoutTheLink_TheSwingIgnoresTheSpellSystem()
    {
        using var rig = new Rig(linked: false);
        rig.QueueHeroicStrike();
        rig.Combat.Attack(rig.Caster, rig.Target);
        rig.CasterSession.Clear();

        rig.Tick(50);

        Assert.Equal(1, rig.WhiteSwings);
        Assert.NotNull(rig.Kit.System.GetState(rig.Caster.Guid)!.MeleeCast);
    }

    [Fact]
    public void QueuedSpellThatKillsItsTarget_IsNotReportedInterrupted()
    {
        // Review finding: the kill runs CombatStop -> AttackStop -> CancelQueuedMeleeSpell from inside Cast; the
        // cast is already past its cast point (SMSG_SPELL_GO sent), so it must not be cancelled.
        using var rig = new Rig();
        Assert.Equal(SpellCastResult.CastOk, rig.QueueHeroicStrike());
        rig.Combat.Attack(rig.Caster, rig.Target);
        rig.Kit.System.Damage = new CombatDamageSink();   // the world wiring (WorldSpellDamageSink): spell damage goes through map combat, so it can kill
        rig.Target.Health = 5;
        rig.CasterSession.Clear();

        rig.Tick(50);

        Assert.False(rig.Target.IsAlive);
        Assert.Contains(WorldOpcode.SmsgSpellGo, Opcodes(rig.CasterSession));
        var packet = ReadAttackerState(Assert.Single(Packets(rig.CasterSession, WorldOpcode.SmsgAttackerstateupdate)));
        Assert.Equal(HeroicStrike, packet.SpellId);
        Assert.Equal(5u, packet.TotalDamage);
        Assert.True(((HitInfo)packet.HitInfo).HasFlag(HitInfo.NoAction));
        Assert.DoesNotContain(SpellCastResult.Interrupted, Packets(rig.CasterSession, WorldOpcode.SmsgCastResult).Where(p => p.Length > 5).Select(p => (SpellCastResult)p[5]));
        Assert.Null(rig.Kit.System.GetState(rig.Caster.Guid)?.MeleeCast);
        Assert.False(rig.Kit.System.IsSpellReady(rig.Caster, Strike()));   // the global cooldown was not wiped
    }

    [Fact]
    public void NextSwingSpell_CanBeQueuedOutOfMeleeReach()
    {
        // vmangos Spell::CheckRange returns CAST_OK for a next-melee-swing spell with the combat range (Spell.cpp:6882).
        using var rig = new Rig();
        rig.Target.Relocate(40, 0, rig.Target.Z, 0, 1);

        Assert.Equal(SpellCastResult.CastOk, rig.QueueHeroicStrike());
        Assert.NotNull(rig.Kit.System.GetState(rig.Caster.Guid)!.MeleeCast);
    }

    [Fact]
    public void NextSwingSpell_NoDamageCompletion_EmitsZeroDamageAnimation()
    {
        using var rig = new Rig(zeroDamage: true);
        Assert.Equal(SpellCastResult.CastOk, rig.QueueHeroicStrike());
        rig.Combat.Attack(rig.Caster, rig.Target);
        rig.CasterSession.Clear();
        rig.Tick(50);

        var packet = ReadAttackerState(Assert.Single(Packets(rig.CasterSession, WorldOpcode.SmsgAttackerstateupdate)));
        Assert.Equal(HeroicStrike, packet.SpellId);
        Assert.Equal(0u, packet.TotalDamage);
        Assert.Equal(0u, packet.SubDamage);
        Assert.Equal((uint)VictimState.Normal, packet.VictimState);
        Assert.True(((HitInfo)packet.HitInfo).HasFlag(HitInfo.NoAction));
    }

    [Fact]
    public void NextSwingSpell_Dodge_EmitsNoDamageAnimation()
        => AssertNoDamageMissPacket(SpellMissInfo.Dodge, VictimState.Dodge);

    [Fact]
    public void NextSwingSpell_Parry_EmitsNoDamageAnimation()
        => AssertNoDamageMissPacket(SpellMissInfo.Parry, VictimState.Parry);

    private static void AssertNoDamageMissPacket(SpellMissInfo miss, VictimState victimState)
    {
        using var rig = new Rig();
        rig.Kit.System.CombatRules = new FixedMissRules(miss);
        Assert.Equal(SpellCastResult.CastOk, rig.QueueHeroicStrike());
        rig.Combat.Attack(rig.Caster, rig.Target);
        rig.CasterSession.Clear();
        rig.Tick(50);

        var packet = ReadAttackerState(Assert.Single(Packets(rig.CasterSession, WorldOpcode.SmsgAttackerstateupdate)));
        Assert.Equal(HeroicStrike, packet.SpellId);
        Assert.Equal(0u, packet.TotalDamage);
        Assert.Equal((uint)victimState, packet.VictimState);
        Assert.True(((HitInfo)packet.HitInfo).HasFlag(HitInfo.NoAction));
        Assert.False(((HitInfo)packet.HitInfo).HasFlag(HitInfo.Miss)); // miss bits come from SpellHitResult's meleeHitInfo at the root hook
    }

    [Fact]
    public void QueuedSpell_TwoEffectsAfterVictimClears_AggregatesBeforeAllDamageLogs()
    {
        using var rig = new Rig(twoDamageEffects: true);
        rig.Kit.System.Damage = new StopAfterFirstDamageSink();
        Assert.Equal(SpellCastResult.CastOk, rig.QueueHeroicStrike());
        rig.Combat.Attack(rig.Caster, rig.Target);
        rig.CasterSession.Clear();
        rig.Tick(50);
        var packet = ReadAttackerState(Assert.Single(rig.AttackerStatePackets));
        Assert.Equal(18u, packet.TotalDamage);
        Assert.Equal(18u, packet.SubDamage);
        Assert.Equal(1, packet.SubDamageCount);
        Assert.Equal(1u, packet.SubDamageSchool);
        Assert.Equal(2, Packets(rig.CasterSession, WorldOpcode.SmsgSpellnonmeleedamagelog).Count);
        List<WorldOpcode> opcodes = [.. Opcodes(rig.CasterSession)];
        Assert.True(opcodes.IndexOf(WorldOpcode.SmsgAttackerstateupdate) < opcodes.IndexOf(WorldOpcode.SmsgSpellnonmeleedamagelog));
        Assert.Equal(85u, SpellSystem.GetPower(rig.Caster, PowerType.Rage));
    }

    private sealed class FixedMissRules(SpellMissInfo miss) : ISpellCombatRules
    {
        public SpellMissInfo RollHit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => miss;
        public bool RollCrit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => false;
        public float CritMultiplier(SpellInfo spell) => 1;
        public uint RollPartialResist(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint damage) => 0;
        public uint ApplyArmor(Unit caster, Unit target, SpellInfo spell, uint damage) => damage;
    }

    private sealed class StopAfterFirstDamageSink : IDamageSink
    {
        private int _calls;
        public uint DealSpellDamage(Unit caster, Unit target, SpellInfo spell, uint damage, bool periodic)
        {
            uint dealt = Math.Min(damage, target.Health);
            target.Health -= dealt;
            if (_calls++ == 0) caster.Map!.Combat.AttackStop(caster);
            return dealt;
        }
        public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount) => 0;
    }

    private sealed class CombatDamageSink : IDamageSink
    {
        public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic)
        {
            uint health = victim.Health;
            caster.Map!.Combat.DealDamage(caster, victim, damage, direct: !periodic, meleeDamage: false);
            return health - Math.Min(health, victim.Health);
        }

        public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount) => 0;
    }

    private readonly record struct AttackerStateFields(uint HitInfo, uint TotalDamage, uint SubDamage, int SubDamageCount, uint SubDamageSchool, uint VictimState, uint SpellId);

    private static AttackerStateFields ReadAttackerState(byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint hitInfo = reader.ReadUInt32();
        reader.ReadPackedGuid();
        reader.ReadPackedGuid();
        uint totalDamage = reader.ReadUInt32();
        byte count = reader.ReadByte();
        uint subDamage = 0;
        uint subDamageSchool = 0;
        for (int i = 0; i < count; i++)
        {
            subDamageSchool = reader.ReadUInt32();
            reader.ReadSingle();
            subDamage += reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadInt32();
        }

        uint victimState = reader.ReadUInt32();
        reader.ReadUInt32();
        uint spellId = reader.ReadUInt32();
        reader.ReadUInt32();
        return new(hitInfo, totalDamage, subDamage, count, subDamageSchool, victimState, spellId);
    }
}
