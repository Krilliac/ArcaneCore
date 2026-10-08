using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// The proc engine across two sessions (docs/areas/procs.md): two scripted bots duel; a damage shield hurts the bot that strikes its bearer
/// (vmangos Unit::TriggerDamageShields), and a reflected lethal spell leaves its caster at 1 health and loses the duel instead of killing it
/// (vmangos Unit::DealDamage, Unit.cpp:770-776: "Fixed bug where you could kill someone in a duel with spell reflection").
/// </summary>
public sealed class ProcScenarioTests
{
    [Fact]
    public async Task DamageShield_HurtsTheBotThatStrikesItsBearer()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new DamageShieldDuelScenario());
    }

    [Fact]
    public async Task ReflectedLethalSpell_InADuel_LeavesItsCasterAtOneHealth_AndLosesTheDuel()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new ReflectedLethalDuelScenario());
    }

    [Fact]
    public async Task Retaliation_StrikesBackTheWarriorThatHitsItsBearerFromTheFront_AndSpendsACharge()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new RetaliationDuelScenario());
    }
}

/// <summary>The synthetic spells of the proc scenarios (added to <see cref="ScenarioTestContent"/>'s spell store).</summary>
internal static class ProcScenarioContent
{
    public const uint Thorns = 991_101;
    public const int ThornsDamage = 25;
    public const uint Reflect = 991_102;
    public const uint Bolt = 991_103;

    /// <summary>Retaliation and its strike under their real ids: the talent proc script keys on 20230 (vmangos UnitAuraProcHandler.cpp:660-675).</summary>
    public const uint Retaliation = 20230;
    public const uint RetaliationStrike = 22858;
    public const uint RetaliationCharges = 30;
    public const int RetaliationDamage = 7;

    public static IReadOnlyList<SpellTemplateRow> Spells =>
    [
        // A self buff with SPELL_AURA_DAMAGE_SHIELD (15): 25 nature damage to each attacker that hits.
        new SpellTemplateRow
        {
            Id = Thorns, SpellName = "Scenario Thorns", School = 3, RangeIndex = 1, DurationIndex = 3, SpellVisual = 1,
            Effect1 = 6, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectBasePoints1 = ThornsDamage - 1, EffectImplicitTargetA1 = 1, EffectApplyAuraName1 = 15,
        },
        // A self buff with SPELL_AURA_REFLECT_SPELLS (28) at 100%.
        new SpellTemplateRow
        {
            Id = Reflect, SpellName = "Scenario Reflection", RangeIndex = 1, DurationIndex = 3, SpellVisual = 1,
            Effect1 = 6, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectBasePoints1 = 99, EffectImplicitTargetA1 = 1, EffectApplyAuraName1 = 28,
        },
        // A 500 frost damage magic bolt at an enemy (TARGET_UNIT_ENEMY 6), reflectable.
        new SpellTemplateRow
        {
            Id = Bolt, SpellName = "Scenario Bolt", School = 4, DmgClass = 1, RangeIndex = 4,
            Effect1 = 2, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectBasePoints1 = 499, EffectImplicitTargetA1 = 6,
        },
        // Retaliation: a warrior self buff whose DUMMY aura (4) procs on TAKE_MELEE_SWING (8), 30 charges.
        new SpellTemplateRow
        {
            Id = Retaliation, SpellName = "Retaliation", RangeIndex = 1, DurationIndex = 3, SpellVisual = 1, SpellFamilyName = 4,
            ProcFlags = 8, ProcChance = 100, ProcCharges = RetaliationCharges,
            Effect1 = 6, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectImplicitTargetA1 = 1, EffectApplyAuraName1 = 4,
        },
        // The retaliatory strike: physical damage at the attacker (TARGET_UNIT_ENEMY 6), always hits (SPELL_ATTR_EX3_ALWAYS_HIT).
        new SpellTemplateRow
        {
            Id = RetaliationStrike, SpellName = "Retaliation", RangeIndex = 4, DmgClass = 2, SpellFamilyName = 4,
            AttributesEx3 = (uint)SpellAttributesEx3Combat.AlwaysHit,
            Effect1 = 2, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectBasePoints1 = RetaliationDamage - 1, EffectImplicitTargetA1 = 6,
        },
    ];

    /// <summary>Learn and cast a self buff, then wait until the server holds the aura.</summary>
    public static Task BuffAsync(ScenarioContext context, ScenarioBot bot, uint spell, string what) => context.StepAsync(what, async () =>
    {
        await context.LearnSpellAsync(bot, spell);
        ScenarioContext.Expect(await bot.CastAsync(spell), $"{what}: cast refused");
        await context.WaitUntilAsync($"{bot.Name} holds the aura of {spell}",
            () => context.Services.GetRequiredService<SpellFeature>().System.HasAura(bot.RequirePlayer(), spell), TimeSpan.FromSeconds(5));
    });

    /// <summary>A challenges B, B accepts and the countdown runs out (the steps of the built-in duel scenario).</summary>
    public static async Task StartDuelAsync(ScenarioContext context, ScenarioBot a, ScenarioBot b)
    {
        await context.StepAsync("A knows Duel", () => context.LearnSpellAsync(a, ScenarioBot.DuelSpell));
        await context.StepAsync("both are healthy and out of combat", () => context.WaitUntilAsync("both out of combat",
            () => a.RequirePlayer() is { IsAlive: true } pa && !pa.Combat.IsInCombat
                && b.RequirePlayer() is { IsAlive: true } pb && !pb.Combat.IsInCombat, TimeSpan.FromSeconds(30)));
        DuelRequestedView request = await context.StepAsync("A challenges B", async () =>
        {
            long mark = b.Mark();
            ScenarioContext.Expect(await a.RequestDuelAsync(b.Guid), "duel cast refused");
            return await b.WaitForPacketAsync(WorldOpcode.SmsgDuelRequested, ScenarioDecoders.DuelRequested, since: mark);
        });
        await context.StepAsync("B accepts; the countdown runs out", async () =>
        {
            ScenarioContext.Expect(await b.AcceptDuelAsync(request.Arbiter), "duel accept refused");
            await context.WaitUntilAsync("the duel started", () => a.RequirePlayer().DuelTeam != 0 && b.RequirePlayer().DuelTeam != 0,
                TimeSpan.FromSeconds(10));
        });
    }
}

/// <summary>B wears a damage shield; A strikes B in a duel and takes the shield's damage, reported to A with SMSG_SPELLDAMAGESHIELD.</summary>
internal sealed class DamageShieldDuelScenario : IPlayerbotScenario
{
    public string Name => "proc-damage-shield";

    public string Description => "a damage shield hurts the bot that strikes its bearer";

    public async Task RunAsync(ScenarioContext context)
    {
        (ScenarioBot a, ScenarioBot b) = await PlayerbotScenarioCatalog.PairAsync(context);
        await ProcScenarioContent.BuffAsync(context, b, ProcScenarioContent.Thorns, "B puts on the damage shield");
        await ProcScenarioContent.StartDuelAsync(context, a, b);
        DamageShieldView shield = await context.StepAsync("A strikes B and the shield strikes back", async () =>
        {
            uint full = await a.ReadAsync(p => p.MaxHealth);
            await context.SetHealthAsync(a, full);
            long mark = a.Mark();
            ScenarioContext.Expect(await a.AttackAsync(b.Guid), "A attack refused");
            DamageShieldView view = await a.WaitForPacketAsync(WorldOpcode.SmsgSpelldamageshield, ScenarioProcDecoders.DamageShield,
                s => s.Attacker == a.Guid.Value, mark);
            ScenarioContext.Expect(await a.StopAttackAsync(), "A attack stop refused");
            return view;
        });
        await context.StepAsync("the packet and the server agree", async () =>
        {
            ScenarioContext.ExpectEqual(b.Guid.Value, shield.Victim, "shield bearer");
            ScenarioContext.ExpectEqual((uint)ProcScenarioContent.ThornsDamage, shield.Damage, "shield damage");
            ScenarioContext.ExpectEqual(3u, shield.School, "shield school (nature)");
            await context.ExpectAsync(a, "A lost health to the shield", p => p.Health <= p.MaxHealth - (uint)ProcScenarioContent.ThornsDamage);
        });
    }
}

/// <summary>
/// A reflects every spell; B, near death, casts a lethal bolt at A in a duel. The bolt comes back, B survives at 1 health and loses the duel;
/// A is untouched.
/// </summary>
internal sealed class ReflectedLethalDuelScenario : IPlayerbotScenario
{
    public string Name => "proc-reflect-duel";

    public string Description => "a reflected lethal spell ends the duel at 1 health instead of killing its caster";

    public async Task RunAsync(ScenarioContext context)
    {
        (ScenarioBot a, ScenarioBot b) = await PlayerbotScenarioCatalog.PairAsync(context);
        await ProcScenarioContent.BuffAsync(context, a, ProcScenarioContent.Reflect, "A raises the reflection");
        await context.StepAsync("B knows the bolt", () => context.LearnSpellAsync(b, ProcScenarioContent.Bolt));
        await ProcScenarioContent.StartDuelAsync(context, a, b);
        uint aHealth = await a.ReadAsync(p => p.Health);
        DuelWinnerView winner = await context.StepAsync("B bolts A; the bolt is reflected", async () =>
        {
            await context.SetHealthAsync(b, 40);
            long mark = b.Mark();
            ScenarioContext.Expect(await b.CastAsync(ProcScenarioContent.Bolt, a.Guid), "bolt cast refused");
            SpellGoView go = await b.WaitForPacketAsync(WorldOpcode.SmsgSpellGo, ScenarioDecoders.SpellGo,
                g => g.SpellId == ProcScenarioContent.Bolt, mark);
            ScenarioContext.Expect(go.Misses.Any(m => m.Guid == a.Guid.Value && m.Reason == (byte)SpellMissInfo.Reflect), "the bolt was not reflected");
            return await b.WaitForPacketAsync(WorldOpcode.SmsgDuelWinner, ScenarioDecoders.DuelWinner, since: mark);
        });
        await context.StepAsync("B survives at 1 health and lost; A is untouched", async () =>
        {
            ScenarioContext.ExpectEqual((byte)0, winner.Reason, "duel winner reason (0 = won)");
            ScenarioContext.Expect(winner.Winner.Equals(a.Name, StringComparison.OrdinalIgnoreCase), $"winner {winner.Winner} is not {a.Name}");
            await context.ExpectAsync(b, "B is alive at 1 health", p => p.IsAlive && p.Health == 1);
            await context.ExpectAsync(a, "A took nothing", p => p.Health == aHealth);
        });
    }
}

/// <summary>
/// B raises Retaliation; A, face to face, swings at B in a duel. Each swing that lands from the front makes B cast the retaliatory strike at A
/// (vmangos HandleDummyAuraProc, case 20230: in front, able to react), seen by A as B's SMSG_SPELL_GO of 22858 hitting A, and costs a charge.
/// </summary>
internal sealed class RetaliationDuelScenario : IPlayerbotScenario
{
    public string Name => "proc-retaliation";

    public string Description => "Retaliation strikes back the warrior that hits its bearer from the front";

    public async Task RunAsync(ScenarioContext context)
    {
        (ScenarioBot a, ScenarioBot b) = await PlayerbotScenarioCatalog.PairAsync(context);
        await ProcScenarioContent.BuffAsync(context, b, ProcScenarioContent.Retaliation, "B raises Retaliation");
        await ProcScenarioContent.StartDuelAsync(context, a, b);
        SpellGoView strike = await context.StepAsync("A swings at B and B strikes back", async () =>
        {
            long mark = a.Mark();
            ScenarioContext.Expect(await a.AttackAsync(b.Guid), "A attack refused");
            SpellGoView go = await a.WaitForPacketAsync(WorldOpcode.SmsgSpellGo, ScenarioDecoders.SpellGo,
                g => g.SpellId == ProcScenarioContent.RetaliationStrike && g.Caster == b.Guid.Value, mark);
            ScenarioContext.Expect(await a.StopAttackAsync(), "A attack stop refused");
            return go;
        });
        await context.StepAsync("the strike hit A and Retaliation lost a charge", async () =>
        {
            ScenarioContext.Expect(strike.Hits.Contains(a.Guid.Value), "the retaliatory strike did not hit A");
            int charges = await context.ReadAsync(() => context.Services.GetRequiredService<SpellFeature>().System
                .GetAuras(b.RequirePlayer()).Single(h => h.Spell.Id == ProcScenarioContent.Retaliation).Charges);
            ScenarioContext.Expect(charges < (int)ProcScenarioContent.RetaliationCharges, $"Retaliation still holds {charges} charges");
        });
    }
}
