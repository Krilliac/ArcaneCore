using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Who a creature attacks on sight and who it helps. Replaceable (seam): the reputation area
/// can register an implementation that accounts for player reputation and at-war state.
/// </summary>
public interface ICreatureHostility
{
    /// <summary>Whether <paramref name="creature"/> considers <paramref name="target"/> an enemy it may aggro on sight.</summary>
    bool IsHostile(Creature creature, Unit target);

    /// <summary>Whether <paramref name="helper"/> joins a fight <paramref name="caller"/> is in (vmangos Creature::CanAssistTo faction check).</summary>
    bool CanAssist(Creature helper, Creature caller);
}

/// <summary>
/// Default hostility from FactionTemplate.dbc rows (vmangos FactionTemplateEntry::IsHostileTo:
/// explicit enemies, then explicit friends, then the hostile mask against the target's own
/// mask). Reputation is not modelled yet, so a template-only answer is used for factions with a
/// reputation list too; contested guards (FACTION_TEMPLATE_FLAG_ATTACK_PVP_ACTIVE_PLAYERS) only
/// attack players with PLAYER_FLAGS_CONTESTED_PVP. Unknown templates are not hostile (fail closed: no aggro).
/// Assistance needs the same faction template (vmangos CanAssistTo with checkfaction).
/// </summary>
public sealed class FactionCreatureHostility(FactionTemplateCatalog factions) : ICreatureHostility
{
    private const uint ContestedGuard = 0x1000;

    public static FactionCreatureHostility Empty { get; } = new(FactionTemplateCatalog.Empty);

    public bool IsHostile(Creature creature, Unit target)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(target);
        if (factions.Find(creature.FactionTemplate) is not { } own || factions.Find(target.FactionTemplate) is not { } other)
        {
            return false;
        }

        if ((own.Flags & ContestedGuard) != 0 && target is Player player && (player.Flags & PlayerFlags.ContestedPvp) != 0)
        {
            return true;
        }

        return own.IsHostileTo(other);
    }

    public bool CanAssist(Creature helper, Creature caller) => helper.FactionTemplate == caller.FactionTemplate;
}

/// <summary>Outcome of a creature cast request.</summary>
public enum CreatureCastResult
{
    Ok,
    NoSpellSystem,
    UnknownSpell,
    AlreadyCasting,
    AuraPresent,
    Failed,
}

/// <summary>
/// Creature spell casting through the world spell system (seam; <see cref="SpellSystemCreatureCaster"/>
/// adapts <see cref="SpellSystem"/>). The aura caster ownership contract is the spell system's:
/// a cast captures the exact creature as the aura owner, and <see cref="OnCreatureRemoved"/>
/// revokes that ownership when the creature leaves the world so a respawn starts a fresh lifetime.
/// </summary>
public interface ICreatureSpellCaster
{
    /// <summary>Cast <paramref name="spellId"/> at <paramref name="target"/> (self when null).</summary>
    CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered);

    bool IsCasting(Creature caster);

    bool HasAura(Unit unit, uint spellId);

    /// <summary>Stop the cast or channel in progress (evade, death).</summary>
    void Interrupt(Creature caster);

    /// <summary>The creature left the world (corpse removed, despawned): drop its spell state and revoke aura ownership.</summary>
    void OnCreatureRemoved(Creature creature);

    /// <summary>Raised when a spell lands on a unit (caster, target, spell).</summary>
    event Action<Unit, Unit, SpellInfo>? SpellHit;
}

/// <summary>
/// Builds a creature's AI from its <c>AIName</c> (vmangos CreatureAISelector / cmangos
/// ScriptMgr::GetCreatureAI). Built-ins: <c>NullAI</c>, <c>ReactorAI</c>, <c>PassiveAI</c>,
/// <c>AggressorAI</c>, <c>EventAI</c>. Scripts register more names before the world starts.
/// An empty name picks ReactorAI for civilians and AggressorAI otherwise; an unknown name uses
/// the same default and is reported once.
/// </summary>
public sealed class CreatureAiFactory
{
    public const string EventAIName = "EventAI";

    private readonly Dictionary<string, Func<Creature, CreatureContent, CreatureAI>> _factories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NullAI"] = static (c, _) => new NullCreatureAI(c),
        ["ReactorAI"] = static (c, _) => new ReactorAI(c),
        ["PassiveAI"] = static (c, _) => new ReactorAI(c),
        ["AggressorAI"] = static (c, _) => new AggressorAI(c),
        [EventAIName] = static (c, content) => new CreatureEventAI(c, content.Ai),
    };

    public IReadOnlyCollection<string> Names => _factories.Keys;

    /// <summary>Register a C# AI under <paramref name="name"/> (a duplicate throws: fail closed).</summary>
    public void Register(string name, Func<Creature, CreatureAI> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        if (!_factories.TryAdd(name, (c, _) => factory(c)))
        {
            throw new InvalidOperationException($"creature AI '{name}' is already registered");
        }
    }

    /// <summary>The AI for <paramref name="creature"/>; <paramref name="unknown"/> is true when its AIName is not registered.</summary>
    public CreatureAI Create(Creature creature, CreatureContent content, out bool unknown)
    {
        ArgumentNullException.ThrowIfNull(creature);
        string name = creature.Template.AIName;
        unknown = false;
        if (!string.IsNullOrEmpty(name))
        {
            if (_factories.TryGetValue(name, out Func<Creature, CreatureContent, CreatureAI>? factory))
            {
                return factory(creature, content);
            }

            unknown = true;
        }

        return creature.Template.Civilian ? new ReactorAI(creature) : new AggressorAI(creature);
    }
}

/// <summary>Everything the creature AI needs from other areas; each member has a working default.</summary>
public sealed class CreatureAiServices
{
    public static CreatureAiServices Default => new();

    public ICreatureHostility Hostility { get; init; } = FactionCreatureHostility.Empty;

    /// <summary>Spell casting; null leaves creatures melee-only (EventAI casts report <see cref="CreatureCastResult.NoSpellSystem"/>).</summary>
    public ICreatureSpellCaster? Spells { get; init; }

    public CreatureAiFactory Factory { get; init; } = new();

    /// <summary>Aura stacks and casting state of any unit (EventAI aura and target-casting events); null: no auras, nobody casting.</summary>
    public IUnitSpellQueries? UnitSpells { get; init; }
}
