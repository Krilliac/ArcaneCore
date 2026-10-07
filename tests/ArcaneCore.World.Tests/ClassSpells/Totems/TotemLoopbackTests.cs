using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Totems;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Spells.Totems;
using ArcaneCore.World.Tests.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.ClassSpells.Totems;

/// <summary>Registers a one-row <c>totem_spell</c> store in every test host (the totem feature loads it at attach).</summary>
internal sealed class TotemTestServices : IWorldTestServices
{
    public const uint TotemEntry = 960101;
    public const uint TotemPassive = 960102;

    public void Register(IServiceCollection services)
        => services.AddSingleton<ITotemDataStore>(new FixedTotemStore());

    private sealed class FixedTotemStore : ITotemDataStore
    {
        public Task<TotemContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TotemContent([(TotemEntry, TotemPassive)]));
    }
}

/// <summary>The totem feature end to end through the world host: real spell system, creature feature, map updaters and packets.</summary>
public sealed class TotemLoopbackTests
{
    private const uint SummonSpell = 960100;

    [Fact]
    public async Task ActiveTotem_PreparesAndLandsBoltThroughProductionWorldTimer_ThenOwnerDeathRemovesIt()
    {
        var template = new CreatureTemplate
        {
            Entry = TotemTestServices.TotemEntry, Name = "Test Active Totem", MinLevel = 1, MaxLevel = 1,
            DisplayIds = [903], Faction = 35, MinLevelHealth = 5, MaxLevelHealth = 5, AIName = "TotemAI",
        };
        var context = new CreatureTestContext(new CreatureContent([template], [], [], [], []));
        CreatureTestStore.Current.Value = context;
        WorldTestHost host;
        try
        {
            host = WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }

        await using (host)
        await using (WorldTestClient client = await host.EnterWorldAsync("SEARING", "Searing"))
        {
            await client.CollectAsync();
            Creature? target = null;
            Creature? totem = null;
            await host.OnWorldAsync(() =>
            {
                Player owner = host.World.FindOnlinePlayer("Searing")!;
                SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
                TotemSystem totems = host.WorldServices.GetRequiredService<TotemFeature>().System!;
                spells.CombatRules = SpellCombatRules.Neutral; // deterministic damage, production sinks/timer unchanged
                spells.Store = new SpellStore(
                [
                    .. spells.Store.All,
                    new SpellInfo
                    {
                        Id = SummonSpell, Name = "Active Totem Summon", RangeIndex = SpellConstants.RangeIndexSelfOnly,
                        Duration = new SpellDuration(60_000, 0, 60_000),
                        Effects = [new SpellEffectInfo
                        {
                            Effect = SpellEffectName.SummonTotemSlot1, BasePoints = 4, BaseDice = 1, DieSides = 1,
                            TargetA = (SpellImplicitTarget)44, MiscValue = (int)TotemTestServices.TotemEntry,
                        }],
                    },
                    new SpellInfo
                    {
                        Id = TotemTestServices.TotemPassive, Name = "Test Searing Bolt", School = SpellSchool.Fire,
                        CastTime = new SpellCastTime(400, 0, 0), RangeIndex = 4, Range = new SpellRange(0, 20),
                        Effects = [new SpellEffectInfo
                        {
                            Effect = SpellEffectName.SchoolDamage, BasePoints = 14, BaseDice = 1, DieSides = 1,
                            TargetA = SpellImplicitTarget.UnitEnemy,
                        }],
                    },
                ], [], []);
                target = host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(owner.Map!)
                    .SpawnTemporary(template with { Entry = 960103, Name = "Bolt Target", Faction = 14, AIName = "NullAI" },
                        owner.X + 8, owner.Y, owner.Z, 0);
                target.MaxHealth = target.Health = 100;
                Assert.True(owner.Map!.Combat.Attack(owner, target, melee: false));
                Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(owner, SummonSpell, SpellCastTargets.ForSelf(), true));
                totem = Assert.IsType<Creature>(totems.GetTotem(owner, TotemSlot.Fire));
            });

            byte[] start;
            do
            {
                start = await client.ReadUntilAsync(WorldOpcode.SmsgSpellStart);
            }
            while (!IsActiveBoltStart(start, totem!.Guid.Value));
            await client.ReadUntilAsync(WorldOpcode.SmsgSpellnonmeleedamagelog);
            await host.WaitForWorldAsync(() => target!.Health < 100, "active totem bolt damage through world timer");
            await host.OnWorldAsync(() =>
            {
                Assert.True(target!.Health <= 85);
                Assert.IsType<NullCreatureAI>(totem!.AI);
                Assert.Null(totem.Combat.Victim);
                Player owner = host.World.FindOnlinePlayer("Searing")!;
                owner.Map!.Combat.Kill(target, owner);
            });
            await host.WaitForWorldAsync(() => !totem!.IsInWorld, "active totem owner-death cleanup");
            await host.OnWorldAsync(() => Assert.Null(host.WorldServices.GetRequiredService<SpellFeature>().System.GetState(totem!.Guid)));
        }
    }

    private static bool IsActiveBoltStart(byte[] payload, ulong totemGuid)
    {
        var reader = new PacketReader(payload);
        _ = reader.ReadPackedGuid();
        ulong caster = reader.ReadPackedGuid();
        uint spell = reader.ReadUInt32();
        _ = reader.ReadUInt16();
        uint castTime = reader.ReadUInt32();
        return caster == totemGuid && spell == TotemTestServices.TotemPassive && castTime == 400;
    }

    [Fact]
    public async Task EarthTotemSummon_SpawnsTotemForTheClient_WithOwnerFields_AndNoTotemBarPacket()
    {
        var template = new CreatureTemplate
        {
            Entry = TotemTestServices.TotemEntry, Name = "Test Totem", MinLevel = 1, MaxLevel = 1, DisplayIds = [903],
            Faction = 35, MinLevelHealth = 5, MaxLevelHealth = 5, AIName = "TotemAI",
        };
        var context = new CreatureTestContext(new CreatureContent([template], [], [], [], []));
        CreatureTestStore.Current.Value = context;
        WorldTestHost host;
        try
        {
            host = WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }

        await using (host)
        await using (WorldTestClient client = await host.EnterWorldAsync("TOTEMER", "Totemer"))
        {
            ulong totemGuid = 0;
            await host.OnWorldAsync(() =>
            {
                Player shaman = host.World.FindOnlinePlayer("Totemer")!;
                SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
                TotemFeature totems = host.WorldServices.GetRequiredService<TotemFeature>();
                Assert.NotNull(totems.System);
                Assert.True(spells.System.HasEffectHandler(SpellEffectName.SummonTotemSlot2));
                spells.System.Store = new SpellStore(
                    [
                        .. spells.System.Store.All,
                        new SpellInfo
                        {
                            Id = SummonSpell,
                            Name = "Test Totem Summon",
                            RangeIndex = SpellConstants.RangeIndexSelfOnly,
                            Duration = new SpellDuration(60_000, 0, 60_000),
                            Effects =
                            [
                                new SpellEffectInfo
                                {
                                    Effect = SpellEffectName.SummonTotemSlot2, BasePoints = 4, BaseDice = 1, DieSides = 1,
                                    TargetA = (SpellImplicitTarget)41, MiscValue = (int)TotemTestServices.TotemEntry, Radius = 2,
                                },
                            ],
                        },
                        new SpellInfo
                        {
                            Id = TotemTestServices.TotemPassive,
                            Name = "Test Totem Passive",
                            Duration = new SpellDuration(-1, 0, -1),
                            Attributes = SpellAttributes.Passive,
                            Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Dummy, TargetA = SpellImplicitTarget.UnitCaster }],
                        },
                    ],
                    [],
                    []);

                Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(shaman, SummonSpell, SpellCastTargets.ForSelf(), triggered: true));
                Creature totem = Assert.IsType<Creature>(totems.System!.GetTotem(shaman, TotemSlot.Earth));
                totemGuid = totem.Guid.Value;
                Assert.Equal(shaman.Guid.Value, totem.GetUInt64(UpdateFields.UnitFieldSummonedby));
                Assert.Equal(SummonSpell, totem.GetUInt32(UpdateFields.UnitCreatedBySpell));
                Assert.Equal((5u, 5u), (totem.Health, totem.MaxHealth));
                Assert.True(spells.System.HasAura(totem, TotemTestServices.TotemPassive));
            });

            // The client learns of the totem, then receives the spawn animation naming it; there is no 1.12.1 totem bar packet.
            var seen = new List<WorldOpcode>();
            byte[] anim;
            while (true)
            {
                (WorldOpcode opcode, byte[] payload) = await client.ReadAsync();
                seen.Add(opcode);
                if (opcode == WorldOpcode.SmsgGameobjectSpawnAnim)
                {
                    anim = payload;
                    break;
                }
            }

            Assert.Equal(totemGuid, BitConverter.ToUInt64(anim));
            Assert.Contains(seen, o => o is WorldOpcode.SmsgUpdateObject or WorldOpcode.SmsgCompressedUpdateObject);
            Assert.DoesNotContain(seen, o => (ushort)o is 0x412 or 0x413);
        }
    }

    [Fact]
    public async Task KillingATotem_GrantsNoExperienceAndNoQuestCredit_WhileAPlainCreatureDoes()
    {
        // Player::IsHonorOrXPTarget (Player.cpp:19943-19954) and RewardSinglePlayerAtKill (Player.cpp:19959-19983: a
        // player-owned victim is PvP, so no XP and no KilledMonster) never reward a totem.
        var template = new CreatureTemplate
        {
            Entry = TotemTestServices.TotemEntry, Name = "Test Totem", MinLevel = 1, MaxLevel = 1, DisplayIds = [903],
            Faction = 35, MinLevelHealth = 5, MaxLevelHealth = 5, AIName = "TotemAI",
        };
        var context = new CreatureTestContext(new CreatureContent([template], [], [], [], []));
        CreatureTestStore.Current.Value = context;
        WorldTestHost host;
        try
        {
            host = WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }

        await using (host)
        await using (WorldTestClient client = await host.EnterWorldAsync("TOTEMKILL", "Totemkill"))
        {
            await host.OnWorldAsync(() =>
            {
                Player shaman = host.World.FindOnlinePlayer("Totemkill")!;
                SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
                TotemFeature totems = host.WorldServices.GetRequiredService<TotemFeature>();
                var credits = new List<uint>();
                using var adapter = new ArcaneCore.World.Npc.QuestObjectiveAdapter(new CreditRecorder(credits));
                adapter.Attach(shaman.Map!);
                spells.System.Store = new SpellStore(
                    [
                        .. spells.System.Store.All,
                        new SpellInfo
                        {
                            Id = SummonSpell,
                            Name = "Test Totem Summon",
                            RangeIndex = SpellConstants.RangeIndexSelfOnly,
                            Duration = new SpellDuration(60_000, 0, 60_000),
                            Effects =
                            [
                                new SpellEffectInfo
                                {
                                    Effect = SpellEffectName.SummonTotemSlot2, BasePoints = 4, BaseDice = 1, DieSides = 1,
                                    TargetA = (SpellImplicitTarget)41, MiscValue = (int)TotemTestServices.TotemEntry, Radius = 2,
                                },
                            ],
                        },
                    ],
                    [],
                    []);
                Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(shaman, SummonSpell, SpellCastTargets.ForSelf(), triggered: true));
                Creature totem = Assert.IsType<Creature>(totems.System!.GetTotem(shaman, TotemSlot.Earth));
                Creature plain = host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(shaman.Map!)
                    .SpawnTemporary(template with { AIName = "NullAI" }, shaman.X + 4, shaman.Y, shaman.Z, 0);

                uint before = ArcaneCore.Game.Progression.PlayerProgression.CurrentXp(shaman);
                shaman.Map!.Combat.Kill(shaman, totem);
                Assert.Equal(before, ArcaneCore.Game.Progression.PlayerProgression.CurrentXp(shaman));
                Assert.Empty(credits);

                // Control: the same pipeline does reward the identical creature when it is not a summoned totem.
                shaman.Map!.Combat.Kill(shaman, plain);
                Assert.True(ArcaneCore.Game.Progression.PlayerProgression.CurrentXp(shaman) > before);
                Assert.Equal([TotemTestServices.TotemEntry], credits);
            });
        }
    }

    private sealed class CreditRecorder(List<uint> credits) : ArcaneCore.Game.Quests.IQuestObjectiveEvents
    {
        public void KilledMonsterCredit(Player player, uint entry, ObjectGuid guid) => credits.Add(entry);

        public void CastedCreatureOrGo(Player player, uint entry, ObjectGuid guid, bool isCreature, uint spellId) { }

        public void TalkedToCreature(Player player, uint entry, ObjectGuid guid) { }

        public void ItemAdded(Player player, uint entry, uint count) { }

        public void ItemRemoved(Player player, uint entry, uint count) { }

        public void AreaExploredOrEventHappens(Player player, uint questId) { }

        public void FailQuest(Player player, uint questId) { }

        public void MoneyChanged(Player player) { }
    }
}
