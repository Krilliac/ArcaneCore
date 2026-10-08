using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Deadmines;

/// <summary>Mr Smite's 66/33 percent stomp, chest run and weapon-change pause, from mangos-classic
/// deadmines/boss_mr_smite.cpp:57-262 (boss_mr_smiteAI::UpdateAI, MovementInform and PhaseEquip*).
/// The three virtual item displays/classes come from ClassicDB z2815 item_template rows 2179, 2183 and 10756,
/// and the starting item 2179 from creature_equip_template row 646 (Creature::SetVirtualItem, Creature.cpp:2747-2777).</summary>
public sealed class MrSmiteAi(Creature creature, DeadminesInstance instance) : AggressorAI(creature)
{
    public enum SmitePhase { First, Second, Third, MovingToChest, Kneeling, Equipping, Resuming }
    private SmitePhase _phase;
    private uint _equipTimer;
    private uint _slamTimer;
    private Unit? _lastVictim;

    public SmitePhase Phase => _phase;

    public override void OnRespawn()
    {
        _phase = SmitePhase.First;
        _equipTimer = 0;
        _slamTimer = 9_000;
        Me.StandState = StandState.Stand;
        SetVirtualItem(0, 7420, 7, 1, 13, 3); // item 2179, default sword
        ClearVirtualItem(1);
        DoCast(Me, 6433, triggered: true); // Nible Reflexes, boss_mr_smiteAI::Reset
    }

    public override void OnReachedHome() => DoCast(Me, 6433, triggered: true);

    public override bool AttackStart(Unit target)
        => _phase is SmitePhase.MovingToChest or SmitePhase.Kneeling or SmitePhase.Equipping or SmitePhase.Resuming
            ? false : base.AttackStart(target);

    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (type != MovementGeneratorType.Point || _phase != SmitePhase.MovingToChest)
        {
            return;
        }

        Me.SetByte(UpdateFields.UnitFieldBytes2, 0, 0); // unarmed sheath
        Me.StandState = StandState.Kneel;
        ClearVirtualItem(0);
        ClearVirtualItem(1);
        _phase = SmitePhase.Kneeling;
        _equipTimer = 3_000;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_phase is SmitePhase.MovingToChest or SmitePhase.Kneeling or SmitePhase.Equipping or SmitePhase.Resuming)
        {
            UpdateEquipmentChange(diffMs);
            return;
        }

        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        float healthPercent = Me.MaxHealth == 0 ? 100f : 100f * Me.Health / Me.MaxHealth;
        if ((_phase == SmitePhase.First && healthPercent < 66f) || (_phase == SmitePhase.Second && healthPercent < 33f))
        {
            if (DoCast(Me, 6432) == CreatureCastResult.Ok) // Smite Stomp
            {
                System?.SayText(Me, healthPercent < 33f ? -1036003 : -1036002);
                _lastVictim = victim;
                System?.Map.Combat.AttackStop(Me, targetSwitch: true);
                System?.RemoveAuras(Me, _phase == SmitePhase.First ? 6433u : 12787u);
                _phase = SmitePhase.Equipping;
                _equipTimer = 2_500;
            }

            return;
        }

        if (_phase == SmitePhase.Third)
        {
            if (_slamTimer <= diffMs)
            {
                if (DoCast(victim, 6435) == CreatureCastResult.Ok)
                {
                    _slamTimer = 11_000;
                }
            }
            else
            {
                _slamTimer -= diffMs;
            }
        }
    }

    private void UpdateEquipmentChange(uint diffMs)
    {
        if (_equipTimer > diffMs)
        {
            _equipTimer -= diffMs;
            return;
        }

        _equipTimer = 0;
        switch (_phase)
        {
            case SmitePhase.Equipping:
                if (instance.SmiteChest is not { } chest)
                {
                    return; // SD2 retries PhaseEquipStart until its chest is present
                }

                _phase = SmitePhase.MovingToChest;
                Me.Motion.Clear();
                System?.SetFacingTo(Me, MathF.Atan2(chest.Y - Me.Y, chest.X - Me.X));
                Me.Motion.MovePoint(0, chest.X, chest.Y, chest.Z, run: true);
                break;
            case SmitePhase.Kneeling:
                if (Me.MaxHealth > 0 && 100f * Me.Health / Me.MaxHealth < 33f)
                {
                    SetVirtualItem(0, 19766, 5, 2, 17, 1); // item 10756, Smite's Mighty Hammer
                    ClearVirtualItem(1);
                    DoCast(Me, 6436); // Smite's Hammer
                }
                else
                {
                    SetVirtualItem(0, 7427, 0, 1, 13, 3); // item 2183, axe in both hands
                    SetVirtualItem(1, 7427, 0, 1, 13, 3);
                }

                Me.StandState = StandState.Stand;
                _phase = SmitePhase.Resuming;
                _equipTimer = 1_000;
                break;
            case SmitePhase.Resuming:
                Me.SetByte(UpdateFields.UnitFieldBytes2, 0, 1); // melee sheath
                _phase = Me.MaxHealth > 0 && 100f * Me.Health / Me.MaxHealth < 33f ? SmitePhase.Third : SmitePhase.Second;
                if (_phase == SmitePhase.Second)
                {
                    DoCast(Me, 12787, triggered: true); // Thrash
                }

                if (_lastVictim is { IsAlive: true } target)
                {
                    AttackStart(target);
                }
                else
                {
                    EnterEvadeMode();
                }

                break;
        }
    }

    private void SetVirtualItem(int slot, uint display, byte subclass, byte material, byte inventoryType, byte sheath)
    {
        int info = UpdateFields.UnitVirtualItemInfo + slot * 2;
        Me.SetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + slot, display);
        Me.SetByte(info, 0, 2); // ITEM_CLASS_WEAPON
        Me.SetByte(info, 1, subclass);
        Me.SetByte(info, 2, material);
        Me.SetByte(info, 3, inventoryType);
        Me.SetByte(info + 1, 0, sheath);
    }

    private void ClearVirtualItem(int slot)
    {
        int info = UpdateFields.UnitVirtualItemInfo + slot * 2;
        Me.SetUInt32(UpdateFields.UnitVirtualItemSlotDisplay + slot, 0);
        Me.SetUInt32(info, 0);
        Me.SetUInt32(info + 1, 0);
    }
}
