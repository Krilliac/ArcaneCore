using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots.Combat;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Combat.RotationTestKit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// A warlock's demon command used to be the only action while the Imp was not already on the victim. The Imp cannot melee and
/// its Firebolt is not on autocast, so that think kills nothing, and the brain never reaches Immolate or Shadow Bolt. The bot
/// then stands in goal=Combat until the stall watch gives the creature up (bot-QA #383, Kobold Laborers; the warlock lines of
/// #407 and #421).
/// </summary>
public sealed class PlayerbotWarlockRotationTests
{
    private static readonly PlayerbotWarlockRotation Warlock = new();

    private static SpellInfo[] Book =>
    [
        Hostile("Shadow Bolt"), Debuff("Immolate"), Debuff("Corruption"), Aura("Demon Skin", 1, SpellImplicitTarget.UnitCaster),
        SelfSpell("Summon Imp", SpellEffectName.SummonPet),
    ];

    [Fact]
    public void StandingWithAnImpOffTheVictim_CastsInsteadOfCommandingThePet()
    {
        RotationState standing = State(Warlock, Book, Self(9, 100, "Demon Skin"), Victim(25f), PlayerbotRole.RangeDps,
            more: ImpOffTheVictim);
        Assert.Equal("Immolate", Next(Warlock.InCombat(standing)));
        // Out of combat the pet command is the whole think: yielding lets the brain reach the cast above.
        Assert.Null(Next(Warlock.OutOfCombat(State(Warlock, Book, Self(9, 100, "Demon Skin"), Victim(25f), PlayerbotRole.RangeDps,
            inCombat: false, more: ImpOffTheVictim))));

        Assert.Equal("Shadow Bolt", Next(Warlock.InCombat(State(Warlock, Book, Self(9, 100, "Demon Skin"),
            Victim(25f, auras: ["Immolate", "Corruption"]), PlayerbotRole.RangeDps, more: ImpOffTheVictim))));
    }

    [Fact]
    public void ClosingIn_SendsTheImp_AndAStuckCommandStillCastsOnceNothingElseIsReady()
    {
        Assert.Equal("pet attack", Next(Warlock.OutOfCombat(State(Warlock, Book, Self(9, 100, "Demon Skin"), Victim(25f),
            PlayerbotRole.RangeDps, inCombat: false, more: s => { ImpOffTheVictim(s); s.IsMoving = true; }))));
        Assert.Equal("pet attack", Next(Warlock.InCombat(State(Warlock, Book, Self(9, 100, "Demon Skin"), Victim(25f),
            PlayerbotRole.RangeDps, more: s => { ImpOffTheVictim(s); s.IsMoving = true; }))));

        // The command did not stick and no nuke can be cast: the demon is still sent, rather than the think going empty.
        Assert.Equal("pet attack", Next(Warlock.InCombat(State(Warlock, Book, Self(9, 100, "Demon Skin"), Victim(25f),
            PlayerbotRole.RangeDps, blocked: ["Immolate", "Corruption", "Shadow Bolt"], more: ImpOffTheVictim))));
        Assert.Equal("pet attack", Next(Warlock.OutOfCombat(State(Warlock, Book, Self(9, 100, "Demon Skin"), Victim(25f),
            PlayerbotRole.RangeDps, inCombat: false, blocked: ["Immolate", "Corruption", "Shadow Bolt"], more: ImpOffTheVictim))));
    }

    private static void ImpOffTheVictim(RotationStateBuilder state)
    {
        state.Pet = RotationPetStatus.Alive;
        state.PetOnVictim = false;
    }
}
