using ArcaneCore.Protocol;

namespace ArcaneCore.Game.WorldState.Weather;

/// <summary>Weather sound ids (vmangos Weather.cpp:40-52 "only for 1.12", GetSound :402-443).</summary>
public static class WeatherSounds
{
    public const uint None = 0;
    public const uint RainLight = 8533;
    public const uint RainMedium = 8534;
    public const uint RainHeavy = 8535;
    public const uint SnowLight = 8536;
    public const uint SnowMedium = 8537;
    public const uint SnowHeavy = 8538;
    public const uint SandstormLight = 8556;
    public const uint SandstormMedium = 8557;
    public const uint SandstormHeavy = 8558;

    /// <summary>The sound for a weather type and grade (thresholds 0.3 / 0.6 / 0.9).</summary>
    public static uint For(WeatherType type, float grade)
    {
        (uint light, uint medium, uint heavy) = type switch
        {
            WeatherType.Rain => (RainLight, RainMedium, RainHeavy),
            WeatherType.Snow => (SnowLight, SnowMedium, SnowHeavy),
            WeatherType.Storm => (SandstormLight, SandstormMedium, SandstormHeavy),
            _ => (None, None, None),
        };

        if (type is not (WeatherType.Rain or WeatherType.Snow or WeatherType.Storm))
        {
            return None;
        }

        if (grade < 0.3f)
        {
            return None;
        }

        return grade < 0.6f ? light : grade < 0.9f ? medium : heavy;
    }
}

/// <summary>Weather packet bodies for build 5875.</summary>
public static class WeatherPackets
{
    /// <summary>
    /// SMSG_WEATHER: u32 type, f32 grade, u32 sound id, u8 instant change (vmangos
    /// Server/Packets/Misc.cpp:404-427 for builds above 1.9.4; wow_messages smsg_weather.wowm
    /// 1.12). vmangos always sends 0 for the last byte.
    /// </summary>
    public static byte[] Build(WeatherType type, float grade, uint soundId, bool instant = false)
    {
        var writer = new PacketWriter(13);
        writer.WriteUInt32((uint)type);
        writer.WriteSingle(grade);
        writer.WriteUInt32(soundId);
        writer.WriteByte(instant ? (byte)1 : (byte)0);
        return writer.ToArray();
    }
}
