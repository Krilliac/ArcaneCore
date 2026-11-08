using ArcaneCore.Game.Combat;
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
            new GossipMenuOption { MenuId = MenuId, Id = 2, OptionId = (byte)GossipOption.Gossip, NpcOptionNpcFlag = (uint)NpcFlags.Gossip, OptionText = "Third", BoxCoded = 1 },
        ],
    };

    private sealed class Rig : IDisposable
    {
        private readonly SmartRig _rig;
        private readonly QuestSeam _quests = new();

        public Rig(IEnumerable<SmartScriptRow> rows, NpcFlags flags = NpcFlags.Gossip)
        {
            _rig = SmartRig.Start(rows, scriptQuests: _quests);
            Creature wolf = _rig.Wolf;
            Npc = new NpcInfo(wolf.Guid, wolf.Entry, 1, flags, _rig.Map.MapId, _rig.Player.X + 1, _rig.Player.Y, _rig.Player.Z, 0.5f, true, false, false, false, MenuId);
            Services = new QuestNpcServices(new QuestStore(QuestContent.Empty), new NpcStore(Content()),
                new QuestNpcDependencies(Creatures: new Lookup(Npc)), new QuestNpcOptions(), new Sink(), () => 100, NullLogger.Instance);
            _quests.Services = Services;
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

        /// <summary>The world's seam (ScriptQuestEvents) reduced to what CLOSE_GOSSIP needs: it reaches the quest service the rig builds after the AI services.</summary>
        private sealed class QuestSeam : IScriptQuestEvents
        {
            public QuestNpcServices? Services { get; set; }

            public void GossipClosed(Player player) => Services?.ForgetGossipMenu(player);

            public void AreaExploredOrEventHappens(Player player, uint questId) { }

            public void FailQuest(Player player, uint questId) { }

            public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source) { }

            public void GroupEventFailHappens(Player player, uint questId) { }

            public IReadOnlyList<Player> GroupMembersOf(Player player) => [];
        }

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
    public void GossipHello_OnANonBattlegroundSpiritGuide_StillRunsTheEvent64Row()
    {
        // A spirit-guide-flagged creature outside a battleground is an ordinary gossip NPC: only the battleground spirit guide handler
        // (NPCHandler.cpp HandleGossipHelloOpcode) returns before the AI hook. A spirit guide is only reachable by the dead.
        using var rig = new Rig([CreatureRow(WolfEntry, 0, SmartEvent.GossipHello, SmartAction.Cast, SmartTarget.ActionInvoker, a1: HelloSpell)],
            NpcFlags.Gossip | NpcFlags.SpiritGuide);
        rig.Smart.Map.Combat.DealDamage(rig.Smart.Wolf, rig.Smart.Player, rig.Smart.Player.MaxHealth, direct: false);
        Assert.False(rig.Smart.Player.IsAlive);
        rig.Smart.Spells.Casts.Clear();

        rig.Services.GossipHello(rig.Smart.Player, rig.Npc.Guid);

        Assert.Single(rig.Smart.Spells.Casts, c => c.Spell == HelloSpell);
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

    [Fact]
    public void GossipSelect_Event62_DoesNotRunForACodedOption()
    {
        using var rig = new Rig([CreatureRow(WolfEntry, 0, SmartEvent.GossipSelect, SmartAction.Cast, SmartTarget.ActionInvoker, e1: MenuId, e2: 2, a1: SelectSpell)]);

        rig.Services.GossipHello(rig.Smart.Player, rig.Npc.Guid);
        rig.Services.GossipSelectOption(rig.Smart.Player, rig.Npc.Guid, 2, "code");

        Assert.Empty(rig.Smart.Spells.Casts);
    }

    private sealed class OrderProbeAi(Creature creature, Func<bool> menuSent, Action<bool> record) : AggressorAI(creature)
    {
        public override void OnGossipHello(Player player) => record(menuSent());
    }

    [Fact]
    public void GossipHello_RaisesTheAiHook_AfterTheGossipMenuWasSent()
    {
        using var rig = new Rig([]);
        bool? menuAlreadySent = null;
        rig.Smart.Wolf.AI = new OrderProbeAi(rig.Smart.Wolf,
            () => rig.Smart.Session.Sent.Any(p => p.Opcode == WorldOpcode.SmsgGossipMessage), sent => menuAlreadySent = sent);

        rig.Services.GossipHello(rig.Smart.Player, rig.Npc.Guid);

        Assert.True(menuAlreadySent);
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

    private static int Completes(Rig rig)
    {
        int count = 0;
        foreach ((WorldOpcode op, byte[] _) in rig.Smart.Session.Sent)
        {
            if (op == WorldOpcode.SmsgGossipComplete) count++;
        }

        return count;
    }

    [Fact]
    public void CloseGossip_FromEvent62_ClosesTheWindow_AndALaterSelectionOfTheOldMenuIsRefused()
    {
        using var rig = new Rig(
        [
            CreatureRow(WolfEntry, 0, SmartEvent.GossipSelect, SmartAction.CloseGossip, SmartTarget.ActionInvoker, e1: MenuId, e2: 1),
            CreatureRow(WolfEntry, 1, SmartEvent.GossipSelect, SmartAction.Cast, SmartTarget.ActionInvoker, e1: MenuId, e2: 0, a1: SelectSpell),
        ]);

        rig.Services.GossipHello(rig.Smart.Player, rig.Npc.Guid);
        rig.Services.GossipSelectOption(rig.Smart.Player, rig.Npc.Guid, 1, null);
        Assert.Equal(1, Completes(rig));

        // The menu was forgotten with the window (GossipDef.cpp:241), so a stale CMSG_GOSSIP_SELECT_OPTION for line 0 does nothing.
        rig.Services.GossipSelectOption(rig.Smart.Player, rig.Npc.Guid, 0, null);
        Assert.Empty(rig.Smart.Spells.Casts);
        Assert.Equal(1, Completes(rig));
    }

    [Fact]
    public void CloseGossip_FromEvent64_ClosesTheWindow_AndALaterSelectionOfTheOldMenuIsRefused()
    {
        using var rig = new Rig(
        [
            CreatureRow(WolfEntry, 0, SmartEvent.GossipHello, SmartAction.CloseGossip, SmartTarget.ActionInvoker),
            CreatureRow(WolfEntry, 1, SmartEvent.GossipSelect, SmartAction.Cast, SmartTarget.ActionInvoker, e1: MenuId, e2: 0, a1: SelectSpell),
        ]);

        rig.Services.GossipHello(rig.Smart.Player, rig.Npc.Guid);
        Assert.Equal(1, Completes(rig));

        rig.Services.GossipSelectOption(rig.Smart.Player, rig.Npc.Guid, 0, null);
        Assert.Empty(rig.Smart.Spells.Casts);
    }

    [Fact]
    public void CloseGossip_FromALinkedRow_ClosesTheWindow_AndALaterSelectionOfTheOldMenuIsRefused()
    {
        using var rig = new Rig(
        [
            CreatureRow(WolfEntry, 0, SmartEvent.GossipSelect, SmartAction.SetEventPhase, SmartTarget.Self, e1: MenuId, e2: 1, a1: 2, link: 1),
            CreatureRow(WolfEntry, 1, SmartEvent.Link, SmartAction.CloseGossip, SmartTarget.ActionInvoker),
            CreatureRow(WolfEntry, 2, SmartEvent.GossipSelect, SmartAction.Cast, SmartTarget.ActionInvoker, e1: MenuId, e2: 0, a1: SelectSpell),
        ]);

        rig.Services.GossipHello(rig.Smart.Player, rig.Npc.Guid);
        rig.Services.GossipSelectOption(rig.Smart.Player, rig.Npc.Guid, 1, null);
        Assert.Equal(1, Completes(rig));
        Assert.Equal(2u, rig.Smart.Script.Phase);

        rig.Services.GossipSelectOption(rig.Smart.Player, rig.Npc.Guid, 0, null);
        Assert.Empty(rig.Smart.Spells.Casts);
    }

    [Fact]
    public void AClosedMenu_OfTheQuestService_IsForgotten_SoAStaleSelectionIsRefused()
    {
        using var rig = new Rig([CreatureRow(WolfEntry, 0, SmartEvent.GossipSelect, SmartAction.Cast, SmartTarget.ActionInvoker, e1: MenuId, e2: 0, a1: SelectSpell)]);

        rig.Services.GossipHello(rig.Smart.Player, rig.Npc.Guid);
        rig.Services.QuestgiverCancel(rig.Smart.Player);
        rig.Services.GossipSelectOption(rig.Smart.Player, rig.Npc.Guid, 0, null);

        Assert.Empty(rig.Smart.Spells.Casts);
    }
}
