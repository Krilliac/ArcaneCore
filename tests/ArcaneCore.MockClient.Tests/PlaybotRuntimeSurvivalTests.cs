using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Playbots;
using ArcaneCore.World.Items;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.MockClient.Tests;

public sealed class PlaybotRuntimeSurvivalTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DamagedNormalPlayerEatsOneOwnedStackThroughRealItemUseAndLogsOut()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(bound.Token);
        await server.AddAccountAsync("FOODBOT", "FOODBOTPASSWORD", bound.Token);
        ItemsFeature items = server.Services.GetRequiredService<ItemsFeature>();
        await items.EnsureLoadedAsync(bound.Token);
        await server.World.InvokeAsync(() =>
        {
            var store = (ItemTemplateStore)items.LoadedStore;
            items.ReplaceTemplates(new ItemTemplateStore([.. store.All, new ItemTemplate
            {
                Entry = 117, Class = (uint)ItemClass.Consumable, Stackable = 20,
                Spells = [new ItemSpell(433, 0, -1, 0, 0, 0, 0)],
            }], store.StartingItems(1, 1)));
            SpellFeature spells = server.Services.GetRequiredService<SpellFeature>();
            spells.System.Store = new SpellStore([.. spells.System.Store.All, new SpellInfo
            {
                Id = 433, Name = "Synthetic starter-food proof", Duration = new SpellDuration(10000, 0, 10000),
                Attributes = SpellAttributes.AllowWhileSitting,
                AuraInterruptFlags = SpellAuraInterruptFlags.StandingCancels,
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModRegen,
                    BasePoints = 99, BaseDice = 1, DieSides = 1, Amplitude = 1000, TargetA = SpellImplicitTarget.UnitCaster }],
            }], [], []);
            return true;
        });
        Item? food = null;
        var selector = new ArrangeOnce(async () => await server.World.InvokeAsync(() =>
        {
            // Fixture arrangement only: the client must observe damage and owned inventory before acting.
            Player player = server.World.FindOnlinePlayer("Boteater")!;
            player.MaxHealth = 100;
            player.Health = 20;
            player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 0);
            Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(117, 3, out food));
            return true;
        }));
        PlaybotRunReport report = await AutonomousPlaybot.RunAsync(new(server.RealmEndpoint, "FOODBOT", "FOODBOTPASSWORD",
            "Boteater", Seconds: 30, Steps: 4, Movement: false), selector, bound.Token);
        Assert.Equal("budget-complete", report.Outcome);
        Assert.True(report.LogoutComplete);
        PlaybotStep eating = Assert.Single(report.Steps, step => step.Action == "Eat");
        Assert.NotNull(eating.Food);
        Assert.Equal((uint)3, eating.Food.StackBefore);
        Assert.Equal((uint)2, eating.Food.StackAfter);
        Assert.True(eating.Food.SpellStarted && eating.Food.AuraObserved && eating.Food.HealthAfter > eating.Food.HealthBefore);
        Assert.True(report.Replies.GetValueOrDefault("SmsgSpellStart") > 0);
        Assert.NotNull(food);
        Assert.Equal((uint)2, food.Count);
    }

    [Fact]
    public async Task UnsolicitedNormalNpcAttackIsObservedAndAnsweredThroughClientMelee()
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(bound.Token);
        await server.AddAccountAsync("DEFENDBOT", "DEFENDBOTPASS", bound.Token);
        var selector = new ArrangeOnce(async () => await server.World.InvokeAsync(() =>
        {
            Player player = server.World.FindOnlinePlayer("Botdefender")!;
            var map = server.World.GetMap(0);
            var creature = (Creature)map.FindObject(new ObjectGuid(SyntheticArcaneServer.FirstTargetGuid))!;
            Assert.True(map.FindUpdater<CreatureMapSystem>()!.AttackStart(creature, player));
            return true;
        }));
        PlaybotRunReport report = await AutonomousPlaybot.RunAsync(new(server.RealmEndpoint, "DEFENDBOT", "DEFENDBOTPASS",
            "Botdefender", Seconds: 25, Steps: 10, AttackEntry: SyntheticArcaneServer.TargetEntry, Movement: false), selector, bound.Token);
        Assert.True(report.Outcome == "budget-complete", System.Text.Json.JsonSerializer.Serialize(report));
        Assert.True(report.LogoutComplete);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
        Assert.Contains(report.Steps, step => step.Action == "Attack" && step.Target == SyntheticArcaneServer.FirstTargetGuid
            && step.DecisionId.Contains(":defend-", StringComparison.Ordinal));
        Assert.True(report.Replies.GetValueOrDefault("SmsgAttackerstateupdate") > 0);
    }

    private sealed class ArrangeOnce(Func<Task<bool>> arrange) : IPlaybotSelector
    {
        private bool _arranged;
        public async Task<PlaybotSelection> SelectAsync(PlaybotDecisionContext context, CancellationToken cancellationToken)
        {
            if (!_arranged)
            {
                _arranged = true;
                await arrange();
            }
            return await new DeterministicPlaybotSelector().SelectAsync(context, cancellationToken);
        }
    }
}
