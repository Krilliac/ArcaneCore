using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
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

    /// <summary>
    /// Whether <paramref name="unit"/>'s faction is hostile to players as such (mangos Unit::IsHostileToPlayers, Object/UnitHostility.cpp:392-407:
    /// the template's hostile mask carries FACTION_MASK_PLAYER): what makes a guard attack a mob that wanders into town. The default
    /// (false) is for implementations that cannot read the faction templates; <see cref="FactionCreatureHostility"/> answers from them.
    /// </summary>
    bool IsHostileToPlayers(Unit unit) => false;

    /// <summary>
    /// Whether <paramref name="creature"/>'s faction is friendly to <paramref name="other"/> in either direction (vmangos
    /// WorldObject::GetFactionReactionTo REP_FRIENDLY, Object.cpp:3734-3741, template part): whom a guard defends. The combat
    /// hooks' <c>IsFriendly</c> is deliberately player-only (FactionCombatHooks remarks), so creature friendliness lives here. The
    /// default (false) is for implementations without faction templates.
    /// </summary>
    bool IsFriendly(Creature creature, Unit other) => false;
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

    /// <summary>DBCEnums.h FACTION_MASK_PLAYER (mangos Server/DBCEnums.h:71): the bit of every player in the faction masks.</summary>
    public const uint FactionMaskPlayer = 1;

    public static FactionCreatureHostility Empty { get; } = new(FactionTemplateCatalog.Empty);

    /// <summary>
    /// FactionTemplateEntry::IsHostileToPlayers (mangos Server/DBCStructure.h:501): the hostile mask has the player bit. The reference's
    /// Unit::IsHostileToPlayers also denies it for a faction with a reputation list (Faction.dbc ReputationIndex), which this catalog does
    /// not carry; a reputation faction hostile by mask (none known) would be over-reported.
    /// </summary>
    public bool IsHostileToPlayers(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return factions.Find(unit.FactionTemplate) is { } own && own.Faction != 0 && (own.HostileMask & FactionMaskPlayer) != 0;
    }

    /// <summary>FactionTemplateEntry::IsFriendlyTo either way, unless <paramref name="creature"/>'s template is hostile to the other's (hostile wins, Object.cpp:3734-3741).</summary>
    public bool IsFriendly(Creature creature, Unit other)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(other);
        if (factions.Find(creature.FactionTemplate) is not { } own || factions.Find(other.FactionTemplate) is not { } target)
        {
            return false;
        }

        return !own.IsHostileTo(target) && (own.IsFriendlyTo(target) || target.IsFriendlyTo(own));
    }

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

    /// <summary>Cast at a world position (ScriptDev2 SpellCastTargets DEST_LOCATION, for Zumrah's grave spell).</summary>
    CreatureCastResult CastAtDestination(Creature caster, uint spellId, float x, float y, float z, bool triggered)
        => CreatureCastResult.NoSpellSystem;

    /// <summary>
    /// A script makes another unit the caster (ScriptDev2 <c>target->CastSpell(target, spell, true)</c>: mangos-classic boss_heigan.cpp
    /// port, vmangos boss_maexxna.cpp UpdateWraps). Caster-relative implicit targets (UNIT_CASTER, the caster's spell_target_position)
    /// then resolve to that unit, not to the scripted creature. <see cref="CreatureCastResult.NoSpellSystem"/> by default.
    /// </summary>
    CreatureCastResult CastByUnit(Unit caster, uint spellId, Unit? target, bool triggered) => CreatureCastResult.NoSpellSystem;

    bool IsCasting(Creature caster);

    bool HasAura(Unit unit, uint spellId);

    /// <summary>Remove every aura of <paramref name="spellId"/> from <paramref name="unit"/> (vmangos Unit::RemoveAurasDueToSpell). Nothing by default.</summary>
    void RemoveAuras(Unit unit, uint spellId)
    {
    }

    /// <summary>
    /// Put the auras of <paramref name="spellId"/> on <paramref name="unit"/> without a cast (vmangos Unit::AddAura); with
    /// <paramref name="permanent"/> the holder never runs out (ADD_AURA_PERMANENT). <see cref="CreatureCastResult.NoSpellSystem"/> by default.
    /// </summary>
    CreatureCastResult AddAura(Unit unit, uint spellId, bool permanent) => CreatureCastResult.NoSpellSystem;

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
/// <c>AggressorAI</c>, <c>CritterAI</c>, <c>GuardAI</c>, <c>EventAI</c>. Scripts register more names before the world starts.
/// An empty name picks GuardAI for a template with the GUARD extra flag, then EventAI only with <c>Creatures:ImplicitEventAi</c> (default off) and creature_ai_scripts rows for the entry or spawn, else ReactorAI for civilians and AggressorAI otherwise; an unknown name uses
/// the same default and is reported once.
/// </summary>
public sealed class CreatureAiFactory
{
    public const string EventAIName = "EventAI";

    public const string GuardAIName = "GuardAI";

    /// <summary>vmangos GuardEventAI: EventAI with the guard on-sight rules (AI/CreatureAIRegistry.cpp:50).</summary>
    public const string GuardEventAIName = "GuardEventAI";

    /// <summary>CreatureType.dbc id of a critter (CREATURE_TYPE_CRITTER).</summary>
    public const uint CritterType = 8;

    private readonly Dictionary<string, Func<Creature, CreatureContent, CreatureAI>> _factories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NullAI"] = static (c, _) => new NullCreatureAI(c),
        ["ReactorAI"] = static (c, _) => new ReactorAI(c),
        ["PassiveAI"] = static (c, _) => new ReactorAI(c),
        ["AggressorAI"] = static (c, _) => new AggressorAI(c),
        ["CritterAI"] = static (c, _) => new CritterAI(c),
        [GuardAIName] = static (c, _) => new GuardAI(c),
        [EventAIName] = static (c, content) => new CreatureEventAI(c, content.Ai)
        {
            UsesGuardSightRules = (c.Template.Behaviour & CreatureBehaviourFlags.Guard) != 0, // vmangos GuardEventAI::Permissible
        },
        [GuardEventAIName] = static (c, content) => new CreatureEventAI(c, content.Ai) { UsesGuardSightRules = true },
    };

    /// <summary>
    /// The creature scripts of this server's quests by creature entry (the ScriptDev2 <c>ScriptName</c> of their <c>creature_template</c> row):
    /// selected before the AIName, as vmangos FactorySelector::selectAI asks the
    /// script name first (AI/CreatureAISelector.cpp:37-50). Built in: <see cref="Scripts.RuulSnowhoofAI"/> and the data-driven escorts of
    /// <see cref="Scripts.Escorts.EscortSpecCatalog"/>.
    /// </summary>
    private readonly Dictionary<uint, Func<Creature, CreatureAI>> _entryScripts = new()
    {
        [Scripts.RuulSnowhoofAI.Entry] = static c => new Scripts.RuulSnowhoofAI(c),
        [Scripts.AMe01AI.Entry] = static c => new Scripts.AMe01AI(c),
        [Scripts.RinjiAI.Entry] = static c => new Scripts.RinjiAI(c),
        [Scripts.MuglashAI.Entry] = static c => new Scripts.MuglashAI(c),
        [Scripts.VolcorAI.Entry] = static c => new Scripts.VolcorAI(c),
        [Scripts.BartlebyAI.Entry] = static c => new Scripts.BartlebyAI(c),
        [Scripts.DashelStonefistAI.Entry] = static c => new Scripts.DashelStonefistAI(c),
        [Scripts.SquireRoweAI.Entry] = static c => new Scripts.SquireRoweAI(c),
        [Scripts.MelizzaBrimbuzzleAI.Entry] = static c => new Scripts.MelizzaBrimbuzzleAI(c),
        [Scripts.ErisHavenfireAI.Entry] = static c => new Scripts.ErisHavenfireAI(c),
        [Scripts.RanshallaAI.Entry] = static c => new Scripts.RanshallaAI(c),
        [Scripts.ReginaldWindsorAI.Entry] = static c => new Scripts.ReginaldWindsorAI(c),
        [Scripts.TaelanFordringAI.Entry] = static c => new Scripts.TaelanFordringAI(c),
        [Scripts.IsillienAI.Entry] = static c => new Scripts.IsillienAI(c),
        [Scripts.TirionFordringAI.Entry] = static c => new Scripts.TirionFordringAI(c),
    };

    /// <summary>The exploration/event quests the entry scripts complete (an escort's quest): <see cref="RegisterEntryScript"/>'s list.</summary>
    private readonly HashSet<uint> _entryScriptQuests = [Scripts.RuulSnowhoofAI.QuestFreedomToRuul, Scripts.AMe01AI.QuestChasingAMe, Scripts.RinjiAI.QuestRinjiTrapped, Scripts.MuglashAI.QuestVorsha,
        Scripts.VolcorAI.QuestEscapeThroughForce, Scripts.VolcorAI.QuestEscapeThroughStealth, Scripts.BartlebyAI.QuestBeat,
        Scripts.DashelStonefistAI.QuestMissingDiploPt8, Scripts.MelizzaBrimbuzzleAI.QuestGetMeOutOfHere,
        Scripts.ErisHavenfireAI.QuestBalanceOfLightAndShadow, Scripts.RanshallaAI.QuestGuardiansAltar,
        Scripts.ReginaldWindsorAI.QuestTheGreatMasquerade,
        Scripts.TaelanFordringAI.QuestInDreams];

    /// <summary>Registers every <see cref="Scripts.Escorts.EscortSpecCatalog"/> escort as an entry script (a clash with a hand-ported one throws).</summary>
    public CreatureAiFactory()
    {
        foreach (Scripts.Escorts.EscortSpec spec in Scripts.Escorts.EscortSpecCatalog.All)
        {
            RegisterEntryScript(spec.Entry, c => new Scripts.Escorts.DataDrivenEscortAI(c, spec), spec.QuestId);
        }
    }

    public IReadOnlyCollection<string> Names => _factories.Keys;

    /// <summary>The exploration/event quests the entry scripts complete (what lets the quest service offer them).</summary>
    public IReadOnlyCollection<uint> ScriptedEventQuests => _entryScriptQuests;

    /// <summary>
    /// Give every creature of <paramref name="entry"/> the script AI <paramref name="factory"/> builds (a duplicate throws: fail closed);
    /// <paramref name="eventQuests"/> are the exploration/event quests it completes.
    /// </summary>
    public void RegisterEntryScript(uint entry, Func<Creature, CreatureAI> factory, params uint[] eventQuests)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(eventQuests);
        if (!_entryScripts.TryAdd(entry, factory))
        {
            throw new InvalidOperationException($"creature entry {entry} already has a script AI");
        }

        _entryScriptQuests.UnionWith(eventQuests);
    }

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
        => Create(creature, content, out unknown, implicitEventAi: false);

    /// <summary>
    /// Select the AI (vmangos FactorySelector::selectAI, AI/CreatureAISelector.cpp:37-100, as far as this server has the classes): the
    /// template's AIName when it names a registered AI; otherwise GuardAI for a template with the GUARD extra flag (mangos
    /// CreatureAISelector.cpp:85-88: the guard check comes after the script name and before the permit contest); otherwise, with
    /// <paramref name="implicitEventAi"/> (<c>Creatures:ImplicitEventAi</c>) and <c>creature_ai_scripts</c> rows for the creature's entry or spawn guid,
    /// EventAI (the cmangos-classic default permit, a deviation from vmangos, off by default; the retail route is the template's AIName
    /// 'EventAI', which classic-db carries); otherwise ReactorAI for a civilian and AggressorAI for the rest. A summoned pet, guardian or
    /// totem never gets the implicit EventAI or GuardAI.
    /// </summary>
    public CreatureAI Create(Creature creature, CreatureContent content, out bool unknown, bool implicitEventAi)
    {
        ArgumentNullException.ThrowIfNull(creature);
        unknown = false;
        // vmangos FactorySelector::selectAI (AI/CreatureAISelector.cpp:37-50): a named script registered by
        // the host precedes AIName. A missing port falls through to the existing entry script or default AI.
        if (creature.Summon is not { Kind: SummonKind.Pet } && creature.CharmerGuid.IsEmpty
            && !string.IsNullOrEmpty(creature.Template.ScriptName)
            && _factories.TryGetValue(creature.Template.ScriptName, out Func<Creature, CreatureContent, CreatureAI>? namedScript))
        {
            return namedScript(creature, content);
        }
        // The script name first (selectAI, AI/CreatureAISelector.cpp:39-46): not for a pet nor a charmed creature.
        if (_entryScripts.Count > 0 && creature.Summon is not { Kind: SummonKind.Pet } && creature.CharmerGuid.IsEmpty
            && _entryScripts.TryGetValue(creature.Template.Entry, out Func<Creature, CreatureAI>? script))
        {
            return script(creature);
        }

        string name = creature.Template.AIName;
        if (!string.IsNullOrEmpty(name))
        {
            if (_factories.TryGetValue(name, out Func<Creature, CreatureContent, CreatureAI>? factory))
            {
                return factory(creature, content);
            }

            unknown = true;
        }
        else if (creature.Summon is null && (creature.Template.Behaviour & CreatureBehaviourFlags.Guard) != 0)
        {
            return new GuardAI(creature);
        }
        else if (implicitEventAi && creature.Summon is null && HasEventRows(creature, content)
            && _factories.TryGetValue(EventAIName, out Func<Creature, CreatureContent, CreatureAI>? eventFactory))
        {
            return eventFactory(creature, content);
        }

        if (string.IsNullOrEmpty(name) && creature.Template.CreatureType == CritterType && creature.Summon is null)
        {
            return new CritterAI(creature); // vmangos selects CritterAI for type 8 before the permit contest (AI/CreatureAISelector.cpp:78-79)
        }

        return creature.Template.Civilian ? new ReactorAI(creature) : new AggressorAI(creature);
    }

    /// <summary>Whether <c>creature_ai_scripts</c> has rows for the creature's entry, or for its spawn (a negative creature_id).</summary>
    private static bool HasEventRows(Creature creature, CreatureContent content)
        => content.Ai.GetEvents(creature.Template.Entry).Count > 0
            || (creature.Spawn is { } spawn && content.Ai.GetGuidEvents(spawn.Guid).Count > 0);
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

    /// <summary>The towns' guard posts (vmangos GuardMgr), shared by every map system built with these services.</summary>
    public GuardPostTable GuardPosts { get; init; } = new();

    /// <summary>
    /// The conditions table (cmangos IsConditionSatisfied) for EventAI rows that carry a condition id (EVENT_T_RECEIVE_EMOTE, EVENT_T_DEATH,
    /// EVENT_T_OOC_LOS); null: such rows never fire. Bound from the world's condition feature.
    /// </summary>
    public Npc.IConditionEvaluator? Conditions { get; init; }

    /// <summary>The area id a creature stands in (vmangos GetAreaId); null asks the map's terrain (<c>Map.GetZoneAndAreaId</c>).</summary>
    public Func<Creature, uint>? AreaOf { get; init; }

    /// <summary>
    /// The zone and area a creature stands in (cmangos GetZoneAndAreaId; the EventAI SPAWNED zone condition); null asks the map's terrain
    /// (<c>Map.GetZoneAndAreaId</c>).
    /// </summary>
    public Func<Creature, (uint ZoneId, uint AreaId)>? ZoneAndAreaOf { get; init; }

    /// <summary>
    /// A creature's team (vmangos Unit::GetTeam, Unit.cpp:4960-4973: its faction's Faction.dbc team field, 469 Alliance or 67 Horde;
    /// null for anything else). A guard post called against an enemy no player controls sends the guard of the civilian's own team
    /// (GuardMgr::GetTeam); null here: nobody comes. Bound by the world from FactionTemplate.dbc and Faction.dbc.
    /// </summary>
    public Func<Creature, Team?>? TeamOf { get; init; }

    /// <summary>
    /// Quest credit that EventAI gives (ACTION_T_QUEST_EVENT 15, ACTION_T_KILLED_MONSTER 33); null: those actions fail. Bound by the world to
    /// its quest service.
    /// </summary>
    public IEventAiQuestEvents? QuestEvents { get; init; }

    /// <summary>
    /// The quest log of DB scripts (QUEST_EXPLORED 7, KILL_CREDIT 8) and player-linked escorts (their quest failure and group range check);
    /// null: those credit nothing and an escort sees only its own player. Bound by the world to its quest service.
    /// </summary>
    public IScriptQuestEvents? ScriptQuests { get; init; }

    /// <summary>
    /// The item templates DB scripts equip creatures with (SCRIPT_COMMAND_SET_EQUIPMENT_SLOTS 42, cmangos Creature::SetVirtualItem); null:
    /// such a step can only empty slots. Bound by the world to its item store.
    /// </summary>
    public Func<uint, ArcaneCore.Kernel.Items.ItemTemplate?>? ItemTemplateOf { get; init; }
}

/// <summary>The quest credit EventAI actions give (cmangos Player methods called from CreatureEventAI::ProcessAction).</summary>
public interface IEventAiQuestEvents
{
    /// <summary>
    /// cmangos Player::AreaExploredOrEventHappens, or with <paramref name="rewardGroup"/> RewardPlayerAndGroupAtEventExplored (every group
    /// member near <paramref name="source"/>): the quest's exploration or event objective is done.
    /// </summary>
    void EventHappened(Player player, uint questId, Creature source, bool rewardGroup);

    /// <summary>cmangos Player::RewardPlayerAndGroupAtEventCredit: kill credit for <paramref name="creatureEntry"/> to the player and its group near the source.</summary>
    void KillCredit(Player player, uint creatureEntry, Creature source);
}
