using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Mods;

/// <inheritdoc cref="ISpellModEngine"/>
public sealed class SpellModEngine : ISpellModEngine
{
    private readonly ConditionalWeakTable<Unit, PlayerMods> _mods = new();

    public SpellModOptions Options { get; } = new();

    public ISpellModOwnerResolver OwnerResolver { get; set; } = new SelfModOwnerResolver();

    public IClassMaskSource? MaskSource { get; set; }

    public event Action<Player, SpellMod, bool>? Changed;

    public float Apply(Unit caster, SpellInfo spell, SpellModOp op, float value)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        if (!Options.Enabled || OwnerResolver.GetModOwner(caster) is not { } owner || !_mods.TryGetValue(owner, out PlayerMods? held))
        {
            return value;
        }

        List<SpellMod>? list = held.Of(op);
        return list is { Count: > 0 } ? SpellModMath.Evaluate(list, spell, op, value) : value;
    }

    public int Apply(Unit caster, SpellInfo spell, SpellModOp op, int value) => (int)Apply(caster, spell, op, (float)value);

    public ulong ClassMask(SpellInfo spell, int effectIndex)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return MaskSource?.TryGetMask(spell.Id, effectIndex) ?? spell.Effects[effectIndex].ItemType;
    }

    /// <summary>
    /// How many modifier auras with proc charges were left unregistered (their consumption path does not exist yet, so applying
    /// them would make a Clearcasting-shaped mod permanent).
    /// </summary>
    public int InertChargedMods { get; internal set; }

    public IReadOnlyList<SpellMod> ModsOf(Unit owner, SpellModOp op)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return _mods.TryGetValue(owner, out PlayerMods? held) && held.Of(op) is { } list ? list : [];
    }

    public void Add(Player owner, SpellMod mod)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(mod);
        _mods.GetOrCreateValue(owner).Add(mod);
        Changed?.Invoke(owner, mod, true);
    }

    public bool Remove(Player owner, SpellMod mod)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(mod);
        if (!_mods.TryGetValue(owner, out PlayerMods? held) || !held.Remove(mod))
        {
            return false;
        }

        Changed?.Invoke(owner, mod, false);
        return true;
    }

    private sealed class PlayerMods
    {
        private readonly List<SpellMod>?[] _byOp = new List<SpellMod>?[(int)SpellModOp.Max];

        public List<SpellMod>? Of(SpellModOp op) => (uint)op < (uint)_byOp.Length ? _byOp[(int)op] : null;

        public void Add(SpellMod mod) => (_byOp[(int)mod.Op] ??= []).Add(mod);

        public bool Remove(SpellMod mod) => _byOp[(int)mod.Op]?.Remove(mod) ?? false;
    }
}
