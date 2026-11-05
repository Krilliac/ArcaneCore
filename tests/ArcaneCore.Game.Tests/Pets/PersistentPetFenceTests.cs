using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// The pet write path follows the handoff fence seams (<see cref="SummonService.WritesRetired"/>, <see cref="SummonService.WritesSealed"/>): a retired character's pet write is skipped and
/// counted, a refusal while sealed is retained for an aborted handoff, and a stop with the handoff undecided does not fail on a refused write.
/// </summary>
/// <remarks>The waits are hang bounds, not timings: the writes run on the thread pool.</remarks>
public sealed class PersistentPetFenceTests
{
    private static readonly TimeSpan HangBound = TimeSpan.FromSeconds(30);

    private static (PetTestKit Kit, Player Player) HunterWithPet()
    {
        PetTestKit kit = new();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(player, PetTestKit.PetSpell));
        return (kit, player);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + HangBound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, what);
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task ARetiredCharactersPetSave_IsSkippedAndCounted_WithoutTouchingTheStore()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            int asked = 0;
            int writes = 0;
            kit.Service.WritesRetired = _ => { Interlocked.Increment(ref asked); return true; };
            kit.Service.SavePersistence = (_, _) => { Interlocked.Increment(ref writes); return Task.CompletedTask; };

            kit.Service.QueueCurrentPetSave(player);
            await WaitUntilAsync(() => Volatile.Read(ref asked) >= 1, "the retired seam was never asked");

            await kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound);
            await kit.Service.StopAsync().WaitAsync(HangBound);
            Assert.Equal(0, Volatile.Read(ref writes));
            Assert.Empty(kit.Service.RetainedPersistenceCharacters);
        }
    }

    [Fact]
    public async Task ARefusalOfARetiredCharacter_IsDroppedNotRetained_AndTheBarrierAndStopStayClean()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            int retired = 0; // the takeover lands while the write is in flight: the seam says retired only once the store has refused
            int attempts = 0;
            kit.Service.WritesRetired = _ => Volatile.Read(ref retired) == 1;
            kit.Service.SavePersistence = (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                Volatile.Write(ref retired, 1);
                throw new ArcaneCore.Kernel.Characters.CharacterOwnershipRejectedException("world", 7);
            };

            kit.Service.QueueCurrentPetSave(player);
            await WaitUntilAsync(() => Volatile.Read(ref attempts) >= 1, "the save was never attempted");
            await kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound);

            Assert.Empty(kit.Service.RetainedPersistenceCharacters);
            Assert.Equal(1, Volatile.Read(ref attempts)); // dropped by the retirement, never retried
            await kit.Service.StopAsync().WaitAsync(HangBound);
        }
    }

    [Fact]
    public async Task ARefusalWhileSealed_IsRetained_AndAnAbortedHandoffsBarrierWritesIt()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            int sealedNow = 1;
            int refuse = 1;
            int written = 0;
            kit.Service.WritesSealed = () => Volatile.Read(ref sealedNow) == 1;
            kit.Service.SavePersistence = (_, _) =>
            {
                if (Volatile.Read(ref refuse) == 1)
                {
                    throw new ArcaneCore.Kernel.Characters.CharacterOwnershipRejectedException("world", 7);
                }

                Interlocked.Increment(ref written);
                return Task.CompletedTask;
            };

            kit.Service.QueueCurrentPetSave(player);
            await WaitUntilAsync(() => kit.Service.RetainedPersistenceCharacters.Count == 1, "the refused save was not retained while sealed");

            // The handoff aborted: A owns the character again, so the next barrier writes what the seal refused.
            Volatile.Write(ref sealedNow, 0);
            Volatile.Write(ref refuse, 0);
            await kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound);
            Assert.Equal(1, Volatile.Read(ref written));
            Assert.Empty(kit.Service.RetainedPersistenceCharacters);
            await kit.Service.StopAsync().WaitAsync(HangBound);
        }
    }

    [Fact]
    public async Task AStopWithTheHandoffUndecided_DoesNotFailOnAWriteTheFenceRefuses()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            kit.Service.WritesSealed = () => true;
            kit.Service.SavePersistence = (_, _) => throw new ArcaneCore.Kernel.Characters.CharacterOwnershipRejectedException("world", 7);

            kit.Service.QueueCurrentPetSave(player);
            await WaitUntilAsync(() => kit.Service.RetainedPersistenceCharacters.Count == 1, "the refused save was not retained while sealed");

            await kit.Service.StopAsync().WaitAsync(HangBound); // would throw "did not drain" without the sealed tolerance
        }
    }

    [Fact]
    public async Task AFenceRefusalOutsideAnyHandoff_StillFailsTheBarrier()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            kit.Service.WritesSealed = () => false;
            kit.Service.WritesRetired = _ => false;
            kit.Service.SavePersistence = (_, _) => throw new ArcaneCore.Kernel.Characters.CharacterOwnershipRejectedException("world", 7);

            kit.Service.QueueCurrentPetSave(player);
            await WaitUntilAsync(() => kit.Service.RetainedPersistenceCharacters.Count == 1, "the refused save was not retained");

            await Assert.ThrowsAsync<ArcaneCore.Kernel.Characters.CharacterOwnershipRejectedException>(() => kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound));
        }
    }
}
