using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Tests.Pets;

public sealed class DismissPetTests
{
    private const uint Dismiss = 991002;
    private const uint Call = 991003;

    [Fact]
    public void HunterDismiss_DetachesButLeavesCallableSnapshotForCallPet()
    {
        using PetTestKit kit = new([
            Spell(Dismiss, Effect(SpellEffectName.DismissPet, 0)),
            Spell(Call, Effect(SpellEffectName.SummonPet, 0, misc: 0))]);
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Cast(owner, PetTestKit.PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        uint number = pet.Summon!.Charm!.PetNumber;
        kit.Cast(owner, Dismiss);
        Assert.True(owner.PetGuid.IsEmpty);
        Assert.Empty(kit.Creatures.Creatures);
        kit.Cast(owner, Call);
        Creature restored = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(number, restored.Summon!.Charm!.PetNumber);
    }

    [Fact]
    public async Task DetachedDismiss_WaitsBehindOlderCurrentSaveAndFlushesDetachedState()
    {
        using PetTestKit kit = new([Spell(Dismiss, Effect(SpellEffectName.DismissPet, 0))]);
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Cast(owner, PetTestKit.PetSpell);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<bool> states = [];
        kit.Service.SavePersistence = async (snapshot, _) => { states.Add(snapshot.IsCurrent); entered.SetResult(); await release.Task; };
        kit.Service.SaveDetachedPersistence = (snapshot, _) => { states.Add(snapshot.IsCurrent); return Task.CompletedTask; };
        kit.Service.QueueCurrentPetSave(owner);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        kit.Cast(owner, Dismiss);
        release.SetResult();
        await kit.Service.FlushCharacterAsync(1);
        Assert.Equal([true, false], states);
    }
}
