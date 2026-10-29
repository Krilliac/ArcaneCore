using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Rules.Creatures;

/// <summary>
/// The creature AI that runs a creature's rules (the engine-backed counterpart of <see cref="CreatureEventAI"/>): the same hooks, the same
/// combat-movement, ranged-mode and melee state, but the events fire through a <see cref="CreatureRuleRunner"/> over the <c>creature</c>
/// host's rules instead of through EventAI rows. It derives from <see cref="CreatureEventAI"/> (with no rows) so the map system's
/// <c>CreatureEventAI</c> checks (ranged mode, chase distance) keep working and the actions reach the AI state they change; the cutover
/// lane folds that state into this class when it deletes EventAI. Register it with <see cref="Register"/>.
/// </summary>
public sealed class CreatureRuleAI : CreatureEventAI
{
    /// <summary>The <c>AIName</c> the rule AI is registered under by default.</summary>
    public const string AiName = "RuleAI";

    private readonly CreatureRuleRunner _runner;

    public CreatureRuleAI(Creature creature, RuleCatalog catalog)
        : base(creature, CreatureAiContent.Empty)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _runner = new CreatureRuleRunner(this, catalog);
    }

    /// <summary>The runner that fires this creature's rules.</summary>
    public CreatureRuleRunner Runner => _runner;

    /// <summary>Register the rule AI under <paramref name="name"/> in <paramref name="factory"/>, running the rules of <paramref name="catalog"/>.</summary>
    public static void Register(CreatureAiFactory factory, RuleCatalog catalog, string name = AiName)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(catalog);
        factory.Register(name, creature => new CreatureRuleAI(creature, catalog));
    }

    public override void MoveInLineOfSight(Unit who)
    {
        _runner.MoveInLineOfSight(who); // cmangos: the OOC LOS events first, then UnitAI::MoveInLineOfSight
        base.MoveInLineOfSight(who);
    }

    public override void OnRespawn()
    {
        base.OnRespawn();
        _runner.Respawn();
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        _runner.EnterCombat(target);
    }

    public override void OnDeath(Unit? killer)
    {
        base.OnDeath(killer);
        _runner.Death(killer);
    }

    public override void OnKilledUnit(Unit victim)
    {
        base.OnKilledUnit(victim);
        _runner.Kill(victim);
    }

    public override void OnReceiveEmote(Player player, uint textEmote)
    {
        base.OnReceiveEmote(player, textEmote);
        _runner.ReceiveEmote(player, textEmote);
    }

    public override void OnReceiveAiEvent(uint eventType, Unit sender, Unit? invoker, uint miscValue)
    {
        base.OnReceiveAiEvent(eventType, sender, invoker, miscValue);
        _runner.ReceiveAiEvent(eventType, sender, invoker);
    }

    public override void OnEvade()
    {
        base.OnEvade();
        _runner.Evade();
    }

    public override void OnReachedHome()
    {
        base.OnReachedHome();
        _runner.ReachedHome();
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        base.OnSpellHit(caster, spell);
        _runner.SpellHit(caster, spell);
    }

    public override void OnSpellHitTarget(Unit target, SpellInfo spell)
    {
        base.OnSpellHitTarget(target, spell);
        _runner.SpellHitTarget(target, spell);
    }

    public override void OnJustSummoned(Creature summoned)
    {
        base.OnJustSummoned(summoned);
        _runner.JustSummoned(summoned);
    }

    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        base.OnSummonedCreatureJustDied(summoned);
        _runner.SummonedJustDied(summoned);
    }

    public override void OnSummonedCreatureDespawn(Creature summoned)
    {
        base.OnSummonedCreatureDespawn(summoned);
        _runner.SummonedDespawned(summoned);
    }

    /// <summary>cmangos UnitAI::UpdateAI: choose the victim, then the rule timer batch.</summary>
    public override void OnUpdate(uint diffMs)
    {
        base.OnUpdate(diffMs);
        if (Me.IsAlive)
        {
            _runner.Update(diffMs);
        }
    }
}
