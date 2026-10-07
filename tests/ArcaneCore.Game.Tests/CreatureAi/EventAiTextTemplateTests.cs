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

public sealed class EventAiTextTemplateTests
{
    [Theory]
    [InlineData(0, 0, -3)]
    [InlineData(25, 0, -3)]
    [InlineData(25.01, 0, -2)]
    [InlineData(50, 0, -2)]
    [InlineData(50.01, 0, -1)]
    [InlineData(100, 1, 0)]
    public void WeightedChoicesThenEqualResidual(float roll, int equalIndex, int expected)
    {
        var content = new CreatureAiContent([], [], textTemplates:
            [new(1, -3, 25), new(1, -2, 25), new(1, -1, 0), new(1, 0, 0)]);
        Assert.Equal(expected, content.SelectTemplateText(1, roll, _ => equalIndex));
    }

    [Fact]
    public void MissingTemplateAndUnallocatedProbabilityReturnZero()
    {
        var content = new CreatureAiContent([], [], textTemplates: [new(1, -1, 20)]);
        Assert.Equal(0, content.SelectTemplateText(2, 10, _ => throw new Exception("No equal draw expected")));
        Assert.Equal(0, content.SelectTemplateText(1, 21, _ => throw new Exception("No equal draw expected")));
    }

    [Fact]
    public void ExplicitChanceAboveHundredKeepsSourceThresholdSemantics()
    {
        var content = new CreatureAiContent([], [], textTemplates: [new(1, -1, 125), new(1, 1, 0)]);
        Assert.Equal(-1, content.SelectTemplateText(1, 100, _ => throw new Exception("No equal draw expected")));
    }

    [Theory]
    [InlineData(0, 1, "Direct P1!")]
    [InlineData(1, 1, "Template P1!")]
    [InlineData(1, 0, "Template $N!")]
    [InlineData(2, 1, null)]
    [InlineData(1, 15, null)]
    [InlineData(3, 1, "Broadcast P1!")]
    public void Action54UsesSelectedTextAndTargetInActualMonsterChat(int templateId, int targetType, string? expected)
    {
        var row = new CreatureAiEvent
        {
            Id = 1, CreatureId = WolfEntry, EventType = (byte)EventAiEventType.Aggro,
            Action1 = new CreatureAiAction(54, -1, targetType, templateId),
        };
        var aiContent = new CreatureAiContent([row],
            [new(-1, "Direct $N!", 0, 7, 0), new(-2, "Template $N!", 0, 7, 0)],
            broadcastTexts: new BroadcastTextCatalog([new(991, "Broadcast $N!", "", 0, 7, 0, [], [])]),
            textTemplates: [new(1, -2, 0), new(3, 991, 0)]);
        var content = new CreatureContent([Template(configure: t => t.AIName = CreatureAiFactory.EventAIName)],
            [Spawn(1, WolfEntry, 5, 0)], [], [], [], aiContent);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, FakeSession session) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        Assert.Empty(Assert.IsType<CreatureEventAI>(wolf.AI).Unsupported);
        map.Combat.DealDamage(player, wolf, 1, direct: false);
        var packets = Packets(session, WorldOpcode.SmsgMessagechat);
        if (expected is null) { Assert.Empty(packets); return; }
        MonsterChat chat = ParseMonsterChat(Assert.Single(packets));
        Assert.Equal(expected, chat.Message);
        Assert.Equal(ChatType.MonsterSay, chat.Type);
        Assert.Equal(7u, chat.Language);
        Assert.Equal(targetType == 0 ? wolf.Guid.Value : player.Guid.Value, chat.Target);
    }
}
