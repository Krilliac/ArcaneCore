using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// mangos-classic blackwing_lair/boss_chromaggus.cpp boss_chromaggusAI::ExecuteAction;
/// vmangos burning_steppes/blackwing_lair/boss_chromaggus.cpp
/// boss_chromaggusAI::Reset/UpdateAI (distinct breaths, vulnerability, affliction and enrage).
/// </summary>
public sealed class ChromaggusAI : RaidBossAI
{
    private static readonly uint[] Breaths = [23308, 23310, 23313, 23315, 23187];
    private static readonly uint[] RightBreaths = [23309, 23312, 23314, 23316, 23189];
    private static readonly uint[] Afflictions = [23153, 23154, 23155, 23170, 23169];
    private static readonly uint[] Vulnerabilities = [22277, 22278, 22279, 22280, 22281];
    private readonly HashSet<ObjectGuid> _redAfflicted = [];
    private bool _enraged;
    private uint _vulnerability;

    public uint LeftBreath { get; }
    public uint RightBreath { get; }

    public ChromaggusAI(Creature creature) : base(creature, 6)
    {
        var raid = creature.Map?.FindUpdater<BlackwingLairInstance>();
        if (raid is not null)
        {
            int first = Array.IndexOf(Breaths, raid.GetData(9));
            int second = Array.IndexOf(RightBreaths, raid.GetData(10));
            if (first < 0)
            {
                first = PickOther(second);
                raid.SetData(9, Breaths[first]);
            }
            if (second < 0)
            {
                second = PickOther(first);
                raid.SetData(10, RightBreaths[second]);
            }
        }
        LeftBreath = raid?.GetData(9) ?? Breaths[0];
        RightBreath = raid?.GetData(10) ?? Breaths[1];
        AddAction(0, Shimmer, () => 45000);
        AddAction(30000, () => Cast(LeftBreath), () => 60000);
        AddAction(60000, () => Cast(RightBreath), () => 60000);
        AddAction(7000, Afflict, () => 7000);
        AddAction(15000, Frenzy, () => 15000);
    }

    private int PickOther(int excluded)
    {
        int index = System?.RandomInt(0, Breaths.Length - (excluded < 0 ? 1 : 2)) ?? 0;
        return excluded >= 0 && index >= excluded ? index + 1 : index;
    }

    private bool Shimmer()
    {
        uint next = Vulnerabilities[System?.RandomInt(0, Vulnerabilities.Length - 1) ?? 0];
        if (!Cast(next)) return false;
        if (_vulnerability != 0) System?.RemoveAuras(Me, _vulnerability);
        _vulnerability = next;
        System?.SayText(Me, 9793);
        return true;
    }

    private bool Frenzy()
    {
        if (!Cast(23128)) return false;
        System?.SayText(Me, 7797);
        return true;
    }

    private bool Afflict()
    {
        uint spell = Afflictions[System?.RandomInt(0, Afflictions.Length - 1) ?? 0];
        Unit[] targets = [.. Me.Combat.Threat.Entries.Select(e => e.Target)
            .Where(t => t.IsAlive && t.IsInWorld && ReferenceEquals(t.Map, Me.Map))];
        if (targets.Length == 0) return false;
        int count = System?.RandomInt(11, 15) ?? 11;
        for (int i = 0; i < count; i++)
        {
            Unit target = targets[System?.RandomInt(0, targets.Length - 1) ?? 0];
            if (System?.HasAura(target, 23174) == true || !Cast(spell, target, triggered: true)) continue;
            if (spell == 23155 && target is Player) _redAfflicted.Add(target.Guid);
            if (target is Player && Afflictions.All(id => System?.HasAura(target, id) == true))
            {
                foreach (uint id in Afflictions) System?.RemoveAuras(target, id);
                Cast(23174, target, triggered: true);
            }
        }
        return true;
    }

    protected override void UpdateCombat(uint diffMs)
    {
        base.UpdateCombat(diffMs);
        if (!_enraged && Me.Health * 5 < Me.MaxHealth && Cast(23537)) _enraged = true;
        foreach (ObjectGuid guid in _redAfflicted.ToArray())
        {
            if (Me.Map?.FindObject(guid) is not Player player || !player.IsAlive)
            {
                if (Cast(23168)) _redAfflicted.Remove(guid);
            }
            else if (System?.HasAura(player, 23155) != true) _redAfflicted.Remove(guid);
        }
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        _enraged = false;
        _redAfflicted.Clear();
        if (_vulnerability != 0) System?.RemoveAuras(Me, _vulnerability);
        _vulnerability = 0;
    }
}
