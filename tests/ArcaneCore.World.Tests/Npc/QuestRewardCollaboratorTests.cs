using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

/// <summary>Spellbook and hold rules the reward collaborators rely on, and the player-aware reward-spell preflight.</summary>
public sealed class QuestRewardCollaboratorTests
{
    private const uint RewardSpell = 700;
    private const uint Taught = 701;
    private const uint Item = 77;

    [Fact]
    public void ForgetSpell_IsRefusedForAHeldCharacter_AndAllowedAfterRelease()
    {
        var cache = new SpellbookCache(null, NullLogger.Instance);
        Player held = NewPlayer(11);
        Assert.True(cache.LearnSpell(held, 700));
        Guid operation = Guid.NewGuid();
        Assert.True(held.BeginQuestSettlement(operation));

        Assert.False(cache.ForgetSpell(held, 700));
        Assert.Equal([700u], cache.GetSpells(held));

        Assert.True(held.EndQuestSettlement(operation));
        Assert.True(cache.ForgetSpell(held, 700));
        Assert.Empty(cache.GetSpells(held));
    }

    [Fact]
    public async Task AdoptCommitted_AddsToACachedBookOnly_AndWritesNothing()
    {
        var store = new Spells.InMemoryCharacterSpellStore();
        await using ServiceProvider services = new ServiceCollection()
            .AddScoped<ICharacterSpellStore>(_ => store).BuildServiceProvider();
        var cache = new SpellbookCache(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        cache.Start();
        cache.LoadCharacter(12, [1]);
        Assert.True(cache.AdoptCommitted(12, [2, 3]));
        Assert.Equal([1u, 2u, 3u], cache.GetSpells(NewPlayer(12)));
        await cache.FlushAsync();
        Assert.Empty(await store.GetAsync(12)); // the reward transaction wrote them; the cache queues nothing

        // A character without a cached book must not get a partial one: it would hide the default-spell path.
        Assert.False(cache.AdoptCommitted(13, [2]));
        Assert.False(cache.ContainsCharacter(13));
        await cache.DisposeAsync();
    }

    [Fact]
    public void TeleportPreflight_NeedsAResolvableDestinationAndAnAcceptingSink_AndMovesNothing()
    {
        SpellInfo teleport = Teleport(RewardSpell, SpellImplicitTarget.LocationDatabase);
        using (var unresolved = new Kit([teleport]))
        {
            Assert.False(unresolved.Effects.TryPrepareRewardSpell(unresolved.Player, ObjectGuid.Empty, RewardSpell, out _));
            Assert.Empty(unresolved.Teleports.Checked);
        }

        var destination = new SpellTargetPosition(0, 5, 6, 7, 1);
        using var kit = new Kit([teleport], [(RewardSpell, destination)]);
        kit.Teleports.Accept = false;
        Assert.False(kit.Effects.TryPrepareRewardSpell(kit.Player, ObjectGuid.Empty, RewardSpell, out QuestRewardSpellGrant refused));
        Assert.Equal(QuestRewardSpellGrant.None, refused);

        kit.Teleports.Accept = true;
        Assert.True(kit.Effects.TryPrepareRewardSpell(kit.Player, ObjectGuid.Empty, RewardSpell, out QuestRewardSpellGrant grant));
        Assert.True(grant.Transient);
        Assert.False(grant.HasDurableGrant);
        Assert.Equal([destination.MapId, destination.MapId], kit.Teleports.Checked);
        Assert.Empty(kit.Teleports.Moved);
    }

    [Fact]
    public void TeleportToAnUnsetHomeBind_IsRefused()
    {
        using var kit = new Kit([Teleport(RewardSpell, SpellImplicitTarget.LocationCasterHomeBind)]);
        Assert.True(kit.Player.Home.IsUnset);
        Assert.False(kit.Effects.TryPrepareRewardSpell(kit.Player, ObjectGuid.Empty, RewardSpell, out _));
    }

    [Fact]
    public void SummonPreflight_NeedsASinkAnOwnerInAMapAndACreatureEntry()
    {
        SpellInfo summon = Spell(RewardSpell, new SpellEffectInfo
        {
            Effect = SpellEffectName.Summon, TargetA = SpellImplicitTarget.UnitCaster, MiscValue = 4321,
        });
        using var kit = new Kit([summon]);
        Assert.False(kit.Effects.TryPrepareRewardSpell(kit.Player, ObjectGuid.Empty, RewardSpell, out _)); // the daemon registers no sink

        kit.Spells.Summons = kit.Summons;
        kit.Summons.Accept = false;
        Assert.False(kit.Effects.TryPrepareRewardSpell(kit.Player, ObjectGuid.Empty, RewardSpell, out _));
        kit.Summons.Accept = true;
        Assert.True(kit.Effects.TryPrepareRewardSpell(kit.Player, ObjectGuid.Empty, RewardSpell, out QuestRewardSpellGrant grant));
        Assert.True(grant.Transient);
        Assert.Equal([4321u, 4321u], kit.Summons.Checked); // refused by the sink, then accepted
        Assert.Empty(kit.Summons.Created);

        Player stranger = NewPlayer(31); // in no map: there is nobody to own the summon
        Assert.False(kit.Effects.TryPrepareRewardSpell(stranger, ObjectGuid.Empty, RewardSpell, out _));

        using var noEntry = new Kit([Spell(RewardSpell, new SpellEffectInfo
        {
            Effect = SpellEffectName.Summon, TargetA = SpellImplicitTarget.UnitCaster, MiscValue = 0,
        })]);
        noEntry.Spells.Summons = noEntry.Summons;
        Assert.False(noEntry.Effects.TryPrepareRewardSpell(noEntry.Player, ObjectGuid.Empty, RewardSpell, out _));
    }

    [Fact]
    public void PublishedTransientTeleport_IsLostWhenItsPreflightNowFails_AndCastOnceWhenItStillPasses()
    {
        SpellInfo teleport = Teleport(RewardSpell, SpellImplicitTarget.LocationDatabase);
        using var kit = new Kit([teleport], [(RewardSpell, new SpellTargetPosition(0, 5, 6, 7, 1))]);
        Quest quest = new QuestStore(new QuestContent([new QuestTemplate { Entry = 1, Method = 2, RewSpell = RewardSpell }], [], [])).Get(1)!;
        Assert.True(kit.Effects.TryPrepareRewardSpell(kit.Player, ObjectGuid.Empty, RewardSpell, out QuestRewardSpellGrant grant));

        kit.Teleports.Accept = false; // the destination became unavailable between prepare and publication
        kit.Effects.PublishRewardSpell(kit.Player, quest, ObjectGuid.Empty, grant);
        Assert.Empty(kit.Teleports.Moved);

        kit.Teleports.Accept = true;
        kit.Effects.PublishRewardSpell(kit.Player, quest, ObjectGuid.Empty, grant);
        Assert.Single(kit.Teleports.Moved);
    }

    [Fact]
    public void LearnGrant_SkipsKnownSpells_NeedsASpellbook_AndTeachesNothingWhilePreparing()
    {
        SpellInfo learn = Spell(RewardSpell,
            new SpellEffectInfo { Effect = SpellEffectName.LearnSpell, TargetA = SpellImplicitTarget.UnitCaster, TriggerSpell = Taught },
            new SpellEffectInfo { Effect = SpellEffectName.LearnSpell, TargetA = SpellImplicitTarget.UnitCaster, TriggerSpell = Taught });
        SpellInfo taught = Spell(Taught, new SpellEffectInfo { Effect = SpellEffectName.Heal, TargetA = SpellImplicitTarget.UnitCaster });
        using var kit = new Kit([learn, taught]);
        Assert.False(kit.Effects.TryPrepareRewardSpell(kit.Player, ObjectGuid.Empty, RewardSpell, out _)); // no spellbook

        var book = new FakeBook();
        kit.Spells.Spellbook = book;
        Assert.True(kit.Effects.TryPrepareRewardSpell(kit.Player, ObjectGuid.Empty, RewardSpell, out QuestRewardSpellGrant grant));
        Assert.Equal([Taught], grant.LearnedSpells); // two effects, one spell
        Assert.False(grant.Transient);
        Assert.Empty(book.Known);

        book.Known.Add(Taught);
        Assert.True(kit.Effects.TryPrepareRewardSpell(kit.Player, ObjectGuid.Empty, RewardSpell, out grant));
        Assert.Empty(grant.LearnedSpells);
        Assert.False(grant.HasPostReleaseEffects);
    }

    [Theory]
    [InlineData(2, 3, 5u)]    // BasePoints 2 + one 1-sided die = 3
    [InlineData(99, 100, 5u)] // clamped to Stackable
    [InlineData(-9, -8, 1u)]  // below one becomes one (vmangos DoCreateItem)
    public void CreateItemCount_IsTheEffectValueClampedToOneAndStackable(int basePoints, int expectedValue, uint stackable)
    {
        SpellInfo create = Spell(RewardSpell, new SpellEffectInfo
        {
            Effect = SpellEffectName.CreateItem, TargetA = SpellImplicitTarget.UnitCaster, ItemType = Item,
            BasePoints = basePoints, BaseDice = 1, DieSides = 1,
        });
        using var kit = new Kit([create], items: [new ItemTemplate { Entry = Item, Name = "Created", Stackable = stackable }]);
        Assert.True(kit.Effects.TryPrepareRewardSpell(kit.Player, ObjectGuid.Empty, RewardSpell, out QuestRewardSpellGrant grant));
        QuestRewardCreatedItem created = Assert.Single(grant.CreatedItems);
        Assert.Equal(Item, created.Entry);
        Assert.Equal(Math.Clamp(expectedValue, 1, (int)stackable), (int)created.Count);
    }

    [Fact]
    public void CreateItemWithAnUnknownTemplate_IsRefused()
    {
        SpellInfo create = Spell(RewardSpell, new SpellEffectInfo
        {
            Effect = SpellEffectName.CreateItem, TargetA = SpellImplicitTarget.UnitCaster, ItemType = Item,
        });
        using var kit = new Kit([create]);
        Assert.True(kit.Effects.CanCastRewardSpell(RewardSpell));
        Assert.False(kit.Effects.TryPrepareRewardSpell(kit.Player, ObjectGuid.Empty, RewardSpell, out _));
    }

    private static SpellInfo Spell(uint id, params SpellEffectInfo[] effects)
        => new() { Id = id, RangeIndex = SpellConstants.RangeIndexSelfOnly, Effects = effects };

    private static SpellInfo Teleport(uint id, SpellImplicitTarget destination) => Spell(id, new SpellEffectInfo
    {
        Effect = SpellEffectName.TeleportUnits, TargetA = SpellImplicitTarget.UnitCaster, TargetB = destination,
    });

    private static Player NewPlayer(int id) => new(
        new CharacterRecord { Id = id, AccountId = 1, Name = $"P{id}", Race = 1, Class = 1, Level = 1 },
        new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400),
        new NullSession());

    private sealed class Kit : IDisposable
    {
        private readonly WorldRuntime _world;

        public Kit(IEnumerable<SpellInfo> spells, IEnumerable<(uint, SpellTargetPosition)>? positions = null,
            IEnumerable<ItemTemplate>? items = null)
        {
            _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new NoopSaveQueue(),
                NullLogger<WorldRuntime>.Instance);
            Spells = new SpellSystem(new SpellStore(spells, [], positions ?? []), () => 0, teleport: Teleports);
            Effects = new QuestRewardEffects(Spells, NullLogger.Instance);
            Player = NewPlayer(21);
            Player.Inventory.Templates = new ItemTemplateStore(items ?? []);
            _world.AddPlayer(Player);
            _world.RunTick(0);
        }

        public SpellSystem Spells { get; }
        public QuestRewardEffects Effects { get; }
        public Player Player { get; }
        public FakeTeleports Teleports { get; } = new();
        public FakeSummons Summons { get; } = new();

        public void Dispose() => _world.Dispose();
    }

    private sealed class FakeTeleports : ITeleportSink
    {
        public bool Accept { get; set; } = true;
        public List<uint> Checked { get; } = [];
        public List<uint> Moved { get; } = [];

        public bool CanTeleport(Unit unit, uint mapId, float x, float y, float z, float orientation)
        {
            Checked.Add(mapId);
            return Accept;
        }

        public bool Teleport(Unit unit, uint mapId, float x, float y, float z, float orientation)
        {
            Moved.Add(mapId);
            return true;
        }
    }

    private sealed class FakeSummons : ISpellSummonSink
    {
        public bool Accept { get; set; } = true;
        public List<uint> Checked { get; } = [];
        public List<uint> Created { get; } = [];

        public bool CanSummon(Unit owner, uint entry)
        {
            Checked.Add(entry);
            return Accept;
        }

        public Unit? Summon(Unit caster, uint entry, float x, float y, float z, float orientation, int durationMs)
        {
            Created.Add(entry);
            return caster;
        }
    }

    private sealed class FakeBook : ISpellbook
    {
        public HashSet<uint> Known { get; } = [];

        public bool HasSpell(Player player, uint spellId) => Known.Contains(spellId);

        public bool LearnSpell(Player player, uint spellId) => Known.Add(spellId);
    }

    private sealed class NoopSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }

    private sealed class NullSession : IPlayerSession
    {
        public int AccountId => 1;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }
}
