using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots.Combat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

public sealed class PlayerbotTargetFilterTests
{
    private static Creature Make(CreatureTemplate template)
        => new(1, template, spawn: null, new CreatureContent([template], [], [], [], []), new Random(1));

    private static CreatureTemplate Template() => new() { Entry = 1, Name = "townsman", Faction = 1, MinLevelHealth = 20, MaxLevelHealth = 20 };

    [Fact]
    public void OnOtherLevel_ExactlyTheLimitIsTheSameLevel_AndJustOverIsAnother()
    {
        var a = new Vector3(0, 0, 0);
        Assert.False(PlayerbotTargetFilter.OnOtherLevel(a, new Vector3(5, 5, 10.0f)));
        Assert.False(PlayerbotTargetFilter.OnOtherLevel(new Vector3(0, 0, 10.0f), a));
        Assert.True(PlayerbotTargetFilter.OnOtherLevel(a, new Vector3(0, 0, 10.01f)));
        Assert.True(PlayerbotTargetFilter.OnOtherLevel(new Vector3(0, 0, -10.01f), a));
        Assert.True(PlayerbotTargetFilter.OnOtherLevel(a, new Vector3(0, 0, 5.01f), 5f));
    }

    [Fact]
    public void IsCivilian_TemplateFlagOrBehaviourFlag()
    {
        Assert.False(PlayerbotTargetFilter.IsCivilian(Make(Template())));
        Assert.True(PlayerbotTargetFilter.IsCivilian(Make(Template() with { Civilian = true })));
        Assert.True(PlayerbotTargetFilter.IsCivilian(Make(Template() with { ExtraFlagsDialect = CreatureExtraFlagsDialect.CMangos, ExtraFlags = 0x00010000 })));
    }

    [Fact]
    public void AggroesOnSight_FollowsTheAi_AndAssumesYesWithoutOne()
    {
        Creature creature = Make(Template());
        Assert.Null(creature.AI);
        Assert.True(PlayerbotTargetFilter.AggroesOnSight(creature));

        creature.AI = new NullCreatureAI(creature);
        Assert.False(PlayerbotTargetFilter.AggroesOnSight(creature));

        creature.AI = new GuardAI(creature);
        Assert.True(PlayerbotTargetFilter.AggroesOnSight(creature));
    }
}
