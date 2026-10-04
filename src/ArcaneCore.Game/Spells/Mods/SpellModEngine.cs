using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Mods;

/// <inheritdoc cref="ISpellModEngine"/>
public sealed class SpellModEngine : ISpellModEngine
{
    private readonly ConditionalWeakTable<Unit, PlayerMods> _mods = new();
    private readonly List<SpellModScope> _windows = [];

    /// <summary>Removes the aura of a mod that ran out of charges; set by <see cref="SpellModModule"/>.</summary>
    internal Action<Player, uint>? RemoveAura { get; set; }

    /// <summary>
    /// The operations vmangos reads with no spell (so no charge is touched): Player::AddGCD, AddCooldown, CalcThreat,
    /// SpellEntry::CalculateDuration, the aura constructors (charges, activation time, speed/haste/attack power amounts) and the
    /// proc chance (SpellAuras.cpp, ThreatManager.cpp:44, UnitAuraProcHandler.cpp:490).
    /// </summary>
    private static bool ReadsWithoutSpell(SpellModOp op) => op is SpellModOp.Duration or SpellModOp.GlobalCooldown or SpellModOp.Cooldown
        or SpellModOp.Threat or SpellModOp.Charges or SpellModOp.ActivationTime or SpellModOp.ChanceOfSuccess or SpellModOp.Haste
        or SpellModOp.AttackPower;

    public SpellModEngine() => OwnerResolver = new PetTotemModOwnerResolver(Options);

    public SpellModOptions Options { get; } = new();

    public ISpellModOwnerResolver OwnerResolver { get; set; }

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
        return list is { Count: > 0 } ? SpellModMath.Evaluate(list, spell, op, value, ConsumingScope(owner, spell, op),
            Options.InstantCastKeepsFlatCastTimeCharge) : value;
    }

    /// <summary>The open window of this player's cast of <paramref name="spell"/>, if the operation is one that spends charges.</summary>
    private SpellModScope? ConsumingScope(Player owner, SpellInfo spell, SpellModOp op)
    {
        if (_windows.Count == 0 || ReadsWithoutSpell(op))
        {
            return null;
        }

        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            SpellModScope scope = _windows[i];
            if (ReferenceEquals(scope.Owner, owner) && scope.Spell.Id == spell.Id && !scope.IsClosed)
            {
                return scope;
            }
        }

        return null;
    }

    public SpellModScope? CreateScope(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        // Only a player's own casts spend and restore charges (vmangos guards RestoreSpellMods/RemoveSpellMods with m_caster->IsPlayer());
        // a pet's or totem's spell reads its owner's mods but never spends the owner's charges, which vmangos would leave stuck at -1.
        return Options.Enabled && caster is Player owner ? new SpellModScope(owner, spell) : null;
    }

    public SpellModWindow Begin(SpellModScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        _windows.Add(scope);
        return new SpellModWindow(this, scope);
    }

    internal void End(SpellModScope scope)
    {
        int index = _windows.LastIndexOf(scope);
        if (index >= 0)
        {
            _windows.RemoveAt(index);
        }
    }

    public void Seal(SpellModScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.IsClosed)
        {
            return;
        }

        scope.IsClosed = true;
        var spent = new SortedSet<uint>();
        foreach (SpellMod mod in scope.Applied)
        {
            if (mod.Charges == -1)
            {
                spent.Add(mod.SpellId);
            }
        }

        scope.Clear();
        foreach (uint spellId in spent)
        {
            RemoveAura?.Invoke(scope.Owner, spellId);
        }
    }

    public void Restore(SpellModScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.IsClosed)
        {
            return;
        }

        scope.IsClosed = true;
        foreach (SpellMod mod in scope.Applied)
        {
            mod.Charges = mod.Charges == -1 ? 1 : mod.Charges + 1;
        }

        scope.Clear();
    }

    public int Apply(Unit caster, SpellInfo spell, SpellModOp op, int value) => (int)Apply(caster, spell, op, (float)value);

    public ulong ClassMask(SpellInfo spell, int effectIndex)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return MaskSource?.TryGetMask(spell.Id, effectIndex) ?? spell.Effects[effectIndex].ItemType;
    }

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
