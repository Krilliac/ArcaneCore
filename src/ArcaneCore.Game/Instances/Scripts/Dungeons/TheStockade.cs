using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Dungeons;

/// <summary>
/// The Stockade (map 34) dungeon instance script and boss encounter AIs:
/// Targorr the Dread (1696), Kam Deepfury (1666), Hamhock (1717),
/// Bazil Thredd (1716), Dextren Ward (1663), and Bruegal Ironknuckle (1720).
/// </summary>
public sealed class TheStockadeInstance(Map map) : ScriptedInstance(map, MaxEncounter)
{
    public const uint MapId = 34;
    public const int MaxEncounter = 6;

    public const uint TypeTargorr = 0;
    public const uint TypeKamDeepfury = 1;
    public const uint TypeHamhock = 2;
    public const uint TypeBazilThredd = 3;
    public const uint TypeDextrenWard = 4;
    public const uint TypeBruegalIronknuckle = 5;

    public const uint TargorrTheDread = 1696;
    public const uint KamDeepfury = 1666;
    public const uint Hamhock = 1717;
    public const uint BazilThredd = 1716;
    public const uint DextrenWard = 1663;
    public const uint BruegalIronknuckle = 1720;

    private readonly HashSet<uint> _registeredAis = [];

    public override void OnCreatureCreate(Creature creature)
    {
        uint entry = creature.Template.Entry;
        if (entry is TargorrTheDread or KamDeepfury or Hamhock or BazilThredd or DextrenWard or BruegalIronknuckle)
        {
            StoreCreature(creature);
        }

        if (creature.System is { } system && _registeredAis.Add(entry))
        {
            switch (entry)
            {
                case TargorrTheDread:
                    system.RegisterEntryAi(entry, c => new TargorrTheDreadAI(c), rebuildExisting: creature.AI is not null);
                    break;
                case KamDeepfury:
                    system.RegisterEntryAi(entry, c => new KamDeepfuryAI(c), rebuildExisting: creature.AI is not null);
                    break;
                case Hamhock:
                    system.RegisterEntryAi(entry, c => new HamhockAI(c), rebuildExisting: creature.AI is not null);
                    break;
                case BazilThredd:
                    system.RegisterEntryAi(entry, c => new BazilThreddAI(c), rebuildExisting: creature.AI is not null);
                    break;
                case DextrenWard:
                    system.RegisterEntryAi(entry, c => new DextrenWardAI(c), rebuildExisting: creature.AI is not null);
                    break;
                case BruegalIronknuckle:
                    system.RegisterEntryAi(entry, c => new BruegalIronknuckleAI(c), rebuildExisting: creature.AI is not null);
                    break;
            }
        }
    }

    public override void OnCreatureDeath(Creature creature)
    {
        switch (creature.Template.Entry)
        {
            case TargorrTheDread:
                SetData(TypeTargorr, EncounterState.Done);
                break;
            case KamDeepfury:
                SetData(TypeKamDeepfury, EncounterState.Done);
                break;
            case Hamhock:
                SetData(TypeHamhock, EncounterState.Done);
                break;
            case BazilThredd:
                SetData(TypeBazilThredd, EncounterState.Done);
                break;
            case DextrenWard:
                SetData(TypeDextrenWard, EncounterState.Done);
                break;
            case BruegalIronknuckle:
                SetData(TypeBruegalIronknuckle, EncounterState.Done);
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

public sealed class TargorrTheDreadAI : RaidBossAI
{
    public const uint ThrashSpell = 3391;

    public TargorrTheDreadAI(Creature creature) : base(creature, TheStockadeInstance.TypeTargorr)
    {
        AddAction(6000, () => Cast(ThrashSpell, Me), () => 10000);
    }
}

public sealed class KamDeepfuryAI : RaidBossAI
{
    public const uint ShieldSlamSpell = 15655;

    public KamDeepfuryAI(Creature creature) : base(creature, TheStockadeInstance.TypeKamDeepfury)
    {
        AddAction(6000, () => Cast(ShieldSlamSpell), () => 10000);
    }
}

public sealed class HamhockAI : RaidBossAI
{
    public const uint ChainLightningSpell = 12058;
    public const uint DemoralizingShoutSpell = 13730;

    public HamhockAI(Creature creature) : base(creature, TheStockadeInstance.TypeHamhock)
    {
        AddAction(5000, () => Cast(ChainLightningSpell), () => 11000);
        AddAction(8000, () => Cast(DemoralizingShoutSpell, Me), () => 18000);
    }
}

public sealed class BazilThreddAI : RaidBossAI
{
    public const uint SmokeBombSpell = 7964;

    public BazilThreddAI(Creature creature) : base(creature, TheStockadeInstance.TypeBazilThredd)
    {
        AddAction(9000, () => Cast(SmokeBombSpell, Me), () => 15000);
    }
}

public sealed class DextrenWardAI : RaidBossAI
{
    public const uint IntimidatingShoutSpell = 5246;
    public const uint StrikeSpell = 12057;

    public DextrenWardAI(Creature creature) : base(creature, TheStockadeInstance.TypeDextrenWard)
    {
        AddAction(5000, () => Cast(StrikeSpell), () => 7000);
        AddAction(12000, () => Cast(IntimidatingShoutSpell), () => 18000);
    }
}

public sealed class BruegalIronknuckleAI : RaidBossAI
{
    public const uint KickSpell = 15614;

    public BruegalIronknuckleAI(Creature creature) : base(creature, TheStockadeInstance.TypeBruegalIronknuckle)
    {
        AddAction(7000, () => Cast(KickSpell), () => 10000);
    }
}
