using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

/// <summary>
/// Guild charters over the real world socket: a petitioner NPC (flags 0x601, as the six classic-db
/// guild masters), the charter item 5863 in the item content, real inventories and the in-memory stores.
/// Payload layouts are written out from vmangos Server/Packets/Petition.cpp and gtker wow_messages,
/// not through the production helpers.
/// </summary>
public sealed class PetitionEndToEndTests
{
    private const uint Entry = 900030;
    private static readonly ObjectGuid Npc = ObjectGuid.WithEntry(HighGuid.Unit, Entry, 900031);

    [Fact]
    public void Handlers_AreRegisteredForEveryPetitionOpcode()
    {
        OpcodeTable table = ArcaneCore.World.WorldServiceCollectionExtensions.BuildOpcodeTable();
        foreach (WorldOpcode opcode in new[]
        {
            WorldOpcode.CmsgPetitionShowlist, WorldOpcode.CmsgPetitionBuy, WorldOpcode.CmsgPetitionShowSignatures, WorldOpcode.CmsgPetitionQuery,
            WorldOpcode.MsgPetitionRename, WorldOpcode.CmsgPetitionSign, WorldOpcode.MsgPetitionDecline, WorldOpcode.CmsgOfferPetition,
            WorldOpcode.CmsgTurnInPetition,
        })
        {
            Assert.True(table.TryGet(opcode, out OpcodeHandler handler), opcode.ToString());
            Assert.NotNull(handler.World);
        }
    }

    [Fact]
    public async Task ShowList_ThenBuy_ChargesACharter_AndStoresThePetition()
    {
        await using Harness h = await Harness.StartAsync();
        await using WorldTestClient owner = await h.EnterAsync("OWNER", "Owner");

        await owner.SendAsync(WorldOpcode.CmsgPetitionShowlist, U64(Npc.Value));
        var list = new PacketReader(await owner.ReadUntilAsync(WorldOpcode.SmsgPetitionShowlist));
        Assert.Equal(Npc.Value, list.ReadUInt64());
        Assert.Equal(1, list.ReadByte());
        Assert.Equal([1u, 5863u, 16161u, 1000u, 1u], [list.ReadUInt32(), list.ReadUInt32(), list.ReadUInt32(), list.ReadUInt32(), list.ReadUInt32()]);

        await owner.SendAsync(WorldOpcode.CmsgPetitionBuy, BuyPayload("Arcane Order"));
        await owner.ReadUntilAsync(WorldOpcode.SmsgItemPushResult);

        Assert.Equal(9000u, await h.Host.PlayerStateAsync("Owner", p => p.Money)); // 10000 - 1000 copper
        ObjectGuid charter = await h.CharterAsync("Owner");
        Assert.Equal(1u, await h.Host.PlayerStateAsync("Owner", p => p.Inventory.GetItemCount(5863)));
        await h.FlushAsync();
        PetitionData stored = Assert.Single(h.Petitions.Snapshot());
        Assert.Equal(("Arcane Order", 1, (int)charter.Low), (stored.Name, stored.OwnerId, stored.CharterItemId));
        Assert.Equal((uint)stored.Id, await h.Host.PlayerStateAsync("Owner", p => p.Inventory.AllItems.Single(i => i.Entry == 5863).EnchantmentId(0)));
    }

    [Fact]
    public async Task TheFormAGuildGossipOption_ClosesTheMenu_AndOpensThePetitionList()
    {
        await using Harness h = await Harness.StartAsync();
        await using WorldTestClient owner = await h.EnterAsync("OWNER", "Owner");

        await owner.SendAsync(WorldOpcode.CmsgGossipHello, U64(Npc.Value));
        await owner.ReadUntilAsync(WorldOpcode.SmsgGossipMessage);
        await owner.SendAsync(WorldOpcode.CmsgGossipSelectOption, [.. U64(Npc.Value), 0, 0, 0, 0]); // option 0: How do I form a guild?

        await owner.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);
        var list = new PacketReader(await owner.ReadUntilAsync(WorldOpcode.SmsgPetitionShowlist));
        Assert.Equal(Npc.Value, list.ReadUInt64());
        Assert.Equal(1, list.ReadByte());
    }

    [Fact]
    public async Task ABuy_OutOfRange_OrWithoutTheTabardFlag_OrWithTooLittleMoney_ChargesNothing()
    {
        await using Harness h = await Harness.StartAsync();
        await using WorldTestClient poor = await h.EnterAsync("POOR", "Poor", money: 999);
        await poor.SendAsync(WorldOpcode.CmsgPetitionBuy, BuyPayload("Arcane Order"));
        var failed = new PacketReader(await poor.ReadUntilAsync(WorldOpcode.SmsgBuyFailed));
        Assert.Equal((Npc.Value, 5863u, (byte)2), (failed.ReadUInt64(), failed.ReadUInt32(), failed.ReadByte()));
        Assert.Equal(999u, await h.Host.PlayerStateAsync("Poor", p => p.Money));

        await using WorldTestClient rich = await h.EnterAsync("RICH", "Rich");
        await h.Host.OnWorldAsync(() => h.Creature("Rich").NpcFlags = (uint)NpcFlags.Petitioner); // not a tabard designer
        await rich.SendAsync(WorldOpcode.CmsgPetitionBuy, BuyPayload("Arcane Order"));
        await rich.SendAsync(WorldOpcode.CmsgPetitionShowlist, U64(Npc.Value));
        await rich.ReadUntilAsync(WorldOpcode.SmsgPetitionShowlist); // the later request answered: the buy before it was ignored
        Assert.Equal(10_000u, await h.Host.PlayerStateAsync("Rich", p => p.Money));
        Assert.Empty(h.Petitions.Snapshot());
    }

    [Fact]
    public async Task OfferSignAndTurnIn_FoundTheGuild_WithTenMembers()
    {
        await using Harness h = await Harness.StartAsync();
        await using WorldTestClient owner = await h.EnterAsync("OWNER", "Owner");
        var signers = new List<WorldTestClient>();
        try
        {
            for (int i = 1; i <= 9; i++)
            {
                signers.Add(await h.EnterAsync("SIGNER" + i, "Signer" + (char)('a' + i - 1)));
            }

            await owner.SendAsync(WorldOpcode.CmsgPetitionBuy, BuyPayload("Arcane Order"));
            await owner.ReadUntilAsync(WorldOpcode.SmsgItemPushResult);
            ObjectGuid charter = await h.CharterAsync("Owner");
            ulong signer1 = await h.GuidAsync("Signera");

            // Offer to the first signer: the target sees the signature list (with the offerer as owner).
            await owner.SendAsync(WorldOpcode.CmsgOfferPetition, [.. U64(charter.Value), .. U64(signer1)]);
            var shown = new PacketReader(await signers[0].ReadUntilAsync(WorldOpcode.SmsgPetitionShowSignatures));
            Assert.Equal((charter.Value, 1ul), (shown.ReadUInt64(), shown.ReadUInt64()));
            uint petitionId = shown.ReadUInt32();
            Assert.Equal(0, shown.ReadByte());

            // Eight signatures are not enough.
            for (int i = 0; i < 8; i++)
            {
                await signers[i].SendAsync(WorldOpcode.CmsgPetitionSign, [.. U64(charter.Value), 0]);
                var ok = new PacketReader(await signers[i].ReadUntilAsync(WorldOpcode.SmsgPetitionSignResults));
                Assert.Equal((charter.Value, (ulong)(i + 2), 0u), (ok.ReadUInt64(), ok.ReadUInt64(), ok.ReadUInt32()));
            }

            await owner.SendAsync(WorldOpcode.CmsgTurnInPetition, U64(charter.Value));
            Assert.Equal(4u, BitConverter.ToUInt32(await owner.ReadUntilAsync(WorldOpcode.SmsgTurnInPetitionResults)));

            // The ninth signature, then the turn-in.
            await signers[8].SendAsync(WorldOpcode.CmsgPetitionSign, [.. U64(charter.Value), 0]);
            await signers[8].ReadUntilAsync(WorldOpcode.SmsgPetitionSignResults);
            await owner.SendAsync(WorldOpcode.CmsgPetitionShowSignatures, U64(charter.Value));
            var signatures = new PacketReader(await owner.ReadUntilAsync(WorldOpcode.SmsgPetitionShowSignatures));
            signatures.ReadUInt64();
            signatures.ReadUInt64();
            Assert.Equal(petitionId, signatures.ReadUInt32());
            Assert.Equal(9, signatures.ReadByte());

            await owner.SendAsync(WorldOpcode.CmsgTurnInPetition, U64(charter.Value));
            Assert.Equal(0u, BitConverter.ToUInt32(await owner.ReadUntilAsync(WorldOpcode.SmsgTurnInPetitionResults)));

            Assert.Equal(0u, await h.Host.PlayerStateAsync("Owner", p => p.Inventory.GetItemCount(5863)));
            (string Name, int Members, byte OwnerRank, byte SignerRank) guild = await h.Host.PlayerStateAsync("Owner", p =>
            {
                Guild g = h.Social(p).Context.Guilds.GetGuildOf(p)!;
                return (g.Name, g.MemberCount, g.Find(p.Guid.Low)!.Rank, g.Find(2)!.Rank);
            });
            Assert.Equal(("Arcane Order", 10, (byte)0, (byte)4), guild);

            await owner.SendAsync(WorldOpcode.CmsgGuildRoster, []);
            Assert.Equal(10u, BitConverter.ToUInt32(await owner.ReadUntilAsync(WorldOpcode.SmsgGuildRoster)));

            await h.FlushAsync();
            Assert.Empty(h.Petitions.Snapshot()); // the turn-in removed the petition...
            GuildData stored = Assert.Single(await h.Host.WorldServices.GetRequiredService<ISocialStore>().GetGuildsAsync()); // ...and stored the guild
            Assert.Equal(10, stored.Members.Count);
        }
        finally
        {
            foreach (WorldTestClient client in signers)
            {
                await client.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task DestroyingTheCharter_RemovesThePetition_SoANewOneCanBeBought()
    {
        await using Harness h = await Harness.StartAsync();
        await using WorldTestClient owner = await h.EnterAsync("OWNER", "Owner");
        await owner.SendAsync(WorldOpcode.CmsgPetitionBuy, BuyPayload("Arcane Order"));
        await owner.ReadUntilAsync(WorldOpcode.SmsgItemPushResult);
        await h.FlushAsync();
        Assert.Single(h.Petitions.Snapshot());
        (byte bag, byte slot) = await h.Host.PlayerStateAsync("Owner", p =>
        {
            Item item = p.Inventory.AllItems.Single(i => i.Entry == 5863);
            return (item.BagSlot, item.Slot);
        });

        await owner.SendAsync(WorldOpcode.CmsgDestroyitem, [bag, slot, 0, 0, 0, 0]);
        await h.Host.WaitForWorldAsync(() => h.Social(h.Host.World.FindOnlinePlayer("Owner")!).Context.Petitions.GetByOwner(1) is null, "the petition to go with the charter");
        await h.FlushAsync();
        Assert.Empty(h.Petitions.Snapshot());

        await owner.SendAsync(WorldOpcode.CmsgPetitionBuy, BuyPayload("Second Order"));
        await owner.ReadUntilAsync(WorldOpcode.SmsgItemPushResult);
        await h.FlushAsync();
        Assert.Equal("Second Order", Assert.Single(h.Petitions.Snapshot()).Name);
    }

    [Fact]
    public async Task APetitionWhoseCharterIsGone_IsRemovedWhenItsOwnerLogsIn()
    {
        await using Harness h = await Harness.StartAsync();
        byte[] key = await h.Host.AddAccountAsync("ORPHAN");
        await using (WorldTestClient first = await h.Host.ConnectAsync())
        {
            await first.AuthenticateAsync("ORPHAN", key);
            await first.CreateCharacterAsync("Orphan");
        }

        // A petition left by a crash between the item save and the petition write: no charter in the bags.
        SocialFeature social = h.Host.WorldServices.GetRequiredService<SocialFeature>();
        await h.Host.World.InvokeAsync(() =>
        {
            social.Context.Petitions.Load([new PetitionData(7, 1, 4242, "Lost Order", [])]);
            return true;
        });
        Assert.Single(await h.Host.World.InvokeAsync(() => social.Context.Petitions.All.ToArray()));

        await using WorldTestClient again = await h.Host.ConnectAsync();
        await again.AuthenticateAsync("ORPHAN", key);
        await again.LoginAsync(1);

        await h.Host.WaitForWorldAsync(() => social.Context.Petitions.GetByOwner(1) is null, "the orphan petition to be healed at login");
    }

    [Fact]
    public async Task AShortBuyPayload_DisconnectsTheClient()
    {
        await using Harness h = await Harness.StartAsync();
        await using WorldTestClient client = await h.EnterAsync("BAD", "Bad");
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgPetitionBuy, [.. U64(Npc.Value), 0, 0, 0, 0]);

        Assert.True(await client.IsClosedByServerAsync());
    }

    [Fact]
    public async Task ADeletedCharacter_LosesItsSignatureAndItsPetition_FromMemory()
    {
        await using Harness h = await Harness.StartAsync();
        await using WorldTestClient owner = await h.EnterAsync("OWNER", "Owner");
        await using WorldTestClient signer = await h.EnterAsync("SIGNER", "Signer");
        await owner.SendAsync(WorldOpcode.CmsgPetitionBuy, BuyPayload("Arcane Order"));
        await owner.ReadUntilAsync(WorldOpcode.SmsgItemPushResult);
        ObjectGuid charter = await h.CharterAsync("Owner");
        await signer.SendAsync(WorldOpcode.CmsgPetitionSign, [.. U64(charter.Value), 0]);
        await signer.ReadUntilAsync(WorldOpcode.SmsgPetitionSignResults);
        SocialPetitionFeature feature = h.Host.WorldServices.GetRequiredService<SocialPetitionFeature>();
        SocialFeature social = h.Host.WorldServices.GetRequiredService<SocialFeature>();

        await feature.OnCharacterDeletedAsync(null!, new ArcaneCore.Kernel.Characters.CharacterRecord { Id = 2, Name = "Signer" });
        Assert.Empty((await h.Host.World.InvokeAsync(() => social.Context.Petitions.GetByOwner(1)!)).Signatures);

        await feature.OnCharacterDeletedAsync(null!, new ArcaneCore.Kernel.Characters.CharacterRecord { Id = 1, Name = "Owner" });
        Assert.Null(await h.Host.World.InvokeAsync(() => social.Context.Petitions.GetByOwner(1)));
    }

    // --- harness ------------------------------------------------------------------------------------

    private static byte[] U64(ulong value)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(value);
        return writer.ToArray();
    }

    /// <summary>CMSG_PETITION_BUY exactly as vmangos reads it (Petition.cpp:30-49).</summary>
    private static byte[] BuyPayload(string name)
    {
        var w = new PacketWriter(96);
        w.WriteUInt64(Npc.Value);
        w.WriteUInt32(0);
        w.WriteUInt64(0);
        w.WriteCString(name);
        for (int i = 0; i < 10; i++)
        {
            w.WriteUInt32(0);
        }

        w.WriteUInt16(0);
        w.WriteByte(0);
        w.WriteUInt32(0); // index
        w.WriteUInt32(0);
        return w.ToArray();
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(WorldTestHost host) => Host = host;

        public WorldTestHost Host { get; }

        public InMemoryPetitionStore Petitions => Host.WorldServices.GetRequiredService<InMemoryPetitionStore>();

        public static async Task<Harness> StartAsync()
        {
            var items = new ItemTestContent();
            items.Templates.Templates.Add(new ItemTemplate { Entry = 5863, Class = 12, Name = "Guild Charter", DisplayId = 16161, Quality = 1, Flags = 0x2000, MaxCount = 1, Bonding = 1 });
            PetitionNpcServices.Current.Value = new object();
            try
            {
                using (items.Use())
                {
                    var host = WorldTestHost.Start();
                    SocialFeature social = host.WorldServices.GetRequiredService<SocialFeature>();
                    await social.GuildsLoaded.WaitAsync(TimeSpan.FromSeconds(10));
                    await host.WorldServices.GetRequiredService<SocialPetitionFeature>().PetitionsLoaded.WaitAsync(TimeSpan.FromSeconds(10));
                    return new Harness(host);
                }
            }
            finally
            {
                PetitionNpcServices.Current.Value = null;
            }
        }

        public async Task<WorldTestClient> EnterAsync(string account, string name, uint money = 10_000)
        {
            WorldTestClient client = await Host.EnterWorldAsync(account, name);
            await Host.WaitForWorldAsync(() => Host.World.FindOnlinePlayer(name)!.VisibleObjects.Contains(Npc), "the petitioner becomes visible");
            await Host.OnWorldAsync(() => Host.World.FindOnlinePlayer(name)!.Money = money);
            await client.CollectAsync();
            return client;
        }

        public Creature Creature(string playerName)
            => (Creature)Host.World.FindOnlinePlayer(playerName)!.Map!.FindObject(Npc)!;

        public SocialFeature Social(Player player) => ((WorldSession)player.Session).Services.GetRequiredService<SocialFeature>();

        public Task<ObjectGuid> CharterAsync(string name) => Host.PlayerStateAsync(name, p => p.Inventory.AllItems.Single(i => i.Entry == 5863).Guid);

        public async Task<ulong> GuidAsync(string name) => (await Host.PlayerStateAsync(name, p => p.Guid)).Value;

        /// <summary>Every petition write queued so far has been attempted.</summary>
        public Task FlushAsync() => Host.WorldServices.GetRequiredService<SocialFeature>().Context.Persistence.FlushAsync();

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}

/// <summary>A petitioner and tabard designer (classic-db creature_template.npcflag 0x601) standing where new characters appear.</summary>
internal sealed class PetitionNpcServices : IWorldTestServices
{
    public static readonly AsyncLocal<object?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is null)
        {
            return;
        }

        services.AddSingleton<ICreatureDataStore, PetitionCreatures>();
        services.AddSingleton<INpcContentStore, PetitionGossip>();
        // creature_template.gossip_menu_id for the NPC (the creature import has no such column yet).
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NpcServices:NpcTemplates:0:Entry"] = "900030",
            ["NpcServices:NpcTemplates:0:GossipMenuId"] = "708",
        }).Build());
        services.AddSingleton(new FactionTemplateCatalog([new(1, 1, 0, 1, 0, 0), new(900011, 0, 0, 8, 0, 0)]));
    }

    /// <summary>classic-db gossip menu 708 for a guild master: option_id 10 "How do I form a guild?" and 11 "I want to create a guild crest".</summary>
    private sealed class PetitionGossip : INpcContentStore
    {
        public Task<NpcContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new NpcContent(
            [],
            [new GossipMenu { Entry = 708, TextId = 5 }],
            [
                new GossipMenuOption { MenuId = 708, Id = 0, OptionIcon = 0, OptionText = "How do I form a guild?", OptionId = 10, NpcOptionNpcFlag = 0x200 },
                new GossipMenuOption { MenuId = 708, Id = 1, OptionIcon = 0, OptionText = "I want to create a guild crest.", OptionId = 11, NpcOptionNpcFlag = 0x400 },
            ],
            [], [], [], [], [], [], []));
    }

    private sealed class PetitionCreatures : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CreatureContent(
            [new CreatureTemplate { Entry = 900030, Name = "Synthetic guild master", Faction = 900011, NpcFlags = 0x601, DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 900031, Entry = 900030, MapId = 0, X = -8948.95f, Y = -132.49f, Z = 83.53f }], [], [], []));
    }
}
