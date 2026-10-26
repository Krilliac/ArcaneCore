using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
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

    /// <summary>The rotation's first think leaves a cast pending (the clock never advances here), and a pending cast returns Update before the pet command; cancel it so the next think reaches the pet.</summary>
    private static void ClearCast(WorldSession session)
    {
        SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
        feature.System.CancelCast(session.Player!, 0);
        Assert.False(feature.System.GetState(session.Player!.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting });
    }

    /// <summary>
    /// A pet's spells start with autocast off and nothing ever turned it on for a bot, so a bot warlock's Imp never fired its bolt. The
    /// bot sends CMSG_PET_SPELL_AUTOCAST for each harmful spell its pet knows, once.
    /// </summary>
    [Fact]
    public async Task ABotWithAPet_EnablesAutocastOnTheHarmfulPetSpells_Once()
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
                Creature target = await AddTargetAsync(host, session, 7, 41);
                Creature pet = await host.World.InvokeAsync(() =>
                {
                    Player player = session.Player!;
                    player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter); // only a hunter restores a cached pet; it turns warlock below
                    SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                    SpellInfo bolt = feature.System.Store.Get(SpellTestServices.Bolt)!;
                    feature.System.Store = new SpellStore(feature.System.Store.All.Append(bolt with { Id = 9101, Name = "Shadow Bolt", Rank = "Rank 1" }), [], []);
                    Assert.True(feature.Spellbook.LearnSpell(player, 9101));
                    var snapshot = new ArcaneCore.Kernel.Characters.Pets.PersistentPetSnapshot((int)player.Guid.Low, 9103, Pets.PetTestServices.Entry, 1, 0,
                        50, 30, 0, 1, [], [new ArcaneCore.Kernel.Characters.Pets.PersistentPetSpell(SpellTestServices.Bolt, Autocast: false, Passive: false)],
                        Name: "Imp", NameTimestamp: 1, RenameAllowed: false);
                    Creature restored = session.Services.GetRequiredService<ArcaneCore.World.Pets.PetsFeature>().Service.RestoreCurrentPet(player, snapshot)!;
                    player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
                    return restored;
                });
                Assert.Equal(ArcaneCore.Game.Pets.ActionType.Disabled, await host.World.InvokeAsync(() => pet.Summon!.Charm!.SpellStates[SpellTestServices.Bolt]));

                var helper = new PlayerbotCombatSpells(session);
                var sent = new List<WorldOpcode>();
                await host.World.InvokeAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(8);
                    session.ManagedDispatchObserver = sent.Add;
                    helper.Update(session.Player!, target, 0);
                    return true;
                });

                Assert.Equal(1, sent.Count(o => o == WorldOpcode.CmsgPetSpellAutocast));
                Assert.Equal(ArcaneCore.Game.Pets.ActionType.Enabled, await host.World.InvokeAsync(() => pet.Summon!.Charm!.SpellStates[SpellTestServices.Bolt]));

                await host.World.InvokeAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(8);
                    ClearCast(session);
                    helper.Update(session.Player!, target, 0);
                    return true;
                });
                Assert.Equal(1, sent.Count(o => o == WorldOpcode.CmsgPetSpellAutocast)); // not repeated

                // A rank that replaces a known spell keeps the spell count, but it is a new spell: autocast is sent for it too.
                await host.World.InvokeAsync(() =>
                {
                    ArcaneCore.Game.Pets.CharmInfo charm = pet.Summon!.Charm!;
                    Assert.True(charm.UnlearnSpell(SpellTestServices.Bolt));
                    Assert.True(charm.LearnSpell(9101, ArcaneCore.Game.Pets.ActionType.Disabled));
                    Assert.Single(charm.SpellStates);
                    session.ManagedBudget = new ManagedActionBudget(8);
                    ClearCast(session);
                    helper.Update(session.Player!, target, 0);
                    return true;
                });
                Assert.Equal(2, sent.Count(o => o == WorldOpcode.CmsgPetSpellAutocast));
                Assert.Equal(ArcaneCore.Game.Pets.ActionType.Enabled, await host.World.InvokeAsync(() => pet.Summon!.Charm!.SpellStates[9101]));
            }
            finally
            {
                session.Kick();
                await session.ManagedClosed;
            }
        }
    }

    [Fact]
    public async Task PetAvoidsCrowdControl_AndRedirectsOrFollowsWhenTargetIsPolymorphed()
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
                Creature sheeped = await AddTargetAsync(host, session, 7, 41);
                Creature liveTarget = await AddTargetAsync(host, session, 8, 42);
                const uint polymorphId = 9103;
                Creature pet = await host.World.InvokeAsync(() =>
                {
                    Player player = session.Player!;
                    player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
                    SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                    SpellInfo bolt = feature.System.Store.Get(SpellTestServices.Bolt)!;
                    var polymorph = new SpellInfo
                    {
                        Id = polymorphId, Name = "Polymorph", Rank = "Rank 1",
                        AuraInterruptFlags = SpellAuraInterruptFlags.Damage,
                        Duration = new SpellDuration(20_000, 0, 20_000),
                        Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Transform, TargetA = SpellImplicitTarget.UnitEnemy }],
                    };
                    feature.System.Store = new SpellStore(
                        feature.System.Store.All.Append(bolt with { Id = 9101, Name = "Shadow Bolt", Rank = "Rank 1" }).Append(polymorph), [], []);
                    Assert.True(feature.Spellbook.LearnSpell(player, 9101));
                    var snapshot = new ArcaneCore.Kernel.Characters.Pets.PersistentPetSnapshot((int)player.Guid.Low, 9102, Pets.PetTestServices.Entry, 1, 0,
                        50, 30, 0, 1, [], [], Name: "Imp", NameTimestamp: 1, RenameAllowed: false);
                    Creature restored = session.Services.GetRequiredService<ArcaneCore.World.Pets.PetsFeature>().Service.RestoreCurrentPet(player, snapshot)!;
                    player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
                    restored.Map!.Combat.Attack(restored, sheeped);
                    Assert.Same(sheeped, restored.Combat.Victim);
                    Assert.True(feature.System.AddAura(sheeped, polymorphId));
                    // Applying a breakable CC aura immediately stops any pet attacking that target (Spell.cpp:1566-1577).
                    Assert.Null(restored.Combat.Victim);
                    return restored;
                });

                var helper = new PlayerbotCombatSpells(session);
                var sent = new List<WorldOpcode>();

                // 1. An idle pet is not commanded onto a Polymorphed target.
                await host.World.InvokeAsync(() =>
                {
                    SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                    session.ManagedBudget = new ManagedActionBudget(4);
                    session.ManagedDispatchObserver = sent.Add;
                    helper.Update(session.Player!, sheeped, 0);
                    feature.System.InterruptCurrentCast(session.Player!);
                    return true;
                });
                Assert.DoesNotContain(WorldOpcode.CmsgPetAction, sent);
                Assert.Null(await host.World.InvokeAsync(() => pet.Combat.Victim));

                // 2. If the pet is fighting a Polymorphed unit while the bot fights a non-CC target, it redirects to the non-CC target.
                sent.Clear();
                await host.World.InvokeAsync(() =>
                {
                    SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                    pet.Map!.Combat.Attack(pet, sheeped);
                    Assert.Same(sheeped, pet.Combat.Victim);
                    session.ManagedBudget = new ManagedActionBudget(4);
                    helper.Update(session.Player!, liveTarget, 1600);
                    feature.System.InterruptCurrentCast(session.Player!);
                    return true;
                });
                Assert.Contains(WorldOpcode.CmsgPetAction, sent);
                Assert.Same(liveTarget, await host.World.InvokeAsync(() => pet.Combat.Victim));

                // 3. If the pet is fighting a Polymorphed unit and the bot's victim is also Polymorphed, the pet is recalled to follow.
                sent.Clear();
                await host.World.InvokeAsync(() =>
                {
                    SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                    pet.Map!.Combat.Attack(pet, sheeped);
                    Assert.Same(sheeped, pet.Combat.Victim);
                    session.ManagedBudget = new ManagedActionBudget(4);
                    helper.Update(session.Player!, sheeped, 3200);
                    feature.System.InterruptCurrentCast(session.Player!);
                    return true;
                });
                Assert.Contains(WorldOpcode.CmsgPetAction, sent);
                Assert.Null(await host.World.InvokeAsync(() => pet.Combat.Victim));
            }
            finally
            {
                session.Kick();
                await session.ManagedClosed;
            }
        }
    }

    /// <summary>
    /// Shadow Word: Pain (and Lesser Heal, Smite, Immolate) was submitted ten times in the Warsong flag rooms: CheckCast
    /// returns <see cref="SpellCastResult.LineOfSight"/> before SPELL_START, and the bot only remembered that refusal for
    /// <see cref="PlayerbotCombatSpells.RefusalBackoffMs"/>. The view must not choose the spell while the wall is there.
    /// </summary>
    [Fact]
    public async Task ShadowWordPain_IsNotChosenWhenAWallBlocksTheTarget_AndIsChosenOnceTheLineIsClear()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature target = await AddTargetAsync(host, session, 10, 589);
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                var pain = new SpellInfo
                {
                    Id = 589,
                    Name = "Shadow Word: Pain",
                    RangeIndex = 4,
                    Range = new SpellRange(0, 30),
                    Effects =
                    [
                        new SpellEffectInfo
                        {
                            Effect = SpellEffectName.ApplyAura,
                            AuraType = AuraType.PeriodicDamage,
                            TargetA = SpellImplicitTarget.UnitEnemy,
                            Amplitude = 3000,
                        },
                    ],
                };
                WorldCollision.Of(host.World).Install(lineOfSight: new WallAtX(player.X + 5f));
                Assert.Equal(SpellCastResult.LineOfSight, SpellLineOfSight.Check(player, pain, target, triggered: false));

                RotationState blocked = PlayerbotCombatView.Build(
                    session, session.Services.GetRequiredService<SpellFeature>(), player, target,
                    PlayerbotAbilities.Empty, PlayerbotRole.Healer, static _ => false);
                Assert.NotNull(blocked.Victim);
                Assert.False(blocked.CanCast(pain, blocked.Victim));

                target.Relocate(player.X + 2f, player.Y, player.Z, 0, host.World.NowMs);
                Assert.Equal(SpellCastResult.CastOk, SpellLineOfSight.Check(player, pain, target, triggered: false));
                Assert.True(blocked.CanCast(pain, blocked.Victim));
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
    /// S-8: a stunned bot kept choosing its rotation and the server answered CastResult Stunned at every think. The view now asks the
    /// caster-aura gate, so a stunned bot chooses nothing but a spell that breaks the stun (the PvP trinket shape).
    /// </summary>
    [Fact]
    public async Task AStunnedBot_ChoosesNoSpellTheStunForbids_ButStillTheOneThatBreaksIt()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature target = await AddTargetAsync(host, session, 10, 589);
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                var pain = new SpellInfo
                {
                    Id = 589,
                    Name = "Shadow Word: Pain",
                    RangeIndex = 4,
                    Range = new SpellRange(0, 30),
                    Effects =
                    [
                        new SpellEffectInfo
                        {
                            Effect = SpellEffectName.ApplyAura,
                            AuraType = AuraType.PeriodicDamage,
                            TargetA = SpellImplicitTarget.UnitEnemy,
                            Amplitude = 3000,
                        },
                    ],
                };
                var trinket = new SpellInfo
                {
                    Id = 900_801,
                    Name = "stun breaker",
                    AttributesEx = (SpellAttributesEx)ArcaneCore.Game.Spells.Rules.SpellRuleFlags.ExImmunityPurgesEffect,
                    Effects =
                    [
                        new SpellEffectInfo
                        {
                            Effect = SpellEffectName.ApplyAura,
                            AuraType = AuraType.MechanicImmunity,
                            MiscValue = (int)ArcaneCore.Game.Spells.Rules.SpellMechanic.Stun,
                            TargetA = SpellImplicitTarget.UnitCaster,
                        },
                    ],
                };

                RotationState free = PlayerbotCombatView.Build(session, feature, player, target,
                    PlayerbotAbilities.Empty, PlayerbotRole.Healer, static _ => false);
                Assert.True(free.CanCast(pain, free.Victim!));

                player.UnitFlags |= UnitFlags.Stunned;
                Assert.Equal(SpellCastResult.Stunned, feature.System.CheckCasterState(player, pain));
                RotationState stunned = PlayerbotCombatView.Build(session, feature, player, target,
                    PlayerbotAbilities.Empty, PlayerbotRole.Healer, static _ => false);
                Assert.False(stunned.CanCast(pain, stunned.Victim!));
                Assert.Equal(SpellCastResult.CastOk, feature.System.CheckCasterState(player, trinket));
                Assert.True(stunned.CanCast(trinket, stunned.Self));

                player.UnitFlags &= ~UnitFlags.Stunned;
                return true;
            });
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task ASpellRequiringATotemTool_IsNotCastableWithoutTheToolInInventory_AndBecomesCastableOnceAdded()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                const uint earthTotemItem = 5175;
                host.WorldServices.GetRequiredService<ArcaneCore.World.Items.ItemsFeature>().ReplaceTemplates(
                    new ArcaneCore.Game.Items.ItemTemplateStore(
                    [
                        new ArcaneCore.Kernel.Items.ItemTemplate
                        {
                            Entry = earthTotemItem,
                            Name = "Earth Totem",
                            Class = 15,
                            Quality = 1,
                            Stackable = 1,
                        },
                    ]));
                SpellFeature feature = session.Services.GetRequiredService<SpellFeature>();
                var strengthOfEarth = new SpellInfo
                {
                    Id = 8075,
                    Name = "Strength of Earth Totem",
                    RangeIndex = SpellConstants.RangeIndexSelfOnly,
                    Totems = [earthTotemItem, 0],
                    Effects =
                    [
                        new SpellEffectInfo
                        {
                            Effect = SpellEffectName.SummonTotemSlot1,
                            TargetA = SpellImplicitTarget.UnitCaster,
                        },
                    ],
                };

                RotationState state = PlayerbotCombatView.Build(session, feature, player, null,
                    PlayerbotAbilities.Empty, PlayerbotRole.MeleeDps, static _ => false);
                Assert.Equal(0u, player.Inventory.GetItemCount(earthTotemItem));
                Assert.False(state.CanCast(strengthOfEarth, state.Self));

                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(earthTotemItem, 1, out _));
                Assert.Equal(1u, player.Inventory.GetItemCount(earthTotemItem));
                Assert.True(state.CanCast(strengthOfEarth, state.Self));
                return true;
            });
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

    /// <summary>A vertical wall at x = <paramref name="wallX"/> blocks every segment that crosses it.</summary>
    private sealed class WallAtX(float wallX) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
            => (from.X - wallX) * (to.X - wallX) > 0;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => null;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }
}
