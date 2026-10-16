using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Aquementas (entry 9453, Tanaris): mangos-classic ScriptDev2 <c>mob_aquementas</c> (kalimdor/tanaris.cpp:37-133, registered :411-414). He
/// spawns friendly (faction 35) and turns into an elemental (faction 91) ten seconds later (Reset :64-71 and UpdateAI :96-105). When he
/// aggroes he yells (-1000168) and, for the player who pulled him (or the owner of the pet that did), makes the Silver Totem of Aquementas
/// (11522) if the player has the Book of Aquor (11169), eleven Silvery Claws (11172) and the Irontree Heart (11173) and no totem yet
/// (SendItem, :73-83). In combat he casts Frost Shock (15089) on his victim every 15 s, the first after 1 s, and Aqua Jet (13586) on himself
/// every 15 s, the first after 5 s (:107-124); a timer fires when it is below the elapsed time and is otherwise counted down, as in the source.
/// </summary>
public sealed class AquementasAI(Creature creature) : ScriptedAI(creature)
{
    public const uint Entry = 9453, SpellAquaJet = 13586, SpellFrostShock = 15089;
    public const uint ItemSilverTotem = 11522, ItemBookOfAquor = 11169, ItemSilveryClaws = 11172, ItemIrontreeHeart = 11173;
    public const uint FactionFriendly = 35, FactionElemental = 91;
    public const int SayAggro = -1000168;
    public const uint SwitchFactionMs = 10_000, ClawsRequired = 11;

    private uint _switchMs, _frostShockMs, _aquaJetMs;

    protected override void Reset()
    {
        _switchMs = SwitchFactionMs;
        _aquaJetMs = 5000;
        _frostShockMs = 1000;
        Me.FactionTemplate = FactionFriendly;
    }

    /// <summary>Aggro (:85-92).</summary>
    public override void OnAggro(Unit target)
    {
        ArgumentNullException.ThrowIfNull(target);
        System?.SayText(Me, SayAggro, target);
        if (target.GetCharmerOrOwnerPlayerOrSelf() is { } player)
        {
            SendItem(player);
        }
    }

    /// <summary>SendItem (:73-83): the totem, when the three quest pieces are held and the totem is not.</summary>
    private static void SendItem(Player player)
    {
        if (Has(ItemBookOfAquor, 1) && Has(ItemSilveryClaws, ClawsRequired) && Has(ItemIrontreeHeart, 1) && !Has(ItemSilverTotem, 1))
        {
            player.Inventory.AddItem(ItemSilverTotem, 1, out _, received: true);
        }

        bool Has(uint entry, uint count) => player.Inventory.GetItemCount(entry) >= count;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_switchMs != 0)
        {
            if (_switchMs <= diffMs)
            {
                Me.FactionTemplate = FactionElemental;
                _switchMs = 0;
            }
            else
            {
                _switchMs -= diffMs;
            }
        }

        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        if (_frostShockMs < diffMs)
        {
            DoCast(victim, SpellFrostShock);
            _frostShockMs = 15_000;
        }
        else
        {
            _frostShockMs -= diffMs;
        }

        if (_aquaJetMs < diffMs)
        {
            DoCast(Me, SpellAquaJet);
            _aquaJetMs = 15_000;
        }
        else
        {
            _aquaJetMs -= diffMs;
        }
    }
}
