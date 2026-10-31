using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.SmartAi.SmartRows;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi.SmartAi;

/// <summary>
/// A creature's gossip reaches its SmartAI: CMSG_GOSSIP_HELLO raises SMART_EVENT_GOSSIP_HELLO (64) and a selected option raises
/// SMART_EVENT_GOSSIP_SELECT (62) with the menu id and the option id (AzerothCore SmartAI::sGossipHello / sGossipSelect, SmartAI.cpp:1202-1210;
/// SmartScript.cpp:4780-4787). The default menu is shown as before: SmartAI does not suppress it.
/// </summary>
public sealed class SmartAiGossipTests
{
    private const uint MenuId = 7;
    private const uint HelloSpell = 111;
    private const uint SelectSpell = 222;

    private static NpcContent Content() => NpcContent.Empty with
    {
        GossipMenus = [new GossipMenu { Entry = MenuId, TextId = 2039 }],
        GossipMenuOptions =
        [
            new GossipMenuOption { MenuId = MenuId, Id = 0, OptionId = (byte)GossipOption.Gossip, NpcOptionNpcFlag = (uint)NpcFlags.Gossip, OptionText = "First" },
            new GossipMenuOption { MenuId = MenuId, Id = 1, OptionId = (byte)GossipOption.Gossip, NpcOptionNpcFlag = (uint)NpcFlags.Gossip, OptionText = "Second" },
        ],
    };

    private sealed class Rig : IDisposable
    {
        private readonly SmartRig _rig;

        public Rig(IEnumerable<SmartScriptRow> rows)
        {
            _rig = SmartRig.Start(rows);
            Creature wolf = _rig.Wolf;
            Npc = new NpcInfo(wolf.Guid, wolf.Entry, 1, NpcFlags.Gossip, _rig.Map.MapId, _rig.Player.X + 1, _rig.Player.Y, _rig.Player.Z, 0.5f, true, false, false, false, MenuId);
            Services = new QuestNpcServices(new QuestStore(QuestContent.Empty), new NpcStore(Content()),
                new QuestNpcDependencies(Creatures: new Lookup(Npc)), new QuestNpcOptions(), new Sink(), () => 100, NullLogger.Instance);
            Services.CompleteLoad(Services.Track(_rig.Player), new CharacterQuestData([], []));
            _rig.Session.Clear();
        }

        public NpcInfo Npc { get; }

        public QuestNpcServices Services { get; }

        public SmartRig Smart => _rig;

        public List<byte[]> Gossip()
        {
            var packets = new List<byte[]>();
            while (_rig.Session.Sent.TryDequeue(out var p))
            {
                if (p.Opcode == WorldOpcode.SmsgGossipMessage) packets.Add(p.Payload);
            }

            return packets;
        }

        public void Dispose() => _rig.Dispose();

        private sealed class Lookup(NpcInfo npc) : ICreatureLookup
        {
            public NpcInfo? Find(Player player, ObjectGuid guid) => guid == npc.Guid ? npc : null;
        }

        private sealed class Sink : IQuestNpcSink
        {
            public void QuestsChanged(Player player, IReadOnlyList<CharacterQuestStatus> rows) { }

            public void TaxiMaskChanged(Player player, IReadOnlyList<uint> mask) { }

            public void CharacterChanged(Player player) { }
        }
    }

    [Fact]
    public void GossipHello_RunsTheCreaturesEvent64Row_WithThePlayerAsInvoker()
    {
        using var rig = new Rig([CreatureRow(WolfEntry, 0, SmartEvent.GossipHello, SmartAction.Cast, SmartTarget.ActionInvoker, a1: HelloSpell)]);

        rig.Services.GossipHello(rig.Smart.Player, rig.Npc.Guid);

        Assert.Single(rig.Smart.Spells.Casts, c => c.Spell == HelloSpell && ReferenceEquals(c.Target, rig.Smart.Player));
    }

    [Fact]
    public void GossipHello_KeepsTheDefaultMenu_ByteForByte()
    {
        using var scripted = new Rig([CreatureRow(WolfEntry, 0, SmartEvent.GossipHello, SmartAction.Cast, SmartTarget.ActionInvoker, a1: HelloSpell)]);
        using var plain = new Rig([]);

        scripted.Services.GossipHello(scripted.Smart.Player, scripted.Npc.Guid);
        plain.Services.GossipHello(plain.Smart.Player, plain.Npc.Guid);

        byte[] withRow = Assert.Single(scripted.Gossip());
        byte[] without = Assert.Single(plain.Gossip());
        Assert.Equal(without, withRow);
        Assert.Single(scripted.Smart.Spells.Casts);
        Assert.Empty(plain.Smart.Spells.Casts);
    }

    [Fact]
    public void GossipSelect_Event62_RunsOnlyForTheMenuAndOptionItNames()
    {
        using var rig = new Rig([CreatureRow(WolfEntry, 0, SmartEvent.GossipSelect, SmartAction.Cast, SmartTarget.ActionInvoker, e1: MenuId, e2: 1, a1: SelectSpell)]);

        rig.Services.GossipHello(rig.Smart.Player, rig.Npc.Guid);
        rig.Services.GossipSelectOption(rig.Smart.Player, rig.Npc.Guid, 0, null);
        Assert.Empty(rig.Smart.Spells.Casts);

        rig.Services.GossipHello(rig.Smart.Player, rig.Npc.Guid);
        rig.Services.GossipSelectOption(rig.Smart.Player, rig.Npc.Guid, 1, null);
        Assert.Single(rig.Smart.Spells.Casts, c => c.Spell == SelectSpell && ReferenceEquals(c.Target, rig.Smart.Player));
    }

    [Fact]
    public void GossipSelect_Event62_DoesNotRunForAnotherMenuId()
    {
        using var rig = new Rig([CreatureRow(WolfEntry, 0, SmartEvent.GossipSelect, SmartAction.Cast, SmartTarget.ActionInvoker, e1: MenuId + 1, e2: 1, a1: SelectSpell)]);

        rig.Services.GossipHello(rig.Smart.Player, rig.Npc.Guid);
        rig.Services.GossipSelectOption(rig.Smart.Player, rig.Npc.Guid, 1, null);

        Assert.Empty(rig.Smart.Spells.Casts);
    }

    private sealed class ThrowingAi(Creature creature) : AggressorAI(creature)
    {
        public override void OnGossipHello(Player player) => throw new InvalidOperationException("hello boom");

        public override void OnGossipSelect(Player player, uint menuId, uint optionId) => throw new InvalidOperationException("select boom");
    }

    [Fact]
    public void AThrowingAiHook_IsRecordedOnce_AndTheGossipReplyStillGoesOut()
    {
        using var rig = new Rig([]);
        rig.Smart.Wolf.AI = new ThrowingAi(rig.Smart.Wolf);
        long before = FaultLedger.Shared.Snapshot().Where(s => s.Site == "npc.gossip-hello-ai").Sum(s => s.Count);

        rig.Services.GossipHello(rig.Smart.Player, rig.Npc.Guid);

        Assert.Single(rig.Gossip());
        Assert.Equal(before + 1, FaultLedger.Shared.Snapshot().Where(s => s.Site == "npc.gossip-hello-ai").Sum(s => s.Count));

        long selectBefore = FaultLedger.Shared.Snapshot().Where(s => s.Site == "npc.gossip-select-ai").Sum(s => s.Count);
        rig.Services.GossipSelectOption(rig.Smart.Player, rig.Npc.Guid, 0, null);
        Assert.Equal(selectBefore + 1, FaultLedger.Shared.Snapshot().Where(s => s.Site == "npc.gossip-select-ai").Sum(s => s.Count));
    }
}
