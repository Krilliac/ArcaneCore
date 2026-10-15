using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Dungeons;

/// <summary>
/// Ragefire Chasm (map 389) dungeon instance script and boss encounter AIs:
/// Oggleflint (11517), Taragaman the Hungerer (11520), Jergosh the Invoker (11518), and Bazzalan (11519).
/// </summary>
public sealed class RagefireChasmInstance(Map map) : ScriptedInstance(map, MaxEncounter)
{
    public const uint MapId = 389;
    public const int MaxEncounter = 4;

    public const uint TypeOggleflint = 0;
    public const uint TypeTaragaman = 1;
    public const uint TypeJergosh = 2;
    public const uint TypeBazzalan = 3;

    public const uint Oggleflint = 11517;
    public const uint TaragamanTheHungerer = 11520;
    public const uint JergoshTheInvoker = 11518;
    public const uint Bazzalan = 11519;

    private readonly HashSet<uint> _registeredAis = [];

    public override void OnCreatureCreate(Creature creature)
    {
        uint entry = creature.Template.Entry;
        if (entry is Oggleflint or TaragamanTheHungerer or JergoshTheInvoker or Bazzalan)
        {
            StoreCreature(creature);
        }

        if (creature.System is { } system && _registeredAis.Add(entry))
        {
            switch (entry)
            {
                case Oggleflint:
                    system.RegisterEntryAi(entry, c => new OggleflintAI(c), rebuildExisting: creature.AI is not null);
                    break;
                case TaragamanTheHungerer:
                    system.RegisterEntryAi(entry, c => new TaragamanAI(c), rebuildExisting: creature.AI is not null);
                    break;
                case JergoshTheInvoker:
                    system.RegisterEntryAi(entry, c => new JergoshAI(c), rebuildExisting: creature.AI is not null);
                    break;
                case Bazzalan:
                    system.RegisterEntryAi(entry, c => new BazzalanAI(c), rebuildExisting: creature.AI is not null);
                    break;
            }
        }
    }

    public override void OnCreatureDeath(Creature creature)
    {
        switch (creature.Template.Entry)
        {
            case Oggleflint:
                SetData(TypeOggleflint, EncounterState.Done);
                break;
            case TaragamanTheHungerer:
                SetData(TypeTaragaman, EncounterState.Done);
                break;
            case JergoshTheInvoker:
                SetData(TypeJergosh, EncounterState.Done);
                break;
            case Bazzalan:
                SetData(TypeBazzalan, EncounterState.Done);
                break;
        }
    }

    public override void SetData(uint type, uint data)
    {
        if (type >= MaxEncounter)
        {
            return;
        }

        Encounters[type] = data;
        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type < MaxEncounter ? Encounters[type] : 0;
}

public sealed class OggleflintAI : RaidBossAI
{
    public const uint CleaveSpell = 40505;

    public OggleflintAI(Creature creature) : base(creature, RagefireChasmInstance.TypeOggleflint)
    {
        AddAction(8000, () => Cast(CleaveSpell), () => 8000);
    }
}

public sealed class TaragamanAI : RaidBossAI
{
    public const uint UppercutSpell = 18072;
    public const uint FireNovaSpell = 11970;

    public TaragamanAI(Creature creature) : base(creature, RagefireChasmInstance.TypeTaragaman)
    {
        AddAction(5000, () => Cast(UppercutSpell), () => 12000);
        AddAction(8000, () => Cast(FireNovaSpell, Me), () => 10000);
    }
}

public sealed class JergoshAI : RaidBossAI
{
    public const uint ImmolateSpell = 20800;
    public const uint CurseOfWeaknessSpell = 18267;

    public JergoshAI(Creature creature) : base(creature, RagefireChasmInstance.TypeJergosh)
    {
        AddAction(4000, () => Cast(ImmolateSpell), () => 12000);
        AddAction(6000, () => Cast(CurseOfWeaknessSpell), () => 15000);
    }
}

public sealed class BazzalanAI : RaidBossAI
{
    public const uint DeadlyPoisonSpell = 3583;
    public const uint SinisterStrikeSpell = 14873;

    public BazzalanAI(Creature creature) : base(creature, RagefireChasmInstance.TypeBazzalan)
    {
        AddAction(5000, () => Cast(SinisterStrikeSpell), () => 8000);
        AddAction(9000, () => Cast(DeadlyPoisonSpell), () => 15000);
    }
}
