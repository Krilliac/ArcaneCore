using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
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

    /// <summary>
    /// <see cref="AddAura(Unit, uint, bool)"/> with <paramref name="caster"/> as the aura's caster (vmangos Unit::AddAura's pCaster; the
    /// cmangos <c>victim-&gt;CastSpell(victim, spell, TRIGGERED, nullptr, nullptr, originalCaster)</c> credit).
    /// </summary>
    CreatureCastResult AddAuraFrom(Unit unit, uint spellId, Unit caster) => CreatureCastResult.NoSpellSystem;

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
    /// <summary>The npcflags that make an AI-less, non-summoned creature a <see cref="NullCreatureAI"/> (spirit healer, spirit guide and the two classic spirit bits).</summary>
    internal const uint SpiritHealerNpcFlags = (uint)(NpcFlags.SpiritHealer | NpcFlags.SpiritGuide) | 16384u | 32768u;

    public const string EventAIName = "EventAI";

    public const string GuardAIName = "GuardAI";

    /// <summary>AzerothCore SmartAI: the creature runs its <c>smart_scripts</c> rows (<see cref="CreatureSmartAI"/>).</summary>
    public const string SmartAIName = "SmartAI";

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
        [SmartAIName] = static (c, content) => new CreatureSmartAI(c, content.Ai),
        ["guard_stormwind"] = static (c, _) => new Scripts.CityGuardAI(c, Team.Alliance, stormwind: true),
        ["guard_bluffwatcher"] = static (c, _) => new GuardAI(c),
        ["guard_contested"] = static (c, _) => new GuardAI(c),
        ["guard_darnassus"] = static (c, _) => new GuardAI(c),
        ["guard_dunmorogh"] = static (c, _) => new GuardAI(c),
        ["guard_durotar"] = static (c, _) => new GuardAI(c),
        ["guard_elwynnforest"] = static (c, _) => new GuardAI(c),
        ["guard_ironforge"] = static (c, _) => new GuardAI(c),
        ["guard_mulgore"] = static (c, _) => new GuardAI(c),
        ["guard_teldrassil"] = static (c, _) => new GuardAI(c),
        ["guard_tirisfal"] = static (c, _) => new GuardAI(c),
        ["guard_undercity"] = static (c, _) => new GuardAI(c),
        ["guard_orgrimmar"] = static (c, _) => new Scripts.CityGuardAI(c, Team.Horde, stormwind: false),
        [Scripts.GuardianOfBlizzardAI.ScriptName] = static (c, _) => new Scripts.GuardianOfBlizzardAI(c),
        [Scripts.AnachronosTheAncientAI.ScriptName] = static (c, _) => new Scripts.AnachronosTheAncientAI(c),
        [Instances.Scripts.Classic.HurleyBlackbreathAI.ScriptName] = static (c, _) => new Instances.Scripts.Classic.HurleyBlackbreathAI(c),
    };

    /// <summary>
    /// The creature scripts of this server's quests by creature entry (the ScriptDev2 <c>ScriptName</c> of their <c>creature_template</c> row):
    /// selected before the AIName, as vmangos FactorySelector::selectAI asks the
    /// script name first (AI/CreatureAISelector.cpp:37-50). Built in: <see cref="Scripts.RuulSnowhoofAI"/> and the data-driven escorts of
    /// <see cref="Scripts.Escorts.EscortSpecCatalog"/>.
    /// </summary>
    private readonly Dictionary<uint, EntryScript> _entryScripts = new()
    {
        [Scripts.RuulSnowhoofAI.Entry] = E(static c => new Scripts.RuulSnowhoofAI(c)),
        [Scripts.AMe01AI.Entry] = E(static c => new Scripts.AMe01AI(c)),
        [Scripts.RinjiAI.Entry] = E(static c => new Scripts.RinjiAI(c)),
        [Scripts.MuglashAI.Entry] = E(static c => new Scripts.MuglashAI(c)),
        [Scripts.VolcorAI.Entry] = E(static c => new Scripts.VolcorAI(c)),
        [Scripts.BartlebyAI.Entry] = E(static c => new Scripts.BartlebyAI(c)),
        [Scripts.DashelStonefistAI.Entry] = E(static c => new Scripts.DashelStonefistAI(c)),
        [Scripts.SquireRoweAI.Entry] = E(static c => new Scripts.SquireRoweAI(c)),
        [Scripts.MelizzaBrimbuzzleAI.Entry] = E(static c => new Scripts.MelizzaBrimbuzzleAI(c)),
        [Scripts.ErisHavenfireAI.Entry] = E(static c => new Scripts.ErisHavenfireAI(c)),
        [Scripts.RanshallaAI.Entry] = E(static c => new Scripts.RanshallaAI(c)),
        [Scripts.ReginaldWindsorAI.Entry] = E(static c => new Scripts.ReginaldWindsorAI(c)),
        [Scripts.TaelanFordringAI.Entry] = E(static c => new Scripts.TaelanFordringAI(c)),
        [Scripts.IsillienAI.Entry] = E(static c => new Scripts.IsillienAI(c)),
        [Scripts.TirionFordringAI.Entry] = E(static c => new Scripts.TirionFordringAI(c)),
        [Scripts.KeeperRemulosAI.Entry] = E(static c => new Scripts.KeeperRemulosAI(c)),
        [Scripts.EranikusAI.Entry] = E(static c => new Scripts.EranikusAI(c)),
        [Scripts.KerlonianAI.Entry] = E(static c => new Scripts.KerlonianAI(c)),
        [Scripts.MistAI.Entry] = E(static c => new Scripts.MistAI(c)),
        [Scripts.DaphneStilwellAI.Entry] = E(static c => new Scripts.DaphneStilwellAI(c)),
        [Scripts.WitchDoctorUnbagwaAI.Entry] = E(static c => new Scripts.WitchDoctorUnbagwaAI(c)),
        [Scripts.ShortJohnMithrilAI.Entry] = E(static c => new Scripts.ShortJohnMithrilAI(c)),
        [Scripts.RestlessSoulAI.Entry] = E(static c => new Scripts.RestlessSoulAI(c)),
        [Scripts.GarmentsOfQuestsAI.EntryShaya] = E(static c => new Scripts.GarmentsOfQuestsAI(c)),
        [Scripts.GarmentsOfQuestsAI.EntryRoberts] = E(static c => new Scripts.GarmentsOfQuestsAI(c)),
        [Scripts.GarmentsOfQuestsAI.EntryDolf] = E(static c => new Scripts.GarmentsOfQuestsAI(c)),
        [Scripts.GarmentsOfQuestsAI.EntryKorja] = E(static c => new Scripts.GarmentsOfQuestsAI(c)),
        [Scripts.GarmentsOfQuestsAI.EntryDgKel] = E(static c => new Scripts.GarmentsOfQuestsAI(c)),
        [Scripts.GrarkLorkrubAI.Entry] = E(static c => new Scripts.GrarkLorkrubAI(c)),
        [Scripts.TapokeSlimJahnAI.Entry] = E(static c => new Scripts.TapokeSlimJahnAI(c)),
        [Scripts.MikhailAI.Entry] = E(static c => new Scripts.MikhailAI(c)),
        [Scripts.PrivateHendelAI.Entry] = E(static c => new Scripts.PrivateHendelAI(c)),
        [Scripts.StinkyIgnatzAI.Entry] = E(static c => new Scripts.StinkyIgnatzAI(c)),
        [Scripts.MagramiSpectreAI.Entry] = E(static c => new Scripts.MagramiSpectreAI(c)),
        [Scripts.PluckyJohnsonAI.Entry] = E(static c => new Scripts.PluckyJohnsonAI(c)),
        [Scripts.TriageDoctorAI.DoctorAlliance] = E(static c => new Scripts.TriageDoctorAI(c)),
        [Scripts.TriageDoctorAI.DoctorHorde] = E(static c => new Scripts.TriageDoctorAI(c)),
        [12923] = E(static c => new Scripts.InjuredPatientAI(c)),
        [12924] = E(static c => new Scripts.InjuredPatientAI(c)),
        [12925] = E(static c => new Scripts.InjuredPatientAI(c)),
        [12936] = E(static c => new Scripts.InjuredPatientAI(c)),
        [12937] = E(static c => new Scripts.InjuredPatientAI(c)),
        [12938] = E(static c => new Scripts.InjuredPatientAI(c)),
        [Scripts.ShayLeafrunnerAI.Entry] = E(static c => new Scripts.ShayLeafrunnerAI(c)),
        [Scripts.ThreshwackonatorAI.Entry] = E(static c => new Scripts.ThreshwackonatorAI(c)),
        [Scripts.LazyPeonAI.Entry] = E(static c => new Scripts.LazyPeonAI(c)),
        [Scripts.GalenGoodwardAI.Entry] = E(static c => new Scripts.GalenGoodwardAI(c)),
        [Scripts.MiranAI.Entry] = E(static c => new Scripts.MiranAI(c)),
        [Scripts.DefiasTraitorAI.Entry] = E(static c => new Scripts.DefiasTraitorAI(c)),
        [Scripts.YennikuAI.Entry] = E(static c => new Scripts.YennikuAI(c)),
        [Scripts.KanatiAI.Entry] = E(static c => new Scripts.KanatiAI(c)),
        [Scripts.Oox09AI.Entry] = E(static c => new Scripts.Oox09AI(c)),
        [Scripts.ForemanKlavenMortwakeAI.Entry] = E(static c => new Scripts.ForemanKlavenMortwakeAI(c)),
        [Scripts.ProspectorRemtravelAI.Entry] = E(static c => new Scripts.ProspectorRemtravelAI(c)),
        [Scripts.GallywixAI.Entry] = E(static c => new Scripts.GallywixAI(c)),
        [Scripts.ForemanSilixizAI.Entry] = E(static c => new Scripts.ForemanSilixizAI(c)),
        [Scripts.VentureCoLookoutAI.Entry] = E(static c => new Scripts.VentureCoLookoutAI(c)),
        [Scripts.CalvinMontagueAI.Entry] = E(static c => new Scripts.CalvinMontagueAI(c)),
        [Scripts.TaskmasterFizzuleAI.Entry] = E(static c => new Scripts.TaskmasterFizzuleAI(c)),
        [Scripts.AquementasAI.Entry] = E(static c => new Scripts.AquementasAI(c)),
        [Scripts.DeathstalkerFaerleiaAI.Entry] = E(static c => new Scripts.DeathstalkerFaerleiaAI(c)),
        [Scripts.SpitelashAI.NpcWarrior] = E(static c => new Scripts.SpitelashAI(c)),
        [Scripts.SpitelashAI.NpcScreamer] = E(static c => new Scripts.SpitelashAI(c)),
        [Scripts.SpitelashAI.NpcSerpentGuard] = E(static c => new Scripts.SpitelashAI(c)),
        [Scripts.SpitelashAI.NpcSiren] = E(static c => new Scripts.SpitelashAI(c)),
        [Scripts.SpitelashAI.NpcMyrmidon] = E(static c => new Scripts.SpitelashAI(c)),
        [Scripts.RabidBearAI.Entry] = E(static c => new Scripts.RabidBearAI(c)),
        [Scripts.FeeroIronhandAI.Entry] = E(static c => new Scripts.FeeroIronhandAI(c)),
        [Scripts.MorokkAI.Entry] = E(static c => new Scripts.MorokkAI(c)),
        [Scripts.ScourgeCauldronAI.Entry] = E(static c => new Scripts.ScourgeCauldronAI(c)),
        [Scripts.RingoAI.Entry] = E(static c => new Scripts.RingoAI(c)),
        [Scripts.RegtharDeathgateAI.Entry] = E(static c => new Scripts.RegtharDeathgateAI(c)),
        [Scripts.AgedDyingAncientKodoAI.NpcAgedKodo] = E(static c => new Scripts.AgedDyingAncientKodoAI(c)),
        [Scripts.AgedDyingAncientKodoAI.NpcDyingKodo] = E(static c => new Scripts.AgedDyingAncientKodoAI(c)),
        [Scripts.AgedDyingAncientKodoAI.NpcAncientKodo] = E(static c => new Scripts.AgedDyingAncientKodoAI(c)),
        [Scripts.AgedDyingAncientKodoAI.NpcTamedKodo] = E(static c => new Scripts.AgedDyingAncientKodoAI(c)),
        [Scripts.ToogaAI.Entry] = E(static c => new Scripts.ToogaAI(c)),
        [Scripts.KindalMoonweaverAI.Entry] = E(static c => new Scripts.KindalMoonweaverAI(c)),
        [Scripts.CapturedSpriteDarterAI.Entry] = E(static c => new Scripts.CapturedSpriteDarterAI(c)),
        [Scripts.TwiggyFlatheadAI.Entry] = E(static c => new Scripts.TwiggyFlatheadAI(c)),
        [Scripts.WizzlecranksShredderAI.Entry] = E(static c => new Scripts.WizzlecranksShredderAI(c)),
        [Scripts.AreiAI.Entry] = E(static c => new Scripts.AreiAI(c)),
        [Scripts.OgronAI.Entry] = E(static c => new Scripts.OgronAI(c)),
        [Scripts.CapturedArkonarinAI.Entry] = E(static c => new Scripts.CapturedArkonarinAI(c)),
        [Scripts.KlinfranAI.NpcFranklin] = E(static c => new Scripts.KlinfranAI(c)),
        [Scripts.KlinfranAI.NpcKlinfran] = E(static c => new Scripts.KlinfranAI(c)),
        [Scripts.SolenorAI.NpcNelson] = E(static c => new Scripts.SolenorAI(c)),
        [Scripts.SolenorAI.NpcSolenor] = E(static c => new Scripts.SolenorAI(c)),
        [Scripts.ArtoriusAI.NpcAmiable] = E(static c => new Scripts.ArtoriusAI(c)),
        [Scripts.ArtoriusAI.NpcDoombringer] = E(static c => new Scripts.ArtoriusAI(c)),
        [Scripts.TheCleanerAI.Entry] = E(static c => new Scripts.TheCleanerAI(c)),
        [Scripts.SimoneTheInconspicuousAI.Entry] = E(static c => new Scripts.SimoneTheInconspicuousAI(c)),
        [Scripts.SimoneTheSeductressAI.Entry] = E(static c => new Scripts.SimoneTheSeductressAI(c)),
        [Scripts.PreciousTheDevourerAI.Entry] = E(static c => new Scripts.PreciousTheDevourerAI(c)),
        [Scripts.MawsAI.Entry] = E(static c => new Scripts.MawsAI(c)),
        [Scripts.ColonelKurzenAI.Entry] = E(static c => new Scripts.ColonelKurzenAI(c)),
        [Scripts.FaulkAI.HenzeEntry] = E(static c => new Scripts.FaulkAI(c)),
        [Scripts.FaulkAI.NarmEntry] = E(static c => new Scripts.FaulkAI(c)),
        [Scripts.CorruptedFurbolgAI.NpcBlackwoodWarrior] = E(static c => new Scripts.CorruptedFurbolgAI(c)),
        [Scripts.CorruptedFurbolgAI.NpcBlackwoodTotemic] = E(static c => new Scripts.CorruptedFurbolgAI(c)),
        [Scripts.TheramoreSparControllerAI.NpcMasterSzigeti] = E(static c => new Scripts.TheramoreSparControllerAI(c)),
        [Scripts.CorkGizeltonAI.Entry] = E(static c => new Scripts.CorkGizeltonAI(c)),
        [Scripts.RiggerGizeltonAI.Entry] = E(static c => new Scripts.RiggerGizeltonAI(c)),
        [Scripts.GuardianOfBlizzardAI.Entry] = E(static c => new Scripts.GuardianOfBlizzardAI(c)),
        [Scripts.AnachronosTheAncientAI.Entry] = E(static c => new Scripts.AnachronosTheAncientAI(c)),
        [Instances.Scripts.SunkenTemple.MalfurionStormrageAI.Entry] = E(static c => new Instances.Scripts.SunkenTemple.MalfurionStormrageAI(c)),
        [Instances.Scripts.DireMaul.DreadsteedRitualAI.Entry] = E(static c => new Instances.Scripts.DireMaul.DreadsteedRitualAI(c)),
        [Instances.Scripts.Classic.HurleyBlackbreathAI.Entry] = E(static c => new Instances.Scripts.Classic.HurleyBlackbreathAI(c)),
    };

    /// <summary>A world-wide script AI for one creature entry, with the name of the implementing type for <see cref="ScriptCatalog"/>.</summary>
    private readonly record struct EntryScript(Func<Creature, CreatureAI> Create, string Name);

    private static EntryScript E<T>(Func<Creature, T> factory) where T : CreatureAI => new(c => factory(c), typeof(T).Name);

    /// <summary>The exploration/event quests the entry scripts complete (an escort's quest): <see cref="RegisterEntryScript"/>'s list.</summary>
    private readonly HashSet<uint> _entryScriptQuests = [Scripts.RuulSnowhoofAI.QuestFreedomToRuul, Scripts.AMe01AI.QuestChasingAMe, Scripts.RinjiAI.QuestRinjiTrapped, Scripts.MuglashAI.QuestVorsha,
        Scripts.VolcorAI.QuestEscapeThroughForce, Scripts.VolcorAI.QuestEscapeThroughStealth, Scripts.BartlebyAI.QuestBeat,
        Scripts.DashelStonefistAI.QuestMissingDiploPt8, Scripts.MelizzaBrimbuzzleAI.QuestGetMeOutOfHere,
        Scripts.ErisHavenfireAI.QuestBalanceOfLightAndShadow, Scripts.RanshallaAI.QuestGuardiansAltar,
        Scripts.ReginaldWindsorAI.QuestTheGreatMasquerade,
        Scripts.TaelanFordringAI.QuestInDreams,
        Scripts.KeeperRemulosAI.QuestNightmareManifests,
        Scripts.KerlonianAI.QuestSleeperAwakened,
        Scripts.MistAI.QuestMist,
        Scripts.DaphneStilwellAI.QuestTomeOfValor,
        Scripts.GrarkLorkrubAI.QuestPrecariousPredicament,
        Scripts.TapokeSlimJahnAI.QuestMissingDiplomat,
        Scripts.PrivateHendelAI.QuestMissingDiplomat16,
        Scripts.StinkyIgnatzAI.QuestAlliance,
        Scripts.StinkyIgnatzAI.QuestHorde,
        Scripts.TriageDoctorAI.QuestTriageA,
        Scripts.TriageDoctorAI.QuestTriageH,
        Scripts.ShayLeafrunnerAI.QuestWanderingShay,
        Scripts.GalenGoodwardAI.QuestGalensEscape,
        Scripts.MiranAI.QuestProtectingTheShipment,
        Scripts.DefiasTraitorAI.QuestDefiasBrotherhood,
        Scripts.KanatiAI.QuestProtectKanati,
        Scripts.Oox09AI.QuestRescueOox09,
        Scripts.ProspectorRemtravelAI.QuestAbsentMindedPt2,
        Scripts.CalvinMontagueAI.QuestCalvin,
        Scripts.DeathstalkerFaerleiaAI.QuestPyrewoodAmbush,
        Scripts.FeeroIronhandAI.QuestSuppliesToAuberdine,
        Scripts.MorokkAI.QuestChallengeMorokk,
        Scripts.RingoAI.QuestALittleHelp,
        Scripts.KatranaPrestorGossip.QuestTrueMasters,
        Scripts.LoramusThalipedesGossip.QuestTheDemonHunter,
        Scripts.LoramusThalipedesGossip.QuestLoramus,
        Scripts.AnachronosTheAncientAI.QuestAPawnOnTheEternalBoard,
        Scripts.ToogaAI.QuestTooga,
        Scripts.KindalMoonweaverAI.QuestFreedomForAllCreatures,
        Scripts.WizzlecranksShredderAI.QuestEscape,
        Scripts.AreiAI.QuestAncientSpirit,
        Scripts.OgronAI.QuestQuestioning,
        Scripts.CapturedArkonarinAI.QuestRescueJaedenar,
        Scripts.CorkGizeltonAI.QuestBodyguardForHire,
        Scripts.CorkGizeltonAI.QuestGizeltonCaravan,
        Instances.Scripts.BlackrockDepths.MarshalWindsorAI.QuestJailBreak];

    /// <summary>Registers every <see cref="Scripts.Escorts.EscortSpecCatalog"/> escort as an entry script (a clash with a hand-ported one throws).</summary>
    public CreatureAiFactory()
    {
        foreach (Scripts.Escorts.EscortSpec spec in Scripts.Escorts.EscortSpecCatalog.All)
        {
            RegisterEntryScript(spec.Entry, E(c => new Scripts.Escorts.DataDrivenEscortAI(c, spec)), spec.QuestId);
        }

        // Blackrock Depths: the gossip-only prisoners of the Jail Break (Marshal Windsor's AI is registered by the instance script).
        RegisterNpcScript(Instances.Scripts.BlackrockDepths.DughalStormwingGossip.ScriptName, nameof(Instances.Scripts.BlackrockDepths.DughalStormwingGossip),
            gossip: new Instances.Scripts.BlackrockDepths.DughalStormwingGossip());
        RegisterNpcScript(Instances.Scripts.BlackrockDepths.TobiasSeecherGossip.ScriptName, nameof(Instances.Scripts.BlackrockDepths.TobiasSeecherGossip),
            gossip: new Instances.Scripts.BlackrockDepths.TobiasSeecherGossip());
        RegisterNpcScript(Scripts.LoramusThalipedesGossip.ScriptName, nameof(Scripts.LoramusThalipedesGossip),
            gossip: new Scripts.LoramusThalipedesGossip());
        // Dungeon and raid ScriptNames that bind by name (an EventAI host, a gossip/quest script, an inert trigger AI).
        Instances.Scripts.Uldaman.ArchaerasAddAI.Register(this);
        Instances.Scripts.Naxxramas.GothikAnchorAI.Register(this);
        Instances.Scripts.Naxxramas.InvisibleManAI.Register(this);
        Instances.Scripts.Stratholme.AuriusScript.Register(this);
    }

    public IReadOnlyCollection<string> Names => _factories.Keys;

    /// <summary>Whether <paramref name="name"/> is a registered named AI (a ScriptName or AIName <see cref="Create(Creature, CreatureContent, out bool)"/> would pick).</summary>
    internal bool IsNamedAi(string name) => !string.IsNullOrEmpty(name) && _factories.ContainsKey(name);

    /// <summary>The implementing type name of the world-wide entry script of <paramref name="entry"/>, or null when it has none.</summary>
    internal string? EntryScriptName(uint entry) => _entryScripts.TryGetValue(entry, out EntryScript script) ? script.Name : null;

    /// <summary>
    /// Whether the host can run the script a template names through the WORLD-WIDE registries only: a registered named AI for
    /// <paramref name="scriptName"/>, or an entry script for <paramref name="entry"/>. It is blind to per-map scripts (the map's
    /// <c>RegisterEntryAi</c> and <c>DungeonBossAis</c>), so it must not be used to call a script missing: ask <see cref="ScriptCatalog"/>
    /// with the map. Read-only; the registries are filled at start-up and read afterwards.
    /// </summary>
    public bool HasScript(uint entry, string scriptName)
        => (!string.IsNullOrEmpty(scriptName) && _factories.ContainsKey(scriptName)) || _entryScripts.ContainsKey(entry);

    /// <summary>The exploration/event quests the entry scripts complete (what lets the quest service offer them).</summary>
    public IReadOnlyCollection<uint> ScriptedEventQuests => _entryScriptQuests;

    /// <summary>
    /// Give every creature of <paramref name="entry"/> the script AI <paramref name="factory"/> builds (a duplicate throws: fail closed);
    /// <paramref name="eventQuests"/> are the exploration/event quests it completes.
    /// </summary>
    public void RegisterEntryScript(uint entry, Func<Creature, CreatureAI> factory, params uint[] eventQuests)
    {
        ArgumentNullException.ThrowIfNull(factory);
        RegisterEntryScript(entry, new EntryScript(factory, "(registered at start-up)"), eventQuests);
    }

    private void RegisterEntryScript(uint entry, EntryScript script, params uint[] eventQuests)
    {
        ArgumentNullException.ThrowIfNull(eventQuests);
        if (!_entryScripts.TryAdd(entry, script))
        {
            throw new InvalidOperationException($"creature entry {entry} already has a script AI");
        }

        _entryScriptQuests.UnionWith(eventQuests);
    }

    /// <summary>
    /// Register a ScriptName AI that hosts the EventAI rows of its creature: <paramref name="factory"/> builds a type derived from
    /// <see cref="CreatureEventAI"/> from the creature and the loaded AI content, so the entry's <c>creature_ai_scripts</c> rows keep firing under the
    /// ScriptName (the ScriptName otherwise replaces EventAI, see <see cref="Create(Creature, CreatureContent, out bool, bool)"/>). A duplicate throws.
    /// </summary>
    public void RegisterEventAiScript(string name, Func<Creature, CreatureAiContent, CreatureEventAI> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        if (_npcScripts.ContainsKey(name) || !_factories.TryAdd(name, (c, content) =>
        {
            CreatureEventAI ai = factory(c, content.Ai);
            // The plain EventAI entry applies the guard rules of a GUARD-flagged template (vmangos GuardEventAI::Permissible); a hosted script keeps them.
            ai.UsesGuardSightRules |= (c.Template.Behaviour & CreatureBehaviourFlags.Guard) != 0;
            return ai;
        }))
        {
            throw new InvalidOperationException($"script '{name}' is already registered");
        }
    }

    private readonly Dictionary<string, NpcScript> _npcScripts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Register a gossip and/or quest script that has no AI of its own, bound by the template's ScriptName (ScriptDev2 <c>pGossipHello</c>,
    /// <c>pQuestAcceptNPC</c>, <c>pQuestRewardedNPC</c>): <paramref name="gossip"/> answers the gossip hello and selection of every creature
    /// with that ScriptName (<see cref="QuestNpcServices.GossipByScriptName"/>, bound by <see cref="CreatureQuestScripts.Attach"/>) and
    /// <paramref name="quest"/> is invoked when a quest is taken from or rewarded by such a creature. <see cref="ScriptCatalog"/> counts the
    /// ScriptName as implemented only because a handler is registered here. The creature still runs the AI of its AIName. At least one
    /// handler is required; a duplicate, or an AI already registered under the name, throws (fail closed).
    /// </summary>
    public void RegisterNpcScript(string scriptName, string implementation, INpcGossipScript? gossip = null, INpcQuestScript? quest = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptName);
        ArgumentException.ThrowIfNullOrWhiteSpace(implementation);
        if (gossip is null && quest is null)
        {
            throw new ArgumentException("a gossip or quest handler is required", nameof(gossip));
        }

        if (_factories.ContainsKey(scriptName) || !_npcScripts.TryAdd(scriptName, new NpcScript(implementation, gossip, quest)))
        {
            throw new InvalidOperationException($"script '{scriptName}' is already registered");
        }
    }

    /// <summary>The gossip/quest-only script registered under <paramref name="scriptName"/>, or null.</summary>
    internal (ScriptKind Kind, string Implementation)? NpcScriptOf(string scriptName)
        => _npcScripts.TryGetValue(scriptName, out NpcScript? script)
            ? (script.Gossip is not null ? ScriptKind.GossipScript : ScriptKind.QuestScript, script.Implementation)
            : null;

    /// <summary>The gossip handler registered for <paramref name="scriptName"/> (a creature's template ScriptName), or null.</summary>
    internal INpcGossipScript? NpcGossipOf(string scriptName)
        => _npcScripts.TryGetValue(scriptName, out NpcScript? script) ? script.Gossip : null;

    /// <summary>The quest handler registered for <paramref name="scriptName"/>, or null.</summary>
    internal INpcQuestScript? NpcQuestOf(string scriptName)
        => _npcScripts.TryGetValue(scriptName, out NpcScript? script) ? script.Quest : null;

    private sealed record NpcScript(string Implementation, INpcGossipScript? Gossip, INpcQuestScript? Quest);

    /// <summary>Register a C# AI under <paramref name="name"/> (a duplicate throws: fail closed).</summary>
    public void Register(string name, Func<Creature, CreatureAI> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        if (_npcScripts.ContainsKey(name) || !_factories.TryAdd(name, (c, _) => factory(c)))
        {
            throw new InvalidOperationException($"script '{name}' is already registered");
        }
    }

    /// <summary>
    /// Whether <see cref="CommitSwap"/> would work, checked without changing anything (hot module lane): every entry and name to remove is
    /// registered, and every one to add is free once the removals are done. Null when it would; else why not.
    /// </summary>
    public string? ValidateSwap(IReadOnlyCollection<uint> removeEntries, IReadOnlyCollection<string> removeNames, IEnumerable<uint> addEntries, IEnumerable<string> addNames)
    {
        ArgumentNullException.ThrowIfNull(removeEntries);
        ArgumentNullException.ThrowIfNull(removeNames);
        foreach (uint entry in removeEntries.Where(e => !_entryScripts.ContainsKey(e)))
        {
            return $"creature entry {entry} has no script AI to remove";
        }

        foreach (string name in removeNames.Where(n => !_factories.ContainsKey(n)))
        {
            return $"creature AI '{name}' is not registered, so it cannot be removed";
        }

        foreach (uint entry in addEntries.Where(e => _entryScripts.ContainsKey(e) && !removeEntries.Contains(e)))
        {
            return $"creature entry {entry} already has a script AI";
        }

        foreach (string name in addNames.Where(n => _factories.ContainsKey(n) && !removeNames.Contains(n, StringComparer.OrdinalIgnoreCase)))
        {
            return $"creature AI '{name}' is already registered";
        }

        return null;
    }

    /// <summary>
    /// Remove the entry scripts and named AIs in <paramref name="removeEntries"/> / <paramref name="removeNames"/> and add the given ones in one step
    /// (the hot module lane; call at a tick boundary on the world thread, after <see cref="ValidateSwap"/>). Creatures already running a removed AI keep
    /// it until they are given a new one; this only changes what the next selection picks.
    /// </summary>
    public void CommitSwap(
        IReadOnlyCollection<uint> removeEntries,
        IReadOnlyCollection<string> removeNames,
        IReadOnlyDictionary<uint, Func<Creature, CreatureAI>> addEntries,
        IReadOnlyDictionary<string, Func<Creature, CreatureAI>> addNames)
    {
        foreach (uint entry in removeEntries)
        {
            _entryScripts.Remove(entry);
        }

        foreach (string name in removeNames)
        {
            _factories.Remove(name);
        }

        foreach ((uint entry, Func<Creature, CreatureAI> factory) in addEntries)
        {
            _entryScripts[entry] = new EntryScript(factory, "(hot-loaded)");
        }

        foreach ((string name, Func<Creature, CreatureAI> factory) in addNames)
        {
            _factories[name] = (c, _) => factory(c);
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
            && _entryScripts.TryGetValue(creature.Template.Entry, out EntryScript script))
        {
            return script.Create(creature);
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

        if (string.IsNullOrEmpty(name) && creature.Summon is null
            && ((creature.NpcFlags | creature.Template.NpcFlags) & SpiritHealerNpcFlags) != 0)
        {
            return new NullCreatureAI(creature);
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
