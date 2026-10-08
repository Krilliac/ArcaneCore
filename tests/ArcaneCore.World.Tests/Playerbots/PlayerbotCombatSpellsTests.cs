using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>Real world-thread coverage for the ordinary hostile spell seam.</summary>
public sealed class PlayerbotCombatSpellsTests
{
    [Fact]
    public async Task LearnedLoadedHostileSpell_UsesOrdinaryCastAndDamagesVisibleCreature()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature target = await AddTargetAsync(host, session, 7, 1);
            uint before = await host.World.InvokeAsync(() => target.Health);
            var helper = new PlayerbotCombatSpells(session);

            bool consumed = await host.World.InvokeAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(2);
                return helper.Update(session.Player!, target, 0);
            });

            Assert.True(consumed);
            await host.WaitForWorldAsync(() => target.Health < before, "ordinary hostile spell damage");
            Assert.Equal(before - 7, await host.World.InvokeAsync(() => target.Health));
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task OutOfRangeTarget_IsRejectedBeforePacketDispatch()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature target = await AddTargetAsync(host, session, 100, 2);
            uint before = await host.World.InvokeAsync(() => target.Health);
            var helper = new PlayerbotCombatSpells(session);

            bool consumed = await host.World.InvokeAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(2);
                return helper.Update(session.Player!, target, 0);
            });

            Assert.False(consumed);
            Assert.Equal(before, await host.World.InvokeAsync(() => target.Health));
            Assert.Empty(session.DrainManagedPackets(
                WorldOpcode.SmsgSpellStart,
                WorldOpcode.SmsgSpellGo));
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task ImmediateRepeatAfterCast_IsRefusedByOrdinaryCooldown()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature target = await AddTargetAsync(host, session, 7, 3);
            var helper = new PlayerbotCombatSpells(session);
            uint before = await host.World.InvokeAsync(() => target.Health);

            // The refusal is the 1.5 s global cooldown, started with the cast, so only its remainder is left
            // when the damage lands. Polling for the damage from the test thread could use that remainder up on
            // a loaded machine, so the repeat is issued from the world tick in which the damage landed.
            var repeat = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void RepeatOnceLanded(uint diffMs)
            {
                if (target.Health < before && !repeat.Task.IsCompleted)
                {
                    session.ManagedBudget = new ManagedActionBudget(2);
                    repeat.TrySetResult(helper.Update(session.Player!, target, 0));
                }
            }

            Assert.True(await host.World.InvokeAsync(() =>
            {
                host.World.Updated += RepeatOnceLanded;
                session.ManagedBudget = new ManagedActionBudget(2);
                return helper.Update(session.Player!, target, 0);
            }));
            bool repeated = await repeat.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await host.World.InvokeAsync(() =>
            {
                host.World.Updated -= RepeatOnceLanded;
                return true;
            });

            Assert.False(repeated);
            Assert.Equal(before - 7, await host.World.InvokeAsync(() => target.Health));
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task QueuedNextSwing_IsAcceptedOnce_AndLeavesWhiteAttackDecisionAvailable()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature target = await AddTargetAsync(host, session, 2, 4);
            uint nextSwing = await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                SpellInfo bolt = feature.System.Store.Get(SpellTestServices.Bolt)!;
                uint id = 8997;
                feature.System.Store = new SpellStore(feature.System.Store.All.Append(
                    bolt with { Id = id, Name = "Test Next Swing", Attributes = bolt.Attributes | SpellAttributes.OnNextSwing }), [], []);
                Assert.True(feature.Spellbook.LearnSpell(player, id));
                return id;
            });
            var helper = new PlayerbotCombatSpells(session);

            Assert.False(await host.World.InvokeAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(2);
                return helper.Update(session.Player!, target, 0);
            }));
            object queued = await host.World.InvokeAsync(() =>
                (object?)session.Services.GetRequiredService<SpellFeature>().System.GetState(session.Player!.Guid)?.MeleeCast
                ?? throw new InvalidOperationException("next-swing spell was not queued"));

            // The queued melee cast is left for the ordinary swing loop; the caller may
            // issue its normal white-attack decision instead of being suppressed.
            Assert.False(await host.World.InvokeAsync(() => helper.Update(session.Player!, target, 0)));
            Assert.Same(queued, await host.World.InvokeAsync(() =>
                (object?)session.Services.GetRequiredService<SpellFeature>().System.GetState(session.Player!.Guid)?.MeleeCast
                ?? throw new InvalidOperationException("next-swing spell was replaced")));

            await host.World.InvokeAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(session.TryManagedAction(WorldOpcode.CmsgAttackswing,
                    PlayerbotNavigation.GuidPayload(target.Guid.Value)));
                return true;
            });
            await host.WaitForWorldAsync(() => session.Services.GetRequiredService<SpellFeature>().System
                .GetState(session.Player!.Guid)?.MeleeCast is null, "next-swing consumption");
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task PowerRefusal_RotatesPastPassiveToViableSpellWithoutPowerGrant()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature target = await AddTargetAsync(host, session, 7, 5);
            uint before = await host.World.InvokeAsync(() => target.Health);
            uint powerBefore = await host.World.InvokeAsync(() =>
                session.Player!.GetUInt32(UpdateFields.UnitFieldPower1 + (int)session.Player.PowerType));
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                SpellInfo bolt = feature.System.Store.Get(SpellTestServices.Bolt)!;
                SpellInfo expensive = bolt with { Id = 8998, Name = "Too Expensive", ManaCost = 1000 };
                SpellInfo passive = bolt with { Id = 8999, Name = "Passive Hostile", Attributes = bolt.Attributes | SpellAttributes.Passive };
                feature.System.Store = new SpellStore(feature.System.Store.All.Append(expensive).Append(passive), [], []);
                Assert.True(feature.Spellbook.LearnSpell(player, expensive.Id));
                Assert.True(feature.Spellbook.LearnSpell(player, passive.Id));
                return true;
            });
            var helper = new PlayerbotCombatSpells(session);

            Assert.False(await host.World.InvokeAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(2);
                return helper.Update(session.Player!, target, 0);
            }));
            Assert.Equal(before, await host.World.InvokeAsync(() => target.Health));
            Assert.True(await host.World.InvokeAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(2);
                return helper.Update(session.Player!, target, 1000);
            }));
            await host.WaitForWorldAsync(() => target.Health < before, "fallback hostile spell damage");
            Assert.Equal(before - 7, await host.World.InvokeAsync(() => target.Health));
            Assert.Equal(powerBefore, await host.World.InvokeAsync(() =>
                session.Player!.GetUInt32(UpdateFields.UnitFieldPower1 + (int)session.Player.PowerType)));
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    private static async Task<Creature> AddTargetAsync(WorldTestHost host, WorldSession session, float xOffset, uint low)
    {
        Creature target = null!;
        await host.World.InvokeAsync(() =>
        {
            Player player = session.Player!;
            target = new Creature(low, new CreatureTemplate
            {
                Entry = 900020,
                Name = "spell target",
                CreatureType = 1,
                MinLevelHealth = 20,
                MaxLevelHealth = 20,
            }, null, new CreatureContent([], [], [], [], []), new Random(1));
            target.Relocate(player.X + xOffset, player.Y, player.Z, 0, host.World.NowMs);
            player.Map!.AddObject(target);
            return true;
        });
        await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(target.Guid), "spell target visibility");
        return target;
    }
}
