using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Procs;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The proc-script registry beyond one script per spell id, and the post-finish deferral of proc casts.
/// <list type="bullet">
/// <item>Icon-keyed scripts: vmangos <c>Unit::HandleDummyAuraProc</c> recognises some talents by SpellFamilyName and SpellIconID rather than by spell
/// id (Magic Absorption, icon 459; Master of Elements, icon 1920, UnitAuraProcHandler.cpp:832-862), so every rank of the talent is served by one
/// registration. The match is made against the proccing spell when it procs, so a spell table loaded or reloaded after the scripts were
/// registered (the world builds its spell system on an empty table and fills it later) needs no re-registration.</item>
/// <item>Post-finish deferral: vmangos casts Ruthlessness (14157) and Seal Fate (14189) "AFTER finishing move (or they get dropped in finish
/// phase)", as a lambda event after the current generic spell (UnitAuraProcHandler.cpp:1592-1615). Here the owner of the finish step (the combo
/// point service, which clears a finisher's points in <c>Spell::finish</c>, Spell.cpp:4374-4395) runs the deferred casts right after that step.</item>
/// </list>
/// </summary>
public sealed partial class SpellSystem
{
    private readonly Dictionary<(uint Family, uint Icon), IProcScript> _iconProcScripts = [];
    private readonly ConditionalWeakTable<SpellCast, List<Action>> _postFinishProcs = new();

    /// <summary>
    /// Install the proc script of every aura spell of <paramref name="spellFamily"/> with SpellIconID <paramref name="iconId"/> (vmangos
    /// <c>dummySpell-&gt;SpellIconID == ...</c> inside the family case). A spell with a script of its own id is served by that script first.
    /// A second registration for the same family and icon is a startup error.
    /// </summary>
    public void RegisterIconProcScript(uint spellFamily, uint iconId, IProcScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (iconId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iconId), "icon 0 is no icon");
        }

        if (!_iconProcScripts.TryAdd((spellFamily, iconId), script))
        {
            throw new InvalidOperationException($"spell family {spellFamily} icon {iconId} already has a proc script");
        }
    }

    /// <summary>The proc script that serves <paramref name="spell"/>: the one registered for its id, else the one for its family and icon, or null.</summary>
    public IProcScript? FindProcScript(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return _procScripts.GetValueOrDefault(spell.Id)
            ?? (spell.SpellIconId != 0 ? _iconProcScripts.GetValueOrDefault((spell.SpellFamilyName, spell.SpellIconId)) : null);
    }

    /// <summary>Whether a periodic trigger-spell script is registered for <paramref name="spellId"/> (<see cref="RegisterPeriodicTriggerScript"/>).</summary>
    public bool HasPeriodicTriggerScript(uint spellId) => _periodicTriggerScripts.ContainsKey(spellId);

    /// <summary>
    /// Whether something runs <see cref="RunPostFinishProcs"/> after its finish step (vmangos without CONFIG_UINT32_SPELL_PROC_DELAY). Off, a proc
    /// that would wait for the end of the cast is cast at once, which is right when nothing clears state at the finish.
    /// </summary>
    public bool PostFinishProcsEnabled { get; private set; }

    /// <summary>Turn the post-finish deferral on: the caller promises to call <see cref="RunPostFinishProcs"/> for every finished cast.</summary>
    public void EnablePostFinishProcs() => PostFinishProcsEnabled = true;

    /// <summary>The generic cast <paramref name="unit"/> is running (vmangos <c>GetCurrentSpell(CURRENT_GENERIC_SPELL)</c>), or null.</summary>
    internal SpellCast? CurrentGenericCast(Unit unit)
        => GetState(unit.Guid) is { } state && ReferenceEquals(state.Unit, unit) && state.CurrentCast is { State: not SpellCastState.Finished } cast ? cast : null;

    /// <summary>Run <paramref name="action"/> when <paramref name="cast"/> has finished (see <see cref="RunPostFinishProcs"/>).</summary>
    internal void DeferUntilFinished(SpellCast cast, Action action) => _postFinishProcs.GetOrCreateValue(cast).Add(action);

    /// <summary>Run, once, the proc casts deferred to the end of <paramref name="cast"/>, in the order they were deferred.</summary>
    public void RunPostFinishProcs(SpellCast cast)
    {
        ArgumentNullException.ThrowIfNull(cast);
        if (!_postFinishProcs.TryGetValue(cast, out List<Action>? actions))
        {
            return;
        }

        _postFinishProcs.Remove(cast);
        foreach (Action action in actions)
        {
            action();
        }
    }
}
