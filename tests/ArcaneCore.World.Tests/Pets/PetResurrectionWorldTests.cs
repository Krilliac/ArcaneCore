using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Pets;

public sealed class PetResurrectionWorldTests
{
    private const uint Entry = 963216;
    private const uint Summon = 963217;
    private const uint Revive = 963218;

    [Theory]
    [InlineData(41, 41u)]
    [InlineData(140, 100u)]
    public async Task KnownNormalClientCast_RevivesExistingPetWithoutPlayerOffer(int health, uint expected)
    {
        var template = new CreatureTemplate
        {
            Entry = Entry, Name = "Pet Revival Test", MinLevel = 1, MaxLevel = 1,
            DisplayIds = [903], MinLevelHealth = 100, MaxLevelHealth = 100, Faction = 35,
        };
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([template], [], [], [], []));
        WorldTestHost host;
        try { host = WorldTestHost.Start(); }
        finally { CreatureTestStore.Current.Value = null; }
        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync("PETREVIVE", "Petrevive");
            uint? restoredHealth = null;
            ObjectGuid petGuid = await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Petrevive")!;
                SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
                feature.System.Store = new SpellStore([.. feature.System.Store.All,
                    new SpellInfo
                    {
                        Id = Summon, Name = "Test Pet Summon", RangeIndex = SpellConstants.RangeIndexSelfOnly,
                        Effects = [new SpellEffectInfo { Effect = SpellEffectName.Summon, MiscValue = (int)Entry, TargetA = SpellImplicitTarget.UnitCaster }],
                    },
                    new SpellInfo
                    {
                        Id = Revive, Name = "Test Pet Revival", AttributesEx2 = SpellAttributesEx2.AllowDeadTarget,
                        RangeIndex = 4, Range = new SpellRange(0, 40),
                        Effects = [new SpellEffectInfo { Effect = SpellEffectName.ResurrectNew, BasePoints = health - 1,
                            BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.Unit }],
                    }], [], []);
                feature.Spellbook.LearnSpell(player, Revive);
                Assert.Equal(SpellCastResult.CastOk, feature.System.CastSpell(player, Summon, SpellCastTargets.ForSelf(), true));
                Creature pet = player.GetPet()!;
                host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(player.Map!)!.KillCreature(pet);
                Assert.Equal(CreatureDeathState.Corpse, pet.DeathState);
                pet.Relocate(player.X + 10, player.Y, player.Z, 0, host.World.NowMs);
                feature.System.SpellHit += (_, target, spell) =>
                {
                    if (spell.Id == Revive && ReferenceEquals(target, pet))
                    {
                        restoredHealth = target.Health;
                    }
                };
                return pet.Guid;
            });
            await client.CollectAsync();
            var writer = new PacketWriter();
            writer.WriteUInt32(Revive);
            SpellCastTargets.ForUnit(petGuid).Write(writer);
            await client.SendAsync(WorldOpcode.CmsgCastSpell, writer.ToArray());
            var packets = await client.CollectAsync();
            Assert.Contains(packets, p => p.Opcode == WorldOpcode.SmsgCastResult);
            Assert.DoesNotContain(packets, p => p.Opcode == WorldOpcode.SmsgResurrectRequest);
            Assert.Contains(packets, p => p.Opcode == WorldOpcode.MsgMoveTeleport);
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Petrevive")!;
                Creature pet = player.GetPet()!;
                Assert.Equal(petGuid, pet.Guid);
                Assert.Equal(DeathState.Alive, pet.Combat.DeathState);
                Assert.Equal(CreatureDeathState.Alive, pet.DeathState);
                Assert.Equal<uint?>(expected, restoredHealth);
                Assert.InRange(pet.Health, expected, pet.MaxHealth); // normal regeneration follows the captured effect result
                Assert.IsType<PetAI>(pet.AI);
                Assert.Equal(0u, pet.GetUInt32(UpdateFields.UnitDynamicFlags));
                Assert.InRange(Math.Abs(player.X - pet.X), 0, PetConstants.FollowDistance + 1);
            });
        }
    }
}
