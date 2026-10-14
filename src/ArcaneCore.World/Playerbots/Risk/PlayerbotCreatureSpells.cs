using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Playerbots;

/// <summary>What a creature's own spells add to its danger: damage per second, its hardest hit, and whether one kills outright.</summary>
internal readonly record struct CreatureSpellThreat(float Dps, int MaxHit, bool Instakill)
{
    internal static readonly CreatureSpellThreat None = new(0, 0, false);

    /// <summary>A creature whose single spell can kill the bot from <paramref name="health"/> (an instakill, or a hit that large).</summary>
    internal bool IsLethalTo(uint health) => Instakill || MaxHit > 0 && MaxHit >= health;
}

/// <summary>
/// A creature's spells as the risk estimate sees them (pure over the data, cached per spell store, EventAI content and entry): the CAST actions of
/// its EventAI rows (cmangos <c>creature_ai_scripts</c> action 11, <c>ACTION_T_CAST</c>; the creature's events in
/// <see cref="EventAiEngine.Holders"/>) and each spell's damage from <c>spell_template</c>: school damage, weapon damage, health
/// leech and the ticks of a periodic damage aura, the dice at their top; <c>SPELL_EFFECT_INSTAKILL</c> kills outright (the Scourge
/// invasion's Skeletal Soldier, Scourge Strike 28265, which killed the live replay's Dawnrover five times in ten minutes). A cast
/// repeats every <c>(Param3 + Param4) / 2</c> ms of its event (the in-combat timer's repeat), 10 s when the row gives none. C# AIs
/// and spells a creature casts any other way are not seen.
/// </summary>
internal static class PlayerbotCreatureSpells
{
    internal const byte ActionCast = 11;
    internal const uint DefaultRepeatMs = 10_000;

    /// <summary>The most entries cached per spell store and EventAI content.</summary>
    internal const int MaxCachedEntries = 4096;

    // Keyed by everything the value is computed from: the spell store, the EventAI content (a reload of either is a new object,
    // so it starts a new cache), the entry, the spawn guid when that spawn has rows of its own, and whether debug-only rows run.
    // Static and shared by every world in the process: test hosts run their world threads in parallel and all start on the same
    // SpellStore.Empty, so the per-content map must be safe for concurrent writers (a plain Dictionary written from two world
    // threads at once can corrupt itself and throw or spin).
    private static readonly ConditionalWeakTable<SpellStore, ConditionalWeakTable<CreatureAiContent, ConcurrentDictionary<CacheKey, CreatureSpellThreat>>> Cache = [];

    private readonly record struct CacheKey(uint Entry, uint SpawnGuid, byte CasterLevel, bool DebugOnlyRows);

    internal static CreatureSpellThreat Of(Creature creature, SpellStore? store)
    {
        if (store is null || creature.AI is not CreatureEventAI ai) return CreatureSpellThreat.None;
        EventAiContext context = ai.Engine.Context;
        CreatureAiContent content = context.Content;
        uint spawnGuid = creature.Spawn is { } spawn && content.GetGuidEvents(spawn.Guid).Count > 0 ? spawn.Guid : 0;
        var key = new CacheKey(creature.Entry, spawnGuid, creature.Level, context.Options.DebugOnlyEvents);
        ConcurrentDictionary<CacheKey, CreatureSpellThreat> cache = Cache.GetOrCreateValue(store).GetOrCreateValue(content);
        if (cache.TryGetValue(key, out CreatureSpellThreat known)) return known;
        CreatureSpellThreat threat = Of(ai.Engine.Holders.Select(h => h.Event), store.Get, creature.Level);
        if (cache.Count < MaxCachedEntries) cache.TryAdd(key, threat);
        return threat;
    }

    internal static CreatureSpellThreat Of(IEnumerable<CreatureAiEvent> events, Func<uint, SpellInfo?> spells, byte casterLevel = 0)
    {
        float dps = 0;
        int maxHit = 0;
        bool instakill = false;
        foreach (CreatureAiEvent row in events)
        {
            foreach (CreatureAiAction action in row.Actions)
            {
                if (action.Type != ActionCast || action.Param1 <= 0 || spells((uint)action.Param1) is not { } spell) continue;
                (int hit, bool kills) = Damage(spell, casterLevel);
                instakill |= kills;
                if (hit <= 0) continue;
                maxHit = Math.Max(maxHit, hit);
                int repeat = (Math.Max(0, row.Param3) + Math.Max(0, row.Param4)) / 2;
                dps += hit / ((repeat > 0 ? repeat : DefaultRepeatMs) / 1000f) * (row.Chance is > 0 and <= 100 ? row.Chance / 100f : 1f);
            }
        }

        return new CreatureSpellThreat(dps, maxHit, instakill);
    }

    /// <summary>One cast's damage (dice at their top; a periodic aura's every tick; scaled by <paramref name="casterLevel"/> when <c>SPELL_ATTR_SCALES_WITH_CREATURE_LEVEL</c> is set) and whether it kills outright.</summary>
    internal static (int Damage, bool Instakill) Damage(SpellInfo spell, byte casterLevel = 0)
    {
        int damage = 0;
        bool instakill = false;
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            float points = effect.BasePoints + Math.Max(1, effect.DieSides);
            if (casterLevel > 0 && SpellMath.ShouldScaleWithCreatureLevel(spell, effect))
                points *= SpellMath.CreatureLevelScaleFactor(casterLevel, spell.SpellLevel);
            int scaled = (int)points;
            switch (effect.Effect)
            {
                case SpellEffectName.Instakill:
                    instakill = true;
                    break;
                case SpellEffectName.SchoolDamage or SpellEffectName.HealthLeech or SpellEffectName.WeaponDamage
                    or SpellEffectName.WeaponDamageNoschool or SpellEffectName.NormalizedWeaponDmg:
                    damage += Math.Max(0, scaled);
                    break;
                case SpellEffectName.ApplyAura when effect.AuraType == AuraType.PeriodicDamage && effect.Amplitude > 0:
                    damage += Math.Max(0, scaled) * Math.Max(1, spell.GetDuration() / (int)effect.Amplitude);
                    break;
            }
        }

        return (damage, instakill);
    }
}
