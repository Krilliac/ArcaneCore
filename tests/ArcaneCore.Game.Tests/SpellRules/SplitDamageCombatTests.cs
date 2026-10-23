using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>
/// The split part of a damage split (Blessing of Sacrifice, SPELL_AURA_SPLIT_DAMAGE_FLAT). vmangos HandleSplitDamage
/// (Unit.cpp:2086-2181) has the attacker deal it to the aura caster as DOT damage (Unit.cpp:2140, 2179), and
/// Unit::DealDamage's ShouldEnterCombat (Unit.cpp:696-717) links no combat for DOT damage that is not a channel: the split
/// alone puts neither the attacker nor the paladin in combat, and pulses no PvP flag. The log is the attacker's
/// SMSG_SPELLNONMELEEDAMAGELOG with SPELL_HIT_TYPE_SPLIT, periodic when the split hit was DOT (Unit.cpp:2138, 2176;
/// SpellCaster.cpp:1098-1114).
/// </summary>
public sealed class SplitDamageCombatTests
{
    private const uint Sacrifice = 947201;
    private const uint Bolt = 947202;
    private const uint FireDot = 947203;
    private const uint SplitHitInfo = 0x08; // SPELL_HIT_TYPE_SPLIT (vmangos SpellDefines.h:221)

    private static SpellTestKit Kit()
    {
        SpellInfo sacrifice = RuleTestKit.Grant(Sacrifice, AuraType.SplitDamageFlat, 40, (int)SpellSchoolMasks.All);
        SpellEffectInfo onTarget = sacrifice.Effects[0] with { TargetA = SpellImplicitTarget.Unit };
        var kit = new SpellTestKit(
            sacrifice with { Effects = [onTarget], RangeIndex = 4, Range = new SpellRange(0, 30) },
            RuleTestKit.Magic(Bolt, SpellSchool.Fire),
            RuleTestKit.Grant(FireDot, AuraType.PeriodicDamage, 20) with
            {
                School = SpellSchool.Fire,
                Attributes = SpellAttributes.AuraIsDebuff,
                Effects = [SpellTestKit.Effect(SpellEffectName.ApplyAura, 20, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)],
                Duration = new SpellDuration(30_000, 0, 30_000),
            });
        kit.System.Damage = new MapCombatDamageSink();
        return kit;
    }

    private static Player Add(SpellTestKit kit, uint guid, float x, out FakeSession session)
    {
        (Player player, session) = kit.AddPlayer(guid, x);
        player.Health = player.MaxHealth = 1000;
        return player;
    }

    private static void Bless(SpellTestKit kit, Player paladin, Player target)
    {
        kit.System.CastSpell(paladin, Sacrifice, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.True(kit.System.HasAura(target, Sacrifice));
        Assert.False(paladin.Combat.IsInCombat);
    }

    [Fact]
    public void AHostileHitOnASacrificedTarget_DoesNotPutThePaladinInCombatWithTheAttacker()
    {
        using SpellTestKit kit = Kit();
        Player paladin = Add(kit, 1, 0, out _);
        Player target = Add(kit, 2, 2, out _);
        Player attacker = Add(kit, 3, 4, out _);
        kit.System.Relations = new FakeRelations { Hostile = { attacker.Guid } };
        attacker.UnitFlags |= UnitFlags.Pvp; // a flagged aggressor pulses the PvP flag of whoever it puts in combat (Unit.cpp:5966-5980)
        Bless(kit, paladin, target);

        SpellDamageResult hit = kit.System.DealDirectDamage(attacker, target, kit.Store.Get(Bolt)!, 100, allowCrit: false);

        Assert.Equal((60u, 40u), (hit.Dealt, hit.Absorbed));
        Assert.Equal(960u, paladin.Health); // the split landed on the paladin
        Assert.True(attacker.Combat.IsInCombat); // the hit itself links the attacker and its target
        Assert.True(target.Combat.IsInCombat);
        Assert.False(paladin.Combat.IsInCombat);
        Assert.False(paladin.Combat.InPvpCombat);
        Assert.Equal(0u, (uint)(paladin.UnitFlags & UnitFlags.Pvp));
    }

    /// <summary>
    /// Lava on a player under a friendly paladin's sacrifice: Player::EnvironmentalDamage absorbs with the player as its own caster
    /// (vmangos Player.cpp:723-728), so the split is the player's DOT damage to the paladin. The environmental-mitigation lane's
    /// SpellEnvironmentalMitigation.Calculate makes exactly this AbsorbDamage call.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LavaOnASacrificedPlayer_PutsNeitherItNorThePaladinInCombatOrPvp(bool burningPlayerFlagged)
    {
        using SpellTestKit kit = Kit();
        Player paladin = Add(kit, 1, 0, out _);
        Player burning = Add(kit, 2, 2, out _);
        paladin.UnitFlags |= UnitFlags.Pvp;
        if (burningPlayerFlagged)
        {
            burning.UnitFlags |= UnitFlags.Pvp;
        }

        Bless(kit, paladin, burning);

        uint absorbed = kit.System.AbsorbDamage(burning, burning, SpellSchoolMasks.Of(SpellSchool.Fire), 100, spell: null);

        Assert.Equal(40u, absorbed);
        Assert.Equal(960u, paladin.Health);
        Assert.False(burning.Combat.IsInCombat);
        Assert.False(paladin.Combat.IsInCombat);
        Assert.False(burning.Combat.InPvpCombat);
        Assert.False(paladin.Combat.InPvpCombat);
        Assert.Equal(burningPlayerFlagged, (burning.UnitFlags & UnitFlags.Pvp) != 0);
        Assert.Equal(0u, burning.Combat.PvpFlagTimer); // no flag refresh either
        Assert.Equal(0u, (uint)(burning.Flags & PlayerFlags.ContestedPvp));
    }

    [Fact]
    public void TheSplitOfADirectHit_IsLoggedBySplitHitInfoFromTheAttacker()
    {
        using SpellTestKit kit = Kit();
        Player paladin = Add(kit, 1, 0, out _);
        Player target = Add(kit, 2, 2, out _);
        Player attacker = Add(kit, 3, 4, out FakeSession attackerSession);
        kit.System.Relations = new FakeRelations { Hostile = { attacker.Guid } };
        Bless(kit, paladin, target);
        attackerSession.Clear();

        kit.System.DealDirectDamage(attacker, target, kit.Store.Get(Bolt)!, 100, allowCrit: false);

        SplitLog log = Assert.Single(SplitLogs(attackerSession));
        Assert.Equal(new SplitLog(paladin.Guid.Value, attacker.Guid.Value, 40, Periodic: false, SplitHitInfo), log);
    }

    [Fact]
    public void TheSplitOfADotTick_IsLoggedAsPeriodic()
    {
        using SpellTestKit kit = Kit();
        Player paladin = Add(kit, 1, 0, out _);
        Player target = Add(kit, 2, 2, out _);
        Player attacker = Add(kit, 3, 4, out FakeSession attackerSession);
        kit.System.Relations = new FakeRelations { Hostile = { attacker.Guid } };
        Bless(kit, paladin, target);
        kit.System.CastSpell(attacker, FireDot, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        attackerSession.Clear();

        kit.Advance(3100);

        Assert.Equal(980u, paladin.Health); // the whole 20 of the tick went to the paladin
        SplitLog log = Assert.Single(SplitLogs(attackerSession));
        Assert.Equal(new SplitLog(paladin.Guid.Value, attacker.Guid.Value, 20, Periodic: true, SplitHitInfo), log);
    }

    private sealed record SplitLog(ulong Target, ulong Caster, uint Damage, bool Periodic, uint HitInfo);

    /// <summary>The SMSG_SPELLNONMELEEDAMAGELOG packets of the split spell (layout: <see cref="SpellPackets.BuildSpellNonMeleeDamageLog"/>).</summary>
    private static List<SplitLog> SplitLogs(FakeSession session)
    {
        List<SplitLog> logs = [];
        foreach (byte[] payload in SpellTestKit.Packets(session, WorldOpcode.SmsgSpellnonmeleedamagelog))
        {
            var reader = new PacketReader(payload);
            ulong target = reader.ReadPackedGuid();
            ulong caster = reader.ReadPackedGuid();
            if (reader.ReadUInt32() != Sacrifice)
            {
                continue;
            }

            uint damage = reader.ReadUInt32();
            reader.ReadByte(); // school
            reader.ReadUInt32(); // absorbed
            reader.ReadUInt32(); // resisted
            bool periodic = reader.ReadByte() != 0;
            reader.ReadByte(); // unused
            reader.ReadUInt32(); // blocked
            logs.Add(new SplitLog(target, caster, damage, periodic, reader.ReadUInt32()));
        }

        return logs;
    }
}
