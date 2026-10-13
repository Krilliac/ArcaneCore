using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ArcaneCore.Data.Content.Spells;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotBudgetTests
{
    private static PlayerbotBudgetLine L(PlayerbotSpendKind kind, uint cost) => new(kind, cost);

    [Fact]
    public void Priority_EachKindMaySpendOnlyWhatTheHigherLinesLeave()
    {
        PlayerbotBudgetLine[] lines = [L(PlayerbotSpendKind.Repair, 50), L(PlayerbotSpendKind.ClassTraining, 300),
            L(PlayerbotSpendKind.Mount, 100), L(PlayerbotSpendKind.Riding, 1000)];
        const uint money = 1000;
        Assert.Equal(1000u, PlayerbotBudgetMath.Spendable(lines, PlayerbotSpendKind.Repair, money, 0));
        Assert.Equal(950u, PlayerbotBudgetMath.Spendable(lines, PlayerbotSpendKind.ClassTraining, money, 0));
        Assert.Equal(650u, PlayerbotBudgetMath.Spendable(lines, PlayerbotSpendKind.Mount, money, 0));
        Assert.Equal(550u, PlayerbotBudgetMath.Spendable(lines, PlayerbotSpendKind.Riding, money, 0));
        // Everything is reserved: nothing discretionary, while class training (above riding) is not held back by the mount saving.
        Assert.Equal(0u, PlayerbotBudgetMath.Spendable(lines, PlayerbotSpendKind.Discretionary, money, 0));
        Assert.True(PlayerbotBudgetMath.Spendable(lines, PlayerbotSpendKind.ClassTraining, 400, 0) >= 300);
    }

    [Fact]
    public void Essentials_KeepAnAllowanceButNeverMoreThanTheMoney()
    {
        PlayerbotBudgetLine[] lines = [L(PlayerbotSpendKind.Riding, 5000)];
        Assert.Equal(200u, PlayerbotBudgetMath.Spendable(lines, PlayerbotSpendKind.Essentials, 1000, 200));
        Assert.Equal(60u, PlayerbotBudgetMath.Spendable(lines, PlayerbotSpendKind.Essentials, 60, 200));
        Assert.Equal(1000u, PlayerbotBudgetMath.Spendable([], PlayerbotSpendKind.Essentials, 1000, 200));
        Assert.Equal(7000u, PlayerbotBudgetMath.Spendable(lines, PlayerbotSpendKind.Essentials, 12000, 200));
    }

    [Fact]
    public void Discretionary_IsWhatTheReservesLeave_ButNeverLessThanTheFloorFractionOfTheMoney()
    {
        PlayerbotBudgetLine[] lines = [L(PlayerbotSpendKind.ClassTraining, 800)];
        Assert.Equal(200u, PlayerbotBudgetMath.Spendable(lines, PlayerbotSpendKind.Discretionary, 1000, 0, 0.10f)); // the reserves leave more than the floor
        Assert.Equal(80u, PlayerbotBudgetMath.Spendable(lines, PlayerbotSpendKind.Discretionary, 800, 0, 0.10f));   // reserved to the last copper: the floor
        Assert.Equal(0u, PlayerbotBudgetMath.Spendable(lines, PlayerbotSpendKind.Discretionary, 800, 0, 0f));       // no floor: nothing
        Assert.Equal(100u, PlayerbotBudgetMath.Spendable([L(PlayerbotSpendKind.Riding, 9_100_000)], PlayerbotSpendKind.Discretionary, 1000, 0, 0.10f));
        Assert.Equal(1000u, PlayerbotBudgetMath.Spendable([L(PlayerbotSpendKind.Riding, 9_100_000)], PlayerbotSpendKind.Discretionary, 1000, 0, 5f)); // capped at the money
        Assert.Equal(0u, PlayerbotBudgetMath.Spendable(lines, PlayerbotSpendKind.Discretionary, 800, 0, float.NaN));
        // The floor is for optional buying only: class training is held to the real reserves.
        Assert.Equal(0u, PlayerbotBudgetMath.Spendable([L(PlayerbotSpendKind.Repair, 800)], PlayerbotSpendKind.ClassTraining, 800, 0, 0.5f));
    }

    [Fact]
    public void FloorFractionOption_DefaultsToTenPercent_AndIsValidatedZeroToOne()
    {
        Assert.Equal(0.10f, new PlayerbotBudgetOptions().DiscretionaryFloorFraction);
        new PlayerbotBudgetOptions { DiscretionaryFloorFraction = 0f }.Validate();
        new PlayerbotBudgetOptions { DiscretionaryFloorFraction = 1f }.Validate();
        Assert.Throws<InvalidOperationException>(() => new PlayerbotBudgetOptions { DiscretionaryFloorFraction = 1.01f }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotBudgetOptions { DiscretionaryFloorFraction = -0.1f }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotBudgetOptions { DiscretionaryFloorFraction = float.NaN }.Validate());
    }

    [Fact]
    public void Sums_SaturateInsteadOfWrapping()
    {
        Assert.Equal(uint.MaxValue, PlayerbotBudgetMath.Sum(uint.MaxValue - 5, 10));
        Assert.Equal(uint.MaxValue, PlayerbotBudgetMath.ReservedAbove([L(PlayerbotSpendKind.Repair, uint.MaxValue), L(PlayerbotSpendKind.Riding, 7)], PlayerbotSpendKind.Discretionary));
        Assert.Equal(0u, PlayerbotBudgetMath.Spendable([L(PlayerbotSpendKind.Repair, uint.MaxValue), L(PlayerbotSpendKind.Riding, 7)], PlayerbotSpendKind.Discretionary, 1000, 0));
    }

    private static TrainerSpell Row(uint spell, uint cost, uint level) => new() { Entry = FarTrainerFixture.Entry, Spell = spell, SpellCost = cost, ReqLevel = level };

    [Fact]
    public async Task ClassSaveAhead_ReservesTheSpellsDueWithinTheNextLevels_FromTheTrainerData()
    {
        var fixture = new FarTrainerFixture { Rows = [Row(944010, 100, 1), Row(944020, 200, 2), Row(944030, 400, 10), Row(944040, 800, 0)] };
        FarTrainerServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ISpellContentStore>(fixture));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    var options = new PlayerbotOptions { Enabled = true };
                    options.Budget.ClassLevelsAhead = 2;
                    var budget = new PlayerbotBudget(session, options);
                    player.Money = 1000;
                    budget.Refresh(player);
                    // 100 learnable now + 200 opened by level 2 (<= level + 2) + the spell with no reqlevel (its own level 1: 800); 400 needs level 10.
                    Assert.Equal(1100u, budget.ClassDue.Total);
                    Assert.Equal(3, budget.ClassDue.Count);
                    Assert.Equal(PlayerbotSpendKind.ClassTraining, budget.Lines[^1].Kind);
                    // Lower priorities see only what is left of the money; class training itself is not held back.
                    Assert.Equal(100u, budget.Spendable(PlayerbotSpendKind.Discretionary, player)); // fully reserved: only the 10% floor
                    Assert.Equal(1000u, budget.Spendable(PlayerbotSpendKind.ClassTraining, player));
                    player.Money = 5000;
                    Assert.Equal(3900u, budget.Spendable(PlayerbotSpendKind.Discretionary, player));
                    options.Budget.ClassLevelsAhead = 0;
                    budget.Invalidate();
                    budget.Refresh(player);
                    Assert.Equal(900u, budget.ClassDue.Total);
                    options.Budget.Enabled = false;
                    Assert.Equal(5000u, budget.Spendable(PlayerbotSpendKind.Discretionary, player));
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { FarTrainerServices.Current.Value = null; }
    }

    [Fact]
    public async Task ClassSaveAhead_LeavesOutAnOfferRedForAReasonNoLevelMends()
    {
        // Open to the bot's level (1) but asks a skill it lacks: Red for ever, so saving for it would never end.
        var fixture = new FarTrainerFixture
        {
            Rows = [Row(944010, 100, 1), new TrainerSpell { Entry = FarTrainerFixture.Entry, Spell = 944020, SpellCost = 5000, ReqLevel = 1, ReqSkill = 164, ReqSkillValue = 300 }],
        };
        FarTrainerServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ISpellContentStore>(fixture));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    var options = new PlayerbotOptions { Enabled = true };
                    var budget = new PlayerbotBudget(session, options);
                    player.Money = 1000;
                    budget.Refresh(player);
                    Assert.Equal(100u, budget.ClassDue.Total);
                    Assert.Equal(1, budget.ClassDue.Count);
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { FarTrainerServices.Current.Value = null; }
    }

    [Fact]
    public async Task ClassSaveAhead_IgnoresAClassTrainerOnAnotherMap()
    {
        var fixture = new FarTrainerFixture { Rows = [Row(944010, 100, 1)], SpawnMapId = 1 };
        FarTrainerServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ISpellContentStore>(fixture));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    var budget = new PlayerbotBudget(session, new PlayerbotOptions { Enabled = true });
                    player.Money = 1000;
                    budget.Refresh(player);
                    Assert.Equal(0, budget.ClassDue.Count);
                    Assert.Equal(1000u, budget.Spendable(PlayerbotSpendKind.Discretionary, player));
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { FarTrainerServices.Current.Value = null; }
    }

    [Fact]
    public void DiscretionaryFloor_NeverTakesTheBotBelowItsRepairCost()
    {
        PlayerbotBudgetLine[] lines = [L(PlayerbotSpendKind.Repair, 950), L(PlayerbotSpendKind.Riding, 9_000_000)];
        Assert.Equal(50u, PlayerbotBudgetMath.Spendable(lines, PlayerbotSpendKind.Discretionary, 1000, 0, 0.5f)); // 50% would be 500
        Assert.Equal(0u, PlayerbotBudgetMath.Spendable([L(PlayerbotSpendKind.Repair, 1000)], PlayerbotSpendKind.Discretionary, 1000, 0, 0.5f));
    }
}
