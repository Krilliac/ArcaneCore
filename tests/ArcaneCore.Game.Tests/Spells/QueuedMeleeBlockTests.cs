using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Game.Tests.Procs;
using ArcaneCore.Kernel.WorldData.Procs;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Procs.TalentProcRig;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// A queued next-swing melee spell (Heroic Strike, Cleave, Maul) the victim partially blocks: the blocked amount rides the spell-owned
/// SMSG_ATTACKERSTATEUPDATE and SMSG_SPELLNONMELEEDAMAGELOG, the attacker state carries the rolled-block and block hit flags, and the hit
/// procs with PROC_EX_BLOCK so Redoubt fires (vmangos Unit.cpp:1621-1634 and CreateProcExtendMask :8818-8819). Field order is unchanged.
/// </summary>
public sealed class QueuedMeleeBlockTests
{
    private const uint HeroicStrike = 930101;
    private const uint Redoubt = 930102;
    private const uint RedoubtProbe = 930103;

    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;

        public override int Next(int minValue, int maxValue) => minValue;
    }

    private sealed class MapUnitResolver : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid)
            => reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
    }

    private sealed class Rig : IDisposable
    {
        public Rig(double blockRoll)
        {
            SpellInfo strike = Spell(HeroicStrike, Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy)) with
            {
                Attributes = (SpellAttributes)0x4 | SpellAttributes.IsAbility,
                RangeIndex = SpellConstants.RangeIndexCombat,
                Range = new SpellRange(0, 5),
                PowerType = (int)PowerType.Rage,
                ManaCost = 15,
                DamageClass = SpellDamageClass.Melee,
            };
            Procs = new TalentProcRig(strike, Talent(Redoubt, AuraType.ProcTriggerSpell, 0, ProcFlags.TakeMeleeAbility, trigger: RedoubtProbe),
                Probe(RedoubtProbe, atEnemy: false));
            Procs.UseRows(new SpellProcEventRecord(Redoubt, 0, 0, 0, 0, 0, 0, (uint)ProcFlagsEx.Block, 0, 0, 0));
            (Caster, Session) = Kit.AddPlayer(1);
            Victim = new CombatTestUnit { MaxHealth = 10_000, Health = 10_000 };
            Victim.Relocate(2, 0, Caster.Z, MathF.PI, 0); // faces the caster
            Caster.Map!.AddObject(Victim);
            Victim.Map!.Combat.Track(Victim);
            Caster.Map.Combat.Random = new ScriptedRandom(); // the white swing never lands first: a scripted roll past every table range
            Kit.System.Units = new MapUnitResolver();
            Kit.System.Random = new FixedRandom(blockRoll);
            Kit.Spellbook.Teach(Caster, HeroicStrike);
            Caster.SetUInt32(UpdateFields.UnitFieldMaxpower1 + 1, 1000);
            SpellSystem.SetPower(Caster, PowerType.Rage, 100);
            CombatEnvironment.Register(Kit.World, new CombatEnvironment(new CombatOptions(), null, new SpellSystemMeleeHooks(Kit.System)));
            Kit.World.RunTick(0);
            Procs.Apply(Victim, Redoubt);
            Session.Clear();
        }

        public TalentProcRig Procs { get; }

        public SpellTestKit Kit => Procs.Kit;

        public Player Caster { get; }

        public FakeSession Session { get; }

        public CombatTestUnit Victim { get; }

        public void Swing()
        {
            Assert.Equal(SpellCastResult.CastOk, Kit.System.HandleCastRequest(Caster, HeroicStrike, SpellCastTargets.ForUnit(Victim.Guid)));
            Caster.Map!.Combat.Attack(Caster, Victim);
            Session.Clear();
            Kit.World.RunTick(50);
        }

        public void Dispose() => Procs.Dispose();
    }

    private static byte[] ExpectedAttackerState(Rig rig, uint hitInfo, uint dealt, uint blocked)
    {
        var w = new PacketWriter(64);
        w.WriteUInt32(hitInfo);
        w.WritePackedGuid(rig.Caster.Guid.Value);
        w.WritePackedGuid(rig.Victim.Guid.Value);
        w.WriteUInt32(dealt);
        w.WriteByte(1);
        w.WriteUInt32(1);       // physical school mask
        w.WriteSingle(dealt);
        w.WriteUInt32(dealt);
        w.WriteUInt32(0);       // absorbed
        w.WriteInt32(0);        // resisted
        w.WriteUInt32((uint)VictimState.Normal); // VICTIMSTATE_NORMAL (1, vmangos SharedDefines.h)
        w.WriteUInt32(0);       // attacker state
        w.WriteUInt32(HeroicStrike);
        w.WriteUInt32(blocked);
        return w.ToArray();
    }

    private static byte[] ExpectedDamageLog(Rig rig, uint dealt, uint blocked)
    {
        var w = new PacketWriter(42);
        w.WritePackedGuid(rig.Victim.Guid.Value);
        w.WritePackedGuid(rig.Caster.Guid.Value);
        w.WriteUInt32(HeroicStrike);
        w.WriteUInt32(dealt);
        w.WriteByte((byte)SpellSchool.Normal); // physical school (the school index, 0; the attacker state above carries the mask)
        w.WriteUInt32(0);       // absorbed
        w.WriteUInt32(0);       // resisted
        w.WriteByte(0);         // periodic
        w.WriteByte(0);
        w.WriteUInt32(blocked);
        w.WriteUInt32(0);       // spell hit type
        w.WriteByte(0);
        return w.ToArray();
    }

    [Fact]
    public void ABlockedQueuedHeroicStrike_CarriesTheBlockedAmountAndTheBlockHitFlags()
    {
        using var rig = new Rig(blockRoll: 0.0);

        rig.Swing();

        // 100 damage, the level 60 creature's block value is 30: 70 lands.
        const uint hit = (uint)(HitInfo.NoAction | HitInfo.AffectsVictim | HitInfo.RolledBlock | HitInfo.Block);
        Assert.Equal(10_000u - 70u, rig.Victim.Health);
        Assert.Equal(ExpectedAttackerState(rig, hit, dealt: 70, blocked: 30), Assert.Single(Packets(rig.Session, WorldOpcode.SmsgAttackerstateupdate)));
        Assert.Equal(ExpectedDamageLog(rig, dealt: 70, blocked: 30), Assert.Single(Packets(rig.Session, WorldOpcode.SmsgSpellnonmeleedamagelog)));
    }

    [Fact]
    public void AnUnblockedQueuedHeroicStrike_KeepsTheUnblockedPacketValues()
    {
        using var rig = new Rig(blockRoll: 0.99);

        rig.Swing();

        const uint hit = (uint)(HitInfo.NoAction | HitInfo.AffectsVictim);
        Assert.Equal(ExpectedAttackerState(rig, hit, dealt: 100, blocked: 0), Assert.Single(Packets(rig.Session, WorldOpcode.SmsgAttackerstateupdate)));
        Assert.Equal(ExpectedDamageLog(rig, dealt: 100, blocked: 0), Assert.Single(Packets(rig.Session, WorldOpcode.SmsgSpellnonmeleedamagelog)));
        Assert.Empty(rig.Procs.Probes);
    }

    [Fact]
    public void ABlockedQueuedHeroicStrike_ProcsRedoubtOnTheVictim()
    {
        using var rig = new Rig(blockRoll: 0.0);

        rig.Swing();

        Assert.Equal(RedoubtProbe, Assert.Single(rig.Procs.Probes).SpellId);
    }
}
