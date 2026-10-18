using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Restless Soul (11122, Stratholme), quest 5282 "The Restless Souls": vmangos <c>mob_restless_soul</c> and <c>mob_freed_soul</c>
/// (scripts/eastern_kingdoms/eastern_plaguelands/stratholme/stratholme.cpp). A player on the quest tags the soul with Egan's Blaster (17368).
/// Five seconds later the player gets the soul's kill credit and the soul dies. Its death leaves a Freed Soul (11136) for five minutes, which
/// casts Soul Freed (17370), says one of broadcast texts 6451-6455 and follows the player.
/// </summary>
public sealed class RestlessSoulAI(Creature creature) : CreatureAI(creature)
{
    public const uint Entry = 11122, NpcFreedSoul = 11136, SpellEganBlaster = 17368, SpellSoulFreed = 17370, QuestRestlessSoul = 5282;
    private const uint DieAfterMs = 5000, FreedSoulLifetimeMs = 300_000;

    private ObjectGuid _tagger;
    private uint _dieMs = DieAfterMs;
    private bool _tagged;

    public bool Tagged => _tagged;
    public Creature? FreedSoul { get; private set; }

    public override void OnRespawn()
    {
        _tagger = ObjectGuid.Empty;
        _dieMs = DieAfterMs;
        _tagged = false;
        FreedSoul = null;
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        base.OnSpellHit(caster, spell);
        if (!_tagged && spell.Id == SpellEganBlaster && caster is Player player && System?.QuestIncomplete(player, QuestRestlessSoul) == true)
        {
            _tagged = true;
            _tagger = player.Guid;
        }
    }

    public override void OnDeath(Unit? killer)
    {
        base.OnDeath(killer);
        if (!_tagged || System is not { } system)
        {
            return;
        }

        // TEMPSUMMON_TIMED_DESPAWN 300000 at the soul; JustSummoned: Soul Freed, then follow the tagger. mob_freed_soul's Reset says the line.
        if (system.SummonAt(Me, NpcFreedSoul, Me.X, Me.Y, Me.Z, Me.Orientation, null, FreedSoulLifetimeMs) is not { } freed)
        {
            return;
        }

        FreedSoul = freed;
        system.CastSpell(freed, SpellSoulFreed, freed, triggered: false);
        system.SayText(freed, system.RandomInt(6451, 6455));
        if (system.Map.FindPlayer(_tagger) is { } player)
        {
            freed.Motion.MoveFollow(player, FollowerAI.FollowDistance, FollowerAI.FollowAngle);
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_tagged && Me.IsAlive)
        {
            if (_dieMs < diffMs)
            {
                if (System?.Map.FindPlayer(_tagger) is { } player)
                {
                    System.KilledMonsterCredit(player, Me.Entry, Me.Guid);
                }

                Me.Map?.Combat.Kill(Me, Me);
                return;
            }

            _dieMs -= diffMs;
        }

        UpdateVictim();
    }
}
