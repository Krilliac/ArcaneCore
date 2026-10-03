namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// Where a modifier aura's class mask comes from when the spell data does not hold it in full. vmangos keeps the mask in the
/// 64-bit <c>EffectItemType</c> (SpellEntry.h:656); ArcaneCore's spell table reads a 32-bit one, so masks above bit 31
/// (about 11% of the classic spell_affect rows) need an overlay. Returning null means "no overlay for this effect: use the
/// spell's own <see cref="SpellEffectInfo.ItemType"/>".
/// </summary>
public interface IClassMaskSource
{
    ulong? TryGetMask(uint spellId, int effectIndex);
}
