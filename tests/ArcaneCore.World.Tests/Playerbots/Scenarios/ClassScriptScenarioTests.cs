using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.Game.Spells.Paladin;
using ArcaneCore.Game.Spells.PersistentAreaAuras;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// The class scripts across two sessions (docs/areas/class-scripts.md): two scripted bots duel. A puts on Seal of Righteousness and its white
/// swings make B take the seal's Holy damage; Judgement then turns the seal into Judgement of Righteousness on B and the seal is gone
/// (vmangos UnitAuraProcHandler.cpp:979-1053, SpellEffects.cpp:4502-4529). A's Consecration puts a ground object down that B's client is told
/// about, and B, standing in it, takes its ticks (vmangos Spell::EffectPersistentAA, DynamicObjectUpdater, Aura::PeriodicTick). A's Swiftmend
/// is refused at B without a heal over time, then consumes the Rejuvenation A put on B and heals B for four of its ticks, which B's client is told
/// (vmangos scripts/spells/spell_druid.cpp:101-160).
/// </summary>
public sealed class ClassScriptScenarioTests
{
    [Fact]
    public async Task SealOfRighteousness_AndJudgement_HitTheDuelOpponent()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new SealAndJudgementDuelScenario());
    }

    [Fact]
    public async Task Consecration_GroundObjectReachesTheOpponent_AndTicksOnIt()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new ConsecrationDuelScenario());
    }

    [Fact]
    public async Task Swiftmend_ConsumesTheRejuvenationOnTheOtherBot_AndItsClientSeesTheHeal()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new SwiftmendScenario());
    }
}

/// <summary>The build 5875 rows the class-script scenarios cast (ids, families, flags and effects as in spell_template; amounts kept small).</summary>
internal static class ClassScriptScenarioContent
{
    public const uint SealOfRighteousness = 21084;
    public const uint SealDamage = 25742;
    public const uint JudgementOfRighteousness = 20187;
    public const int JudgementDamage = 15;
    public const uint Consecration = 26573;
    public const int ConsecrationTick = 8;

    public const uint Rejuvenation = 774;
    public const int RejuvenationTick = 8;

    private const uint SealDurationIndex = 9;
    private const uint RejuvenationDurationIndex = 29;
    private const uint ConsecrationDurationIndex = 31;
    private const uint ConsecrationRadiusIndex = 14;

    public static SpellContent Extend(SpellContent content) => content with
    {
        Spells = [.. content.Spells, .. Spells],
        Durations = [.. content.Durations,
            new SpellDurationRow { Id = SealDurationIndex, Duration = 30_000, MaxDuration = 30_000 },
            new SpellDurationRow { Id = ConsecrationDurationIndex, Duration = 8_000, MaxDuration = 8_000 },
            new SpellDurationRow { Id = RejuvenationDurationIndex, Duration = 12_000, MaxDuration = 12_000 }],
        Radii = [.. content.Radii, new SpellRadiusRow { Id = ConsecrationRadiusIndex, Radius = 8, RadiusMax = 8 }],
    };

    private static IReadOnlyList<SpellTemplateRow> Spells =>
    [
        // Seal of Righteousness rank 1: a self DUMMY aura procced by DEAL_MELEE_SWING; effect 2's dummy names Judgement of Righteousness (20186 + 1).
        new SpellTemplateRow
        {
            Id = SealOfRighteousness, SpellName = "Seal of Righteousness", School = 1, RangeIndex = 1, DurationIndex = SealDurationIndex, SpellVisual = 1,
            SpellFamilyName = PaladinSpells.Family, SpellFamilyFlags = 0x08000000, ProcFlags = 4, ProcChance = 100, DmgClass = 1,
            Effect1 = 6, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectBasePoints1 = 999, EffectImplicitTargetA1 = 1, EffectApplyAuraName1 = 4,
            Effect3 = 6, EffectBaseDice3 = 1, EffectDieSides3 = 1, EffectBasePoints3 = (int)JudgementOfRighteousness - 1, EffectImplicitTargetA3 = 1,
            EffectApplyAuraName3 = 4,
        },
        // The seal's Holy damage (custom base points from the proc).
        new SpellTemplateRow
        {
            Id = SealDamage, SpellName = "Seal of Righteousness", School = 1, RangeIndex = 4, DmgClass = 1,
            AttributesEx3 = (uint)SpellAttributesEx3Combat.AlwaysHit,
            Effect1 = 2, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectImplicitTargetA1 = 6,
        },
        new SpellTemplateRow
        {
            Id = JudgementOfRighteousness, SpellName = "Judgement of Righteousness", School = 1, RangeIndex = 4, DmgClass = 1, BaseLevel = 1,
            AttributesEx3 = (uint)SpellAttributesEx3Combat.AlwaysHit,
            SpellFamilyName = PaladinSpells.Family, SpellFamilyFlags = 0x400,
            Effect1 = 2, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectBasePoints1 = JudgementDamage - 1, EffectImplicitTargetA1 = 6,
        },
        // Judgement: SCRIPT_EFFECT at an enemy, needs AURA_STATE_JUDGEMENT (5) on the caster.
        new SpellTemplateRow
        {
            Id = PaladinSpells.Judgement, SpellName = "Judgement", School = 1, RangeIndex = 4, CasterAuraState = 5,
            SpellFamilyName = PaladinSpells.Family, SpellFamilyFlags = 0x800000,
            Effect1 = 77, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectImplicitTargetA1 = 6,
        },
        // Consecration rank 1: PERSISTENT_AREA_AURA of PERIODIC_DAMAGE, 8 every second, 8 yards around the caster, 8 seconds.
        new SpellTemplateRow
        {
            Id = Consecration, SpellName = "Consecration", School = 1, RangeIndex = 1, DurationIndex = ConsecrationDurationIndex, SpellVisual = 1,
            SpellFamilyName = PaladinSpells.Family, SpellFamilyFlags = 0x20, DmgClass = 1,
            Effect1 = 27, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectBasePoints1 = ConsecrationTick - 1, EffectImplicitTargetA1 = 18,
            EffectImplicitTargetB1 = 16, EffectRadiusIndex1 = ConsecrationRadiusIndex, EffectApplyAuraName1 = 3, EffectAmplitude1 = 1000,
        },
        // Rejuvenation rank 1: PERIODIC_HEAL 8 every 3 s for 12 s at a friend, druid family flag 0x10 (no mana cost, no global cooldown here).
        new SpellTemplateRow
        {
            Id = Rejuvenation, SpellName = "Rejuvenation", School = 3, RangeIndex = 4, DurationIndex = RejuvenationDurationIndex, SpellVisual = 1,
            SpellFamilyName = SwiftmendScript.DruidFamily, SpellFamilyFlags = SwiftmendScript.RejuvenationFlag, DmgClass = 1,
            Effect1 = 6, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectBasePoints1 = RejuvenationTick - 1, EffectImplicitTargetA1 = 21,
            EffectApplyAuraName1 = 8, EffectAmplitude1 = 3000,
        },
        // Swiftmend: HEAL 1 at a friend, druid family.
        new SpellTemplateRow
        {
            Id = SwiftmendScript.Swiftmend, SpellName = "Swiftmend", School = 3, RangeIndex = 4, SpellVisual = 1,
            SpellFamilyName = SwiftmendScript.DruidFamily, SpellFamilyFlags = 0x200000000, DmgClass = 1,
            Effect1 = 10, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectBasePoints1 = 0, EffectImplicitTargetA1 = 21,
        },
    ];
}

/// <summary>
/// A's Swiftmend at B: refused while B has no heal over time (A's client gets TARGET_AURASTATE), then, after A's Rejuvenation on B, it consumes the
/// Rejuvenation and heals B for 1 plus four ticks of 8, which B's client sees in SMSG_SPELLHEALLOG.
/// </summary>
internal sealed class SwiftmendScenario : IPlayerbotScenario
{
    public string Name => "class-swiftmend";

    public string Description => "Swiftmend is refused without a heal over time, then consumes Rejuvenation and heals the other bot";

    public async Task RunAsync(ScenarioContext context)
    {
        (ScenarioBot a, ScenarioBot b) = await PlayerbotScenarioCatalog.PairAsync(context);
        await context.StepAsync("A knows Rejuvenation and Swiftmend", async () =>
        {
            await context.LearnSpellAsync(a, ClassScriptScenarioContent.Rejuvenation);
            await context.LearnSpellAsync(a, SwiftmendScript.Swiftmend);
        });
        SpellSystem spells = context.Services.GetRequiredService<SpellFeature>().System;

        await context.StepAsync("A's Swiftmend at B without a heal over time is refused", async () =>
        {
            long mark = a.Mark();
            ScenarioContext.Expect(await a.CastAsync(SwiftmendScript.Swiftmend, b.Guid), "Swiftmend not sent");
            byte reason = await a.WaitForPacketAsync(WorldOpcode.SmsgCastResult, CastFailure, r => r != 0, mark);
            ScenarioContext.ExpectEqual((byte)SpellCastResult.TargetAurastate, reason, "cast failure reason");
        });

        await context.StepAsync("A puts Rejuvenation on B", async () =>
        {
            await context.SetHealthAsync(b, 1);
            ScenarioContext.Expect(await a.CastAsync(ClassScriptScenarioContent.Rejuvenation, b.Guid), "Rejuvenation not sent");
            await context.WaitUntilAsync("B holds Rejuvenation", () => spells.HasAura(b.RequirePlayer(), ClassScriptScenarioContent.Rejuvenation));
        });

        (uint Amount, bool Critical) heal = await context.StepAsync("A's Swiftmend at B heals it", async () =>
        {
            long mark = b.Mark();
            ScenarioContext.Expect(await a.CastAsync(SwiftmendScript.Swiftmend, b.Guid), "Swiftmend not sent");
            return await b.WaitForPacketAsync(WorldOpcode.SmsgSpellheallog, payload => HealOf(payload, SwiftmendScript.Swiftmend), h => h.Amount != 0, mark);
        });
        await context.StepAsync("the heal is four Rejuvenation ticks and the Rejuvenation is gone", async () =>
        {
            uint expected = 1 + (4 * ClassScriptScenarioContent.RejuvenationTick);
            ScenarioContext.ExpectEqual(heal.Critical ? expected + (expected / 2) : expected, heal.Amount, "Swiftmend heal");
            // Read once, not waited for: the Rejuvenation has most of its 12 seconds left, so only the consumption can have removed it.
            bool holds = await context.ReadAsync(() => spells.HasAura(b.RequirePlayer(), ClassScriptScenarioContent.Rejuvenation));
            ScenarioContext.Expect(!holds, "B still holds Rejuvenation after Swiftmend");
        });
    }

    /// <summary>SMSG_CAST_RESULT: u32 spell, u8 status (2 = failure), u8 reason; 0 for another spell or a success.</summary>
    private static byte CastFailure(byte[] payload)
        => payload.Length >= 6 && BitConverter.ToUInt32(payload, 0) == SwiftmendScript.Swiftmend && payload[4] == 2 ? payload[5] : (byte)0;

    /// <summary>SMSG_SPELLHEALLOG: packed target, packed caster, u32 spell, u32 amount, u8 critical; (0, false) for another spell.</summary>
    private static (uint Amount, bool Critical) HealOf(byte[] payload, uint spellId)
    {
        var reader = new PacketReader(payload);
        _ = reader.ReadPackedGuid();
        _ = reader.ReadPackedGuid();
        uint spell = reader.ReadUInt32();
        uint amount = reader.ReadUInt32();
        bool critical = reader.ReadByte() != 0;
        return spell == spellId ? (amount, critical) : (0, false);
    }
}

/// <summary>A seals, strikes B in a duel until the seal procs, then judges B.</summary>
internal sealed class SealAndJudgementDuelScenario : IPlayerbotScenario
{
    public string Name => "class-seal-judgement";

    public string Description => "Seal of Righteousness procs on white swings and Judgement turns it into Judgement of Righteousness";

    public async Task RunAsync(ScenarioContext context)
    {
        (ScenarioBot a, ScenarioBot b) = await PlayerbotScenarioCatalog.PairAsync(context);
        await context.StepAsync("A knows Judgement", () => context.LearnSpellAsync(a, PaladinSpells.Judgement));
        await ProcScenarioContent.BuffAsync(context, a, ClassScriptScenarioContent.SealOfRighteousness, "A puts on Seal of Righteousness");
        await ProcScenarioContent.StartDuelAsync(context, a, b);
        SpellSystem spells = context.Services.GetRequiredService<SpellFeature>().System;

        SpellDamageView seal = await context.StepAsync("A strikes B until the seal procs", async () =>
        {
            uint full = await b.ReadAsync(p => p.MaxHealth);
            await context.SetHealthAsync(b, full);
            long mark = b.Mark();
            ScenarioContext.Expect(await a.AttackAsync(b.Guid), "A attack refused");
            SpellDamageView view = await b.WaitForPacketAsync(WorldOpcode.SmsgSpellnonmeleedamagelog, ScenarioClassDecoders.SpellDamage,
                d => d.SpellId == ClassScriptScenarioContent.SealDamage, mark);
            ScenarioContext.Expect(await a.StopAttackAsync(), "A attack stop refused");
            return view;
        });
        await context.StepAsync("the seal's damage came from A to B", () =>
        {
            ScenarioContext.ExpectEqual(b.Guid.Value, seal.Target, "seal damage target");
            ScenarioContext.ExpectEqual(a.Guid.Value, seal.Caster, "seal damage caster");
            ScenarioContext.Expect(seal.Damage + seal.Absorbed + seal.Resisted > 0, "the seal dealt nothing");
            return Task.CompletedTask;
        });

        SpellDamageView judgement = await context.StepAsync("A judges B", async () =>
        {
            await context.SetHealthAsync(b, await b.ReadAsync(p => p.MaxHealth));
            long mark = b.Mark();
            ScenarioContext.Expect(await a.CastAsync(PaladinSpells.Judgement, b.Guid), "Judgement refused");
            return await b.WaitForPacketAsync(WorldOpcode.SmsgSpellnonmeleedamagelog, ScenarioClassDecoders.SpellDamage,
                d => d.SpellId == ClassScriptScenarioContent.JudgementOfRighteousness, mark);
        });
        await context.StepAsync("Judgement of Righteousness hit B and the seal is gone", async () =>
        {
            ScenarioContext.ExpectEqual((uint)ClassScriptScenarioContent.JudgementDamage, judgement.Damage + judgement.Absorbed + judgement.Resisted, "judgement damage");
            await context.WaitUntilAsync("A no longer holds the seal",
                () => !spells.HasAura(a.RequirePlayer(), ClassScriptScenarioContent.SealOfRighteousness), TimeSpan.FromSeconds(5));
            await context.ExpectAsync(a, "A lost the Judgement aura state", p => (p.GetUInt32(Game.UpdateFields.UnitFieldAurastate) & PaladinAuraRules.JudgementStateBit) == 0);
        });
    }
}

/// <summary>A consecrates the ground under the duel; B is told about the ground object and takes its ticks.</summary>
internal sealed class ConsecrationDuelScenario : IPlayerbotScenario
{
    public string Name => "class-consecration";

    public string Description => "Consecration's ground object reaches the opponent's client and ticks on it";

    public async Task RunAsync(ScenarioContext context)
    {
        (ScenarioBot a, ScenarioBot b) = await PlayerbotScenarioCatalog.PairAsync(context);
        await context.StepAsync("A knows Consecration", () => context.LearnSpellAsync(a, ClassScriptScenarioContent.Consecration));
        await ProcScenarioContent.StartDuelAsync(context, a, b);
        SpellSystem spells = context.Services.GetRequiredService<SpellFeature>().System;

        PeriodicAuraView tick = await context.StepAsync("A consecrates; B takes a tick", async () =>
        {
            long mark = b.Mark();
            ScenarioContext.Expect(await a.CastAsync(ClassScriptScenarioContent.Consecration), "Consecration refused");
            return await b.WaitForPacketAsync(WorldOpcode.SmsgPeriodicauralog, ScenarioClassDecoders.PeriodicAura,
                p => p.SpellId == ClassScriptScenarioContent.Consecration && p.Target == b.Guid.Value, mark);
        });
        await context.StepAsync("the tick, the ground object and B's client agree", async () =>
        {
            ScenarioContext.ExpectEqual(a.Guid.Value, tick.Caster, "tick caster");
            ScenarioContext.ExpectEqual((uint)AuraType.PeriodicDamage, tick.AuraType, "tick aura type");
            ScenarioContext.ExpectEqual((uint)ClassScriptScenarioContent.ConsecrationTick, tick.Amount, "tick damage");
            DynamicObject? ground = await context.ReadAsync(() => spells.GetDynamicObjects(a.RequirePlayer()).FirstOrDefault());
            ScenarioContext.Expect(ground is not null, "A has no ground object");
            await context.ExpectAsync(b, "B's client was told about the ground object", p => p.VisibleObjects.Contains(ground!.Guid));
        });
    }
}
