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
    public const uint Cooldown = 9005;
    public const uint SlowBolt = 9006;
    public const uint DeathDot = 9007;
    public const uint DeathStun = 9008;
    public const uint DeathRoot = 9009;

    /// <summary>An auto-repeat spell (ranged slot 0x2, Ex2 0x20) that heals the caster: the Auto Shot toggle without weapon or ammo checks (ranged lane).</summary>
    public const uint SelfShoot = 9010;

    /// <summary>Spell 14824 of classic-db (the Light Quiver equip spell): passive, aura 141 MOD_RANGED_AMMO_HASTE +10 (ranged lane).</summary>
    public const uint QuiverHasteSpell = 14824;

    /// <summary>
    /// A synthetic non-passive "Equip:" item spell (items lane): permanent dummy aura that stacks on itself up to 3, so a saved copy
    /// restored at login on top of the equip replay would show as a second stack instead of refreshing in place.
    /// </summary>
    public const uint StackingEquipSpell = 9011;

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(Content()));
        services.AddSingleton<ICharacterSpellStore, InMemoryCharacterSpellStore>();
        services.AddSingleton<ICharacterSpellStateStore, InMemoryCharacterSpellStateStore>();
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
            With(Spell(Cooldown, "Test Cooldown", effect: 3, value: 0, targetA: 1), c => c.RecoveryTime = 60_000),
            With(Spell(SlowBolt, "Test Slow Bolt", effect: 2, value: 3, targetA: 6), b =>
            {
                b.CastingTimeIndex = 5;
                b.RangeIndex = 4;
                b.InterruptFlags = 0x2; // damage pushback
            }),
            With(Spell(DeathDot, "Test Death DoT", effect: 6, value: 1, targetA: 1), d =>
            {
                d.EffectApplyAuraName1 = 3; // SPELL_AURA_PERIODIC_DAMAGE
                d.EffectAmplitude1 = 3000;
                d.DurationIndex = 3;
                d.SpellVisual = 1;
            }),
            With(Spell(DeathStun, "Test Death Stun", effect: 6, value: 0, targetA: 1), s =>
            {
                s.EffectApplyAuraName1 = 12; // SPELL_AURA_MOD_STUN
                s.DurationIndex = 3;
                s.SpellVisual = 1;
            }),
            With(Spell(QuiverHasteSpell, "Test Quiver Haste", effect: 6, value: 10, targetA: 1), q =>
            {
                q.EffectApplyAuraName1 = 141;
                q.Attributes = 0x40; // passive
                q.SpellVisual = 1;
            }),
            With(Spell(SelfShoot, "Test Self Shoot", effect: 10, value: 1, targetA: 1), s =>
            {
                s.Attributes = 0x12;
                s.AttributesEx2 = 0x20;
                s.StartRecoveryCategory = 0;
                s.StartRecoveryTime = 0;
            }),
            With(Spell(DeathRoot, "Test Death Root", effect: 6, value: 0, targetA: 1), s =>
            {
                s.EffectApplyAuraName1 = 26; // SPELL_AURA_MOD_ROOT
                s.DurationIndex = 3;
                s.SpellVisual = 1;
            }),
            With(Spell(StackingEquipSpell, "Test Stacking Equip", effect: 6, value: 1, targetA: 1), s =>
            {
                s.EffectApplyAuraName1 = 4; // SPELL_AURA_DUMMY
                s.DurationIndex = 21;       // permanent
                s.StackAmount = 3;
                s.SpellVisual = 1;
            }),
        ],
        [new SpellCastTimeRow { Id = 2, CastTime = 500, MinCastTime = 500 }, new SpellCastTimeRow { Id = 5, CastTime = 3000, MinCastTime = 3000 }],
        [new SpellDurationRow { Id = 3, Duration = 15000, MaxDuration = 15000 }, new SpellDurationRow { Id = 21, Duration = -1, MaxDuration = -1 }],
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

/// <summary>In-memory <see cref="ICharacterSpellStateStore"/>; <see cref="FailSave"/> makes saves throw.</summary>
internal sealed class InMemoryCharacterSpellStateStore : ICharacterSpellStateStore
{
    private readonly ConcurrentDictionary<int, CharacterSpellState> _states = new();

    public bool FailSave { get; set; }

    public int Saves { get; private set; }

    public Task<CharacterSpellState> LoadAsync(int characterId, CancellationToken cancellationToken = default)
        => Task.FromResult(_states.TryGetValue(characterId, out CharacterSpellState? state) ? state : new CharacterSpellState([], []));

    public Task SaveAsync(int characterId, CharacterSpellState state, CancellationToken cancellationToken = default)
    {
        if (FailSave)
        {
            throw new InvalidOperationException("spell state storage unavailable");
        }

        Saves++;
        _states[characterId] = new CharacterSpellState([.. state.Cooldowns], [.. state.Auras], [.. state.CooldownOwners ?? []]);
        return Task.CompletedTask;
    }

    public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        _states.TryRemove(characterId, out _);
        return Task.CompletedTask;
    }
}
