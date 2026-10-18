using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// The warlock Dreadsteed ritual in Dire Maul West ("The Dreadsteed of Xoroth", 7631): mangos-classic
/// AI/ScriptDevAI/scripts/kalimdor/dire_maul/instance_dire_maul.cpp (TYPE_DREADSTEED in Update, SetData, OnObjectCreate,
/// OnCreatureDeath and ProcessDreadsteedRitualStart) and dire_maul.cpp (event_spells_warlock_dreadsteed, npc_warlock_mount_ritual and
/// go_dreadsteed_quest_objects). classic-db z2815 carries the matching DB side: J'eevee's path 1 and its waypoint scripts 1450004-1450008
/// (the bell, wheel and candle), the nine demon waves (relays 1450101-1450109) and event 8428's Dreadsteed summon.
/// <list type="number">
/// <item>Event 8420 from a player while the ritual is NOT_STARTED or FAILED: the player summons the ritual controller (14501) and J'eevee
/// (14500) on her path 1, TYPE_DREADSTEED becomes IN_PROGRESS and the event's DB script runs. Otherwise nothing.</item>
/// <item>While IN_PROGRESS the ritual fails when the instance has no player but GMs while J'eevee is there, and once she is gone when the bell,
/// the wheel and the candle are all deactivated.</item>
/// <item>FAIL despawns the controller, the portals, the symbols, the three objects and the circle.</item>
/// <item><see cref="DreadsteedRitualAI"/> runs the waves and ends with SPECIAL; event 8428 from a player while SPECIAL sets DONE and its DB
/// script summons the Xorothian Dreadsteed. Lord Hel'nurath's death also sets DONE.</item>
/// </list>
/// </summary>
public sealed partial class DireMaulInstance
{
    public const uint TypeDreadsteed = 18;
    public const uint NpcJeevee = 14500, NpcWarlockDummyInfernal = 14501, NpcLordHelnurath = 14506;
    public const uint GoBellOfDethmoora = 179674, GoDoomsdayCandle = 179673, GoWheelOfBlackMarch = 179672, GoDreadsteedPortal = 179681,
        GoWarlockRitualCircle = 179668, GoMountQuestSymbol1 = 179669, GoMountQuestSymbol2 = 179670, GoMountQuestSymbol3 = 179671,
        GoRitualCandleAura = 179688;
    public const uint SpellBlackMarchBlessing = 23120, SpellRitualCandleAura = 23226, SpellRitualBellAura = 23117;

    /// <summary>ProcessEventId_event_spells_warlock_dreadsteed: where the player summons the controller and J'eevee.</summary>
    public static readonly (float X, float Y, float Z, float O) RitualSummon = (-37.9392f, 812.805f, -29.4525f, 4.81711f);

    private readonly List<ObjectGuid> _dreadsteedPortals = [];
    private readonly List<ObjectGuid> _ritualSymbols = [];
    private DreadsteedQuestObjectAI? _questObjectAi;
    private ObjectGuid _ritualStarter;

    /// <summary>The ritual symbols in creation order (<c>GetRitualSymbolGuids</c>); wave N shows symbol N-1.</summary>
    internal IReadOnlyList<ObjectGuid> RitualSymbols => _ritualSymbols;

    /// <summary>The player who started the ritual: J'eevee's spawner, who gets the objects' help when she is the one using them.</summary>
    internal ObjectGuid RitualStarter => _ritualStarter;

    private void RegisterDreadsteedObjectAi(uint entry)
        => Instance.FindUpdater<GameObjectMapSystem>()?.RegisterAi(entry, _questObjectAi ??= new DreadsteedQuestObjectAI(this));

    /// <summary>ProcessEventId_event_spells_warlock_dreadsteed (dire_maul.cpp:43-80): true stops the event's DB script.</summary>
    public override bool OnSpellEvent(Unit caster, uint eventId, WorldObject? target)
    {
        if (eventId is not (ScriptedEvents.SummonJeevee or ScriptedEvents.SummonDreadsteed))
        {
            return base.OnSpellEvent(caster, eventId, target);
        }

        if (caster is not Player player)
        {
            return true;
        }

        uint state = Encounters[TypeDreadsteed];
        if (eventId == ScriptedEvents.SummonJeevee)
        {
            if (state is not (EncounterState.NotStarted or EncounterState.Fail)
                || Instance.FindUpdater<CreatureMapSystem>() is not { } creatures)
            {
                return true;
            }

            _ritualStarter = player.Guid;
            creatures.SummonDeadDespawnAt(NpcWarlockDummyInfernal, RitualSummon.X, RitualSummon.Y, RitualSummon.Z, RitualSummon.O);
            // J'eevee starts on path 1 here; path 0 is the Scholomance event.
            if (creatures.SummonDeadDespawnAt(NpcJeevee, RitualSummon.X, RitualSummon.Y, RitualSummon.Z, RitualSummon.O) is { } imp)
            {
                creatures.ChangeMovement(imp, 2, 1, 0);
            }

            SetData(TypeDreadsteed, EncounterState.InProgress);
            return false; // the rest is the DB script
        }

        if (state != EncounterState.Special)
        {
            return true;
        }

        SetData(TypeDreadsteed, EncounterState.Done);
        return false; // the DB script summons the Dreadsteed
    }

    private bool SetDreadsteedData(uint type, uint data)
    {
        if (type != TypeDreadsteed)
        {
            return false;
        }

        Encounters[type] = data;
        if (data == EncounterState.Fail)
        {
            CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
            GameObjectMapSystem? objects = Instance.FindUpdater<GameObjectMapSystem>();
            if (GetSingleCreatureFromStorage(NpcWarlockDummyInfernal) is { IsAlive: true } ritual)
            {
                creatures?.ForcedDespawn(ritual, 0);
            }

            if (objects is not null)
            {
                foreach (ObjectGuid guid in _dreadsteedPortals.Concat(_ritualSymbols))
                {
                    DespawnIfSpawned(objects, objects.Find(guid));
                }

                foreach (uint entry in new[] { GoBellOfDethmoora, GoDoomsdayCandle, GoWheelOfBlackMarch, GoWarlockRitualCircle })
                {
                    DespawnIfSpawned(objects, GetSingleGameObjectFromStorage(entry));
                }
            }
        }

        SaveIfDone(data);
        return true;
    }

    private static void DespawnIfSpawned(GameObjectMapSystem objects, GameObject? go)
    {
        if (go is { IsSpawned: true })
        {
            objects.Despawn(go);
        }
    }

    /// <summary>instance_dire_maul::Update: the failure checks while the ritual is IN_PROGRESS.</summary>
    public override void Update(uint diffMs)
    {
        base.Update(diffMs);
        if (Encounters[TypeDreadsteed] != EncounterState.InProgress)
        {
            return;
        }

        if (GetSingleCreatureFromStorage(NpcJeevee) is not null)
        {
            // While J'eevee is there, the ritual fails only when no player but GMs is left.
            if (!Instance.Players.Any(p => !p.IsGameMaster))
            {
                SetData(TypeDreadsteed, EncounterState.Fail);
            }

            return;
        }

        if (GetSingleGameObjectFromStorage(GoBellOfDethmoora) is { } bell && bell.LootState != GameObjectLootState.Activated
            && GetSingleGameObjectFromStorage(GoWheelOfBlackMarch) is { } wheel && wheel.LootState != GameObjectLootState.Activated
            && GetSingleGameObjectFromStorage(GoDoomsdayCandle) is { } candle && candle.LootState != GameObjectLootState.Activated)
        {
            SetData(TypeDreadsteed, EncounterState.Fail);
        }
    }

    /// <summary>ProcessDreadsteedRitualStart: the circle for 900 s and the side portals for 380 s.</summary>
    internal void ProcessDreadsteedRitualStart()
    {
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } creatures || Instance.FindUpdater<GameObjectMapSystem>() is not { } objects)
        {
            return;
        }

        if (GetSingleGameObjectFromStorage(GoWarlockRitualCircle) is { } circle)
        {
            creatures.RespawnGameObjectFor(circle, 900);
        }

        foreach (ObjectGuid guid in _dreadsteedPortals)
        {
            if (objects.Find(guid) is { } portal)
            {
                creatures.RespawnGameObjectFor(portal, 380);
            }
        }
    }

    /// <summary>The bell, the wheel and the candle (<see cref="GoBellOfDethmoora"/> and the others), or null.</summary>
    internal GameObject? RitualObject(uint entry) => GetSingleGameObjectFromStorage(entry);

    /// <summary>The Warlock Mount Ritual Circle, or null.</summary>
    internal GameObject? RitualCircle => GetSingleGameObjectFromStorage(GoWarlockRitualCircle);

    /// <summary>The invisible candle trap that casts the candle's aura (it is the true caster of 23226), or null.</summary>
    internal GameObject? RitualCandleAuraCaster => GetSingleGameObjectFromStorage(GoRitualCandleAura);

    /// <summary>
    /// dire_maul.cpp DreadsteedQuestObjects for the bell, the wheel and the candle. One that spawns while the ritual is not IN_PROGRESS goes
    /// again at once. Once used, it casts its aura on its last user every 6 s (the candle's through the candle aura trap): a player, or for
    /// J'eevee the player who summoned her. The reference keeps pulsing after the controller deactivates the object; here the pulse stops
    /// while the object is not activated, so a deactivated object stops helping until a player uses it again.
    /// </summary>
    private sealed class DreadsteedQuestObjectAI(DireMaulInstance instance) : IGameObjectAi
    {
        public const uint PulseMs = 6000;
        private readonly Dictionary<ObjectGuid, (ObjectGuid User, long NextMs)> _pulses = [];

        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

        public void OnRespawn(GameObjectMapSystem objects, GameObject go)
        {
            _pulses.Remove(go.Guid);
            if (instance.GetData(TypeDreadsteed) != EncounterState.InProgress)
            {
                objects.Despawn(go);
            }
        }

        public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
        {
            ObjectGuid target = user is Player ? user.Guid : instance.RitualStarter;
            _pulses[go.Guid] = (target, objects.ClockMs); // m_uiPulseTimer = 1
            return false; // the button still activates
        }

        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
        {
            if (!_pulses.TryGetValue(go.Guid, out (ObjectGuid User, long NextMs) pulse))
            {
                return;
            }

            if (!go.IsSpawned || go.LootState == GameObjectLootState.JustDeactivated)
            {
                _pulses.Remove(go.Guid); // OnLootStateChange(GO_JUST_DEACTIVATED)
                return;
            }

            if (objects.ClockMs < pulse.NextMs || go.LootState != GameObjectLootState.Activated)
            {
                return;
            }

            _pulses[go.Guid] = (pulse.User, objects.ClockMs + PulseMs);
            uint spellId = go.Entry switch
            {
                GoBellOfDethmoora => SpellRitualBellAura,
                GoDoomsdayCandle => SpellRitualCandleAura,
                GoWheelOfBlackMarch => SpellBlackMarchBlessing,
                _ => 0,
            };

            // The reference casts only while the candle aura trap exists, for all three.
            if (spellId == 0 || instance.RitualCandleAuraCaster is not { } candleAura || objects.Map.FindObject(pulse.User) is not Unit target)
            {
                return;
            }

            objects.Spells?.Cast(spellId == SpellRitualCandleAura ? candleAura : go, spellId, target, unitCaster: null);
        }
    }
}
