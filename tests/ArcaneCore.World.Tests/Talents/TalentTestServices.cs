using System.Collections.Concurrent;
using ArcaneCore.Data.Characters.Talents;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.World.Talents;
using ArcaneCore.World.Tests.Npc;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Talents;

/// <summary>
/// A synthetic talent setup for one <see cref="WorldTestHost"/>: set <see cref="Current"/> before
/// <c>WorldTestHost.Start</c> (the services are only registered then, so every other test keeps the inert feature).
/// It also makes the quest-interaction creature a gossip trainer: gossip menu 0 offers GOSSIP_OPTION_UNLEARNTALENTS,
/// and NpcServices:NpcTemplates names it a warrior class trainer.
/// </summary>
internal sealed class TalentTestServices : IWorldTestServices
{
    public static readonly AsyncLocal<TalentWorldFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture)
        {
            return;
        }

        services.AddSingleton(fixture.Catalog);
        services.AddSingleton<ICharacterTalentStore>(fixture.Store);
        services.AddSingleton(fixture.Abilities);
        services.AddSingleton<IConfiguration>(fixture.Configuration());
        services.AddSingleton<INpcContentStore>(fixture);
        services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(fixture.SpellContent()));
        services.AddSingleton<ITalentResetFlagStore>(sp => fixture.ResetFlags.Attach(sp.GetRequiredService<ICharacterStore>()));
    }
}

internal sealed class TalentWorldFixture : INpcContentStore
{
    // talent 1 (warrior tab, row 0): three passive-less ranks; talent 2: row 1; talent 5: an ability talent with a trainer-learned rank 2
    public const uint T1R1 = 20001, T1R2 = 20002, T1R3 = 20003;
    public const uint Row1R1 = 20011;
    public const uint P1 = 20021, P2 = 20022;
    public const uint MageR1 = 20901;

    /// <summary>"Untalent Visual Effect" (vmangos SkillHandler.cpp:57).</summary>
    public const uint UntalentVisual = 14867;

    /// <summary>A self-cast permanent SPELL_AURA_FEIGN_DEATH (the wipe ends it, vmangos SkillHandler.cpp:46-48).</summary>
    public const uint FeignDeath = 20990;

    /// <summary>The reset-at-login requests of this host (<see cref="ITalentResetFlagStore"/>).</summary>
    public MemoryTalentResetFlags ResetFlags { get; } = new();

    public Dictionary<string, string?> Settings { get; } = [];

    public MemoryTalentStore Store { get; } = new();

    public TalentCatalog Catalog { get; } = new(
        [new TalentTabRecord(1, 1u << 0, 0), new TalentTabRecord(2, 1u << 7, 1)],
        [
            new TalentRecord(1, 1, 0, 0, [T1R1, T1R2, T1R3, 0, 0], 0, 0, 0),
            new TalentRecord(2, 1, 1, 0, [Row1R1, 0, 0, 0, 0], 0, 0, 0),
            new TalentRecord(5, 1, 0, 3, [P1, 0, 0, 0, 0], 0, 0, 0),
            new TalentRecord(9, 2, 0, 0, [MageR1, 0, 0, 0, 0], 0, 0, 0),
        ]);

    /// <summary>P2 is the trainer-learned rank after the talent ability P1.</summary>
    public SkillLineAbilityCatalog Abilities { get; } = new([new SkillLineAbilityRecord(1, 26, P1, 0, 0, 0, P2, 0, 0, 0)]);

    public IConfiguration Configuration()
    {
        Dictionary<string, string?> values = new()
        {
            ["NpcServices:NpcTemplates:0:Entry"] = QuestInteractionFixture.Entry.ToString(),
            ["NpcServices:NpcTemplates:0:TrainerType"] = "Class",
            ["NpcServices:NpcTemplates:0:TrainerClass"] = TrainerClass.ToString(),
        };
        foreach ((string key, string? value) in Settings)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>The class id the trainer teaches (1 = warrior, the class the test client creates).</summary>
    public byte TrainerClass { get; set; } = 1;

    public SpellContent SpellContent()
    {
        SpellContent baseContent = SpellTestServices.Content();
        var visual = new SpellTemplateRow
        {
            Id = UntalentVisual,
            SpellName = "Untalent Visual Effect",
            RangeIndex = 1,
            Effect1 = 3,                      // SPELL_EFFECT_DUMMY
            EffectBaseDice1 = 1,
            EffectDieSides1 = 1,
            EffectImplicitTargetA1 = 25,      // TARGET_UNIT
        };
        var feign = new SpellTemplateRow
        {
            Id = FeignDeath,
            SpellName = "Test Feign Death",
            RangeIndex = 1,
            Effect1 = 6,                      // SPELL_EFFECT_APPLY_AURA
            EffectBaseDice1 = 1,
            EffectDieSides1 = 1,
            EffectImplicitTargetA1 = 1,       // TARGET_UNIT_CASTER
            EffectApplyAuraName1 = (uint)AuraType.FeignDeath,
            DurationIndex = 21,               // permanent
            SpellVisual = 1,
        };
        return baseContent with { Spells = [.. baseContent.Spells, visual, feign] };
    }

    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(NpcContent.Empty with
    {
        GossipMenuOptions =
        [
            new GossipMenuOption
            {
                MenuId = 0, Id = 0, OptionId = (byte)GossipOption.UnlearnTalents, NpcOptionNpcFlag = (uint)NpcFlags.Trainer,
                OptionText = "I wish to unlearn my talents.",
            },
        ],
    });
}

/// <summary>In-memory <see cref="ICharacterTalentStore"/>; <see cref="FailWrites"/> makes every write throw.</summary>
internal sealed class MemoryTalentStore : ICharacterTalentStore
{
    private readonly ConcurrentDictionary<int, CharacterTalentState> _respec = new();
    private readonly ConcurrentDictionary<(int, uint), byte> _disabled = new();

    public volatile bool FailWrites;

    public int Writes => Volatile.Read(ref _writes);

    private int _writes;

    public CharacterTalentState? RespecOf(int characterId) => _respec.GetValueOrDefault(characterId);

    public uint[] DisabledOf(int characterId) => [.. _disabled.Keys.Where(k => k.Item1 == characterId).Select(k => k.Item2).Order()];

    public void SeedDisabled(int characterId, params uint[] spells)
    {
        foreach (uint spell in spells)
        {
            _disabled[(characterId, spell)] = 0;
        }
    }

    public Task<CharacterTalentState?> GetAsync(int characterId, CancellationToken cancellationToken = default)
        => Task.FromResult(_respec.GetValueOrDefault(characterId));

    public Task SaveAsync(int characterId, CharacterTalentState state, CancellationToken cancellationToken = default)
    {
        Write();
        _respec[characterId] = state;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<uint>> GetDisabledAsync(int characterId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<uint>>(DisabledOf(characterId));

    public Task AddDisabledAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default)
    {
        Write();
        foreach (uint spell in spells)
        {
            _disabled[(characterId, spell)] = 0;
        }

        return Task.CompletedTask;
    }

    public Task RemoveDisabledAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default)
    {
        Write();
        foreach (uint spell in spells)
        {
            _disabled.TryRemove((characterId, spell), out _);
        }

        return Task.CompletedTask;
    }

    public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        Write();
        _respec.TryRemove(characterId, out _);
        foreach ((int, uint) key in _disabled.Keys.Where(k => k.Item1 == characterId))
        {
            _disabled.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    private void Write()
    {
        if (FailWrites)
        {
            throw new IOException("talent store unavailable");
        }

        Interlocked.Increment(ref _writes);
    }
}

/// <summary>
/// In-memory <see cref="ITalentResetFlagStore"/> over the host's characters (the same rules as <see cref="EfTalentResetFlagStore"/>,
/// which <c>TalentResetFlagStoreTests</c> runs on SQLite); <see cref="FailWrites"/> makes every write throw.
/// </summary>
internal sealed class MemoryTalentResetFlags : ITalentResetFlagStore
{
    private readonly ConcurrentDictionary<int, byte> _flagged = new();
    private ICharacterStore? _characters;

    public volatile bool FailWrites;

    /// <summary>While set, reading a request throws (a database outage during a login).</summary>
    public volatile bool FailReads;

    public MemoryTalentResetFlags Attach(ICharacterStore characters)
    {
        _characters = characters;
        return this;
    }

    public bool IsFlagged(int characterId) => _flagged.ContainsKey(characterId);

    public void Flag(int characterId) => _flagged[characterId] = 0;

    public async Task<bool> FlagAsync(int characterId, CancellationToken cancellationToken = default)
    {
        Write();
        if (await Characters.GetByIdAsync(characterId, cancellationToken) is null)
        {
            return false;
        }

        _flagged[characterId] = 0;
        return true;
    }

    public async Task<int> FlagAllAsync(CancellationToken cancellationToken = default)
    {
        Write();
        int flagged = 0;
        foreach (CharacterIdentity identity in await Characters.GetAllIdentitiesAsync(cancellationToken))
        {
            if (_flagged.TryAdd(identity.Id, 0))
            {
                flagged++;
            }
        }

        return flagged;
    }

    public Task<bool> IsFlaggedAsync(int characterId, CancellationToken cancellationToken = default)
        => FailReads ? Task.FromException<bool>(new IOException("character database unavailable")) : Task.FromResult(_flagged.ContainsKey(characterId));

    public Task ClearAsync(int characterId, CancellationToken cancellationToken = default)
    {
        Write();
        _flagged.TryRemove(characterId, out _);
        return Task.CompletedTask;
    }

    private ICharacterStore Characters => _characters ?? throw new InvalidOperationException("not attached to a character store");

    private void Write()
    {
        if (FailWrites)
        {
            throw new IOException("character database unavailable");
        }
    }
}
