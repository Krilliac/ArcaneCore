using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// mangos-classic ScriptDev2 world/guards.cpp (at 3e8597afe7): the guard_* script names. All but two are plain <c>guardAI</c>, i.e.
/// <see cref="GuardAI"/>. <c>guard_orgrimmar</c> and <c>guard_stormwind</c> also answer a text emote from their own team
/// (guardAI::DoReplyToTextEmote), and a Stormwind guard hit by Windsor's Inspiration (20275) salutes him with one of seven lines, at most
/// once every 2 minutes.
/// </summary>
public sealed class CityGuardAI(Creature creature, Team team, bool stormwind) : GuardAI(creature)
{
    public const uint SpellWindsorInspirationEffect = 20275;
    public static readonly string[] PlainScriptNames =
    [
        "guard_bluffwatcher", "guard_contested", "guard_darnassus", "guard_dunmorogh", "guard_durotar", "guard_elwynnforest",
        "guard_ironforge", "guard_mulgore", "guard_teldrassil", "guard_tirisfal", "guard_undercity",
    ];
    private static readonly int[] s_salutes = [-1000842, -1000843, -1000844, -1000845, -1000846, -1000847, -1000848];

    private uint _saluteWaitMs;

    /// <summary>guardAI::DoReplyToTextEmote: kiss → bow, wave → wave, salute → salute, shy → flex, rude/chicken → point.</summary>
    public static uint ReplyTo(uint textEmote) => textEmote switch
    {
        58 => 2,          // TEXTEMOTE_KISS → EMOTE_ONESHOT_BOW
        101 => 3,         // TEXTEMOTE_WAVE → EMOTE_ONESHOT_WAVE
        78 => 66,         // TEXTEMOTE_SALUTE → EMOTE_ONESHOT_SALUTE
        84 => 23,         // TEXTEMOTE_SHY → EMOTE_ONESHOT_FLEX
        77 or 22 => 25,   // TEXTEMOTE_RUDE, TEXTEMOTE_CHICKEN → EMOTE_ONESHOT_POINT
        _ => 0,
    };

    public override void OnReceiveEmote(Player player, uint textEmote)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Team == team && ReplyTo(textEmote) is var emote and not 0)
        {
            System?.PlayEmote(Me, emote);
        }
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (stormwind && spell.Id == SpellWindsorInspirationEffect && _saluteWaitMs == 0 && System is { } system)
        {
            system.SayText(Me, s_salutes[system.RandomInt(0, s_salutes.Length - 1)]);
            _saluteWaitMs = 120_000;
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        _saluteWaitMs = _saluteWaitMs > diffMs ? _saluteWaitMs - diffMs : 0;
        base.OnUpdate(diffMs);
    }
}
