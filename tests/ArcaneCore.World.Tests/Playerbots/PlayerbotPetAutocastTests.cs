using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.Logging;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Combat.RotationTestKit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A managed bot turns on the autocast of its pet's harmful spells (<see cref="PlayerbotPetControl"/>), chosen by spell id, and the pet
/// unsummon reasons keep routine departures out of the normal log.
/// </summary>
public sealed class PlayerbotPetAutocastTests
{
    private const uint GrowlId = 940001;
    private const uint ClawId = 940002;
    private const uint CowerLikePassiveId = 940003;
    private const uint FriendBuffId = 940004;
    private const uint UnknownId = 940005;
    private const uint SameNameId = 940006;


    private static readonly Dictionary<uint, SpellInfo> Book = new()
    {
        [GrowlId] = Hostile("Growl", id: GrowlId),
        [ClawId] = Hostile("Claw", id: ClawId),
        [CowerLikePassiveId] = Hostile("Furious Howl Passive", id: CowerLikePassiveId) with { Attributes = SpellAttributes.Passive },
        [FriendBuffId] = Aura("Pet Buff") with { Id = FriendBuffId },
    };

    private static SpellInfo? Lookup(uint id) => Book.GetValueOrDefault(id);

    private static CharmInfo HunterPet()
    {
        var charm = new CharmInfo(ReactState.Defensive);
        charm.LearnSpell(GrowlId);
        charm.LearnSpell(ClawId);
        return charm;
    }

    [Fact]
    public void HunterPet_GrowlAndClaw_AreFoundWithAutocastOff()
    {
        Assert.Equal(new uint[] { GrowlId, ClawId }, PlayerbotPetControl.HarmfulSpellsWithAutocastOff(HunterPet(), Lookup));
    }

    [Fact]
    public void AutocastOn_LeavesNothingToEnable()
    {
        CharmInfo charm = HunterPet();
        charm.SetSpellAutocast(GrowlId, true);
        charm.SetSpellAutocast(ClawId, true);

        Assert.Empty(PlayerbotPetControl.HarmfulSpellsWithAutocastOff(charm, Lookup));
    }

    [Fact]
    public void OnlyHarmfulCastableKnownSpells_AreChosen_ById()
    {
        CharmInfo charm = HunterPet();
        charm.LearnSpell(CowerLikePassiveId, ActionType.Passive);
        charm.LearnSpell(FriendBuffId);
        charm.LearnSpell(UnknownId);
        charm.LearnSpell(SameNameId);

        // A spell that shares a name with Growl but is another id is its own spell: it is chosen by its data, and an id the store does not
        // know (UnknownId, learned but absent from the book) is skipped.
        var withSameName = new Dictionary<uint, SpellInfo>(Book) { [SameNameId] = Hostile("Growl", id: SameNameId) };
        Assert.Equal(new uint[] { GrowlId, ClawId, SameNameId }, PlayerbotPetControl.HarmfulSpellsWithAutocastOff(charm, id => withSameName.GetValueOrDefault(id)));
        Assert.Equal(new uint[] { GrowlId, ClawId }, PlayerbotPetControl.HarmfulSpellsWithAutocastOff(charm, Lookup));
    }

    [Theory]
    [InlineData(PetUnsummonReason.Unspecified)]
    [InlineData(PetUnsummonReason.Dismissed)]
    [InlineData(PetUnsummonReason.Replaced)]
    [InlineData(PetUnsummonReason.OwnerLeft)]
    [InlineData(PetUnsummonReason.OwnerDied)]
    [InlineData(PetUnsummonReason.CorpseDecayed)]
    [InlineData(PetUnsummonReason.Expired)]
    public void RoutineUnsummonReasons_AreLoggedAtDebug(PetUnsummonReason reason)
    {
        Assert.Equal(LogLevel.Debug, PetUnsummonReasons.LevelOf(reason));
    }

    [Fact]
    public void RunAway_IsLoggedAtInformation()
    {
        Assert.Equal(LogLevel.Information, PetUnsummonReasons.LevelOf(PetUnsummonReason.RanAway));
    }
}
