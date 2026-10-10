using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.Stratholme;

/// <summary>mobs_spectral_ghostly_citizenAI (mangos-classic eastern_kingdoms/stratholme/stratholmeScripts.cpp:205-300), ClassicDB ScriptName
/// on Spectral Citizen 10384 and Ghostly Citizen 10385: Incorporeal Defense, Egan's Blaster frees the soul (quest 5282), emote replies.</summary>
public sealed class SpectralGhostlyCitizenAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellIncorporealDefense = 16331, SpellSlap = 6754, SpellEganBlaster = 17368, SpellSoulFreed = 17370,
        SpellSummonFreedSoul = 17408;
    // EmotesText.dbc / Emotes.dbc ids; they match the vanilla TextEmote and Emote enums in tests/ThirdParty/WowWorldMessages.
    public const uint TextEmoteBow = 17, TextEmoteDance = 34, TextEmoteKiss = 58, TextEmoteRude = 77, TextEmoteWave = 101;
    public const uint EmoteOneshotBow = 2, EmoteOneshotWave = 3, EmoteOneshotRude = 14, EmoteOneshotFlex = 23;
    private const float InteractionDistance = 5f;

    private uint _dieMs = 2_000;
    private bool _tagged;

    public bool Tagged => _tagged;

    public override void OnRespawn()
    {
        _dieMs = 2_000;
        _tagged = false;
        CombatMovement = true;
        ApplyDefense();
    }

    public override void OnEvade() => ApplyDefense();

    // Reset(): DoCastSpellIfCan(nullptr, SPELL_INCORPOREAL_DEFENSE, CAST_TRIGGERED | CAST_AURA_NOT_PRESENT).
    private void ApplyDefense()
    {
        if (System?.HasAura(Me, SpellIncorporealDefense) != true)
        {
            DoCast(Me, SpellIncorporealDefense, triggered: true);
        }
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        if (_tagged || spell.Id != SpellEganBlaster)
        {
            return;
        }

        _tagged = true;
        DoCast(null, SpellSoulFreed);
        CombatMovement = false;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_tagged)
        {
            if (_dieMs < diffMs)
            {
                DoCast(null, SpellSummonFreedSoul, triggered: true);
                System?.ForcedDespawn(Me, 0);
                CombatMovement = true;
                _tagged = false;
                return;
            }

            _dieMs -= diffMs;
        }

        UpdateVictim();
    }

    private bool WithinInteraction(Player player)
    {
        float dx = Me.X - player.X, dy = Me.Y - player.Y, dz = Me.Z - player.Z;
        return (dx * dx) + (dy * dy) + (dz * dz) <= InteractionDistance * InteractionDistance;
    }

    public override void OnReceiveEmote(Player player, uint textEmote)
    {
        switch (textEmote)
        {
            case TextEmoteDance:
                EnterEvadeMode();
                break;
            case TextEmoteRude:
                if (WithinInteraction(player))
                {
                    DoCast(player, SpellSlap);
                }
                else
                {
                    System?.PlayEmote(Me, EmoteOneshotRude);
                }

                break;
            case TextEmoteWave:
                System?.PlayEmote(Me, EmoteOneshotWave);
                break;
            case TextEmoteBow:
                System?.PlayEmote(Me, EmoteOneshotBow);
                break;
            case TextEmoteKiss:
                System?.PlayEmote(Me, EmoteOneshotFlex);
                break;
        }
    }
}
