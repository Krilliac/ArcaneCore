using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.GameObjects;

/// <summary>Spell effect 86's GameObjectActions values in the Classic client and mangos-classic.</summary>
public enum GameObjectSpellAction
{
    None = 0,
    AnimateCustom0 = 1,
    AnimateCustom1 = 2,
    AnimateCustom2 = 3,
    AnimateCustom3 = 4,
    Disturb = 5,
    Unlock = 6,
    Lock = 7,
    Open = 8,
    OpenAndUnlock = 9,
    Close = 10,
    ToggleOpen = 11,
    Destroy = 12,
    Rebuild = 13,
    Creation = 14,
    Despawn = 15,
    MakeInert = 16,
    MakeActive = 17,
    CloseAndLock = 18,
}

public sealed partial class GameObjectMapSystem
{
    private static readonly uint[] s_templars = [15209, 15211, 15212, 15307];
    private static readonly uint[] s_dukes = [15206, 15207, 15208, 15220];
    private static readonly uint[] s_royals = [15203, 15204, 15205, 15305];
    private readonly HashSet<(uint Spell, int Action)> _reportedSpellActions = [];

    /// <summary>
    /// Apply SPELL_EFFECT_ACTIVATE_OBJECT to a tracked object on this map. This calls the same object
    /// state machine as a scripted use; it does not treat a missing or despawned GUID as an object.
    /// </summary>
    public GameObjectUseResult ActivateBySpell(Unit caster, GameObject go, uint spellId, int actionValue, Random random)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(go);
        ArgumentNullException.ThrowIfNull(random);
        if (!ReferenceEquals(caster.Map, Map) || !go.IsSpawned || !Tracks(go)) return GameObjectUseResult.NotFound;
        if (AiOf(go)?.OnActivateBySpell(this, go, caster, spellId, unchecked((uint)actionValue)) == true)
            return GameObjectUseResult.Ok;

        GameObjectSpellAction action = (GameObjectSpellAction)actionValue;
        GameObjectUseResult result = action switch
        {
            >= GameObjectSpellAction.AnimateCustom0 and <= GameObjectSpellAction.AnimateCustom3 => Animate(go, (uint)action - 1),
            GameObjectSpellAction.Disturb or GameObjectSpellAction.Open => spellId == 17731
                ? Animate(go, 0) : UseByUnit(caster, go),
            GameObjectSpellAction.Unlock => SetFlag(go, GameObjectFlags.Locked, false),
            GameObjectSpellAction.Lock => SetFlag(go, GameObjectFlags.Locked, true),
            GameObjectSpellAction.OpenAndUnlock => OpenAndUnlock(go),
            GameObjectSpellAction.Close or GameObjectSpellAction.Rebuild => ResetBySpell(go),
            GameObjectSpellAction.Destroy => ActivateDoorOrButton(go, go.Template.AutoCloseSeconds(), alternative: true),
            GameObjectSpellAction.Despawn => DespawnBySpell(go),
            GameObjectSpellAction.MakeInert => MakeInert(go, caster, spellId, random),
            GameObjectSpellAction.MakeActive => SetFlag(go, GameObjectFlags.NoInteract, false),
            GameObjectSpellAction.CloseAndLock => CloseAndLock(go),
            _ => GameObjectUseResult.Unsupported,
        };
        if (result == GameObjectUseResult.Unsupported && _reportedSpellActions.Add((spellId, actionValue)))
        {
            _logger.LogWarning("ACTIVATE_OBJECT spell {Spell} action {Action} is unsupported for game object {Object} type {Type}",
                spellId, actionValue, go.Entry, go.Type);
        }

        return result;
    }

    private GameObjectUseResult Animate(GameObject go, uint id)
    {
        SendCustomAnim(go, id);
        return GameObjectUseResult.Ok;
    }

    private static GameObjectUseResult SetFlag(GameObject go, GameObjectFlags flag, bool set)
    {
        go.Flags = set ? go.Flags | flag : go.Flags & ~flag;
        return GameObjectUseResult.Ok;
    }

    private GameObjectUseResult OpenAndUnlock(GameObject go)
    {
        if (go.Type is GameObjectType.Door or GameObjectType.Button)
            ActivateDoorOrButton(go, go.Template.AutoCloseSeconds());
        go.Flags &= ~GameObjectFlags.Locked;
        return GameObjectUseResult.Ok;
    }

    private static GameObjectUseResult ResetBySpell(GameObject go)
    {
        if (go.LootState is not (GameObjectLootState.Ready or GameObjectLootState.JustDeactivated))
        {
            ResetToReady(go);
            go.LootState = GameObjectLootState.JustDeactivated;
        }
        return GameObjectUseResult.Ok;
    }

    private static GameObjectUseResult CloseAndLock(GameObject go)
    {
        ResetBySpell(go);
        go.Flags |= GameObjectFlags.Locked;
        return GameObjectUseResult.Ok;
    }

    private GameObjectUseResult DespawnBySpell(GameObject go)
    {
        go.LootState = GameObjectLootState.JustDeactivated;
        Despawn(go);
        return GameObjectUseResult.Ok;
    }

    private GameObjectUseResult MakeInert(GameObject go, Unit caster, uint spellId, Random random)
    {
        uint creature = SilithusSummon(spellId, random);
        if (creature != 0 && Map.FindUpdater<CreatureMapSystem>() is { } creatures)
        {
            float orientation = MathF.Atan2(caster.Y - go.Y, caster.X - go.X);
            creatures.SummonInstanceCreatureTimedOocOrDead(creature, go.X, go.Y, go.Z, orientation, 60_000);
        }

        go.Flags |= GameObjectFlags.NoInteract;
        return GameObjectUseResult.Ok;
    }

    private static uint SilithusSummon(uint spellId, Random random) => spellId switch
    {
        24734 => s_templars[random.Next(s_templars.Length)],
        24744 => 15209, 24756 => 15212, 24758 => 15307, 24760 => 15211,
        24763 => s_dukes[random.Next(s_dukes.Length)],
        24765 => 15206, 24768 => 15220, 24770 => 15208, 24772 => 15207,
        24784 => s_royals[random.Next(s_royals.Length)],
        24786 => 15203, 24788 => 15204, 24789 => 15205, 24790 => 15305,
        _ => 0,
    };

    /// <summary>GameObject::Use for a creature on a goober: the player-only gossip and quest paths are skipped.</summary>
    private GameObjectUseResult UseGooberByUnit(Unit user, GameObject go)
    {
        if (user is Player player)
            return UseGoober(player, go, lockChecked: true, scriptTookUse: InstanceScriptTookUse(player, go));
        if (go.CooldownUntilMs > _clockMs || go.LootState != GameObjectLootState.Ready)
            return GameObjectUseResult.InUse;

        TriggerLinkedTrap(go, user);
        uint autoClose = go.Template.AutoCloseSeconds();
        go.Flags |= GameObjectFlags.InUse;
        go.LootState = GameObjectLootState.Activated;
        go.UseCount++;
        if (GameObjectInfoView.HasCustomAnim(go.GetUInt32(UpdateFields.GameobjectDisplayid))
            || (autoClose > 0 && go.Template.GetData(4) != 0))
            SendCustomAnim(go, 0);
        else
            go.State = GameObjectState.Active;
        go.ResetAfterSecond = ClockSeconds + autoClose;
        return GameObjectUseResult.Ok;
    }
}
