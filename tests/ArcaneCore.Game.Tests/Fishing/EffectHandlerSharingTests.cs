using ArcaneCore.Game.Fishing;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Fishing;

/// <summary>
/// SpellSystem.RegisterEffect keeps one handler per effect, last registration wins. TRANS_DOOR is shared with other transmitted objects
/// (vmangos Spell::EffectTransmitted also summons rituals and traps), so the fishing registration must chain to the handler installed
/// before it; Pickpocket and Disenchant have no other owner, so a second claim must fail loudly instead of replacing the first.
/// </summary>
public sealed class EffectHandlerSharingTests
{
    private const uint TransDoorSpell = 7700;

    [Fact]
    public void FishingSpells_HandsAnObjectItDoesNotOwn_ToTheHandlerInstalledBefore()
    {
        using var kit = new SpellTestKit(
            SpellTestKit.Spell(TransDoorSpell, SpellTestKit.Effect(SpellEffectName.TransDoor, 0)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        int ritualCalls = 0;
        kit.System.RegisterEffect(SpellEffectName.TransDoor, _ => ritualCalls++);
        new FishingSpells(() => []).Register(kit.System); // no fishing service on any map: nothing here is a fishing node
        (var player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, TransDoorSpell);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, TransDoorSpell, SpellCastTargets.ForSelf()));

        Assert.Equal(1, ritualCalls);
    }

    [Fact]
    public void PickpocketAndDisenchant_RefuseASecondRegistration()
    {
        using var kit = new SpellTestKit();
        new PickpocketSpells(_ => null).Register(kit.System);
        new DisenchantSpells(_ => null).Register(kit.System);

        Assert.Throws<InvalidOperationException>(() => new PickpocketSpells(_ => null).Register(kit.System));
        Assert.Throws<InvalidOperationException>(() => new DisenchantSpells(_ => null).Register(kit.System));
    }
}
