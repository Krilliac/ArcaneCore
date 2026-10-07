using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.MockClient.Playbots;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

public sealed class PlaybotFoodTests
{
    private const ulong Character = 14;

    [Fact]
    public void LowHealthSelectsOwnedFoodWithObservedSlotAndFreshIdentity()
    {
        ulong itemGuid = ObjectGuid.Item(22).Value;
        Dictionary<int, uint> player = Player(20, 100, itemGuid, backpackIndex: 5);
        Dictionary<int, uint> item = Item(itemGuid, Character, stack: 4);

        PlaybotCandidate? candidate = PlaybotFood.FindCandidate(7, Character, player, _ => item);

        Assert.NotNull(candidate);
        Assert.Equal($"7:eat:{itemGuid}:4", candidate.Id);
        Assert.Equal(PlaybotActionKind.Eat, candidate.Kind);
        Assert.Equal(100, candidate.Priority);
        Assert.Equal(itemGuid, candidate.Target);
        Assert.Equal((uint)(InventorySlots.ItemStart + 5), candidate.Value);
        Assert.Equal((uint)4, candidate.ExpectedStack);
    }

    [Fact]
    public void CandidateIdentityChangesWhenRevisionOrStackChanges()
    {
        ulong itemGuid = ObjectGuid.Item(22).Value;
        Dictionary<int, uint> player = Player(20, 100, itemGuid, backpackIndex: 1);
        Dictionary<int, uint> item = Item(itemGuid, Character, stack: 2);

        PlaybotCandidate first = PlaybotFood.FindCandidate(7, Character, player, _ => item)!;
        PlaybotCandidate second = PlaybotFood.FindCandidate(8, Character, player, _ => Item(itemGuid, Character, stack: 1))!;

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void UnknownHealthOrFlagsDoesNotAssumeFoodIsSafe()
    {
        ulong itemGuid = ObjectGuid.Item(22).Value;
        Dictionary<int, uint> player = Player(20, 100, itemGuid, backpackIndex: 1);
        player.Remove(UpdateFields.UnitFieldHealth);
        Assert.Null(PlaybotFood.FindCandidate(1, Character, player, _ => Item(itemGuid, Character, 2)));

        player = Player(20, 100, itemGuid, backpackIndex: 1);
        player.Remove(UpdateFields.UnitFieldFlags);
        Assert.Null(PlaybotFood.FindCandidate(1, Character, player, _ => Item(itemGuid, Character, 2)));
        Assert.Null(PlaybotFood.FindCandidate(1, 0, Player(20, 100, itemGuid, 1), _ => Item(itemGuid, 0, 2)));
    }

    [Theory]
    [InlineData(60, 100)]
    [InlineData(100, 100)]
    [InlineData(0, 100)]
    public void FullEnoughOrDeadHealthDoesNotEat(uint health, uint maxHealth)
    {
        ulong itemGuid = ObjectGuid.Item(22).Value;
        Assert.Null(PlaybotFood.FindCandidate(1, Character,
            Player(health, maxHealth, itemGuid, backpackIndex: 1), _ => Item(itemGuid, Character, 2)));
    }

    [Fact]
    public void CombatAndExistingFoodAuraSuppressEating()
    {
        ulong itemGuid = ObjectGuid.Item(22).Value;
        Dictionary<int, uint> player = Player(20, 100, itemGuid, backpackIndex: 1);
        player[UpdateFields.UnitFieldFlags] = (uint)UnitFlags.InCombat;
        Assert.Null(PlaybotFood.FindCandidate(1, Character, player, _ => Item(itemGuid, Character, 2)));

        player = Player(20, 100, itemGuid, backpackIndex: 1);
        player[UpdateFields.UnitFieldAura + 3] = 433;
        Assert.Null(PlaybotFood.FindCandidate(1, Character, player, _ => Item(itemGuid, Character, 2)));
    }

    [Fact]
    public void MissingZeroStackAndForeignFoodAreAbsentCandidates()
    {
        ulong itemGuid = ObjectGuid.Item(22).Value;
        Dictionary<int, uint> player = Player(20, 100, itemGuid, backpackIndex: 1);
        Assert.Null(PlaybotFood.FindCandidate(1, Character, player, _ => new Dictionary<int, uint>()));
        Assert.Null(PlaybotFood.FindCandidate(1, Character, player, _ => Item(itemGuid, Character, 0)));
        Assert.Null(PlaybotFood.FindCandidate(1, Character, player, _ => Item(itemGuid, Character + 1, 2)));
    }

    [Fact]
    public void NonFoodBackpackItemDoesNotBecomeFoodCandidate()
    {
        ulong itemGuid = ObjectGuid.Item(22).Value;
        Dictionary<int, uint> player = Player(20, 100, itemGuid, backpackIndex: 2);
        Dictionary<int, uint> item = Item(itemGuid, Character, 2);
        item[UpdateFields.ObjectFieldEntry] = 999;

        Assert.Null(PlaybotFood.FindCandidate(1, Character, player, _ => item));
    }

    private static Dictionary<int, uint> Player(uint health, uint maxHealth, ulong itemGuid, int backpackIndex)
        => new()
        {
            [UpdateFields.UnitFieldHealth] = health,
            [UpdateFields.UnitFieldMaxhealth] = maxHealth,
            [UpdateFields.UnitFieldFlags] = 0,
            [UpdateFields.PlayerFieldPackSlot1 + (backpackIndex * 2)] = (uint)itemGuid,
            [UpdateFields.PlayerFieldPackSlot1 + (backpackIndex * 2) + 1] = (uint)(itemGuid >> 32),
        };

    private static Dictionary<int, uint> Item(ulong guid, ulong owner, uint stack)
        => new()
        {
            [UpdateFields.ObjectFieldEntry] = 117,
            [UpdateFields.ItemFieldOwner] = (uint)owner,
            [UpdateFields.ItemFieldOwner + 1] = (uint)(owner >> 32),
            [UpdateFields.ItemFieldStackCount] = stack,
        };
}
