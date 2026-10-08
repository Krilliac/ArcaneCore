using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// The combat stat auras across two sessions: in a duel, bot A curses bot B with +900% physical damage taken (SPELL_AURA_MOD_DAMAGE_PERCENT_TAKEN,
/// vmangos Unit::MeleeDamageBonusTaken) and B exposes itself to melee (+100 SPELL_AURA_MOD_ATTACKER_MELEE_HIT_CHANCE, SpellCaster::GetMeleeMissChance);
/// A then swings at B from behind. Every swing B sees in SMSG_ATTACKERSTATEUPDATE must land (no miss, and from behind a player cannot dodge, parry
/// or block) and carry the tenfold damage, far above anything A's own damage fields allow. Both spells are cast by the bots through
/// CMSG_CAST_SPELL; the server state (both auras) is asserted as well as the packets.
/// </summary>
public sealed class CombatStatScenarioTests
{
    [Fact]
    public async Task DamageTakenAndAttackerHitAuras_ShapeTheOpponentsWhiteSwings()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new CombatStatAurasScenario());
    }
}

/// <summary>Scenario "combat-stat-auras" (see <see cref="CombatStatScenarioTests"/>).</summary>
internal sealed class CombatStatAurasScenario : IPlayerbotScenario
{
    public const uint Vulnerability = 990_900;  // MOD_DAMAGE_PERCENT_TAKEN +900, physical, on an enemy
    public const uint Exposed = 990_901;        // MOD_ATTACKER_MELEE_HIT_CHANCE +100, on self
    private const int Swings = 3;

    public string Name => "combat-stat-auras";

    public string Description => "A curses B with damage taken, B exposes itself, A's white swings all land for tenfold damage";

    public async Task RunAsync(ScenarioContext context)
    {
        (ScenarioBot a, ScenarioBot b) = await context.StepAsync("login", async () =>
            (await context.LoginAsync(PlayerbotScenarioCatalog.BotA), await context.LoginAsync(PlayerbotScenarioCatalog.BotB)));
        await context.StepAsync("install the test spells; A knows Duel and the curse, B knows the exposure", async () =>
        {
            await context.ReadAsync(() =>
            {
                SpellSystem spells = context.Services.GetRequiredService<SpellFeature>().System;
                spells.Store = new SpellStore([.. spells.Store.All.Where(s => s.Id is not (Vulnerability or Exposed)), Curse(), Exposure()], [], []);
                return true;
            });
            await context.LearnSpellAsync(a, ScenarioBot.DuelSpell);
            await context.LearnSpellAsync(a, Vulnerability);
            await context.LearnSpellAsync(b, Exposed);
        });
        await context.StepAsync("both are healthy and out of combat", () => context.WaitUntilAsync("both out of combat",
            () => a.RequirePlayerForTests() is { IsAlive: true } pa && !pa.Combat.IsInCombat
                && b.RequirePlayerForTests() is { IsAlive: true } pb && !pb.Combat.IsInCombat, TimeSpan.FromSeconds(30)));
        await context.StepAsync("A challenges B, B accepts, the countdown runs out", async () =>
        {
            await context.PlaceFacingAsync(a, b);
            long mark = b.Mark();
            ScenarioContext.Expect(await a.RequestDuelAsync(b.Guid), "duel cast refused");
            DuelRequestedView request = await b.WaitForPacketAsync(WorldOpcode.SmsgDuelRequested, ScenarioDecoders.DuelRequested, since: mark);
            ScenarioContext.Expect(await b.AcceptDuelAsync(request.Arbiter), "duel accept refused");
            await context.WaitUntilAsync("the duel started", () => a.RequirePlayerForTests().DuelTeam != 0 && b.RequirePlayerForTests().DuelTeam != 0,
                TimeSpan.FromSeconds(10));
        });
        await context.StepAsync("A curses B and B exposes itself; the server holds both auras", async () =>
        {
            ScenarioContext.Expect(await a.CastAsync(Vulnerability, b.Guid), "curse cast refused");
            ScenarioContext.Expect(await b.CastAsync(Exposed), "exposure cast refused");
            await context.WaitUntilAsync("B has both auras", () =>
            {
                SpellSystem spells = context.Services.GetRequiredService<SpellFeature>().System;
                Player pb = b.RequirePlayerForTests();
                return spells.HasAura(pb, Vulnerability) && spells.HasAura(pb, Exposed);
            });
        });

        // B must last the swings: the harness raises its health; A stands behind B (no dodge, parry or block against a player from behind).
        float maxDamage = await a.ReadAsync(p => p.GetFloat(UpdateFields.UnitFieldMaxdamage));
        await context.StepAsync("A stands behind B with plenty of health to hit", async () =>
        {
            await context.ReadAsync(() =>
            {
                Player pb = b.RequirePlayerForTests();
                pb.MaxHealth = 100_000;
                pb.Health = 100_000;
                return true;
            });
            (float x, float y, float z) = await b.ReadAsync(p => (p.X, p.Y, p.Z));
            await context.PlaceAsync(b, 0, x, y, z, 0f);
            await context.PlaceAsync(a, 0, x - 2f, y, z, 0f);
        });
        await context.StepAsync($"A's first {Swings} white swings at B all land for tenfold damage", async () =>
        {
            long mark = a.Mark();
            ScenarioContext.Expect(await a.AttackAsync(b.Guid), "A attack refused");
            await context.WaitUntilAsync($"{Swings} swings", () => a.Received(WorldOpcode.SmsgAttackerstateupdate, ScenarioDecoders.AttackerState, mark)
                .Count(s => s.Attacker == a.Guid.Value && s.Victim == b.Guid.Value) >= Swings, TimeSpan.FromSeconds(30));
            IReadOnlyList<AttackerStateView> swings = [.. a.Received(WorldOpcode.SmsgAttackerstateupdate, ScenarioDecoders.AttackerState, mark)
                .Where(s => s.Attacker == a.Guid.Value && s.Victim == b.Guid.Value).Take(Swings)];
            foreach (AttackerStateView swing in swings)
            {
                ScenarioContext.Expect((swing.HitInfo & HitInfo.Miss) == 0, $"a swing missed ({swing.HitInfo})");
                ScenarioContext.ExpectEqual(VictimState.Normal, swing.VictimState, "victim state of a landed swing");
                ScenarioContext.Expect(swing.TotalDamage >= 5f * maxDamage,
                    $"swing dealt {swing.TotalDamage}, not the tenfold damage of A's {maxDamage} maximum (the damage taken aura was ignored)");
            }

            ScenarioContext.Expect(await a.StopAttackAsync(), "A stop attack refused");
        });
    }

    private static SpellInfo Curse() => new()
    {
        Id = Vulnerability,
        Name = "Scenario Vulnerability",
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        Duration = new SpellDuration(60_000, 0, 60_000),
        SpellVisual = 1,
        Attributes = SpellAttributes.AuraIsDebuff,
        Effects =
        [
            new SpellEffectInfo
            {
                Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModDamagePercentTaken, BasePoints = 899, BaseDice = 1, DieSides = 1, MiscValue = 1,
                TargetA = SpellImplicitTarget.UnitEnemy,
            },
            new(),
            new(),
        ],
    };

    private static SpellInfo Exposure() => new()
    {
        Id = Exposed,
        Name = "Scenario Exposure",
        RangeIndex = SpellConstants.RangeIndexSelfOnly,
        Duration = new SpellDuration(60_000, 0, 60_000),
        SpellVisual = 1,
        Effects =
        [
            new SpellEffectInfo
            {
                Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModAttackerMeleeHitChance, BasePoints = 99, BaseDice = 1, DieSides = 1,
                TargetA = SpellImplicitTarget.UnitCaster,
            },
            new(),
            new(),
        ],
    };
}
