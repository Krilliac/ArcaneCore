using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.ClassScripts;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Kernel.WorldData.Procs;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Deep Wounds talent rank chain (12834 -> 12849 -> 12867) and proc inheritance: Rank 2 (12849) and Rank 3 (12867) inherit
/// Rank 1 (12834)'s <c>spell_proc_event</c> (<c>ProcEx = CriticalHit</c>) through <see cref="SpellRankChains"/> built from
/// <see cref="TalentRecord"/>s, so <c>12721</c> (<c>Deep Wound</c>, a normal-hit harmful ability) does not recursively re-proc
/// Deep Wounds into <c>proc/recursion-cut</c>.
/// </summary>
public sealed class WarriorDeepWoundsTests
{
    public const uint DeepWoundsRank1 = 12834;
    public const uint DeepWoundsRank2 = 12849;
    public const uint DeepWoundsRank3 = 12867;
    public const uint DeepWoundsTriggerRank3 = 12868;
    public const uint DeepWoundDebuff = DeepWoundsScript.DeepWound; // 12721

    private static SkillLineAbilityRecord Ability(uint id, uint spell, uint forward = 0)
        => new(id, 26, spell, 0, 0, 0, forward, 0, 0, 0);

    private static TalentRecord DeepWoundsTalent()
        => new(121, 161, 2, 1, [DeepWoundsRank1, DeepWoundsRank2, DeepWoundsRank3, 0, 0], 0, 0, 0);

    [Fact]
    public void SpellRankChains_LinksMultiRankTalent_WhenSkillLineAbilityHasNoForwardLink()
    {
        SpellRankChains chains = new(
            [
                Ability(1, DeepWoundsRank1, forward: 0),
                Ability(2, DeepWoundsRank2, forward: 0),
                Ability(3, DeepWoundsRank3, forward: 0),
            ],
            [DeepWoundsTalent()]);

        Assert.Equal((byte)1, chains.Rank(DeepWoundsRank1));
        Assert.Equal((byte)2, chains.Rank(DeepWoundsRank2));
        Assert.Equal((byte)3, chains.Rank(DeepWoundsRank3));
        Assert.Equal(DeepWoundsRank1, chains.First(DeepWoundsRank1));
        Assert.Equal(DeepWoundsRank1, chains.First(DeepWoundsRank2));
        Assert.Equal(DeepWoundsRank1, chains.First(DeepWoundsRank3));
        Assert.Equal(0u, chains.Previous(DeepWoundsRank1));
        Assert.Equal(DeepWoundsRank1, chains.Previous(DeepWoundsRank2));
        Assert.Equal(DeepWoundsRank2, chains.Previous(DeepWoundsRank3));
        Assert.Equal(DeepWoundsRank2, chains.Next(DeepWoundsRank1));
        Assert.Equal(DeepWoundsRank3, chains.Next(DeepWoundsRank2));
        Assert.Equal(0u, chains.Next(DeepWoundsRank3));
        Assert.True(chains.IsHighRankOf(DeepWoundsRank3, DeepWoundsRank1));
        Assert.True(chains.IsHighRankOf(DeepWoundsRank2, DeepWoundsRank1));
    }

    [Fact]
    public void DeepWoundsRank2And3_InheritCriticalHitProcEx_AndDoNotRecursivelyProcDeepWound12721()
    {
        SpellRankChains chains = new(
            [
                Ability(1, DeepWoundsRank1, forward: 0),
                Ability(2, DeepWoundsRank2, forward: 0),
                Ability(3, DeepWoundsRank3, forward: 0),
            ],
            [DeepWoundsTalent()]);

        var procContent = new SpellProcEventContent(
        [
            new SpellProcEventRecord(
                Entry: DeepWoundsRank1,
                SchoolMask: 0,
                SpellFamilyName: 0,
                SpellFamilyMask0: 0,
                SpellFamilyMask1: 0,
                SpellFamilyMask2: 0,
                ProcFlags: 0,
                ProcEx: (uint)ProcFlagsEx.CriticalHit,
                PpmRate: 0f,
                CustomChance: 0f,
                Cooldown: 0),
        ]);

        IReadOnlyDictionary<uint, SpellProcEventRecord> resolved = procContent.Resolve(chains);
        Assert.True(resolved.TryGetValue(DeepWoundsRank2, out SpellProcEventRecord? rank2Proc));
        Assert.Equal((uint)ProcFlagsEx.CriticalHit, rank2Proc!.ProcEx);
        Assert.True(resolved.TryGetValue(DeepWoundsRank3, out SpellProcEventRecord? rank3Proc));
        Assert.Equal((uint)ProcFlagsEx.CriticalHit, rank3Proc!.ProcEx);

        // Spell.dbc for 12867: ProcFlags = 0x11154 (includes DealMeleeAttack | DealMeleeAbility | DealHarmfulAbility), ProcChance = 100.
        SpellInfo rank3Passive = Spell(DeepWoundsRank3,
            new SpellEffectInfo
            {
                Effect = SpellEffectName.ApplyAura,
                TargetA = SpellImplicitTarget.UnitCaster,
                AuraType = AuraType.ProcTriggerSpell,
                TriggerSpell = DeepWoundsTriggerRank3,
            }) with
        {
            Attributes = SpellAttributes.Passive,
            Duration = new SpellDuration(-1, 0, -1),
            ProcFlags = ProcFlags.DealMeleeSwing | ProcFlags.DealMeleeAbility | ProcFlags.DealHarmfulAbility | ProcFlags.DealHarmfulSpell,
            ProcChance = 100,
        };
        SpellInfo rank3Dummy = Spell(DeepWoundsTriggerRank3, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        // Spell.dbc for 12721 (Deep Wound): Physical DamageClass = Melee (2), PreventionType = Melee (2), so its hit fires DealHarmfulAbility + NormalHit.
        SpellInfo deepWound = Spell(DeepWoundDebuff,
            Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            Attributes = SpellAttributes.AuraIsDebuff,
            DamageClass = SpellDamageClass.Melee,
            PreventionType = SpellConstants.PreventionTypePacify,
            Duration = new SpellDuration(12_000, 0, 12_000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

        using var kit = new SpellTestKit(rank3Passive, rank3Dummy, deepWound);
        (Player warrior, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 3, 0);
        warrior.SetFloat(UpdateFields.UnitFieldMindamage, 100);
        warrior.SetFloat(UpdateFields.UnitFieldMaxdamage, 200);
        victim.MaxHealth = 10_000;
        victim.Health = 10_000;
        kit.System.Relations = new FakeRelations { Hostile = { victim.Guid } };
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));

        var procTable = new SpellProcEventTable(ranks: () => chains);
        procTable.Replace(procContent);
        kit.System.ProcEvents = procTable;

        int deepWoundCasts = 0;
        kit.System.SpellHitTarget += (_, _, spellId) =>
        {
            if (spellId == DeepWoundDebuff)
            {
                deepWoundCasts++;
            }
        };

        // Apply passive Deep Wounds Rank 3 (12867) in an earlier proc event so it is eligible on the next melee swing.
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(warrior, DeepWoundsRank3, SpellCastTargets.ForSelf(), triggered: true));

        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "proc/recursion-cut" && n.Subject == $"spell {DeepWoundDebuff}");

        kit.System.ProcDamageAndSpell(warrior, new ProcEvent
        {
            Victim = victim,
            AttackerFlags = ProcFlags.DealMeleeSwing,
            VictimFlags = ProcFlags.TakeMeleeSwing,
            Extra = ProcFlagsEx.CriticalHit,
            Amount = 150,
            OriginalAmount = 150,
        });

        Assert.Empty(capture.Notices);
        Assert.Equal(1, deepWoundCasts);
        Assert.True(kit.System.HasAura(victim, DeepWoundDebuff));
    }
}
