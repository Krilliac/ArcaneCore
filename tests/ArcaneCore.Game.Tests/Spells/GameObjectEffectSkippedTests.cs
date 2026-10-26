using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Application;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// <c>spell/gameobject-effect-skipped</c>: a cast aimed at a game object (OPEN_LOCK, implicit target GAMEOBJECT, so the caster carries the effect)
/// that loses its effect before any handler runs. One test per skip branch of the spell system (a pending quest settlement, an application rule
/// that zeroes the effect mask, an immune carrier), and negatives: a unit cast with no game object in the mask, and a unit target of a mixed cast.
/// Each spell id is used by one test so concurrent tests do not see each other's notices.
/// </summary>
public sealed class GameObjectEffectSkippedTests
{
    private const string Key = "spell/gameobject-effect-skipped";
    private const uint Chest = 990301;
    private const uint ImmuneToOpening = 7_301_900;
    private const uint ImmuneToDamage = 7_301_901;

    private sealed class ZeroMaskRule : ISpellApplicationRule
    {
        public void Begin(SpellApplication application) => application.EffectMask = 0;

        public bool AcceptHolder(SpellApplication application, SpellAuraHolder holder) => true;
    }

    /// <summary>Starts a quest settlement for the caster when the first target is applied, so the next target (the carrier) finds it pending.</summary>
    private sealed class SettleCasterRule : ISpellApplicationRule
    {
        public void Begin(SpellApplication application)
        {
            if (application.Cast.Caster is Player player)
            {
                player.BeginQuestSettlement(Guid.NewGuid());
            }
        }

        public bool AcceptHolder(SpellApplication application, SpellAuraHolder holder) => true;
    }

    private sealed class Rig : IDisposable
    {
        public Rig(params SpellInfo[] spells)
        {
            Kit = new SpellTestKit([RuleTestKit.Grant(ImmuneToOpening, AuraType.EffectImmunity, 0, (int)SpellEffectName.OpenLock)
                    with { AttributesEx = (SpellAttributesEx)SpellRuleFlags.ExImmunityToHostileAndFriendly }, // the opening spell is positive
                RuleTestKit.Grant(ImmuneToDamage, AuraType.EffectImmunity, 0, (int)SpellEffectName.SchoolDamage), .. spells]);
            var map = Kit.World.GetMap(0);
            Objects = new GameObjectMapSystem(map, new GameObjectContent([GameObjectTestKit.GoTemplate(Chest, GameObjectType.Chest)], [], [], [], []));
            map.AddUpdater(Objects);
            (Caster, _) = Kit.AddPlayer(1);
            (Other, _) = Kit.AddPlayer(2, 5);
            Object = Objects.Summon(Chest, 2, 0, Caster.Z, 0)!;
        }

        public SpellTestKit Kit { get; }
        public GameObjectMapSystem Objects { get; }
        public Player Caster { get; }
        public Player Other { get; }
        public GameObject Object { get; }

        public SpellCastTargets AtObject() => new() { Mask = SpellCastTargetFlags.GameObject, GameObject = Object.Guid };

        public void Dispose() => Kit.Dispose();
    }

    private static SpellInfo OpenLock(uint id) => Spell(id, Effect(SpellEffectName.OpenLock, 0, SpellImplicitTarget.GameObject, misc: 12)) with
    {
        RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };

    private static AnomalyCapture CaptureFor(uint spell)
        => AnomalyLedger.Shared.Capture(n => n.Key == Key && n.Subject == $"spell {spell}");

    [Fact]
    public void PendingQuestSettlementOfTheCaster_ReportsTheSkippedObjectEffectOnce()
    {
        // CheckCast refuses a caster that is already pending, so the settlement starts during the cast: while the first target (a unit) is applied.
        const uint spell = 7_301_001;
        SpellInfo mixed = Spell(spell,
            Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy),
            Effect(SpellEffectName.OpenLock, 0, SpellImplicitTarget.GameObject, misc: 12)) with
        {
            School = SpellSchool.Fire, RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0,
        };
        using var rig = new Rig(mixed);
        var relations = new FakeRelations();
        relations.Hostile.Add(rig.Other.Guid);
        rig.Kit.System.Relations = relations;
        rig.Kit.System.ApplicationRules.Add(new SettleCasterRule());
        SpellCastTargets targets = SpellCastTargets.ForUnit(rig.Other.Guid);
        targets.Mask |= SpellCastTargetFlags.GameObject;
        targets.GameObject = rig.Object.Guid;
        using AnomalyCapture cap = CaptureFor(spell);

        rig.Kit.System.CastSpell(rig.Caster, spell, targets, triggered: true);

        Assert.True(rig.Caster.IsQuestSettlementPending);
        AnomalyNotice notice = Assert.Single(cap.Notices);
        Assert.Contains("a quest settlement is pending", notice.Detail);
        Assert.Contains($"object guid {rig.Object.Guid.Low}", notice.Detail);
        Assert.Contains($"caster guid {rig.Caster.Guid.Low}", notice.Detail);
    }

    [Fact]
    public void ApplicationRuleThatZeroesTheMask_ReportsTheSkippedObjectEffectOnce()
    {
        const uint spell = 7_301_002;
        using var rig = new Rig(OpenLock(spell));
        rig.Kit.System.ApplicationRules.Add(new ZeroMaskRule());
        using AnomalyCapture cap = CaptureFor(spell);

        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Caster, spell, rig.AtObject(), triggered: true));

        Assert.Contains("the application rules removed every effect", Assert.Single(cap.Notices).Detail);
    }

    [Fact]
    public void ImmuneCarrier_ReportsTheSkippedObjectEffectOnce()
    {
        const uint spell = 7_301_003;
        using var rig = new Rig(OpenLock(spell));
        RuleTestKit.Apply(rig.Kit, rig.Caster, ImmuneToOpening);
        using AnomalyCapture cap = CaptureFor(spell);

        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Caster, spell, rig.AtObject(), triggered: true));

        Assert.Contains("the carrier missed as immune", Assert.Single(cap.Notices).Detail);
    }

    [Fact]
    public void NormalObjectCast_ReportsNothing()
    {
        const uint spell = 7_301_004;
        using var rig = new Rig(OpenLock(spell));
        using AnomalyCapture cap = CaptureFor(spell);

        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Caster, spell, rig.AtObject(), triggered: true));

        Assert.Empty(cap.Notices);
    }

    [Fact]
    public void ImmuneUnitTargetWithNoObjectInTheMask_ReportsNothing()
    {
        const uint spell = 7_301_005;
        SpellInfo damage = Spell(spell, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            School = SpellSchool.Fire, RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0,
        };
        using var rig = new Rig(damage);
        var relations = new FakeRelations();
        relations.Hostile.Add(rig.Other.Guid);
        rig.Kit.System.Relations = relations;
        RuleTestKit.Apply(rig.Kit, rig.Other, ImmuneToDamage);
        using AnomalyCapture cap = CaptureFor(spell);

        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Caster, spell, SpellCastTargets.ForUnit(rig.Other.Guid), triggered: true));

        Assert.Empty(cap.Notices);
    }

    [Fact]
    public void ImmuneUnitTargetOfAMixedCast_IsNotTheCarrier_AndReportsNothing()
    {
        const uint spell = 7_301_006;
        SpellInfo mixed = Spell(spell,
            Effect(SpellEffectName.OpenLock, 0, SpellImplicitTarget.GameObject, misc: 12),
            Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)) with
        {
            School = SpellSchool.Fire, RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0,
        };
        using var rig = new Rig(mixed);
        var relations = new FakeRelations();
        relations.Hostile.Add(rig.Other.Guid);
        rig.Kit.System.Relations = relations;
        RuleTestKit.Apply(rig.Kit, rig.Other, ImmuneToDamage);
        SpellCastTargets targets = SpellCastTargets.ForUnit(rig.Other.Guid);
        targets.Mask |= SpellCastTargetFlags.GameObject;
        targets.GameObject = rig.Object.Guid;
        using AnomalyCapture cap = CaptureFor(spell);

        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.CastSpell(rig.Caster, spell, targets, triggered: true));

        Assert.Empty(cap.Notices);
    }
}
