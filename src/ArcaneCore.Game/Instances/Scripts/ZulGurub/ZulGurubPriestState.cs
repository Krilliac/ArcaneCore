using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.ZulGurub;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// mangos-classic .../zulgurub/zulgurub.cpp OnCreatureCreate, SetData and RemoveHakkarPowerStack.
/// Priest deaths feed their DONE states through the map kill hook until their individual AIs are ported.
/// This is state/power bookkeeping, not an implementation of those priest encounters.
/// </summary>
public sealed partial class ZulGurubInstance
{
    private bool _priestDeathsSubscribed;
    private bool _thekalDamageSubscribed;

    /// <summary>Thekal (14509) and his zealots (11347 Lor'Khan, 11348 Zath) from <c>m_npcEntryGuidStore</c>.</summary>
    public Creature? FindThekalCompanion(uint entry) => GetSingleCreatureFromStorage(entry);

    public override void OnCreatureCreate(Creature creature)
    {
        if (!_priestDeathsSubscribed)
        {
            Instance.Combat.UnitKilled += OnRaidUnitKilled;
            _priestDeathsSubscribed = true;
        }

        if (creature.Template.Entry == 14834)
        {
            StoreCreature(creature);
        }

        if (creature.Template.Entry is 14509 or 11347 or 11348)
        {
            StoreCreature(creature);
            if (!_thekalDamageSubscribed)
            {
                Instance.Combat.DamageTaken += OnThekalDamage;
                _thekalDamageSubscribed = true;
            }
        }

        if (creature.Template.Entry is 11382 or 11380) StoreCreature(creature); // Mandokir, Jin'do (SAY_GRATS_JINDO)
    }

    /// <summary>Jin'do (11380) from <c>m_npcEntryGuidStore</c>.</summary>
    public Creature? FindJindo() => GetSingleCreatureFromStorage(11380);

    private void OnRaidUnitKilled(Unit? killer, Unit victim)
    {
        // mob_ohganAI::KilledUnit: a player Ohgan kills in combat is revived by the closest Chained Spirit, as with Mandokir's own kills.
        if (victim is Player && killer is Creature { Entry: 14988, IsAlive: true } ohgan && ohgan.Combat.IsInCombat &&
            GetSingleCreatureFromStorage(11382)?.AI is MandokirAI raptorOwner)
        {
            raptorOwner.ReviveWithChainedSpirit(victim);
        }

        if (victim is not Creature creature)
        {
            return;
        }

        uint priest = creature.Template.Entry switch
        {
            14517 => 0, // Jeklik
            14507 => 1, // Venoxis
            14510 => 2, // Marli
            14509 => 3, // Thekal
            14515 => 4, // Arlokk
            _ => uint.MaxValue,
        };
        if (priest < 5)
        {
            SetData(priest, EncounterState.Done);
        }

        if (creature.Template.Entry == 14988 && GetSingleCreatureFromStorage(11382)?.AI is MandokirAI mandokir)
            mandokir.OnOhganDeath();
    }

    private void OnThekalDamage(Unit attacker, Unit victim, uint damage)
    {
        if (victim is Creature { AI: ThekalCompanionAI ai } creature &&
            damage >= creature.Health)
            ai.OnLethalDamage();
    }

    private bool SetPriestData(uint type, uint data)
    {
        if (type >= 5)
        {
            return false;
        }

        if (Encounters[type] == data)
        {
            return true;
        }

        Encounters[type] = data;
        if (type == 4 && data is EncounterState.InProgress or EncounterState.Done or EncounterState.Fail)
            DoUseDoorOrButton(180497);
        if (data == EncounterState.Done && GetSingleCreatureFromStorage(14834) is { IsAlive: true } hakkar)
        {
            Instance.FindUpdater<CreatureMapSystem>()?.CastSpell(hakkar, 24693, hakkar, triggered: true);
        }

        SaveIfDone(data);
        return true;
    }
}
