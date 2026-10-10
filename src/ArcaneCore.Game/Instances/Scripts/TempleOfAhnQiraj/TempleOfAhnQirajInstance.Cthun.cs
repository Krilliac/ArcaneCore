using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

public sealed partial class TempleOfAhnQirajInstance
{
    private sealed class StomachState
    {
        public uint AcidMs = 5000;
        public uint SinceEnteredMs;
        public uint SinceExitedMs;
        public bool Exiting;
    }

    private readonly Dictionary<ObjectGuid, StomachState> _stomach = [];
    public Creature? CthunBody => GetSingleCreatureFromStorage(15727);
    public Creature? CthunEye => GetSingleCreatureFromStorage(15589);

    public bool IsInCthunStomach(Unit unit) => _stomach.TryGetValue(unit.Guid, out StomachState? state) && !state.Exiting;

    /// <summary>Move one grabbed player into the existing map-531 stomach and start Digestive Acid.</summary>
    public bool SendToCthunStomach(Player player)
    {
        if (GetData(CThun) != EncounterState.InProgress || player.Map != Instance
            || !player.IsAlive || player.IsGameMaster || IsInCthunStomach(player)) return false;
        ITeleportSink teleports = Instance.Combat.SpellMitigation?.Teleports ?? new NearTeleportSink();
        if (!teleports.Teleport(player, 531, -8562f, 2037f, -96f, 5.05f)) return false;
        _stomach[player.Guid] = new StomachState();
        ApplyDigestiveAcid(player);
        return true;
    }

    private void ApplyDigestiveAcid(Player player)
    {
        if (CthunBody is { IsAlive: true } body && Instance.Combat.SpellMitigation is { } spells)
            spells.AddAura(player, 26476, caster: body);
    }

    /// <summary>The stomach air trigger sends the player back to C'Thun's room.</summary>
    public override void OnAreaTrigger(Player player, uint triggerId)
    {
        if (!player.IsAlive || player.IsGameMaster) return;
        if (HandleTempleAreaTrigger(player, triggerId)) return;
        if (triggerId == 4034 && _stomach.TryGetValue(player.Guid, out StomachState? state))
        {
            ITeleportSink teleports = Instance.Combat.SpellMitigation?.Teleports ?? new NearTeleportSink();
            if (teleports.Teleport(player, 531, -8578f, 1986.8f, 100.4f, player.Orientation))
            {
                state.Exiting = true;
                state.SinceExitedMs = 0;
                Instance.Combat.SpellMitigation?.RemoveAuras(player, 26476);
            }
        }
        else if (triggerId == 4036 && GetData(CThun) == EncounterState.InProgress && CthunBody is { } body)
        {
            body.System?.SummonAt(body, 15800, -8578f, 1986.8f, 100.22f, 0, null, 1000);
            if (Instance.Combat.SpellMitigation is { } spells)
                spells.CastSpell(body, 26230, SpellCastTargets.ForSelf(), triggered: true);
        }
        else if (triggerId == 4033 && IsInCthunStomach(player) && CthunBody is { } cthun)
        {
            cthun.System?.SummonAt(cthun, 15922, player.X, player.Y, player.Z, 0, null, 4000);
        }
    }

    public override void OnPlayerLeave(Player player)
    {
        _stomach.Remove(player.Guid);
        Instance.Combat.SpellMitigation?.RemoveAuras(player, 26476);
    }

    public override void Update(uint diffMs)
    {
        UpdateTwinsIntro(diffMs);
        foreach ((ObjectGuid guid, StomachState state) in _stomach.ToArray())
        {
            if (Instance.FindObject(guid) is not Player { IsAlive: true } player)
            {
                _stomach.Remove(guid);
                continue;
            }
            state.SinceEnteredMs += diffMs;
            if (state.Exiting)
            {
                state.SinceExitedMs += diffMs;
                if (player.Z > 0 && state.SinceExitedMs > 1500) _stomach.Remove(guid);
                continue;
            }
            if (player.Z <= 0 && Instance.Combat.SpellMitigation is { } activeSpells
                && !activeSpells.HasAura(player, 26476))
            {
                // The near-teleport acknowledgement can remove an aura applied before relocation.
                ApplyDigestiveAcid(player);
                state.AcidMs = 5000;
            }
            if (state.AcidMs <= diffMs)
            {
                ApplyDigestiveAcid(player);
                state.AcidMs = 5000;
            }
            else state.AcidMs -= diffMs;
            if (player.Z > 0 && state.SinceEnteredMs > 4000)
            {
                Instance.Combat.SpellMitigation?.RemoveAuras(player, 26476);
                _stomach.Remove(guid);
            }
        }
    }

    /// <summary>End a wipe when the remaining raid is entirely inside the stomach.</summary>
    public bool KillPlayersInCthunStomach()
    {
        foreach (ObjectGuid guid in _stomach.Keys.ToArray())
        {
            if (Instance.FindObject(guid) is Player player)
            {
                Instance.Combat.SpellMitigation?.RemoveAuras(player, 26476);
                if (player.IsAlive && player.InvincibilityHpThreshold == 0)
                    Instance.Combat.Kill(player, player, durabilityLoss: false);
                else if (player.IsAlive) continue;
            }
            _stomach.Remove(guid);
        }
        return _stomach.Count == 0;
    }
}
