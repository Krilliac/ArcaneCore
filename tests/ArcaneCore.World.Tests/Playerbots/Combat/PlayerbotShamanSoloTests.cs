using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots.Combat;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Combat.RotationTestKit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// A shaman with neither Elemental Mastery nor Stormstrike is a healer. Alone and under 60% it used to heal or drop only
/// totems, so Lightning Bolt never went out. Alone it bolts unless health is an emergency; in a group it still heals first.
/// </summary>
public sealed class PlayerbotShamanSoloTests
{
    private static readonly PlayerbotShamanRotation Shaman = new();

    private static SpellInfo[] Book =>
    [
        Hostile("Lightning Bolt"), Hostile("Earth Shock"), SelfSpell("Searing Totem", SpellEffectName.SummonTotem),
        Heal("Healing Wave", 60), Aura("Lightning Shield", 1, SpellImplicitTarget.UnitCaster),
    ];

    [Fact]
    public void ShamanHealer_BoltsWhileHurtAlone_HealsInAnEmergency_AndHealsItsGroup()
    {
        // Alone under 60% used to be Healing Wave, or only a totem between 50 and 60, so the mob never died.
        Assert.Equal("Lightning Bolt", Next(Shaman.InCombat(State(Shaman, Book, Self(7, 40f), Victim(15f), PlayerbotRole.Healer))));
        Assert.Equal("Lightning Bolt", Next(Shaman.InCombat(State(Shaman, Book, Self(7, 55f), Victim(15f), PlayerbotRole.Healer))));
        Assert.Equal("Earth Shock", Next(Shaman.InCombat(State(Shaman, Book, Self(7, 40f), Victim(15f, casting: true), PlayerbotRole.Healer))));

        RotationAction emergency = Shaman.InCombat(State(Shaman, Book, Self(7, 20f), Victim(15f), PlayerbotRole.Healer))!.Value;
        Assert.Equal(BotGuid, emergency.Target);
        Assert.Equal("Healing Wave", emergency.Spell!.Name);

        RotationAction grouped = Shaman.InCombat(State(Shaman, Book, Self(7, 40f), Victim(15f), PlayerbotRole.Healer,
            more: s => s.Party.Add(Member(8, 1, 100f))))!.Value;
        Assert.Equal(BotGuid, grouped.Target);
        Assert.Equal("Healing Wave", grouped.Spell!.Name);
        // Still under 60% but above the 50% heal, a grouped healer drops a totem instead of bolting.
        Assert.Equal("Searing Totem", Next(Shaman.InCombat(State(Shaman, Book, Self(7, 55f), Victim(15f), PlayerbotRole.Healer,
            more: s => s.Party.Add(Member(8, 1, 100f))))));
    }
}
