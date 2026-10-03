using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Totems;
using ArcaneCore.Protocol;
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
}
