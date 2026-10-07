using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// Summoning rituals (GAMEOBJECT_TYPE_SUMMONING_RITUAL: Ritual of Summoning, Ritual of Doom, the dungeon altars): the participants, the ritual
/// spell once enough of them joined, and the end of the ritual (vmangos GameObject::AddUniqueUse, RemoveUniqueUse, FinishRitual,
/// GameObject.cpp:739-831, and GameObject::Use, GameObject.cpp:1733-1796, 1993-2027; behaviour re-implemented, no code copied).
/// </summary>
public sealed partial class GameObjectMapSystem
{
    /// <summary>summoningRitual columns (GameObjectDefines.h:403-414).</summary>
    public const int RitualParticipantsData = 0;

    public const int RitualSpellData = 1;

    public const int RitualAnimSpellData = 2;

    public const int RitualPersistentData = 3;

    public const int RitualCasterTargetSpellData = 4;

    public const int RitualCastersGroupedData = 6;

    /// <summary>GameObject::Use (GameObject.cpp:2010-2016): only the warlock's Summoning Portal sends its spell at the summon target.</summary>
    public const uint PlayerSummoningRitualEntry = 36727;

    /// <summary>
    /// Spell::EffectTransmitted for a ritual created by a player (SpellEffects.cpp:5746-5762): the caster's selection becomes the summon target
    /// and the caster the first participant.
    /// </summary>
    public void BeginRitual(GameObject ritual, Player owner, ObjectGuid summonTarget)
    {
        ArgumentNullException.ThrowIfNull(ritual);
        ArgumentNullException.ThrowIfNull(owner);
        ritual.SummonTarget = summonTarget;
        AddUniqueUse(ritual, owner);
    }

    /// <summary>
    /// GameObject::AddUniqueUse (GameObject.cpp:739-772): the use is counted; a new participant is remembered (the first one as the first user)
    /// and, on a ritual with an animation spell, any participant other than the owner starts channelling it at the ritual.
    /// </summary>
    internal void AddUniqueUse(GameObject go, Player player)
    {
        go.UseCount++;
        if (!go.UniqueUsers.Add(player.Guid))
        {
            return;
        }

        if (go.FirstUser.IsEmpty)
        {
            go.FirstUser = player.Guid;
        }

        if (go.Type == GameObjectType.SummoningRitual && go.Template.GetData(RitualAnimSpellData) is var anim and not 0 && go.OwnerGuid != player.Guid)
        {
            Spells?.StartRitualAnimation(player, anim, go);
        }
    }

    /// <summary>
    /// GAMEOBJECT_TYPE_SUMMONING_RITUAL use (GameObject.cpp:1733-1796): a ritual with an owner takes helpers only (never the owner), from the
    /// owner's raid when castersGrouped (data6) is set, and only while the owner channels; a wild ritual takes anyone, grouped with its first
    /// user when castersGrouped. The participant is counted; once reqParticipants (data0) took part, the ritual turns active and its spell (data1)
    /// is cast by the owner (by the first user for a wild ritual), triggered, at the ritual, aimed at the summon target for the warlock portal.
    /// A spell that went off finishes the ritual.
    /// </summary>
    private GameObjectUseResult UseRitual(Player player, GameObject go)
    {
        Unit caster = player;
        bool grouped = go.Template.GetData(RitualCastersGroupedData) != 0;
        if (OwnerOf(go) is { } owner)
        {
            if (owner is not Player ownerPlayer || ReferenceEquals(player, ownerPlayer) || (grouped && !InSameRaid(player, ownerPlayer)))
            {
                return GameObjectUseResult.NotUsable;
            }

            if (Spells?.IsChanneling(ownerPlayer) != true)
            {
                return GameObjectUseResult.NotUsable;
            }

            caster = ownerPlayer;
        }
        else if (!go.FirstUser.IsEmpty && player.Guid != go.FirstUser && grouped
            && (Map.FindObject(go.FirstUser) is not Player first || !InSameRaid(player, first)))
        {
            // Limit: vmangos asks the user's group whether the first user is a member, which works while the first user is offline; here the
            // first user must be in this map to be found.
            return GameObjectUseResult.NotUsable;
        }

        AddUniqueUse(go, player);
        uint spellId = go.Template.GetData(RitualSpellData);
        if (go.UniqueUsers.Count < go.Template.GetData(RitualParticipantsData) || spellId == 0 || go.State == GameObjectState.Active)
        {
            return GameObjectUseResult.Ok;
        }

        if (go.OwnerGuid.IsEmpty && Map.FindObject(go.FirstUser) is Player firstUser)
        {
            caster = firstUser;
        }

        go.State = GameObjectState.Active;
        ObjectGuid summonTarget = go.Entry == PlayerSummoningRitualEntry ? go.SummonTarget : default;
        if (Spells?.CastRitualSpell(go, spellId, caster, summonTarget) == true)
        {
            FinishRitual(go);
        }

        return GameObjectUseResult.Ok;
    }

    /// <summary>
    /// GameObject::FinishRitual (GameObject.cpp:803-831), once the ritual spell went off: the owner takes the cooldown of the spell that created the
    /// ritual, a ritual that is not persistent (data3) is used up, and the caster-target spell (data4, the Ritual of Doom sacrifice) is cast by a
    /// random participant on himself. Participants are drawn in guid order for a deterministic seed.
    /// </summary>
    internal void FinishRitual(GameObject go)
    {
        if (OwnerOf(go) is Player owner && go.SpellId != 0)
        {
            Spells?.StartCreatingSpellCooldown(owner, go.SpellId);
        }

        if (go.Template.GetData(RitualPersistentData) == 0)
        {
            go.LootState = GameObjectLootState.JustDeactivated;
        }

        if (go.Template.GetData(RitualCasterTargetSpellData) is var sacrifice and not 0 && go.UniqueUsers.Count > 0)
        {
            ObjectGuid[] users = [.. go.UniqueUsers.OrderBy(g => g.Value)];
            if (Map.FindObject(users[Random.Next(users.Length)]) is Player victim)
            {
                Spells?.Cast(go, sacrifice, victim, victim);
            }
        }
    }

    /// <summary>
    /// GameObject::RemoveUniqueUse (GameObject.cpp:774-801), when a participant stopped channelling: when the owner left, or too few helpers remain
    /// while the ritual is active, a ritual that is not persistent goes (at once unless it is active: an active ritual stays until its spell ends),
    /// and the ritual is ready again.
    /// </summary>
    internal void RemoveUniqueUse(GameObject go, Player player)
    {
        if (!go.UniqueUsers.Remove(player.Guid))
        {
            return;
        }

        bool ownerLeft = player.Guid == go.OwnerGuid;
        if (!ownerLeft && !(go.State == GameObjectState.Active && go.UniqueUsers.Count < go.Template.GetData(RitualParticipantsData)))
        {
            return;
        }

        if (go.Template.GetData(RitualPersistentData) == 0 && go.State != GameObjectState.Active)
        {
            go.LootState = GameObjectLootState.JustDeactivated;
        }

        go.State = GameObjectState.Ready;
    }

    /// <summary>
    /// Unit::RemoveAllGameObjects when <paramref name="player"/> leaves the map (Unit.cpp:4159-4170): the rituals the player created go, and the
    /// rituals the player took part in lose the participant.
    /// </summary>
    private void OnRitualParticipantLeft(Player player)
    {
        foreach (GameObject go in _objects.Values.Where(g => g.Type == GameObjectType.SummoningRitual).ToArray())
        {
            if (go.OwnerGuid == player.Guid)
            {
                Remove(go);
            }
            else if (go.LootState != GameObjectLootState.JustDeactivated)
            {
                RemoveUniqueUse(go, player);
            }
        }
    }

    /// <summary>
    /// A channel of <paramref name="player"/> ended (Spell::cancel, Spell.cpp:3560-3597, and the channel end of Spell::update, :4785-4796): every
    /// ritual of this map the player took part in loses the participant (<see cref="RemoveUniqueUse"/>), and a ritual the player created with that
    /// spell goes with the channel unless its ritual spell already went off (Unit::RemoveGameObject(spellId, true)).
    /// </summary>
    public void OnChannelEnded(Player player, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(player);
        foreach (GameObject go in _objects.Values.Where(g => g.Type == GameObjectType.SummoningRitual).ToArray())
        {
            bool created = go.OwnerGuid == player.Guid && go.SpellId == spellId;
            bool helper = go.Template.GetData(RitualAnimSpellData) == spellId && go.UniqueUsers.Contains(player.Guid);
            if (!created && !helper)
            {
                continue;
            }

            if (go.LootState != GameObjectLootState.JustDeactivated)
            {
                RemoveUniqueUse(go, player);
            }

            if (created && Tracks(go) && go.State != GameObjectState.Active)
            {
                Remove(go);
            }
        }
    }
}
