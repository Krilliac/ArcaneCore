using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>
/// One cast's units: a fresh caster of the spell's class, the target its implicit targets ask for, and any helper a driver needs.
/// Every unit is removed again on dispose (and any creature the spell spawned), so no state carries from one spell to the next.
/// </summary>
internal sealed class SweepScene : IDisposable
{
    private readonly List<Player> _players = [];
    private readonly List<Creature> _creatures = [];
    private readonly HashSet<Creature> _before;
    private Creature? _striker;
    private Player? _enemyPlayer;
    private WorldSession? _session;

    public SweepScene(SpellSweepRig rig, SpellInfo spell, Class cls, byte level, SweepTargetKind kind, float targetDistance = 5f)
    {
        Rig = rig;
        Spell = spell;
        Kind = kind;
        _before = [.. rig.Creatures.Creatures];
        Caster = AddPlayer(cls, level, 0, 0);
        switch (kind)
        {
            case SweepTargetKind.Hostile:
                Creature mob = AddCreature(hostile: true, level, targetDistance, 0);
                Target = mob;
                Targets = SpellCastTargets.ForUnit(mob.Guid);
                break;
            case SweepTargetKind.Friendly:
                Player friend = AddPlayer(cls, level, targetDistance, 0);
                Target = friend;
                Targets = SpellCastTargets.ForUnit(friend.Guid);
                break;
            case SweepTargetKind.Location:
                Target = Caster;
                Targets = new SpellCastTargets
                {
                    Mask = SpellCastTargetFlags.DestLocation, Dest = (Caster.X + targetDistance, Caster.Y, Caster.Z),
                };
                break;
            default:
                // Self, and game object targets (no game object system state is staged; the cast meets its own target check).
                Target = Caster;
                Targets = SpellCastTargets.ForSelf();
                break;
        }

        // A hostile creature in sight makes enemy-near-caster and enemy area effects find a victim too.
        if (kind != SweepTargetKind.Hostile)
        {
            Bystander = AddCreature(hostile: true, level, 8f, 3f);
        }
    }

    public SpellSweepRig Rig { get; }

    public SpellInfo Spell { get; }

    public SweepTargetKind Kind { get; }

    public Player Caster { get; }

    public Unit Target { get; }

    public Creature? Bystander { get; }

    public SpellCastTargets Targets { get; }

    /// <summary>Every unit of the scene the spell could have put an aura on.</summary>
    public IEnumerable<Unit> Units => _players.Cast<Unit>().Concat(_creatures);

    /// <summary>A session for the production movement path (the handlers read its world, services and logger: the rig's capture, so a fault logged there counts).</summary>
    public WorldSession Session => _session ??= new WorldSession(new MemoryStream(), "spell-sweep", Rig.Services, Rig.Opcodes, Rig.World,
        Rig.Registry, new WorldSessionOptions(), Rig.Services.GetRequiredService<ILoggerFactory>().CreateLogger("spell-sweep.session"));

    /// <summary>A hostile creature next to the scene that attacks or is attacked by the drivers.</summary>
    public Creature Striker() => _striker ??= AddCreature(hostile: true, 60, -2f, 0);

    /// <summary>A player of the other faction (an enemy of the creature drivers' victims that is not the caster).</summary>
    public Player EnemyPlayer() => _enemyPlayer ??= AddPlayer(Class.Warrior, 60, -2f, 0, Race.Orc);

    public Player AddPlayer(Class cls, byte level, float dx, float dy, Race? race = null)
    {
        Player player = Rig.AddPlayer(cls, level, dx, dy, race);
        _players.Add(player);
        return player;
    }

    public Creature AddCreature(bool hostile, byte level, float dx, float dy)
    {
        Creature creature = Rig.AddCreature(hostile, level, dx, dy);
        _creatures.Add(creature);
        return creature;
    }

    public void Dispose()
    {
        foreach (Player player in _players)
        {
            Rig.RemovePlayer(player);
        }

        foreach (Creature creature in _creatures.Concat(Rig.Creatures.Creatures.Where(c => !_before.Contains(c)).ToArray()).Distinct())
        {
            Rig.RemoveCreature(creature);
        }

        Rig.World.RunTick(0);
    }
}
