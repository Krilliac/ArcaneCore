using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData.WorldState;

namespace ArcaneCore.World.WorldState;

/// <summary>scourge_invasion.cpp SummonCultists / DespawnCultists / DespawnShadowsOfDoom, run by a damaged shard every hour.</summary>
internal static class ScourgeButtress
{
    public const float InspectDistance = 28f; // cmangos INSPECT_DISTANCE

    /// <summary>
    /// The damaged shard's hourly buttress (placeholder for 27888 in the reference): full health, out-of-combat Shadows of Doom within 200 yd
    /// go, old summoner shields go, and four Cultist Engineers are placed on the circle (6.95 x 6.75 yd ellipse, facing in).
    /// </summary>
    public static int Run(Creature shard, CreatureMapSystem creatures)
    {
        shard.Health = shard.MaxHealth;
        foreach (Creature shadow in creatures.CreaturesOfEntryInRange(shard, ScourgeInvasionCatalog.ShadowOfDoom, 200f)
                     .Where(c => c.IsAlive && !c.Combat.IsInCombat).ToArray())
            creatures.ForcedDespawn(shadow, 0);
        foreach (Creature old in creatures.CreaturesOfEntryInRange(shard, ScourgeInvasionCatalog.CultistEngineer, InspectDistance)
                     .Where(c => c.IsAlive).ToArray())
            creatures.ForcedDespawn(old, 0);
        if (shard.Map?.FindUpdater<GameObjectMapSystem>() is { } objects)
            foreach (GameObject shield in objects.GameObjects.Where(g => g.Entry == ScourgeInvasionCatalog.SummonerShield
                         && InvasionCircleAi.DistanceSquared(g, shard) <= InspectDistance * InspectDistance).ToArray())
                objects.Remove(shield);

        GameObject? circle = shard.Map?.FindUpdater<GameObjectMapSystem>()?.GameObjects
            .Where(g => g.IsSpawned && g.Entry == ScourgeInvasionCatalog.SummonCircle && InvasionCircleAi.DistanceSquared(g, shard) <= 9f)
            .MinBy(g => InvasionCircleAi.DistanceSquared(g, shard));
        if (circle is null) return 0;
        int placed = 0;
        for (int i = 0; i < 4; i++)
        {
            float angle = i * (MathF.PI / 2f) + circle.Orientation;
            float x = circle.X + 6.95f * MathF.Cos(angle), y = circle.Y + 6.75f * MathF.Sin(angle);
            if (creatures.SummonAt(shard, ScourgeInvasionCatalog.CultistEngineer, x, y, circle.Z, angle - MathF.PI, null, 3_600_000) is
                { AI: CultistEngineerAi cultist })
            {
                cultist.Begin(shard);
                placed++;
            }
        }
        return placed;
    }
}

/// <summary>
/// npc_cultist_engineer: passive; on placement it raises a Summoner Shield (181142), shows the spawn-in visual and a second later channels
/// Buttress (28078) into its shard. Its death damages the shard (28041) and removes its shield. Selecting its gossip line with eight
/// Necrotic Runes summons a Shadow of Doom for that player and the cultist dies (31315 + 3617).
/// </summary>
internal sealed class CultistEngineerAi(Creature creature) : CreatureAI(creature)
{
    private Creature? _shard;
    private uint _channelMs;
    public GameObject? Shield { get; private set; }
    public bool Channelling { get; private set; }

    public override bool AttackStart(Unit target) => false;
    public override void MoveInLineOfSight(Unit who) { }

    public void Begin(Creature shard)
    {
        _shard = shard;
        Shield = Me.Map?.FindUpdater<GameObjectMapSystem>()?.Summon(ScourgeInvasionCatalog.SummonerShield, Me.X, Me.Y, Me.Z, Me.Orientation, 3_600);
        DoCast(Me, ScourgeInvasionCatalog.MinionSpawnIn, triggered: true);
        _channelMs = 1_000;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_channelMs == 0 || !Me.IsAlive) return;
        if (_channelMs > diffMs)
        {
            _channelMs -= diffMs;
            return;
        }
        _channelMs = 0;
        Channelling = true;
        if (_shard is { IsAlive: true } shard) DoCast(shard, ScourgeInvasionCatalog.ButtressChannel, triggered: true);
    }

    public override void OnDeath(Unit? killer)
    {
        Channelling = false;
        if (System is { } system && system.CreaturesOfEntryInRange(Me, ScourgeInvasionCatalog.DamagedNecroticShard, 15f)
                .FirstOrDefault(c => c.IsAlive) is { } shard)
            system.CastSpell(shard, ScourgeInvasionCatalog.DamageCrystal, shard, triggered: true);
        if (Shield is { } shield && Me.Map?.FindUpdater<GameObjectMapSystem>() is { } objects) objects.Remove(shield);
        Shield = null;
    }
}

/// <summary>
/// ScourgeMinion for the Shadow of Doom (16143): it comes immune to players, faces and speaks to its summoner with a smoke visual, and
/// 5 s later attacks that player; in combat Mind Flay every 6.5-13 s and Fear every 14.5 s (both first after 2 s); on death it zaps the
/// nearest damaged shard (28056).
/// </summary>
internal sealed class ShadowOfDoomAi(Creature creature, Random random) : CreatureAI(creature)
{
    private Player? _summoner;
    private uint _attackMs;
    private uint _flayMs = 2_000;
    private uint _fearMs = 2_000;

    public Player? Summoner => _summoner;

    public void Summoned(Player summoner)
    {
        _summoner = summoner;
        Me.UnitFlags |= UnitFlags.ImmuneToPlayer;
        System?.SayText(Me, ScourgeInvasionCatalog.ShadowOfDoomTexts[random.Next(ScourgeInvasionCatalog.ShadowOfDoomTexts.Count)], summoner);
        DoCast(Me, ScourgeInvasionCatalog.SpawnSmoke, triggered: true);
        _attackMs = 5_000;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!Me.IsAlive) return;
        if (_attackMs > 0)
        {
            if (_attackMs > diffMs)
            {
                _attackMs -= diffMs;
                return;
            }
            _attackMs = 0;
            Me.UnitFlags &= ~UnitFlags.ImmuneToPlayer;
            if (_summoner is { IsAlive: true } player && ReferenceEquals(player.Map, Me.Map)) AttackStart(player);
        }
        UpdateVictim();
        if (!Me.Combat.IsInCombat || Victim is not { } victim) return;
        if (_flayMs > diffMs) _flayMs -= diffMs;
        else
        {
            DoCast(victim, ScourgeInvasionCatalog.MindFlay);
            _flayMs = (uint)random.Next(6_500, 13_001);
        }
        if (_fearMs > diffMs) _fearMs -= diffMs;
        else
        {
            DoCast(victim, ScourgeInvasionCatalog.Fear);
            _fearMs = 14_500;
        }
    }

    public override void OnDeath(Unit? killer)
    {
        if (System?.CreaturesOfEntryInRange(Me, ScourgeInvasionCatalog.DamagedNecroticShard, 200f).FirstOrDefault(c => c.IsAlive) is { } shard)
            DoCast(shard, ScourgeInvasionCatalog.ZapCrystalCorpse, triggered: true);
    }
}

/// <summary>The Cultist Engineer's gossip (BCT 8436 with option 12112): eight Necrotic Runes summon a Shadow of Doom for the player.</summary>
internal sealed class CultistEngineerGossip(Func<int, string?> text, Random random) : INpcGossipScript
{
    public const uint DisruptAction = 1;

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        if (npc.Entry != ScourgeInvasionCatalog.CultistEngineer) return null;
        string line = text(ScourgeInvasionCatalog.CultistGossipOption) ?? "Use 8 necrotic runes and disrupt his ritual.";
        return new ScriptedGossipMenu(false, (uint)ScourgeInvasionCatalog.CultistGossipText, [new ScriptedGossipItem(0, line, 1, DisruptAction)]);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => SelectReply(player, npc, sender, action).NpcTextId;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        if (npc.Entry == ScourgeInvasionCatalog.CultistEngineer && action == DisruptAction
            && player.Map?.FindObject(npc.Guid) is Creature cultist)
            Disrupt(player, cultist, random);
        return new ScriptedGossipReply(0, Close: true);
    }

    /// <summary>The 31315 Summon Boss reagent check, its summon (1 h) and SummonBoss::OnSummon, then the cultist's Quiet Suicide.</summary>
    public static Creature? Disrupt(Player player, Creature cultist, Random random)
    {
        if (!cultist.IsAlive || cultist.AI is not CultistEngineerAi || cultist.Map?.FindUpdater<CreatureMapSystem>() is not { } creatures
            || player.Inventory.GetItemCount(ScourgeInvasionCatalog.NecroticRune) < ScourgeInvasionCatalog.NecroticRunesForBoss)
            return null;
        float facing = MathF.Atan2(player.Y - cultist.Y, player.X - cultist.X);
        Creature? shadow = creatures.SummonInstanceCreatureTimedOocOrDead(ScourgeInvasionCatalog.ShadowOfDoom,
            cultist.X, cultist.Y, cultist.Z, facing, 3_600_000);
        if (shadow is null) return null;
        player.Inventory.DestroyItemCount(ScourgeInvasionCatalog.NecroticRune, ScourgeInvasionCatalog.NecroticRunesForBoss);
        if (shadow.AI is ShadowOfDoomAi ai) ai.Summoned(player);
        creatures.KillCreature(cultist);
        _ = random;
        return shadow;
    }
}
