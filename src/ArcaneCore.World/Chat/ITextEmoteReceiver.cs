using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Chat;

/// <summary>
/// A world feature that reacts when a player targets a creature with a text emote (vmangos HandleTextEmoteOpcode,
/// ChatHandler.cpp:750-752: <c>((Creature*)unit)->AI()->ReceiveEmote(GetPlayer(), packet.textEmote)</c>). The creature's
/// own AI (<see cref="Creature.ReceiveEmote"/>, EventAI's EVENT_T_RECEIVE_EMOTE) hears it first; features that implement
/// this interface hear it after. Picked up when the feature is an <see cref="Features.IWorldFeature"/>; called on the world
/// thread after the emote was announced.
/// </summary>
public interface ITextEmoteReceiver
{
    void ReceiveEmote(Creature creature, Player player, uint textEmote);
}
