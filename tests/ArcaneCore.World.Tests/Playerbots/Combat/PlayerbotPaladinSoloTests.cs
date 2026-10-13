using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots.Combat;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Combat.RotationTestKit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// A paladin without Holy Shield or Sanctity Aura is a healer. Alone and hurt it used to return only a heal (or nothing,
/// which still consumed the think), so Judgement and its seal never ran.
/// </summary>
public sealed class PlayerbotPaladinSoloTests
{
    private static readonly PlayerbotPaladinRotation Paladin = new();

    // The same book PlayerbotClassRotationTests already uses for a paladin.
    private static SpellInfo[] PaladinBook =>
    [
        Aura("Devotion Aura", 1, SpellImplicitTarget.UnitCaster), Aura("Seal of Righteousness", 1, SpellImplicitTarget.UnitCaster),
        Hostile("Judgement", 0), Aura("Blessing of Might"), Aura("Blessing of Wisdom"), SelfSpell("Lay on Hands"), Heal("Holy Light", 40),
        Dispel("Cleanse", 4),
    ];

    [Fact]
    public void PaladinHealer_SealsOrJudgesWhileHurtAlone_HealsInAnEmergency_AndHealsItsGroup()
    {
        // Alone under 80% used to be Holy Light every think, so the mob never died (bot QA combat stalls).
        Assert.Equal("Seal of Righteousness", Next(Paladin.InCombat(State(Paladin, PaladinBook, Self(2, 40f), Victim(3f),
            PlayerbotRole.Healer, power: 100, maxPower: 100))));
        Assert.Equal("Judgement", Next(Paladin.InCombat(State(Paladin, PaladinBook, Self(2, 40f, "Seal of Righteousness"), Victim(3f),
            PlayerbotRole.Healer, power: 100, maxPower: 100))));

        RotationAction emergency = Paladin.InCombat(State(Paladin, PaladinBook, Self(2, 20f), Victim(3f), PlayerbotRole.Healer))!.Value;
        Assert.Equal(BotGuid, emergency.Target);
        Assert.Equal("Holy Light", emergency.Spell!.Name);

        RotationAction grouped = Paladin.InCombat(State(Paladin, PaladinBook, Self(2, 40f), Victim(3f), PlayerbotRole.Healer,
            more: s => s.Party.Add(Member(7, 1, 100f))))!.Value;
        Assert.Equal(BotGuid, grouped.Target);
        Assert.Equal("Holy Light", grouped.Spell!.Name);
    }
}
