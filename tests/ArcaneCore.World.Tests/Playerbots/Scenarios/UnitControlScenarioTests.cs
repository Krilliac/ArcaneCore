using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// Unit control across sessions (docs/areas/unit-control.md): a bot possesses its duel opponent and moves it with its own movement packets
/// while the opponent's client loses control (vmangos Unit::ModPossess, Player::SetClientControl, HandleMovementOpcodes with the confirmed
/// mover); a bot charms the scenario wolf, gets its bar and dismisses it, and the wolf turns on it (HandleModCharm, Unit::HandlePetCommand
/// COMMAND_DISMISS → Uncharm); a priest bot with Spirit of Redemption survives the wolf's killing blow as the spirit and dies when it ends.
/// </summary>
public sealed class UnitControlScenarioTests
{
    [Fact]
    public async Task Possess_TheController_MovesItsDuelOpponent_UntilThePossessionEnds()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new PossessScenario());
    }

    [Fact]
    public async Task Charm_TheWolfObeys_AndTurnsOnItsCharmerWhenDismissed()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new CharmScenario());
    }

    [Fact]
    public async Task SpiritOfRedemption_ThePriestSurvivesTheKillingBlow_AndDiesWhenTheSpiritEnds()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new SpiritOfRedemptionScenario());
    }
}

/// <summary>The synthetic spells of the unit-control scenarios (added to <see cref="ScenarioTestContent"/>'s spell store).</summary>
internal static class UnitControlScenarioContent
{
    public const uint Possess = 991_201;
    public const uint Charm = 991_202;
    public const uint Talent = 20711;
    public const uint Spirit = 27827;
    public const uint SpiritLinked1 = 27792;
    public const uint SpiritLinked2 = 27795;
    public const uint Suicide = 27965;
    public const uint Untransform = 25100;

    private const uint PassiveAttributes = 0x1C0; // PASSIVE | DO_NOT_DISPLAY | ... (classic-db 27792 / 27795)

    public static IReadOnlyList<SpellTemplateRow> Spells =>
    [
        // SPELL_AURA_MOD_POSSESS (2) on an enemy, up to level 60, 10 s.
        new SpellTemplateRow
        {
            Id = Possess, SpellName = "Scenario Mind Control", School = 5, RangeIndex = 4, DurationIndex = 4, SpellVisual = 1,
            Effect1 = 6, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectBasePoints1 = 59, EffectImplicitTargetA1 = 6, EffectApplyAuraName1 = 2,
        },
        // SPELL_AURA_MOD_CHARM (6) on an enemy, up to level 60, 10 minutes.
        new SpellTemplateRow
        {
            Id = Charm, SpellName = "Scenario Enslave", School = 5, RangeIndex = 4, DurationIndex = 3, SpellVisual = 1,
            Effect1 = 6, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectBasePoints1 = 59, EffectImplicitTargetA1 = 6, EffectApplyAuraName1 = 6,
        },
        // The classic-db Spirit of Redemption chain, reduced to what it needs.
        new SpellTemplateRow
        {
            Id = Talent, SpellName = "Spirit of Redemption", Attributes = 464, RangeIndex = 1,
            Effect1 = 6, EffectImplicitTargetA1 = 1, EffectApplyAuraName1 = 4,
        },
        new SpellTemplateRow
        {
            Id = Spirit, SpellName = "Spirit of Redemption", School = 1, RangeIndex = 1, DurationIndex = 4,
            Effect1 = 10, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectImplicitTargetA1 = 1,
            Effect2 = 6, EffectImplicitTargetA2 = 1, EffectApplyAuraName2 = 82,
            Effect3 = 6, EffectImplicitTargetA3 = 1, EffectApplyAuraName3 = 36, EffectMiscValue3 = 32,
        },
        new SpellTemplateRow
        {
            Id = SpiritLinked1, SpellName = "Spirit of Redemption", Attributes = PassiveAttributes, RangeIndex = 1,
            Effect1 = 6, EffectImplicitTargetA1 = 1, EffectApplyAuraName1 = 93,
            Effect2 = 6, EffectImplicitTargetA2 = 1, EffectApplyAuraName2 = 26,
        },
        new SpellTemplateRow
        {
            Id = SpiritLinked2, SpellName = "Spirit of Redemption", Attributes = PassiveAttributes, RangeIndex = 1,
            Effect1 = 6, EffectImplicitTargetA1 = 1, EffectApplyAuraName1 = 25,
            Effect2 = 6, EffectImplicitTargetA2 = 1, EffectApplyAuraName2 = 176,
            Effect3 = 6, EffectImplicitTargetA3 = 1, EffectApplyAuraName3 = 94,
        },
        new SpellTemplateRow { Id = Suicide, SpellName = "Suicide", RangeIndex = 1, Effect1 = 1, EffectImplicitTargetA1 = 1 },
        new SpellTemplateRow { Id = Untransform, SpellName = "Untransform Hero", RangeIndex = 1, Effect1 = 3, EffectImplicitTargetA1 = 1 },
    ];

    public static Task<bool> HasAuraAsync(ScenarioContext context, ScenarioBot bot, uint spell)
        => context.ReadAsync(() => context.Services.GetRequiredService<SpellFeature>().System.HasAura(bot.RequirePlayer(), spell));
}

/// <summary>A possesses B in a duel, moves B with its own movement packets while B's are ignored, and gives B back when the aura ends.</summary>
internal sealed class PossessScenario : IPlayerbotScenario
{
    public string Name => "control-possess";

    public string Description => "a bot possesses its duel opponent and moves it until the possession ends";

    public async Task RunAsync(ScenarioContext context)
    {
        (ScenarioBot a, ScenarioBot b) = await PlayerbotScenarioCatalog.PairAsync(context);
        await ProcScenarioContent.StartDuelAsync(context, a, b);
        await context.StepAsync("A knows the possession", () => context.LearnSpellAsync(a, UnitControlScenarioContent.Possess));

        await context.StepAsync("A possesses B: B loses control of itself, A gets it and B's bar", async () =>
        {
            long markA = a.Mark();
            long markB = b.Mark();
            ScenarioContext.Expect(await a.CastAsync(UnitControlScenarioContent.Possess, b.Guid), "possess cast refused");
            ClientControlView self = await b.WaitForPacketAsync(WorldOpcode.SmsgClientControlUpdate, ScenarioControlPackets.ClientControl,
                v => v.Unit == b.Guid.Value, markB);
            ScenarioContext.Expect(!self.AllowMove, "B may still move itself");
            await a.WaitForPacketAsync(WorldOpcode.SmsgClientControlUpdate, ScenarioControlPackets.ClientControl,
                v => v.Unit == b.Guid.Value && v.AllowMove, markA);
            PetBarView bar = await a.WaitForPacketAsync(WorldOpcode.SmsgPetSpells, ScenarioControlPackets.PetBar, v => v.Unit == b.Guid.Value, markA);
            ScenarioContext.ExpectEqual(0x07000002u, bar.Bar[0], "possess bar slot 0 (attack)");
            await context.ExpectAsync(b, "B is charmed by A", p => p.CharmerGuid == a.Guid);
            await context.ExpectAsync(a, "A charms B and looks through B", p => p.CharmGuid == b.Guid
                && p.GetUInt64(UpdateFields.PlayerFarsight) == b.Guid.Value);
        });

        (float x, float y, float z, float o) = await b.ReadAsync(p => (p.X, p.Y, p.Z, p.Orientation));
        float ax = await a.ReadAsync(p => p.X);
        await context.StepAsync("A moves B: the server moves B and B's client sees itself move", async () =>
        {
            ScenarioContext.Expect(await a.SendAsync(WorldOpcode.CmsgSetActiveMover, ScenarioControlPackets.SetActiveMover(b.Guid.Value)), "set mover refused");
            long markB = b.Mark();
            ScenarioContext.Expect(await a.SendAsync(WorldOpcode.MsgMoveHeartbeat, ScenarioControlPackets.Movement(x + 3f, y, z, o, 1000)), "move refused");
            await context.WaitUntilAsync("B stands 3 yards further", () => b.RequirePlayer() is { } p && MathF.Abs(p.X - (x + 3f)) < 0.01f);
            MoveView seen = await b.WaitForPacketAsync(WorldOpcode.MsgMoveHeartbeat, ScenarioControlPackets.Move, v => v.Mover == b.Guid.Value, markB);
            ScenarioContext.Expect(MathF.Abs(seen.X - (x + 3f)) < 0.01f, $"B's client saw itself at {seen.X}");
            await context.ExpectAsync(a, "A did not move", p => p.X == ax);
        });

        await context.StepAsync("B's own movement is ignored while possessed", async () =>
        {
            ScenarioContext.Expect(await b.SendAsync(WorldOpcode.MsgMoveHeartbeat, ScenarioControlPackets.Movement(x - 5f, y, z, o, 1200)), "move refused");
            await context.IdleAsync(TimeSpan.FromSeconds(1));
            await context.ExpectAsync(b, "B did not move itself", p => MathF.Abs(p.X - (x + 3f)) < 0.01f);
        });

        await context.StepAsync("the possession ends: B gets its control back, A its own and an empty bar", async () =>
        {
            long markA = a.Mark();
            long markB = b.Mark();
            await context.WaitUntilAsync("the possess aura expired",
                () => !context.Services.GetRequiredService<SpellFeature>().System.HasAura(b.RequirePlayer(), UnitControlScenarioContent.Possess),
                TimeSpan.FromSeconds(15));
            await b.WaitForPacketAsync(WorldOpcode.SmsgClientControlUpdate, ScenarioControlPackets.ClientControl,
                v => v.Unit == b.Guid.Value && v.AllowMove, markB);
            await a.WaitForPacketAsync(WorldOpcode.SmsgClientControlUpdate, ScenarioControlPackets.ClientControl,
                v => v.Unit == a.Guid.Value && v.AllowMove, markA);
            await a.WaitForPacketAsync(WorldOpcode.SmsgPetSpells, ScenarioControlPackets.PetBar, v => v.Unit == 0, markA);
            await context.ExpectAsync(b, "B is free", p => p.CharmerGuid.IsEmpty);
            await context.ExpectAsync(a, "A charms nothing and looks through its own eyes", p => p.CharmGuid.IsEmpty
                && p.GetUInt64(UpdateFields.PlayerFarsight) == 0);
        });

        await context.StepAsync("B moves itself again", async () =>
        {
            ScenarioContext.Expect(await b.SendAsync(WorldOpcode.MsgMoveHeartbeat, ScenarioControlPackets.Movement(x + 1f, y, z, o, 20_000)), "move refused");
            await context.WaitUntilAsync("B stands where it moved", () => b.RequirePlayer() is { } p && MathF.Abs(p.X - (x + 1f)) < 0.01f);
        });
    }
}

/// <summary>A charms the scenario wolf: it gets the wolf's bar, the wolf takes A's faction and follows; dismissed, the wolf turns on A.</summary>
internal sealed class CharmScenario : IPlayerbotScenario
{
    public string Name => "control-charm";

    public string Description => "a bot charms the wolf, then dismisses it and the wolf turns on it";

    public async Task RunAsync(ScenarioContext context)
    {
        ScenarioBot a = await context.StepAsync("login " + PlayerbotScenarioCatalog.BotA, () => context.LoginAsync(PlayerbotScenarioCatalog.BotA));
        await context.StepAsync("A stands near the wolf", () => context.PlaceAsync(a, 0, ScenarioTestContent.WolfX - 8f, ScenarioTestContent.WolfY, ScenarioTestContent.StartZ));
        await context.StepAsync("A knows the charm", () => context.LearnSpellAsync(a, UnitControlScenarioContent.Charm));
        ulong wolf = ScenarioTestContent.Wolf.Value;

        await context.StepAsync("A charms the wolf and gets its bar", async () =>
        {
            long mark = a.Mark();
            ScenarioContext.Expect(await a.CastAsync(UnitControlScenarioContent.Charm, ScenarioTestContent.Wolf), "charm cast refused");
            PetBarView bar = await a.WaitForPacketAsync(WorldOpcode.SmsgPetSpells, ScenarioControlPackets.PetBar, v => v.Unit == wolf, mark);
            ScenarioContext.ExpectEqual((byte)ReactState.Defensive, bar.React, "charm react state");
            ScenarioContext.ExpectEqual((byte)CommandState.Follow, bar.Command, "charm command state");
            uint faction = await a.ReadAsync(p => p.FactionTemplate);
            await context.WaitUntilAsync("the wolf is A's", () => Wolf(context) is { } w && w.CharmerGuid == a.Guid && w.FactionTemplate == faction
                && w.Motion.CurrentType == MovementGeneratorType.Follow);
        });

        await context.StepAsync("A dismisses the wolf: it is released and turns on A", async () =>
        {
            long mark = a.Mark();
            ScenarioContext.Expect(await a.SendAsync(WorldOpcode.CmsgPetAction,
                ScenarioControlPackets.PetAction(wolf, (uint)CommandState.Dismiss, (byte)ActionType.Command)), "dismiss refused");
            await a.WaitForPacketAsync(WorldOpcode.SmsgPetSpells, ScenarioControlPackets.PetBar, v => v.Unit == 0, mark);
            await context.WaitUntilAsync("the wolf is wild again and attacks A",
                () => Wolf(context) is { } w && w.CharmerGuid.IsEmpty && w.FactionTemplate == 14 && w.Combat.Victim?.Guid == a.Guid);
            await context.ExpectAsync(a, "A charms nothing", p => p.CharmGuid.IsEmpty);
        });
    }

    private static Creature? Wolf(ScenarioContext context)
        => context.World.GetMap(0).FindObject(ScenarioTestContent.Wolf) as Creature;
}

/// <summary>A priest with Spirit of Redemption at 1 health is killed by the wolf: it becomes the spirit, then dies when the spirit ends.</summary>
internal sealed class SpiritOfRedemptionScenario : IPlayerbotScenario
{
    public string Name => "spirit-of-redemption";

    public string Description => "a priest survives the killing blow as the spirit and dies when it ends";

    public async Task RunAsync(ScenarioContext context)
    {
        ScenarioBot a = await context.StepAsync("login " + PlayerbotScenarioCatalog.BotA, () => context.LoginAsync(PlayerbotScenarioCatalog.BotA));
        await context.StepAsync("A is a priest with the talent, next to the wolf", async () =>
        {
            await context.ReadAsync(() =>
            {
                a.RequirePlayer().SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Priest);
                return true;
            });
            await context.LearnSpellAsync(a, UnitControlScenarioContent.Talent);
            if (!await UnitControlScenarioContent.HasAuraAsync(context, a, UnitControlScenarioContent.Talent))
            {
                await context.ReadAsync(() => context.Services.GetRequiredService<SpellFeature>().System.CastSpell(
                    a.RequirePlayer(), UnitControlScenarioContent.Talent, SpellCastTargets.ForSelf(), triggered: true));
            }

            ScenarioContext.Expect(await UnitControlScenarioContent.HasAuraAsync(context, a, UnitControlScenarioContent.Talent), "no talent aura");
            await context.PlaceAsync(a, 0, ScenarioTestContent.WolfX - 1.5f, ScenarioTestContent.WolfY, ScenarioTestContent.StartZ);
        });

        await context.StepAsync("the wolf's killing blow makes A the spirit", async () =>
        {
            await context.SetHealthAsync(a, 1);
            ScenarioContext.Expect(await a.AttackAsync(ScenarioTestContent.Wolf), "attack refused");
            await context.WaitUntilAsync("A became the spirit",
                () => context.Services.GetRequiredService<SpellFeature>().System.HasAura(a.RequirePlayer(), UnitControlScenarioContent.Spirit),
                TimeSpan.FromSeconds(30));
            await context.ExpectAsync(a, "A is alive at full health in the spirit form",
                p => p.IsAlive && p.Health == p.MaxHealth && p.GetByte(UpdateFields.UnitFieldBytes1, 2) == 32);
            ScenarioContext.Expect(await UnitControlScenarioContent.HasAuraAsync(context, a, UnitControlScenarioContent.SpiritLinked2), "no spirit aura");
        });

        await context.StepAsync("the spirit ends and A dies", () => context.WaitUntilAsync("A is dead",
            () => a.RequirePlayer() is { IsAlive: false }, TimeSpan.FromSeconds(20)));
    }
}
