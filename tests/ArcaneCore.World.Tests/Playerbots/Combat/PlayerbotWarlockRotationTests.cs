using ArcaneCore.Game;
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

    /// <summary>
    /// The CI flake (run 38454056590, "the Imp never attacked the wolf"): the Imp came out while the warlock already stood at range.
    /// Every think then had a cast ready, the cast won over the pet command (one action per think) and a cast in progress ends the
    /// think, so three Shadow Bolts killed the wolf without the demon ever being sent.
    /// </summary>
    [Fact]
    public void StandingAndCasting_StillSendsTheImpAtTheVictim_UntilItIsOnIt()
    {
        RotationState standing = State(Warlock, Book, Self(9, 100, "Demon Skin"), Victim(20f), PlayerbotRole.RangeDps, more: ImpOffTheVictim);
        RotationAction? cast = Warlock.InCombat(standing);
        Assert.Equal("Immolate", Next(cast));
        Assert.Equal("pet attack", Next(Warlock.PetCompanion(standing, cast)));

        // The pull: out of combat the think yields to the cast (no action), and the demon is sent in that same think.
        RotationState pull = State(Warlock, Book, Self(9, 100, "Demon Skin"), Victim(20f), PlayerbotRole.RangeDps, inCombat: false,
            more: ImpOffTheVictim);
        RotationAction? yielded = Warlock.OutOfCombat(pull);
        Assert.Null(yielded);
        Assert.Equal("pet attack", Next(Warlock.PetCompanion(pull, yielded)));

        // Once the Imp is fighting, or there is no Imp, nothing is sent.
        RotationState onVictim = State(Warlock, Book, Self(9, 100, "Demon Skin"), Victim(20f), PlayerbotRole.RangeDps,
            more: s => { s.Pet = RotationPetStatus.Alive; s.PetFighting = true; });
        Assert.Null(Warlock.PetCompanion(onVictim, Warlock.InCombat(onVictim)));
        RotationState noPet = State(Warlock, Book, Self(9, 100, "Demon Skin"), Victim(20f), PlayerbotRole.RangeDps);
        Assert.Null(Warlock.PetCompanion(noPet, Warlock.InCombat(noPet)));
    }

    /// <summary>A pet with a victim of its own is not pulled off it, and no command goes beside a cast aimed at the pet (Health Funnel).</summary>
    [Fact]
    public void ABusyImp_IsLeftAlone_AndNoCommandGoesBesideACastAtTheImp()
    {
        RotationState busy = State(Warlock, Book, Self(9, 100, "Demon Skin"), Victim(20f), PlayerbotRole.RangeDps,
            more: s => { s.Pet = RotationPetStatus.Alive; s.PetFighting = true; });
        Assert.Equal("Immolate", Next(Warlock.InCombat(busy)));
        Assert.Null(Warlock.PetCompanion(busy, Warlock.InCombat(busy)));
        Assert.Null(Next(Warlock.OutOfCombat(State(Warlock, Book, Self(9, 100, "Demon Skin"), Victim(25f), PlayerbotRole.RangeDps,
            inCombat: false, more: s => { s.Pet = RotationPetStatus.Alive; s.PetFighting = true; }))));

        var imp = new RotationUnit { Guid = new ObjectGuid(0xF140000000000099UL), Distance = 2f, Health = 30, MaxHealth = 100, IsAlive = true };
        RotationState idle = State(Warlock, Book, Self(9, 100, "Demon Skin"), Victim(20f), PlayerbotRole.RangeDps,
            more: s => { ImpOffTheVictim(s); s.PetUnit = imp; });
        var funnel = new RotationAction(RotationActionKind.Cast, Book[0], imp.Guid);
        Assert.Null(Warlock.PetCompanion(idle, funnel));
        Assert.Equal("pet attack", Next(Warlock.PetCompanion(idle, new RotationAction(RotationActionKind.Cast, Book[0], VictimGuid))));
    }

    [Fact]
    public void CrowdControlledTarget_IsNotCommanded_AndAPetFightingCrowdControlIsRedirectedOrRecalled()
    {
        // An idle Imp is not sent onto a crowd-controlled victim (moving or standing).
        RotationState ccVictim = State(Warlock, Book, Self(9, 100, "Demon Skin"), Victim(20f, crowdControlled: true), PlayerbotRole.RangeDps,
            more: s => { ImpOffTheVictim(s); s.IsMoving = true; });
        Assert.Null(Warlock.PetCompanion(ccVictim, Warlock.InCombat(ccVictim)));
        Assert.Null(Next(Warlock.OutOfCombat(State(Warlock, Book, Self(9, 100, "Demon Skin"), Victim(20f, crowdControlled: true),
            PlayerbotRole.RangeDps, inCombat: false, more: ImpOffTheVictim))));

        // A pet whose own victim was crowd-controlled is redirected to the warlock's non-CC victim, or recalled to follow when none exists.
        RotationState redirect = State(Warlock, Book, Self(9, 100, "Demon Skin"), Victim(20f), PlayerbotRole.RangeDps,
            more: s => { s.Pet = RotationPetStatus.Alive; s.PetFighting = true; s.PetFightingCrowdControlled = true; });
        RotationAction? cast = Warlock.InCombat(redirect);
        Assert.Equal("Immolate", Next(cast));
        Assert.Equal("pet attack", Next(Warlock.PetCompanion(redirect, cast)));

        RotationState recall = State(Warlock, Book, Self(9, 100, "Demon Skin"), Victim(20f, crowdControlled: true), PlayerbotRole.RangeDps,
            more: s => { s.Pet = RotationPetStatus.Alive; s.PetFighting = true; s.PetFightingCrowdControlled = true; });
        Assert.Equal("pet follow", Next(Warlock.PetCompanion(recall, Warlock.InCombat(recall))));
    }

    private static void ImpOffTheVictim(RotationStateBuilder state)
    {
        state.Pet = RotationPetStatus.Alive;
    }
}
