using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Progression;

/// <summary>
/// Experience packets for build 5875, after vmangos/core 4b3d241 <c>Server/Packets/Misc.cpp</c>
/// (LogXpGain, LevelUpInfo) and Player::SendLogXPGain / GiveLevel.
/// </summary>
public static class ProgressionPackets
{
    /// <summary>
    /// SMSG_LOG_XPGAIN: victim GUID (empty for non-kill), total XP including rested bonus, type
    /// (0 kill, 1 non-kill); kills add the XP without the rested bonus and a float group bonus (1 = none).
    /// 21 bytes for a kill, 13 otherwise.
    /// </summary>
    public static PacketWriter LogXpGain(ObjectGuid victim, uint givenXp, uint restedXp)
    {
        bool kill = !victim.IsEmpty;
        var w = new PacketWriter(kill ? 21 : 13);
        w.WriteUInt64(victim.Value);
        w.WriteUInt32(unchecked(givenXp + restedXp));
        w.WriteByte(kill ? (byte)0 : (byte)1);
        if (kill)
        {
            w.WriteUInt32(givenXp);
            w.WriteSingle(1.0f);
        }

        return w;
    }

    /// <summary>
    /// SMSG_LEVELUP_INFO (48 bytes): new level, health gain, five power gains (only mana is
    /// used), five stat gains. Gains are written as two's-complement u32 like the reference.
    /// </summary>
    public static PacketWriter LevelUpInfo(uint level, int healthGain, int manaGain, ReadOnlySpan<int> statGains)
    {
        if (statGains.Length != 5)
        {
            throw new ArgumentException("five stat gains are required", nameof(statGains));
        }

        var w = new PacketWriter(48);
        w.WriteUInt32(level);
        w.WriteUInt32(unchecked((uint)healthGain));
        w.WriteUInt32(unchecked((uint)manaGain));
        for (int i = 1; i < 5; i++)
        {
            w.WriteUInt32(0);
        }

        foreach (int gain in statGains)
        {
            w.WriteUInt32(unchecked((uint)gain));
        }

        return w;
    }
}
