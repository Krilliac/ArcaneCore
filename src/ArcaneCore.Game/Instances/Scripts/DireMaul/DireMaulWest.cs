using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// Dire Maul East's Zevrim/Ironbark states and West's Warpwood, Immol'thar, Prince and pylon states (mangos-classic
/// AI/ScriptDevAI/scripts/kalimdor/dire_maul/instance_dire_maul.cpp: OnCreatureCreate, OnObjectCreate, SetData TYPE_ZEVRIM..TYPE_PYLON_5,
/// OnCreatureEnterCombat, OnCreatureDeath, CheckAllGeneratorsDestroyed, ProcessForceFieldOpening, PylonGuardJustDied; dire_maul.h:8-70).
/// <list type="number">
/// <item>Zevrim's death sets TYPE_ZEVRIM DONE: Old Ironbark yells and becomes Ironbark the Redeemed; the Redeemed's death sets TYPE_IRONBARK.</item>
/// <item>Tendris' death sets TYPE_WARPWOOD DONE and uses the Warpwood door.</item>
/// <item>The Arcane Aberrations and Mana Remnants (the generator guards) belong to the unfinished crystal generator within 20 yards of their spawn;
/// when the last of them dies the pylon is DONE and its generator is used. The guards are sorted into fixed per-generator sets at
/// their first fight (SortPylonGuards, by spawn home); the pylon is DONE when its set empties, wherever the last guard dies.</item>
/// <item>With all five pylons DONE the force field is used and the Highborne summoners attack a living Immol'thar.</item>
/// <item>Immol'thar's death sets TYPE_IMMOLTHAR DONE: the Prince yells and turns hostile (until he respawns) and the chest aura goes. A Prince
/// created while Immol'thar is DONE is hostile too.</item>
/// </list>
/// Not ported: the reference's TEMPFACTION_TOGGLE_IMMUNE_TO_PLAYER flag (temporary faction toggles change no unit flags here yet).
/// </summary>
public sealed partial class DireMaulInstance
{
    public const uint TypeZevrim = 1, TypeIronbark = 2, TypeWarpwood = 3, TypeImmolthar = 4, TypePrince = 5, TypePylon1 = 6, PylonCount = 5;
    public const uint NpcZevrim = 11490, NpcOldIronbark = 11491, NpcIronbarkRedeemed = 14241, NpcTendris = 11489, NpcPrinceTortheldrin = 11486,
        NpcImmolthar = 11496, NpcArcaneAberration = 11480, NpcManaRemnant = 11483, NpcHighborneSummoner = 11466;
    public const uint GoConservatoryDoor = 176907, GoPrincesChestAura = 179563, GoForcefield = 179503, GoWarpwoodDoor = 177221;
    public const int SayFreeImmolthar = -1429000, SayKillImmolthar = -1429001, SayIronbarkRedeem = -1429002;
    private const float PylonGuardRadius = 20f;

    /// <summary>GO_CRYSTAL_GENERATOR_1..5 in pylon order.</summary>
    public static readonly uint[] CrystalGenerators = [177259, 177257, 177258, 179504, 179505];

    private readonly List<ObjectGuid> _generatorGuards = [];
    private readonly List<ObjectGuid>[] _pylonGuardSets = [[], [], [], [], []];
    private readonly List<ObjectGuid> _highborneSummoners = [];

    /// <summary>CheckAllGeneratorsDestroyed.</summary>
    public bool AllGeneratorsDestroyed
    {
        get
        {
            for (uint i = 0; i < PylonCount; i++)
            {
                if (Encounters[TypePylon1 + i] != EncounterState.Done)
                {
                    return false;
                }
            }

            return true;
        }
    }

    private static bool IsWestOrEastType(uint type) => type is >= TypeZevrim and < TypePylon1 + PylonCount;

    private void CreateWestCreature(Creature creature)
    {
        switch (creature.Template.Entry)
        {
            case NpcPrinceTortheldrin:
                if (Encounters[TypeImmolthar] == EncounterState.Done)
                {
                    creature.SetFactionTemporary(FactionHostile, TemporaryFactionFlags.RestoreRespawn);
                }

                StoreCreature(creature);
                break;
            case NpcArcaneAberration:
            case NpcManaRemnant:
                if (!_generatorGuards.Contains(creature.Guid))
                {
                    _generatorGuards.Add(creature.Guid);
                }

                break;
            case NpcHighborneSummoner:
                if (!_highborneSummoners.Contains(creature.Guid))
                {
                    _highborneSummoners.Add(creature.Guid);
                }

                break;
            case NpcOldIronbark:
            case NpcImmolthar:
                StoreCreature(creature);
                break;
        }
    }

    /// <summary>The OnObjectCreate cases of the generators, the force field, the Warpwood and conservatory doors and the chest aura; false for any other entry.</summary>
    private bool CreateWestObject(GameObject go)
    {
        int generator = Array.IndexOf(CrystalGenerators, go.Entry);
        if (generator >= 0)
        {
            OpenIf(go, Encounters[TypePylon1 + generator] == EncounterState.Done);
            StoreGameObject(go);
            return true;
        }

        switch (go.Entry)
        {
            case GoForcefield:
                OpenIf(go, AllGeneratorsDestroyed);
                break;
            case GoWarpwoodDoor:
                OpenIf(go, Encounters[TypeWarpwood] == EncounterState.Done);
                break;
            case GoConservatoryDoor:
                OpenIf(go, Encounters[TypeIronbark] == EncounterState.Done);
                break;
            case GoPrincesChestAura:
                break;
            default:
                return false;
        }

        StoreGameObject(go);
        return true;
    }

    private bool SetWestData(uint type, uint data)
    {
        if (!IsWestOrEastType(type))
        {
            return false;
        }

        bool repeatDone = data == EncounterState.Done && Encounters[type] == EncounterState.Done;
        Encounters[type] = data;
        if (data == EncounterState.Done && !repeatDone)
        {
            switch (type)
            {
                case TypeZevrim:
                    if (GetSingleCreatureFromStorage(NpcOldIronbark) is { } ironbark)
                    {
                        ironbark.System?.SayText(ironbark, SayIronbarkRedeem);
                        ironbark.System?.UpdateEntry(ironbark, NpcIronbarkRedeemed);
                    }

                    break;
                case TypeWarpwood:
                    DoUseDoorOrButton(GoWarpwoodDoor);
                    break;
                case TypeImmolthar:
                    if (GetSingleCreatureFromStorage(NpcPrinceTortheldrin) is { } prince)
                    {
                        prince.System?.SayText(prince, SayFreeImmolthar);
                        prince.SetFactionTemporary(FactionHostile, TemporaryFactionFlags.RestoreRespawn);
                        if (GetSingleGameObjectFromStorage(GoPrincesChestAura) is { } aura
                            && Instance.FindUpdater<GameObjectMapSystem>() is { } objects)
                        {
                            objects.Despawn(aura);
                        }
                    }

                    break;
                case >= TypePylon1:
                    DoUseDoorOrButton(CrystalGenerators[type - TypePylon1]);
                    if (AllGeneratorsDestroyed)
                    {
                        ProcessForceFieldOpening();
                    }

                    break;
            }
        }

        SaveIfDone(data);
        return true;
    }

    /// <summary>ProcessForceFieldOpening: the field opens and the summoners attack a living Immol'thar.</summary>
    private void ProcessForceFieldOpening()
    {
        DoUseDoorOrButton(GoForcefield);
        if (GetSingleCreatureFromStorage(NpcImmolthar) is not { IsAlive: true } immolthar
            || Instance.FindUpdater<CreatureMapSystem>() is not { } creatures)
        {
            return;
        }

        bool yelled = false;
        foreach (ObjectGuid guid in _highborneSummoners)
        {
            if (creatures.FindCreature(guid) is not { } summoner)
            {
                continue;
            }

            if (!yelled)
            {
                creatures.SayText(summoner, SayKillImmolthar);
                yelled = true;
            }

            if (summoner.IsAlive)
            {
                summoner.AI?.AttackStart(immolthar);
            }
        }

        _highborneSummoners.Clear();
    }

    /// <summary>
    /// SortPylonGuards: every living guard within the radius of an unfinished generator joins that generator's set (once); dead and missing
    /// guards leave the guard list. The reference calls it at the first fight of a guard, so a guard pulled out of range later still counts.
    /// Positions are the guards' spawn homes, which is where an unpulled guard stands.
    /// </summary>
    private void SortPylonGuards()
    {
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } creatures)
        {
            return;
        }

        _generatorGuards.RemoveAll(guid => creatures.FindCreature(guid) is not { IsAlive: true });
        foreach (ObjectGuid guid in _generatorGuards)
        {
            if (creatures.FindCreature(guid) is { } guard)
            {
                AssignToPylonSet(guard);
            }
        }
    }

    private void AssignToPylonSet(Creature guard)
    {
        for (int i = 0; i < PylonCount; i++)
        {
            if (_pylonGuardSets[i].Contains(guard.Guid))
            {
                return;
            }
        }

        for (int i = 0; i < PylonCount; i++)
        {
            if (Encounters[TypePylon1 + i] != EncounterState.Done
                && GetSingleGameObjectFromStorage(CrystalGenerators[i]) is { } generator
                && NearHome(guard, generator))
            {
                _pylonGuardSets[i].Add(guard.Guid);
                return;
            }
        }
    }

    /// <summary>PylonGuardJustDied: the pylon finishes when the last guard of its fixed set dies, wherever it dies.</summary>
    private void PylonGuardJustDied(Creature dead)
    {
        if (FindPylonSet(dead.Guid) < 0)
        {
            // The guard died without the instance hearing its first fight: sort the living ones and place this one by its home.
            SortPylonGuards();
            AssignToPylonSet(dead);
        }

        int set = FindPylonSet(dead.Guid);
        if (set < 0)
        {
            return;
        }

        _pylonGuardSets[set].Remove(dead.Guid);
        if (_pylonGuardSets[set].Count == 0 && Encounters[TypePylon1 + set] != EncounterState.Done)
        {
            SetData(TypePylon1 + (uint)set, EncounterState.Done);
        }
    }

    private int FindPylonSet(ObjectGuid guid)
    {
        for (int i = 0; i < PylonCount; i++)
        {
            if (_pylonGuardSets[i].Contains(guid))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool NearHome(Creature creature, GameObject generator)
    {
        float dx = creature.Home.X - generator.X, dy = creature.Home.Y - generator.Y;
        return dx * dx + dy * dy <= PylonGuardRadius * PylonGuardRadius;
    }

    /// <summary>OnCreatureDeath for the East and West bosses and the generator guards; true when the creature was one of them.</summary>
    private bool WestCreatureDied(Creature creature)
    {
        switch (creature.Template.Entry)
        {
            case NpcZevrim:
                SetData(TypeZevrim, EncounterState.Done);
                return true;
            case NpcIronbarkRedeemed:
                SetData(TypeIronbark, EncounterState.Done);
                return true;
            case NpcTendris:
                SetData(TypeWarpwood, EncounterState.Done);
                return true;
            case NpcImmolthar:
                SetData(TypeImmolthar, EncounterState.Done);
                return true;
            case NpcArcaneAberration:
            case NpcManaRemnant:
                PylonGuardJustDied(creature);
                return true;
            default:
                return false;
        }
    }

    public override void OnCreatureEnterCombat(Creature creature)
    {
        switch (creature.Template.Entry)
        {
            case NpcImmolthar:
                SetData(TypeImmolthar, EncounterState.InProgress);
                break;
            case NpcArcaneAberration:
            case NpcManaRemnant:
                SortPylonGuards();
                break;
        }
    }
}
