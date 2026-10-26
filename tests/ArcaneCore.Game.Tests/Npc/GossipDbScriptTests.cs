using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// The gossip DB scripts (world schema 42) as the quest service raises them for the creature systems: a selected option's
/// <c>gossip_menu_option.action_script_id</c> after the option's own action, the creature as the source (mangos-classic Player::OnGossipSelect,
/// Player.cpp:11953-11960), and the chosen text's <c>gossip_menu.script_id</c> when the menu is shown, the player as the source
/// (Player::GetGossipTextId, :11979-12004). The ids are classic-db z2815's: option 1 of menu 21 runs script 21 (QUEST_EXPLORED 6981),
/// menu 1405's text 2039 runs script 1405 (the Screecher Spirit's kill credit); the menus here are shortened to those lines.
/// </summary>
public sealed class GossipDbScriptTests
{
    [Fact]
    public void SelectingAScriptedOption_StartsItsScript_WithTheCreatureAsTheSource()
    {
        NpcContent content = NpcContent.Empty with
        {
            GossipMenuOptions =
            [
                new GossipMenuOption { MenuId = 0, Id = 0, OptionId = (byte)GossipOption.Gossip, NpcOptionNpcFlag = (uint)NpcFlags.Gossip,
                    OptionText = "Can you tell me about this shard?", ActionScriptId = 21 },
                new GossipMenuOption { MenuId = 0, Id = 1, OptionId = (byte)GossipOption.Gossip, NpcOptionNpcFlag = (uint)NpcFlags.Gossip,
                    OptionText = "Unscripted" },
            ],
        };
        using var kit = new NpcServiceTestKit(NpcFlags.Gossip, content);
        var started = new List<(Player Player, ObjectGuid Source, uint Script, bool PlayerIsSource)>();
        kit.Services.GossipScriptStarted += (player, npc, script, playerIsSource) => started.Add((player, npc, script, playerIsSource));

        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        Assert.Empty(started); // the default menu has no text script
        kit.Services.GossipSelectOption(kit.Player, kit.Npc.Guid, 1, null);
        Assert.Empty(started);
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        kit.Services.GossipSelectOption(kit.Player, kit.Npc.Guid, 0, null);

        Assert.Equal([(kit.Player, kit.Npc.Guid, 21u, false)], started);
    }

    [Fact]
    public void SelectingAnOption_OfAMenuBuiltForAnotherCreature_IsIgnored()
    {
        NpcContent content = NpcContent.Empty with
        {
            GossipMenuOptions =
            [
                new GossipMenuOption { MenuId = 0, Id = 0, OptionId = (byte)GossipOption.Gossip, NpcOptionNpcFlag = (uint)NpcFlags.Gossip,
                    OptionText = "Scripted", ActionScriptId = 21 },
            ],
        };
        using var kit = new NpcServiceTestKit(NpcFlags.Gossip, content);
        var started = new List<ObjectGuid>();
        kit.Services.GossipScriptStarted += (_, npc, _, _) => started.Add(npc);

        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        ObjectGuid first = kit.Npc.Guid;
        kit.Npc = kit.Npc with { Guid = ObjectGuid.WithEntry(HighGuid.Unit, NpcServiceTestKit.Entry, 78) };
        kit.Services.GossipSelectOption(kit.Player, kit.Npc.Guid, 0, null);
        Assert.Empty(started); // RED on main: the second creature runs the first one's script

        kit.Npc = kit.Npc with { Guid = first };
        kit.Services.GossipSelectOption(kit.Player, first, 0, null);
        Assert.Equal([first], started);
    }

    [Fact]
    public void ShowingAMenuWhoseTextHasAScript_StartsIt_WithThePlayerAsTheSource()
    {
        NpcContent content = NpcContent.Empty with
        {
            GossipMenus = [new GossipMenu { Entry = 1405, TextId = 2039, ScriptId = 1405 }, new GossipMenu { Entry = 1406, TextId = 2040 }],
        };
        using var kit = new NpcServiceTestKit(NpcFlags.Gossip, content);
        var started = new List<(ObjectGuid Source, uint Script, bool PlayerIsSource)>();
        kit.Services.GossipScriptStarted += (_, npc, script, playerIsSource) => started.Add((npc, script, playerIsSource));

        kit.Npc = kit.Npc with { GossipMenuId = 1406 };
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        Assert.Empty(started);
        kit.Npc = kit.Npc with { GossipMenuId = 1405 };
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);

        Assert.Equal([(kit.Npc.Guid, 1405u, true)], started);
    }

    [Fact]
    public void QuestList_OfACreatureWithoutGossip_StartsTheMenuTextScript_WithThePlayerAsTheSource()
    {
        // vmangos SendPreparedQuest looks up the default menu text only when the list has more than one quest
        // (Player.cpp:12450-12465). That lookup is GetGossipTextId, so the text's script starts. Hello on a
        // quest giver with no gossip flag never reaches SendPreparedGossip's own lookup.
        using var kit = NewQuestListKit(quests: 2, menuId: 1405, scriptId: 1405);
        var started = new List<(ObjectGuid Npc, uint Script, bool PlayerIsSource)>();
        kit.Services.GossipScriptStarted += (_, npc, script, playerIsSource) => started.Add((npc, script, playerIsSource));

        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);

        Assert.Equal([(kit.Npc.Guid, 1405u, true)], started);
        Assert.Contains(kit.Session.Sent, p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestList);
    }

    [Fact]
    public void OneQuest_OpensDetails_AndDoesNotStartTheMenuTextScript()
    {
        using var kit = NewQuestListKit(quests: 1, menuId: 1405, scriptId: 1405);
        var started = new List<uint>();
        kit.Services.GossipScriptStarted += (_, _, script, _) => started.Add(script);

        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);

        Assert.Empty(started);
        Assert.Contains(kit.Session.Sent, p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestDetails);
    }

    private static QuestListKit NewQuestListKit(int quests, uint menuId, uint scriptId) => new(quests, menuId, scriptId);

    private sealed class QuestListKit : IDisposable
    {
        public QuestListKit(int quests, uint menuId, uint scriptId)
        {
            Player = TestWorld.CreatePlayer(1, 0, 0, Session);
            World.AddPlayer(Player);
            Npc = new NpcInfo(ObjectGuid.WithEntry(HighGuid.Unit, 1, 1), 1, 1, NpcFlags.QuestGiver, 0, 0, 0, Player.Z,
                0.5f, true, false, false, false, menuId);
            QuestTemplate[] templates = Enumerable.Range(0, quests)
                .Select(i => new QuestTemplate { Entry = (uint)(15 + i), Method = 2, Title = $"Task {i}" })
                .ToArray();
            var content = new QuestContent(templates,
                templates.Select(t => new CreatureQuestRelation { Id = 1, Quest = t.Entry }).ToArray(), []);
            var npcContent = NpcContent.Empty with
            {
                GossipMenus = [new GossipMenu { Entry = menuId, TextId = 2039, ScriptId = scriptId }],
            };
            Services = new QuestNpcServices(new QuestStore(content), new NpcStore(npcContent),
                new QuestNpcDependencies(Creatures: new Lookup(Npc)), new QuestNpcOptions(), new Sink(), () => 100,
                NullLogger.Instance);
            PlayerNpcState state = Services.Track(Player);
            Services.CompleteLoad(state, new CharacterQuestData([], []));
        }

        public WorldRuntime World { get; } = TestWorld.CreateRuntime();

        public FakeSession Session { get; } = new();

        public Player Player { get; }

        public NpcInfo Npc { get; }

        public QuestNpcServices Services { get; }

        public void Dispose() => World.Dispose();

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
}
