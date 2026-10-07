using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

public sealed class ImportedNpcMetadataWorldTests
{
    // The imported service fields are creature_template columns (CreatureNpcMetadataDataModule, vmangos layout) and reach NpcInfo through
    // CreatureQuestLookup; NpcServices:NpcTemplates rows replace an entry's fields as a whole.
    [Fact]
    public void ExplicitConfigurationOverridesTheWholeEntry()
    {
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<NpcServicesFeature>(sp => new NpcServicesFeature(sp, NullLogger<NpcServicesFeature>.Instance))
            .BuildServiceProvider();
        NpcServicesFeature feature = services.GetRequiredService<NpcServicesFeature>();
        feature.Options.NpcTemplates.Add(new NpcTemplateMetadata { Entry = 152, GossipMenuId = 88, TrainerType = TrainerType.Class, TrainerClass = 8, TrainerSpell = 4321 });
        ICreatureLookup lookup = feature.Extend(new QuestNpcDependencies(Creatures: new FixedCreatureLookup()), NpcStore.Empty).Creatures!;
        NpcInfo npc = lookup.Find(null!, default)!;

        Assert.Equal((uint)88, npc.GossipMenuId);
        Assert.Equal(TrainerType.Class, npc.TrainerType);
        Assert.Equal((byte)8, npc.TrainerClass);
        Assert.Equal((uint)4321, npc.TrainerSpell);
        Assert.True((npc.NpcFlags & NpcFlags.Vendor) != 0);
    }

    [Fact]
    public void InvalidConfiguredClassOrRaceMetadataFailsClosed()
    {
        foreach (NpcTemplateMetadata invalid in new[]
        {
            new NpcTemplateMetadata { Entry = 911, TrainerType = TrainerType.Class, TrainerClass = 6 },
            new NpcTemplateMetadata { Entry = 911, TrainerType = TrainerType.Class, TrainerClass = 1, TrainerRace = 9 },
            new NpcTemplateMetadata { Entry = 911, TrainerType = TrainerType.Class, TrainerClass = 10 },
            new NpcTemplateMetadata { Entry = 0, TrainerType = TrainerType.Class, TrainerClass = 1 },
        })
        {
            using ServiceProvider configured = new ServiceCollection()
                .AddSingleton<NpcServicesFeature>(sp => new NpcServicesFeature(sp, NullLogger<NpcServicesFeature>.Instance))
                .BuildServiceProvider();
            NpcServicesFeature feature = configured.GetRequiredService<NpcServicesFeature>();
            feature.Options.NpcTemplates.Add(invalid);
            Assert.Throws<InvalidDataException>(() => feature.Extend(new QuestNpcDependencies(), NpcStore.Empty));
        }
    }

    [Fact]
    public async Task ImportedTrainerMetadata_ReachesRealWorldTrainerList_WarriorAllowedMageRefused()
    {
        var fixture = new TrainerMetadataFixture();
        TrainerMetadataWorldServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start();
            await using WorldTestClient warrior = await host.EnterWorldAsync("METAWARRIOR", "MetaWarrior");
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("MetaWarrior")!.VisibleObjects.Contains(TrainerMetadataFixture.Guid), "trainer visible");
            await warrior.SendAsync(WorldOpcode.CmsgTrainerList, GuidBody());
            await warrior.ReadUntilAsync(WorldOpcode.SmsgTrainerList);

            byte[] key = await host.AddAccountAsync("METAMAGE");
            await using WorldTestClient mage = await host.ConnectAsync();
            await mage.AuthenticateAsync("METAMAGE", key);
            await mage.CreateCharacterAsync("MetaMage", cls: 8);
            var account = await host.Accounts.FindByUsernameAsync("METAMAGE");
            var mageRecord = Assert.Single(await host.Characters.GetByAccountAsync(account!.Id));
            await mage.LoginAsync((ulong)mageRecord.Id);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("MetaMage")!.VisibleObjects.Contains(TrainerMetadataFixture.Guid), "trainer visible");
            await mage.SendAsync(WorldOpcode.CmsgTrainerList, GuidBody());
            bool refused = false;
            for (int packet = 0; packet < 128; packet++)
            {
                var reply = await mage.ReadAsync();
                Assert.NotEqual(WorldOpcode.SmsgTrainerList, reply.Opcode);
                if (reply.Opcode == WorldOpcode.SmsgGossipMessage)
                {
                    refused = true;
                    break;
                }
            }
            Assert.True(refused, "Wrong-class trainer must return its refusal gossip instead of a trainer list.");
        }
        finally
        {
            TrainerMetadataWorldServices.Current.Value = null;
        }
    }

    private static byte[] GuidBody()
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(TrainerMetadataFixture.Guid.Value);
        return writer.ToArray();
    }

    private sealed class FixedCreatureLookup : ICreatureLookup
    {
        public NpcInfo? Find(ArcaneCore.Game.Entities.Player player, ArcaneCore.Game.ObjectGuid guid)
            => new(default, 152, 152, NpcFlags.Trainer | NpcFlags.Vendor, 0, 0, 0, 0, 1, true, false, false, false, 0, TrainerType.Class, 0, 0, 0, 0);
    }
}

internal sealed class TrainerMetadataWorldServices : IWorldTestServices
{
    public static readonly AsyncLocal<TrainerMetadataFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture) return;
        services.AddSingleton<INpcContentStore>(fixture);
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton<IWorldDataStore>(new TrainerPlayerInfo());
        services.AddSingleton(new FactionTemplateCatalog([
            new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
            new FactionTemplateRecord(900011, 0, 0, 8, 0, 0),
        ]));
    }
}

internal sealed class TrainerPlayerInfo : IWorldDataStore
{
    private readonly InMemoryWorldDataStore _defaults = new();
    public Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default)
        => Task.FromResult<StartPosition?>(race == 1 && cls is 1 or 8
            ? new StartPosition(0, 12, -8949.95f, -132.493f, 83.5312f, 0f) : null);
    public Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default)
        => _defaults.GetRaceInfoAsync(race, gender, cancellationToken);
    public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default)
        => cls == 8 ? Task.FromResult<ClassInfo?>(new ClassInfo(60, 100, 0)) : _defaults.GetClassInfoAsync(cls, cancellationToken);
    public Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default)
        => Task.FromResult(race == 1 && cls is 1 or 8);
}

internal sealed class TrainerMetadataFixture : INpcContentStore, ICreatureDataStore
{
    public const uint Entry = 911;
    public static readonly ObjectGuid Guid = ObjectGuid.WithEntry(HighGuid.Unit, Entry, 91101);

    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(NpcContent.Empty with { TrainerSpells = [new TrainerSpell { Entry = Entry, Spell = 991001, SpellCost = 1 }] });

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(new CreatureContent(
            [new CreatureTemplate { Entry = Entry, Name = "Metadata trainer", Faction = 900011, NpcFlags = (uint)NpcFlags.Trainer, DisplayIds = [49], TrainerType = 0, TrainerClass = 1 }],
            [new CreatureSpawn { Guid = 91101, Entry = Entry, MapId = 0, X = -8948.95f, Y = -132.49f, Z = 83.53f }], [], [], []));
}
