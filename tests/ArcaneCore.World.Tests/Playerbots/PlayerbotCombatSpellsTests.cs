using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>Real world-thread coverage for the ordinary hostile spell seam.</summary>
public sealed class PlayerbotCombatSpellsTests
{
    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    [Fact]
    public async Task ManagedHostileCast_UsesManualWorldTimeForDamage()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<IWorldFeature, ManualClockFeature>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature target = await AddTargetAsync(host, session, 7, 50);
            uint before = await host.World.InvokeAsync(() => target.Health);
            var helper = new PlayerbotCombatSpells(session);
            Assert.True(await host.World.InvokeAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(2);
                return helper.Update(session.Player!, target, 0);
            }));

            await host.World.AdvanceClockAsync(450);
            Assert.Equal(before, await host.World.InvokeAsync(() => target.Health));
            await host.World.AdvanceClockAsync(50);
            Assert.Equal(before - 7, await host.World.InvokeAsync(() => target.Health));
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    /// <summary>A caster running at its target stops and casts in one think: the cast waited for the next think, seconds later on a crowded server, before.</summary>
    [Fact]
    public async Task AMovingCaster_StopsAndCastsInTheSameThink()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature target = await AddTargetAsync(host, session, 7, 3);
            uint before = await host.World.InvokeAsync(() => target.Health);
            var helper = new PlayerbotCombatSpells(session);
            Assert.True(await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                var options = new PlayerbotOptions { MaxPathPoints = 8, MaxRouteYards = 400 };
                Assert.True(PlayerbotNavigation.TryPlan(player, new(player.X + 100, player.Y, player.Z), options, out PlayerbotRoute? route));
                session.ManagedBudget = new ManagedActionBudget(4);
                Assert.True(PlayerbotNavigation.TryAdvance(session, route!, options, 500, host.World.NowMs));
                Assert.True(player.Movement.HasFlag(MovementFlags.MaskMoving));

                session.ManagedBudget = new ManagedActionBudget(2);
                bool consumed = helper.Update(player, target, 0);
                Assert.False(player.Movement.HasFlag(MovementFlags.MaskMoving));
                return consumed;
            }));
            await host.WaitForWorldAsync(() => target.Health < before, "the cast of the think that stopped the bot");
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

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

    /// <summary>
    /// vmangos PartyBotAI::OnPacketReceived: SMSG_LEARNED_SPELL marks the spell data stale, so a newly learned higher rank is used
    /// from the next decision on and a newly learned talent spell changes the role (AutoAssignRole).
    /// </summary>
    [Fact]
    public async Task SpellData_IsRebuiltFromTheBook_WhenTheServerAnnouncesALearnedSpell()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                SpellInfo bolt = feature.System.Store.Get(SpellTestServices.Bolt)!;
                SpellInfo rend1 = bolt with { Id = 8981, Name = "Rend", Rank = "Rank 1" };
                SpellInfo rend2 = bolt with { Id = 8982, Name = "Rend", Rank = "Rank 2" };
                SpellInfo shieldSlam = bolt with { Id = World.Playerbots.Combat.PlayerbotRoles.ShieldSlam, Name = "Shield Slam", Rank = "Rank 1" };
                feature.System.Store = new SpellStore(feature.System.Store.All.Append(rend1).Append(rend2).Append(shieldSlam), [], []);
                Assert.True(feature.System.LearnSpell(player, rend1.Id));

                var helper = new PlayerbotCombatSpells(session);
                Assert.Equal(rend1.Id, helper.Abilities(player)["Rend"]!.Id);
                Assert.Equal(World.Playerbots.Combat.PlayerbotRole.MeleeDps, helper.RoleOf(player));

                Assert.True(feature.System.LearnSpell(player, rend2.Id)); // SMSG_LEARNED_SPELL
                Assert.Equal(rend2.Id, helper.Abilities(player)["Rend"]!.Id);
                Assert.True(feature.System.LearnSpell(player, shieldSlam.Id));
                Assert.Equal(World.Playerbots.Combat.PlayerbotRole.Tank, helper.RoleOf(player));
                return true;
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    /// <summary>
    /// The out-of-combat upkeep takes a warrior's stance through the ordinary CMSG_CAST_SPELL handler (the aura is on the player),
    /// then has nothing left to do; a warrior fights in melee.
    /// </summary>
    [Fact]
    public async Task OutOfCombatUpkeep_TakesTheStance_ThroughTheOrdinaryCastHandler()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            uint stance = await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                var battleStance = new SpellInfo
                {
                    Id = 8983, Name = "Battle Stance", RangeIndex = SpellConstants.RangeIndexSelfOnly, Duration = new SpellDuration(-1, 0, -1),
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModShapeshift, MiscValue = 17,
                        TargetA = SpellImplicitTarget.UnitCaster }],
                };
                feature.System.Store = new SpellStore(feature.System.Store.All.Append(battleStance), [], []);
                Assert.True(feature.Spellbook.LearnSpell(player, battleStance.Id));
                return battleStance.Id;
            });
            var helper = new PlayerbotCombatSpells(session);
            Assert.True(await host.World.InvokeAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(2);
                return helper.UpdateOutOfCombat(session.Player!, 0);
            }));
            await host.WaitForWorldAsync(() => session.Services.GetRequiredService<SpellFeature>().System.HasAura(session.Player!, stance),
                "Battle Stance aura");
            Assert.Equal(ShapeshiftForm.BattleStance, await host.World.InvokeAsync(() => ShapeshiftService.GetForm(session.Player!)));
            Assert.False(await host.World.InvokeAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(2);
                return helper.UpdateOutOfCombat(session.Player!, 0); // nothing left to do
            }));
            Assert.Equal(World.Playerbots.Combat.PlayerbotClassRotation.MeleeRange, await host.World.InvokeAsync(() => helper.PreferredRange(session.Player!)));
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    /// <summary>
    /// A standing warlock with a cast ready and an idle demon out: one think produces the cast and the CMSG_PET_ACTION attack, and the
    /// demon takes the target. (The CI flake "the Imp never attacked the wolf": the cast won every think, so the command never went.)
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStandingWarlock_CastsAndSendsItsIdleDemon_InOneThink(bool outOfCombatPull)
    {
        var template = new CreatureTemplate { Entry = Pets.PetTestServices.Entry, Name = "Test Imp", MinLevel = 1, MaxLevel = 1,
            DisplayIds = [903], MinLevelHealth = Pets.PetTestServices.Health, MaxLevelHealth = Pets.PetTestServices.Health, Faction = 35 };
        Creatures.CreatureTestStore.Current.Value = new Creatures.CreatureTestContext(new CreatureContent([template], [], [], [], []));
        WorldTestHost host;
        try { host = WorldTestHost.Start(); }
        finally { Creatures.CreatureTestStore.Current.Value = null; }

        await using (host)
        {
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature target = await AddTargetAsync(host, session, 7, 31 + (outOfCombatPull ? 1u : 0u));
            uint before = await host.World.InvokeAsync(() => target.Health);
            Creature pet = await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter); // only a hunter restores a cached pet; it turns warlock below
                SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                SpellInfo bolt = feature.System.Store.Get(SpellTestServices.Bolt)!;
                feature.System.Store = new SpellStore(feature.System.Store.All.Append(bolt with { Id = 9101, Name = "Shadow Bolt", Rank = "Rank 1" }), [], []);
                Assert.True(feature.Spellbook.LearnSpell(player, 9101));
                var snapshot = new ArcaneCore.Kernel.Characters.Pets.PersistentPetSnapshot((int)player.Guid.Low, 9102, Pets.PetTestServices.Entry, 1, 0,
                    50, 30, 0, 1, [], [], Name: "Imp", NameTimestamp: 1, RenameAllowed: false);
                Creature restored = session.Services.GetRequiredService<ArcaneCore.World.Pets.PetsFeature>().Service.RestoreCurrentPet(player, snapshot)!;
                Assert.Null(restored.Combat.Victim);
                player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
                return restored;
            });

            var helper = new PlayerbotCombatSpells(session);
            var sent = new List<WorldOpcode>();
            bool consumed = await host.World.InvokeAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(4);
                session.ManagedDispatchObserver = sent.Add;
                if (!outOfCombatPull) return helper.Update(session.Player!, target, 0);
                helper.PullTarget = target;
                return helper.UpdateOutOfCombat(session.Player!, 0);
            });

            Assert.Contains(WorldOpcode.CmsgPetAction, sent);
            Assert.Same(target, await host.World.InvokeAsync(() => pet.Combat.Victim));
            if (outOfCombatPull)
            {
                // Upkeep yields to the pull cast (the brain's combat think casts it next), so only the demon is sent here.
                Assert.False(consumed);
                return;
            }

            Assert.True(consumed);
            Assert.Contains(WorldOpcode.CmsgCastSpell, sent);
            await host.WaitForWorldAsync(() => target.Health < before, "the warlock's Shadow Bolt");
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
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
