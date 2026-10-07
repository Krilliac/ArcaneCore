using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public static class FoodDrinkVisualPackets
{
    public const uint FoodVisualKit = 406;
    public const uint DrinkVisualKit = 438;
    public const uint EatEmote = 7;

    public static byte[] PlaySpellVisual(ObjectGuid caster, uint visualKit)
    {
        var payload = new byte[12];
        BitConverter.GetBytes(caster.Value).CopyTo(payload, 0);
        BitConverter.GetBytes(visualKit).CopyTo(payload, 8);
        return payload;
    }

    public static byte[] Emote(ObjectGuid caster, uint emote)
    {
        var payload = new byte[12];
        BitConverter.GetBytes(emote).CopyTo(payload, 0);
        BitConverter.GetBytes(caster.Value).CopyTo(payload, 4);
        return payload;
    }
}
