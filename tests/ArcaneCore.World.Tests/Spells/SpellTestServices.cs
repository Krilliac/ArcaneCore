using System.Collections.Concurrent;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Content.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// In-memory spell tables and spellbooks for <see cref="WorldTestHost"/> (discovered). The
/// content is synthetic: the human warrior starts with <see cref="Heal"/> and <see cref="Bolt"/>;
/// no passive spell is a starting spell, so logins of other tests stay packet-for-packet unchanged.
/// </summary>
internal sealed class SpellTestServices : IWorldTestServices
{
    public const uint Heal = 9001;
    public const uint Bolt = 9002;
    public const uint Learnable = 9003;
    public const uint Renew = 9004;

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(Content()));
        services.AddSingleton<ICharacterSpellStore, InMemoryCharacterSpellStore>();
    }

    public static SpellContent Content() => new(
        [
            Spell(Heal, "Test Heal", effect: 10, value: 20, targetA: 1),
            With(Spell(Bolt, "Test Bolt", effect: 2, value: 7, targetA: 6), b =>
            {
                b.CastingTimeIndex = 2;
                b.RangeIndex = 4;
                b.InterruptFlags = 0x1; // movement
            }),
            Spell(Learnable, "Test Learnable", effect: 10, value: 5, targetA: 1),
            With(Spell(Renew, "Test Renew", effect: 6, value: 3, targetA: 1), r =>
            {
                r.EffectApplyAuraName1 = 8; // SPELL_AURA_PERIODIC_HEAL
                r.EffectAmplitude1 = 1000;
                r.DurationIndex = 3;
                r.SpellVisual = 1;
            }),
        ],
        [new SpellCastTimeRow { Id = 2, CastTime = 500, MinCastTime = 500 }],
        [new SpellDurationRow { Id = 3, Duration = 15000, MaxDuration = 15000 }],
        [new SpellRangeRow { Id = 1 }, new SpellRangeRow { Id = 4, MaxRange = 30 }],
        [],
        [new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = Heal }, new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = Bolt }],
        []);

    private static SpellTemplateRow Spell(uint id, string name, uint effect, int value, uint targetA) => new()
    {
        Id = id,
        SpellName = name,
        RangeIndex = 1,
        Effect1 = effect,
        EffectBasePoints1 = value - 1,
        EffectBaseDice1 = 1,
        EffectDieSides1 = 1,
        EffectImplicitTargetA1 = targetA,
        StartRecoveryCategory = 133,
        StartRecoveryTime = 1500,
    };

    private static SpellTemplateRow With(SpellTemplateRow row, Action<SpellTemplateRow> change)
    {
        change(row);
        return row;
    }
}

internal sealed class InMemorySpellContentStore(SpellContent content) : ISpellContentStore
{
    public Task<SpellContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);

    public Task ReplaceDbcTablesAsync(SpellDbcContent dbc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class InMemoryCharacterSpellStore : ICharacterSpellStore
{
    private readonly ConcurrentDictionary<(int, uint), byte> _rows = new();

    public Task<IReadOnlyList<CharacterSpellRow>> GetAllAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CharacterSpellRow>>(
            [.. _rows.Keys.Order().Select(k => new CharacterSpellRow { CharacterId = k.Item1, Spell = k.Item2 })]);

    public Task<IReadOnlyList<uint>> GetAsync(int characterId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<uint>>([.. _rows.Keys.Where(k => k.Item1 == characterId).Select(k => k.Item2).Order()]);

    public Task AddAsync(int characterId, IReadOnlyCollection<uint> spells, CancellationToken cancellationToken = default)
    {
        foreach (uint spell in spells)
        {
            _rows.TryAdd((characterId, spell), 0);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(int characterId, uint spell, CancellationToken cancellationToken = default)
    {
        _rows.TryRemove((characterId, spell), out _);
        return Task.CompletedTask;
    }

    public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        foreach ((int, uint) key in _rows.Keys.Where(k => k.Item1 == characterId))
        {
            _rows.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }
}
