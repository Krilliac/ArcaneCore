using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_EFFECT_INEBRIATE (100): drinks make a player drunk (vmangos Spell::EffectInebriate, SpellEffects.cpp:5076-5089), and the player sobers by
/// 256 every 10 seconds (Player::HandleSobering, Player.cpp:777-783, from Player::Update :1277-1283). The drunk value is the high 15 bits of the
/// first half of PLAYER_BYTES_3 (Player::SetDrunkValue, :796-808: <c>gender | (drunk &amp; 0xFFFE)</c>), which the client reads.
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>The sobering interval (vmangos: 10 s).</summary>
    public const uint SoberingIntervalMs = 10_000;

    private readonly Dictionary<ObjectGuid, (Player Player, uint TimerMs)> _drunkPlayers = [];

    private void InstallInebriate() => RegisterEffect(SpellEffectName.Inebriate, context =>
    {
        if (context.Target is not Player player)
        {
            return;
        }

        uint drunk = GetDrunkValue(player) + (uint)Math.Max(context.Value, 0) * 256;
        SetDrunkValue(player, (ushort)Math.Min(drunk, 0xFFFFu));
    });

    /// <summary>The player's drunk value (0 sober … 0xFFFE).</summary>
    public static ushort GetDrunkValue(Player player) => (ushort)(player.GetUInt16(UpdateFields.PlayerBytes3, 0) & 0xFFFE);

    /// <summary>vmangos Player::GetDrunkenstateByValue: 0 sober, 1 tipsy, 2 drunk, 3 smashed.</summary>
    public static int DrunkenState(ushort value) => value >= 23000 ? 3 : value >= 12800 ? 2 : (value & 0xFFFE) != 0 ? 1 : 0;

    /// <summary>vmangos Player::SetDrunkValue: write the value next to the gender bit and track the player for sobering while it is above 0.</summary>
    public void SetDrunkValue(Player player, ushort value)
    {
        ArgumentNullException.ThrowIfNull(player);
        ushort gender = (ushort)(player.GetUInt16(UpdateFields.PlayerBytes3, 0) & 0x0001);
        player.SetUInt16(UpdateFields.PlayerBytes3, 0, (ushort)(gender | (value & 0xFFFE)));
        if ((value & 0xFFFE) == 0)
        {
            _drunkPlayers.Remove(player.Guid);
        }
        else if (!_drunkPlayers.ContainsKey(player.Guid))
        {
            _drunkPlayers[player.Guid] = (player, 0);
        }
    }

    private void UpdateSobering(uint diffMs)
    {
        if (_drunkPlayers.Count == 0)
        {
            return;
        }

        foreach ((ObjectGuid guid, (Player player, uint elapsed)) in _drunkPlayers.ToArray())
        {
            uint timer = elapsed;
            if (!player.IsInWorld)
            {
                _drunkPlayers.Remove(guid);
                continue;
            }

            timer += diffMs;
            if (timer > SoberingIntervalMs)
            {
                _drunkPlayers[guid] = (player, 0);
                ushort drunk = GetDrunkValue(player);
                SetDrunkValue(player, (ushort)(drunk <= 256 ? 0 : drunk - 256));
            }
            else
            {
                _drunkPlayers[guid] = (player, timer);
            }
        }
    }
}
