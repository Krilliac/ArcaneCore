using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// What the widened EventAI content model changes for the running AI: positive text ids are
/// <c>broadcast_text</c> ids (classic-db carries no <c>creature_ai_texts</c> rows, every text action is positive),
/// and flag values above one byte (1024/1025 = combat action) no longer set the random-action bit.
/// </summary>
public sealed class CreatureEventAiContentTests
{
    private static CreatureAiEvent Row(uint id, EventAiEventType type, uint flags, CreatureAiAction a1, CreatureAiAction a2 = default, CreatureAiAction a3 = default)
        => new()
        {
            Id = id,
            CreatureId = WolfEntry,
            EventType = (byte)type,
            Flags = flags,
            Action1 = a1,
            Action2 = a2,
            Action3 = a3,
        };

    private static CreatureContent Content(IEnumerable<CreatureAiEvent> events, BroadcastTextCatalog catalog)
        => new(
            [Template(configure: t => t.AIName = CreatureAiFactory.EventAIName)],
            [Spawn(1, WolfEntry, 5, 0)], [], [], [],
            new CreatureAiContent(events, [], catalog));

    [Fact]
    public void ATextActionWithAPositiveId_SpeaksTheBroadcastTextWithItsChatType()
    {
        // The row shape of every classic-db text action: action type 1, param1 = broadcast_text id.
        var catalog = new BroadcastTextCatalog([new BroadcastText(91150, "%s squeals and bolts for cover!", string.Empty, 2, 0, 0, [0, 0, 0], [0, 0, 0])]);
        CreatureContent content = Content([Row(1, EventAiEventType.Aggro, 0, new CreatureAiAction((byte)EventAiActionType.Text, 91150, 0, 0))], catalog);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, FakeSession session) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);

        map.Combat.DealDamage(player, wolf, 1, direct: false);

        MonsterChat chat = ParseMonsterChat(Assert.Single(Packets(session, WorldOpcode.SmsgMessagechat)));
        Assert.Equal(ChatType.MonsterEmote, chat.Type);
        Assert.Equal("%s squeals and bolts for cover!", chat.Message);
    }

    [Fact]
    public void CombatActionFlags_DoNotMakeAThreeActionRowRandom()
    {
        // Flags 1024 = EFLAG_COMBAT_ACTION. Clamped to a byte (255) the row also carried 0x20 (random action) and 0x01.
        var catalog = new BroadcastTextCatalog(
        [
            new BroadcastText(1, "one", string.Empty, 0, 0, 0, [0, 0, 0], [0, 0, 0]),
            new BroadcastText(2, "two", string.Empty, 0, 0, 0, [0, 0, 0], [0, 0, 0]),
            new BroadcastText(3, "three", string.Empty, 0, 0, 0, [0, 0, 0], [0, 0, 0]),
        ]);
        CreatureAiAction Say(int id) => new((byte)EventAiActionType.Text, id, 0, 0);
        CreatureContent content = Content([Row(1, EventAiEventType.Aggro, 1024, Say(1), Say(2), Say(3))], catalog);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, FakeSession session) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);

        map.Combat.DealDamage(player, wolf, 1, direct: false);

        Assert.Equal(
            ["one", "two", "three"],
            Packets(session, WorldOpcode.SmsgMessagechat).Select(p => ParseMonsterChat(p).Message));
    }
}
