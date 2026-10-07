using Xunit;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// One player next to one configurable NPC, with the NPC services wired to the real inventory
/// (<see cref="InventoryItemService"/>) and whatever extra collaborators a test passes.
/// Items 91001.. are synthetic vendor goods.
/// </summary>
internal sealed class NpcServiceKit : IDisposable
{
    public const uint Bread = 91001;       // stackable 20, buy 25, sell 6
    public const uint Sword = 91002;       // weapon (2/7), ilvl 10, quality 2, durability 50, buy 1000, sell 250
    public const uint Junk = 91003;        // not sellable (sell price 0)
    public const uint Helm = 91004;        // armor (4/1), ilvl 10, quality 1, durability 40
    public const uint Lantern = 91005;     // single, buy 300, sell 75
    public const uint Pouch = 91006;       // 6-slot bag
    public const uint Wand = 91007;        // single, an on-use spell with 10 expendable charges (-10), sell 200
    public const uint Entry = 500;

    public static IReadOnlyList<ItemTemplate> Templates { get; } =
    [
        new() { Entry = Bread, Class = 0, Name = "Bread", DisplayId = 11, Quality = 1, Stackable = 20, BuyPrice = 25, SellPrice = 6, BuyCount = 5 },
        new() { Entry = Sword, Class = 2, SubClass = 7, Name = "Sword", DisplayId = 12, Quality = 2, ItemLevel = 10, InventoryType = 13,
            Delay = 2000, MaxDurability = 50, BuyPrice = 1000, SellPrice = 250, Damages = [new ItemDamage(5, 9, 0)] },
        new() { Entry = Junk, Class = 15, Name = "Junk", DisplayId = 13, Stackable = 5 },
        new() { Entry = Helm, Class = 4, SubClass = 1, Name = "Helm", DisplayId = 14, Quality = 1, ItemLevel = 10, InventoryType = 1,
            Armor = 10, MaxDurability = 40, BuyPrice = 400, SellPrice = 100 },
        new() { Entry = Lantern, Class = 15, Name = "Lantern", DisplayId = 15, Quality = 1, BuyPrice = 300, SellPrice = 75 },
        new() { Entry = Pouch, Class = 1, SubClass = 0, Name = "Pouch", DisplayId = 16, Quality = 1, InventoryType = 18, ContainerSlots = 6 },
        new() { Entry = Wand, Class = 0, Name = "Wand", DisplayId = 17, Quality = 1, BuyPrice = 800, SellPrice = 200,
            Spells = [new ItemSpell(133, 0, -10, 0, 0, 0, 0)] },
    ];

    public static ItemTemplateStore Store { get; } = new(Templates, []);

    public NpcServiceKit(NpcFlags flags, NpcContent? content = null, QuestNpcDependencies? extra = null, float npcDistance = 1,
        bool hostile = false, uint mapId = 0, long now = 1_000, RepairCostTable? repair = null, BankBagSlotPriceTable? bankPrices = null,
        Func<Player, byte, bool>? persistBankSlots = null)
    {
        Now = now;
        Map map = World.GetMap(mapId);
        Player = TestWorld.CreatePlayer(1, 0, 0, Session, mapId);
        Player.Inventory.Templates = Store;
        Player.Inventory.GuidAllocator = new ItemGuidAllocator();
        Player.Inventory.Load([]);
        World.AddPlayer(Player);
        World.RunTick(0);
        Npc = new NpcInfo(ObjectGuid.WithEntry(HighGuid.Unit, Entry, 77), Entry, 77, flags, map.MapId,
            Player.X + npcDistance, Player.Y, Player.Z, 0.5f, true, hostile, false, false, 0);
        Lookup = new MutableLookup(this);
        Items = new InventoryItemService(() => Store, repair ?? RepairCostTable.Empty, bankPrices ?? BankBagSlotPriceTable.Empty,
            () => Now, persistBankSlots);
        Items.SessionStarted(Player);
        QuestNpcDependencies deps = (extra ?? new QuestNpcDependencies()) with { Creatures = Lookup };
        deps = deps with { Items = deps.Items ?? Items };
        Npcs = new NpcStore(content ?? NpcContent.Empty);
        Services = new QuestNpcServices(new QuestStore(QuestContent.Empty), Npcs, deps, new QuestNpcOptions(), Sink, () => Now,
            NullLogger.Instance);
        State = Services.Track(Player);
        Services.CompleteLoad(State, new CharacterQuestData([], []));
        Session.Clear();
    }

    public long Now { get; set; }

    public WorldRuntime World { get; } = TestWorld.CreateRuntime();

    public FakeSession Session { get; } = new();

    public RecordingSink Sink { get; } = new();

    public Player Player { get; }

    public NpcInfo Npc { get; set; }

    public MutableLookup Lookup { get; }

    public InventoryItemService Items { get; }

    public NpcStore Npcs { get; }

    public QuestNpcServices Services { get; }

    public PlayerNpcState State { get; }

    public void Dispose() => World.Dispose();

    public Item Give(uint entry, uint count = 1) => ItemTestData.Give(Player.Inventory, entry, count);

    public List<(WorldOpcode Opcode, byte[] Payload)> Drain()
    {
        var list = new List<(WorldOpcode, byte[])>();
        while (Session.Sent.TryDequeue(out var p))
        {
            list.Add(p);
        }

        return list;
    }

    public byte[] Single(WorldOpcode opcode) => Assert.Single(Drain(), p => p.Opcode == opcode).Payload;

    public bool Sent(WorldOpcode opcode) => Session.Sent.Any(p => p.Opcode == opcode);

    internal sealed class MutableLookup(NpcServiceKit kit) : ICreatureLookup
    {
        public NpcInfo? Find(Player player, ObjectGuid guid) => guid == kit.Npc.Guid ? kit.Npc : null;
    }

    internal sealed class RecordingSink : IQuestNpcSink
    {
        public int CharacterChanges { get; private set; }

        public Action<Player>? OnCharacterChanged { get; set; }

        public List<uint[]> TaxiMasks { get; } = [];

        public void QuestsChanged(Player player, IReadOnlyList<CharacterQuestStatus> rows)
        {
        }

        public void TaxiMaskChanged(Player player, IReadOnlyList<uint> mask) => TaxiMasks.Add([.. mask]);

        public void CharacterChanged(Player player)
        {
            CharacterChanges++;
            OnCharacterChanged?.Invoke(player);
        }
    }
}
