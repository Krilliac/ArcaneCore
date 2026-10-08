using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// Dire Maul North's Gordok Tribute run (mangos-classic
/// src/game/AI/ScriptDevAI/scripts/kalimdor/dire_maul/instance_dire_maul.cpp:
/// OnCreatureCreate, OnCreatureDeath, SetData TYPE_KING_GORDOK and guard cases,
/// CheckConditionCriteriaMeet). The tribute chest's condition ID is the number of six
/// named guards still alive; these states are part of the existing 19-state save string.
/// </summary>
public sealed partial class DireMaulInstance
{
    public const uint TypeKingGordok = 11, TypeMoldar = 12, TypeFengus = 13,
        TypeSlipkik = 14, TypeKromcrush = 15, TypeChorush = 16, TypeStomperKreeg = 17;
    public const uint NpcKingGordok = 11501, NpcMoldar = 14326, NpcFengus = 14321,
        NpcSlipkik = 14323, NpcKromcrush = 14325, NpcChorush = 14324,
        NpcStomperKreeg = 14322, NpcMizzle = 14353;
    public const uint GoGordokTribute = 179564, GoNorthLibraryDoor = 179549;

    private static readonly uint[] TributeGuardTypes =
        [TypeMoldar, TypeFengus, TypeSlipkik, TypeKromcrush, TypeChorush, TypeStomperKreeg];

    /// <summary>instance_dire_maul::CheckConditionCriteriaMeet, condition IDs 0-6.</summary>
    public int TributeGuardsSpared => TributeGuardTypes.Count(type => Encounters[type] != EncounterState.Done);

    public bool TributeConditionMet(uint conditionId) => conditionId <= 6 && TributeGuardsSpared == conditionId;

    public override bool CheckConditionCriteriaMeet(Player player, uint conditionId) => TributeConditionMet(conditionId);

    public override void OnCreatureCreate(Creature creature)
    {
        if (creature.Template.Entry is NpcKingGordok or NpcKromcrush or NpcChorush)
        {
            StoreCreature(creature);
        }
    }

    public override void OnCreatureDeath(Creature creature)
    {
        uint type = creature.Template.Entry switch
        {
            NpcKingGordok => TypeKingGordok,
            NpcMoldar => TypeMoldar,
            NpcFengus => TypeFengus,
            NpcSlipkik => TypeSlipkik,
            NpcKromcrush => TypeKromcrush,
            NpcChorush => TypeChorush,
            NpcStomperKreeg => TypeStomperKreeg,
            _ => uint.MaxValue,
        };

        if (type != uint.MaxValue)
        {
            SetData(type, EncounterState.Done);
        }
    }

    public override void OnPlayerLeave(Player player)
        => Instance.FindUpdater<CreatureMapSystem>()?.RemoveAuras(player, 22799); // SPELL_KING_OF_GORDOK

    private bool SetAdditionalData(uint type, uint data)
    {
        if (type is not (TypeKingGordok or TypeMoldar or TypeFengus or TypeSlipkik or TypeKromcrush or TypeChorush or TypeStomperKreeg))
        {
            return false;
        }

        Encounters[type] = data;
        if (type == TypeKingGordok && data == EncounterState.Done)
        {
            // SetData(TYPE_KING_GORDOK): living Kromcrush and Cho'Rush become friendly,
            // Cho'Rush announces the king's death and summons Mizzle for the DB path.
            foreach (uint entry in new uint[] { NpcKromcrush, NpcChorush })
            {
                if (GetSingleCreatureFromStorage(entry) is { IsAlive: true } ogre)
                {
                    ogre.FactionTemplate = 35;
                    if (ogre.Combat.Victim is not null)
                    {
                        ogre.AI?.EnterEvadeMode();
                    }

                    if (entry == NpcChorush)
                    {
                        ogre.System?.SayText(ogre, -1429003);
                        if (ogre.System?.SummonCorpseDespawn(ogre, NpcMizzle, 683.296f, 484.384f, 29.544f, 0.0174f) is { } mizzle)
                        {
                            mizzle.System?.ChangeMovement(mizzle, 2, 0, 0);
                        }
                    }
                }
            }
        }

        SaveIfDone(data);
        return true;
    }
}
