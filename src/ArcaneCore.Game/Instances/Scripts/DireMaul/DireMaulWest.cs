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
/// <item>The Arcane Aberrations and Mana Remnants (the generator guards) belong to the unfinished crystal generator within 20 yards of them;
/// when the last of them dies the pylon is DONE and its generator is used. The reference sorts the guards at their first combat; this port
/// resolves the generator at death, which gives the same grouping unless a guard was pulled out of its generator's radius.</item>
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
                _generatorGuards.Add(creature.Guid);
                break;
            case NpcHighborneSummoner:
                _highborneSummoners.Add(creature.Guid);
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

    /// <summary>PylonGuardJustDied: the pylon finishes with the last guard of its generator.</summary>
    private void PylonGuardJustDied(Creature dead)
    {
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } creatures)
        {
            return;
        }

        for (int i = 0; i < PylonCount; i++)
        {
            if (Encounters[TypePylon1 + i] == EncounterState.Done
                || GetSingleGameObjectFromStorage(CrystalGenerators[i]) is not { } generator
                || !Near(dead, generator))
            {
                continue;
            }

            bool othersAlive = false;
            foreach (ObjectGuid guid in _generatorGuards)
            {
                if (guid != dead.Guid && creatures.FindCreature(guid) is { IsAlive: true } guard && Near(guard, generator))
                {
                    othersAlive = true;
                    break;
                }
            }

            if (!othersAlive)
            {
                SetData(TypePylon1 + (uint)i, EncounterState.Done);
            }

            return;
        }
    }

    private static bool Near(Creature creature, GameObject generator)
    {
        float dx = creature.X - generator.X, dy = creature.Y - generator.Y;
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
        if (creature.Template.Entry == NpcImmolthar)
        {
            SetData(TypeImmolthar, EncounterState.InProgress);
        }
    }
}
