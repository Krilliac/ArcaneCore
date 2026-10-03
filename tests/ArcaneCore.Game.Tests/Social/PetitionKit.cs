using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Tests.Npc;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// Players, a petitioner/tabard-designer NPC, the real NPC services and the real inventory, around a
/// <see cref="SocialFixture"/> (fake directory and persistence). Item 5863 is the guild charter; item
/// 91005 fills a backpack slot.
/// </summary>
internal sealed class PetitionKit : IDisposable
{
    public const uint Filler = 91005;
    public const int NpcGuidCounter = 77;

    private static readonly ItemTemplateStore Templates = new(
    [
        new ItemTemplate { Entry = PetitionConstants.CharterEntry, Class = 12, Name = "Guild Charter", DisplayId = 16161, Quality = 1, Flags = 0x2000, MaxCount = 1, Bonding = 1 },
        new ItemTemplate { Entry = Filler, Class = 15, Name = "Lantern", DisplayId = 15, Quality = 1 },
    ], []);

    private readonly List<(Player Player, PlayerNpcState State)> _tracked = [];

    public PetitionKit(NpcFlags npcFlags = NpcFlags.Petitioner | NpcFlags.TabardDesigner, GuildOptions? options = null, bool withTemplates = true)
    {
        F = new SocialFixture();
        WithTemplates = withTemplates;
        F.Context.Guilds.Options = options ?? new GuildOptions();
        F.Context.Guilds.Load([]);
        F.Context.Petitions.Load([]);
        Npc = new NpcInfo(ObjectGuid.WithEntry(HighGuid.Unit, 500, NpcGuidCounter), 500, NpcGuidCounter, npcFlags, 0,
            1, 0, 83.5f, 0.5f, true, false, false, false, 0);
        var deps = new QuestNpcDependencies { Creatures = new Lookup(this) };
        Services = new QuestNpcServices(new QuestStore(QuestContent.Empty), new NpcStore(NpcContent.Empty), deps, new QuestNpcOptions(), Sink,
            () => 1_000, NullLogger.Instance);
        F.Context.Petitions.Npc = Services;
        F.Context.Guilds.Npc = Services;
    }

    public SocialFixture F { get; }

    public bool WithTemplates { get; }

    public NpcInfo Npc { get; set; }

    public NpcServiceKit.RecordingSink Sink { get; } = new();

    public QuestNpcServices Services { get; }

    public PetitionManager Petitions => F.Context.Petitions;

    public GuildManager Guilds => F.Context.Guilds;

    public FakePersistence Persistence => F.Persistence;

    /// <summary>An online player next to the NPC with a real inventory, money and NPC state.</summary>
    public Player Add(uint guid, Race race = Race.Human, int accountId = 0, uint money = 10_000)
    {
        Player player = F.AddPlayer(guid, race, accountId: accountId);
        player.Inventory.Templates = WithTemplates ? Templates : ItemTemplateStore.Empty;
        player.Inventory.GuidAllocator = new ItemGuidAllocator(guid * 1000);
        player.Inventory.Load([]);
        player.Money = money;
        F.World.RunTick(0);
        PlayerNpcState state = Services.Track(player);
        Services.CompleteLoad(state, new CharacterQuestData([], []));
        _tracked.Add((player, state));
        F.Session(player).Clear();
        return player;
    }

    public List<byte[]> Sent(Player player, WorldOpcode opcode) => F.Sent(player, opcode);

    /// <summary>The player buys a charter named <paramref name="name"/> and returns the charter item.</summary>
    public Item Buy(Player player, string name = "Arcane Order")
    {
        Petitions.Buy(player, Npc.Guid, name);
        return Charter(player) ?? throw new InvalidOperationException("no charter was bought");
    }

    public Item? Charter(Player player) => player.Inventory.AllItems.FirstOrDefault(i => i.Entry == PetitionConstants.CharterEntry);

    /// <summary>A completed petition: <paramref name="owner"/> buys and <paramref name="signers"/> sign.</summary>
    public (Petition Petition, Item Charter) Signed(Player owner, params Player[] signers)
    {
        Item charter = Buy(owner);
        foreach (Player signer in signers)
        {
            Petitions.Sign(signer, charter.Guid);
        }

        return (Petitions.GetByOwner(owner.Guid.Low)!, charter);
    }

    public void ClearAll() => F.ClearAll();

    public void Dispose() => F.Dispose();

    private sealed class Lookup(PetitionKit kit) : ICreatureLookup
    {
        public NpcInfo? Find(Player player, ObjectGuid guid) => guid == kit.Npc.Guid ? kit.Npc : null;
    }
}
