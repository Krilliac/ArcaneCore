using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Spells;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// <see cref="SpellStoreFactory.Build"/> carries <c>spell_script_target.TargetCondition</c> onto the store's rows. Dropping the argument
/// would leave the targeting tests green (they build <see cref="SpellStore"/> by hand), so this pins the hand-over. Written for the
/// integration gate; not run in the lane.
/// </summary>
public sealed class SpellStoreFactoryScriptTargetConditionTests
{
    [Fact]
    public void ANonEmptyTargetCondition_ReachesTheStoreRowAndParses()
    {
        var content = new SpellContent([new SpellTemplateRow { Id = 990901 }], [], [], [], [], [], [])
        {
            ScriptTargets =
            [
                new SpellScriptTargetRow { SpellId = 990901, Type = 1, TargetEntry = 77, InverseEffectMask = 0, TargetCondition = "health:5:2" },
                new SpellScriptTargetRow { SpellId = 990901, Type = 1, TargetEntry = 78, InverseEffectMask = 0 },
                new SpellScriptTargetRow { SpellId = 424242, Type = 1, TargetEntry = 79, InverseEffectMask = 0, TargetCondition = "gostate:2" },
            ],
        };

        SpellStore store = SpellStoreFactory.Build(content);

        IReadOnlyList<SpellStore.ScriptTarget> rows = store.GetScriptTargets(990901);
        Assert.Equal(2, rows.Count); // the row of an unknown spell is skipped
        SpellStore.ScriptTarget conditional = Assert.Single(rows, r => r.TargetEntry == 77);
        Assert.Equal("health:5:2", conditional.Condition);
        Assert.NotNull(conditional.ParsedCondition);
        SpellStore.ScriptTarget plain = Assert.Single(rows, r => r.TargetEntry == 78);
        Assert.Equal(string.Empty, plain.Condition);
        Assert.Null(plain.ParsedCondition);
    }
}
