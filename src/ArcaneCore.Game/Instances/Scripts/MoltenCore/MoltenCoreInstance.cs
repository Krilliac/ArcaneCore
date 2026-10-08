using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.MoltenCore;

/// <summary>mangos-classic molten_core.cpp instance_molten_core::{SetData,OnObjectCreate,DoSpawnMajordomoIfCan,SpawnMajordomo,Load}.
/// The encounter IDs deliberately use ClassicDB/mangos-classic ordering, not vmangos' different ordering.</summary>
[InstanceScript(409)]
public sealed class MoltenCoreInstance(Map map) : ScriptedInstance(map, 10)
{
    public const uint Majordomo = 8, Ragnaros = 9;
    public static IReadOnlyList<uint> BossEntries { get; } = Array.AsReadOnly<uint>([12118, 11982, 12259, 12057, 12264, 12056, 11988, 12098, 12018, 11502]);
    public static IReadOnlyList<uint> RuneEntries { get; } = Array.AsReadOnly<uint>([176956, 176957, 176955, 176953, 176952, 176954, 176951]);
    private static readonly uint[] Circles = [178192, 178193, 178191, 178189, 178188, 178190, 178187];
    private CreatureMapSystem? _registered;
    private bool _initialSummon;
    private sealed class Anxiety(SpellSystem system, SpellAuraHolder holder)
    {
        public SpellSystem System { get; } = system;
        public SpellAuraHolder Holder { get; } = holder;
        public uint Timer = 5000;
    }
    private readonly List<Anxiety> _anxieties = [];
    internal void TrackAnxiety(SpellSystem system, SpellAuraHolder holder) => _anxieties.Add(new(system, holder));
    private void UpdateAnxiety(uint diffMs)
    {
        foreach (Anxiety entry in _anxieties.ToArray())
        {
            SpellAuraHolder holder = entry.Holder;
            if (holder.IsRemoved || !ReferenceEquals(holder.Target.Map, Instance)) { _anxieties.Remove(entry); continue; }
            if (entry.Timer > diffMs) { entry.Timer -= diffMs; continue; }
            entry.Timer = 5000;
            if (Instance.FindObject(holder.CasterGuid) is not Unit { IsAlive: true } caster) continue;
            Unit target = holder.Target;
            float radius = holder.Spell.Effects[0].Radius + caster.BoundingRadius + target.BoundingRadius;
            float dx = caster.X - target.X, dy = caster.Y - target.Y, dz = caster.Z - target.Z;
            if (target.IsAlive && dx * dx + dy * dy + dz * dz > radius * radius)
                entry.System.CastSpell(target, holder.Spell.Id == 21094 ? 21095u : 23492u, SpellCastTargets.ForSelf(), triggered: true);
        }
    }
    public override uint GetData(uint type) => type < 10 ? Encounters[type] : 0;
    public override bool IsEncounterInProgress => Encounters.Contains(EncounterState.InProgress);
    public bool RunesDoused => Enumerable.Range(1, 7).All(i => Encounters[i] == EncounterState.Special);

    public override void SetData(uint type, uint data)
    {
        if (type >= 10) return;
        Encounters[type] = data;
        if (type is >= 1 and <= 7 && data is EncounterState.Done or EncounterState.Special)
        {
            if (GetSingleGameObjectFromStorage(Circles[type - 1]) is { } circle) circle.LootState = GameObjectLootState.JustDeactivated;
            if (data == EncounterState.Special && GetSingleGameObjectFromStorage(RuneEntries[(int)type - 1]) is { } rune) rune.State = GameObjectState.Active;
        }
        if (type == Majordomo && data == EncounterState.Done && GetSingleGameObjectFromStorage(179703) is { } chest)
            Instance.FindUpdater<GameObjectMapSystem>()?.ForceRespawn(chest);
        if (data is EncounterState.Done or EncounterState.Special) SaveToDB();
        if (data == EncounterState.Special) _initialSummon = true;
    }

    /// <summary>molten_coreScripts.cpp GOUse_go_molten_core_rune: only the dead boss's rune can be extinguished.</summary>
    public bool DouseRune(uint entry)
    {
        int index = RuneEntries.ToList().IndexOf(entry);
        if (index < 0 || GetData((uint)index + 1) != EncounterState.Done) return false;
        SetData((uint)index + 1, EncounterState.Special);
        return true;
    }

    public override void OnObjectCreate(GameObject go)
    {
        StoreGameObject(go);
        int rune = RuneEntries.ToList().IndexOf(go.Entry);
        if (rune >= 0)
        {
            if (Encounters[rune + 1] == EncounterState.Special) go.State = GameObjectState.Active;
            Instance.FindUpdater<GameObjectMapSystem>()?.RegisterAi(go.Entry, new RuneAI(this));
        }
        int circle = Array.IndexOf(Circles, go.Entry);
        if (circle >= 0 && Encounters[circle + 1] is EncounterState.Done or EncounterState.Special)
            go.LootState = GameObjectLootState.JustDeactivated;
    }
    public override void OnCreatureCreate(Creature creature)
    {
        StoreCreature(creature);
        if (creature.System is { } system) Register(system);
        uint? boss = creature.Entry switch { 11661 => 2u, 12099 => 3u, 11672 => 6u, 11662 => 7u, _ => (uint?)null };
        if (boss is { } type && GetData(type) is EncounterState.Done or EncounterState.Special)
            creature.System?.ForcedDespawn(creature, 1); // ClassicDB FLAG_CANT_SPAWN_IF_BOSS_DEAD
    }
    internal void SetLavaPresentation(bool visible)
    {
        foreach (uint entry in new uint[] { 178107, 178108 })
            if (GetSingleGameObjectFromStorage(entry) is { } go)
            {
                if (visible) Instance.FindUpdater<GameObjectMapSystem>()?.ForceRespawn(go);
                else go.LootState = GameObjectLootState.JustDeactivated;
            }
    }
    private void Register(CreatureMapSystem system)
    {
        if (ReferenceEquals(_registered, system)) return;
        _registered = system;
        for (uint i = 0; i < 8; i++)
        {
            uint type = i;
            system.RegisterEntryAi(BossEntries[(int)i], c => new MoltenCoreBossAI(c, this, type));
        }
        foreach (uint entry in new uint[] { 12099, 11662, 11672 })
            system.RegisterEntryAi(entry, c => new MoltenCoreBossAI(c, this, null));
        system.RegisterEntryAi(12018, c => new MajordomoAI(c, this));
        system.RegisterEntryAi(11502, c => new RagnarosAI(c, this));
        // Remaining guards/healers/elites retain ClassicDB's combat EventAI.
    }
    public override void Update(uint diffMs)
    {
        UpdateAnxiety(diffMs);
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } system) return;
        Register(system);
        if (!RunesDoused || GetData(Ragnaros) == EncounterState.Done || GetSingleCreatureFromStorage(12018) is not null
            || system.Creatures.Any(c => c.Entry == 11502)
            || !Instance.Players.Any()) return;
        if (system.Content.FindTemplate(12018) is not { } template) return; // no invented template
        bool defeated = GetData(Majordomo) == EncounterState.Done;
        Creature domo = defeated
            ? system.SpawnTemporary(template, 848.933f, -812.875f, -229.601f, 4.046f)
            : system.SpawnTemporary(template, 758.0892f, -1176.712f, -118.6403f, 3.124139f);
        if (defeated) ((MajordomoAI)domo.AI!).MakeFriendly();
        else
        {
            if (_initialSummon) system.SayText(domo, 7566); // vmangos SAY_RUNES_DESTROYED
            SpawnGuards(domo);
        }
        _initialSummon = false;
    }
    internal void SpawnGuards(Creature domo)
    {
        // mangos-classic molten_core.cpp m_aBosspawnLocs, SpawnMajordomo.
        (uint Entry, float X, float Y, float Z, float O)[] positions =
        [
            (11664,737.945f,-1156.48f,-118.945f,4.46804f),(11664,752.520f,-1191.02f,-118.218f,2.49582f),
            (11664,752.953f,-1163.94f,-118.869f,3.70010f),(11664,738.814f,-1197.40f,-118.018f,1.83260f),
            (11663,746.939f,-1194.87f,-118.016f,2.21657f),(11663,747.132f,-1158.87f,-118.897f,4.03171f),
            (11663,757.116f,-1170.12f,-118.793f,3.40339f),(11663,755.910f,-1184.46f,-118.449f,2.80998f)
        ];
        foreach (var p in positions) domo.System?.SummonCorpseDespawn(domo, p.Entry, p.X, p.Y, p.Z, p.O);
    }
    private sealed class RuneAI(MoltenCoreInstance raid) : IGameObjectAi
    {
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;
        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs) { }
        public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user) => true;
        public bool OnUnlockedUse(GameObjectMapSystem objects, GameObject go, Player user)
        {
            int index = RuneEntries.ToList().IndexOf(go.Entry);
            if (index >= 0 && raid.GetData((uint)index + 1) == EncounterState.Done
                && objects.ToggleDoorOrButton(go) == GameObjectUseResult.Ok)
                raid.DouseRune(go.Entry);
            return true;
        }
    }
}
