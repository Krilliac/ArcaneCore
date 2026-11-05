using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// The branches of the abandon delete (<see cref="SummonService.QueueDeletePet"/>): a retired character's delete is skipped, a transient failure is
/// retried in the task, a fence refusal (also one a store wrapped) is final in the task and owed to the next barrier, and the barrier runs the owed delete.
/// Written for the integration gate; not run in the lane.
/// </summary>
/// <remarks>The waits are hang bounds, not timings: the deletes run on the thread pool. The one pause (<see cref="NoRetryPause"/>) only gives a
/// wrongly retried delete time to show; a correct run asserts the same count whatever the pause.</remarks>
public sealed class PersistentPetDeleteTests
{
    private static readonly TimeSpan HangBound = TimeSpan.FromSeconds(30);

    // Longer than the first in-task retry delay (100 ms), so a refusal wrongly treated as transient would have been tried again by then.
    private static readonly TimeSpan NoRetryPause = TimeSpan.FromMilliseconds(600);

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
    public async Task ARetiredCharactersDelete_IsSkipped_WithoutTouchingTheStore()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            int deletes = 0;
            int asked = 0;
            kit.Service.WritesRetired = _ => { Interlocked.Increment(ref asked); return true; };
            kit.Service.DeletePersistence = (_, _) => { Interlocked.Increment(ref deletes); return Task.CompletedTask; };

            kit.Service.QueueDeletePet(player);
            await WaitUntilAsync(() => Volatile.Read(ref asked) >= 1, "the retired seam was never asked");
            await kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound);

            Assert.Equal(0, Volatile.Read(ref deletes));
            await kit.Service.StopAsync().WaitAsync(HangBound);
        }
    }

    [Fact]
    public async Task ATransientDeleteFailure_IsRetriedInTheTask_UntilItSucceeds()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            int deletes = 0;
            kit.Service.DeletePersistence = (_, _) =>
            {
                if (Interlocked.Increment(ref deletes) < 3) throw new IOException("store busy");
                return Task.CompletedTask;
            };

            kit.Service.QueueDeletePet(player);
            await kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound);

            Assert.Equal(3, Volatile.Read(ref deletes)); // two failures, then the third attempt; nothing left owed for the barrier
            await kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound);
            Assert.Equal(3, Volatile.Read(ref deletes));
            await kit.Service.StopAsync().WaitAsync(HangBound);
        }
    }

    [Fact]
    public async Task ADeleteThatFailsEveryAttempt_IsOwedAndTheBarrierRunsItAgain()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            int deletes = 0;
            int fail = 1;
            kit.Service.DeletePersistence = (_, _) =>
            {
                Interlocked.Increment(ref deletes);
                return Volatile.Read(ref fail) == 1 ? throw new IOException("store down") : Task.CompletedTask;
            };

            kit.Service.QueueDeletePet(player);
            await WaitUntilAsync(() => Volatile.Read(ref deletes) >= 3, "the three in-task attempts did not happen");

            Volatile.Write(ref fail, 0);
            await kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound);

            Assert.Equal(4, Volatile.Read(ref deletes)); // the barrier's retry of the owed delete
            await kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound);
            Assert.Equal(4, Volatile.Read(ref deletes)); // done: nothing owed any more
            await kit.Service.StopAsync().WaitAsync(HangBound);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFenceRefusalOfTheDelete_IsNotRetriedInTheTask_AndIsOwedToTheBarrier(bool wrapped)
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            int deletes = 0;
            int refuse = 1;
            kit.Service.WritesSealed = () => true;
            kit.Service.DeletePersistence = (_, _) =>
            {
                Interlocked.Increment(ref deletes);
                if (Volatile.Read(ref refuse) == 0) return Task.CompletedTask;
                var refusal = new CharacterOwnershipRejectedException("world", 7);
                throw wrapped ? new InvalidOperationException("the commit failed", refusal) : refusal;
            };

            kit.Service.QueueDeletePet(player);
            await WaitUntilAsync(() => Volatile.Read(ref deletes) >= 1, "the delete was never attempted");
            await Task.Delay(NoRetryPause);
            Assert.Equal(1, Volatile.Read(ref deletes)); // a refusal is final, not transient

            // The handoff aborted: the character is ours again, so the next barrier runs the delete the seal refused.
            Volatile.Write(ref refuse, 0);
            await kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound);
            Assert.Equal(2, Volatile.Read(ref deletes));
            await kit.Service.StopAsync().WaitAsync(HangBound);
        }
    }

    [Fact]
    public async Task ARefusalOfARetiredCharactersDelete_IsDropped_NotOwed()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            int deletes = 0;
            int retired = 0; // the takeover lands while the delete is in flight: the seam says retired only once the store has refused
            kit.Service.WritesRetired = _ => Volatile.Read(ref retired) == 1;
            kit.Service.DeletePersistence = (_, _) =>
            {
                Interlocked.Increment(ref deletes);
                Volatile.Write(ref retired, 1);
                throw new CharacterOwnershipRejectedException("world", 7);
            };

            kit.Service.QueueDeletePet(player);
            await WaitUntilAsync(() => Volatile.Read(ref deletes) >= 1, "the delete was never attempted");
            await kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound);

            Assert.Equal(1, Volatile.Read(ref deletes)); // dropped by the retirement: not retried, not owed
            await kit.Service.StopAsync().WaitAsync(HangBound);
        }
    }
}
