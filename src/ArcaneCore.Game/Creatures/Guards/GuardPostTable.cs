using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures;

/// <summary>What <see cref="GuardPostTable.TryUse"/> decided for a call for guards.</summary>
public enum GuardPostUse
{
    /// <summary>The area has no guard post: the caller falls back to the nearest friendly guard (vmangos CallNearestGuard).</summary>
    NoPost,

    /// <summary>The post is cooling down or has no charge left: nothing happens and the caller may try again.</summary>
    Unavailable,

    /// <summary>A charge was spent: the caller speaks its call and summons <see cref="GuardPostCall.GuardEntry"/> (0: none for that team).</summary>
    Used,
}

/// <summary>The outcome of one call at a guard post.</summary>
public readonly record struct GuardPostCall(GuardPostUse Use, uint GuardEntry);

/// <summary>
/// vmangos GuardMgr (src/game/GuardMgr.cpp, re-implemented): the guard posts of the towns, keyed by area id, with the creature a post
/// sends for each team, and each post's charges (10, <c>GUARD_POST_MAX_CHARGES</c>) and cooldown (10 s after a use,
/// <c>GUARD_POST_USE_COOLDOWN</c>); every post regains one charge per minute (<c>GUARD_POST_RECHARGE_TIME</c>, GuardMgr::Update).
/// The reference ticks one world-wide recharge timer; here a post is recharged lazily from the server clock when it is next used,
/// which gives the same count of charges at any use. The patch 1.7 elites (Sepulcher, Menethil, Hammerfall) are the build 5875 rows.
/// One table is shared by every map (it lives in <see cref="CreatureAiServices"/>); calls are serialised with a lock because maps may
/// update on different threads. The text a civilian speaks is <see cref="GetTextId"/>.
/// </summary>
public sealed class GuardPostTable
{
    /// <summary>vmangos GUARD_POST_MAX_CHARGES.</summary>
    public const uint MaxCharges = 10;

    /// <summary>vmangos GUARD_POST_USE_COOLDOWN (ms).</summary>
    public const uint UseCooldownMs = 10000;

    /// <summary>vmangos GUARD_POST_RECHARGE_TIME (ms).</summary>
    public const uint RechargeMs = 60000;

    /// <summary>vmangos: the summoned guard is a TEMPSUMMON_TIMED_OR_DEAD_DESPAWN of 2 minutes (GuardMgr.cpp:451).</summary>
    public const uint GuardDespawnMs = 120000;

    /// <summary>vmangos: the guard appears 5 yd from the civilian (GetNearPoint distance 5, GuardMgr.cpp:450).</summary>
    public const float SummonDistance = 5.0f;

    /// <summary>vmangos CallNearestGuard: the search radius for a friendly guard when the area has no post (Creature.cpp:3946).</summary>
    public const float NearestGuardRadius = 50.0f;

    // Area ids (vmangos GuardMgr.cpp GuardAreas).
    public const uint AreaRazorHill = 362;

    // Broadcast text ids (vmangos GuardMgr.cpp GuardTexts).
    public const uint TextGuardHuman = 4403;       // Guards! Help me!
    public const uint TextGuardNightElf = 4564;    // Sentinels, come to my defense!
    public const uint TextGuardOrc = 4561;         // Guards!
    public const uint TextGuardOrc2 = 4558;        // Grunts! Attack!
    public const uint TextGuardTauren = 4560;      // You will not defile our sacred land!
    public const uint TextGuardTroll = 4559;       // Guardians! Defend Sen'jin!
    public const uint TextGuardDwarf = 4583;       // Guards!
    public const uint TextGuardUndead = 4484;      // Intruders! Attack the intruders!
    public const uint TextGuardGnome = 8546;       // Help! Guards! It's going to step on me!

    private static readonly IReadOnlyDictionary<uint, (uint Alliance, uint Horde)> Posts = new Dictionary<uint, (uint, uint)>
    {
        // area id: (alliance guard entry, horde guard entry) — vmangos GuardMgr::GuardMgr (GuardMgr.cpp:205-275).
        [1519] = (68, 0),        // Stormwind City: Stormwind City Guard
        [415] = (6087, 0),       // Astranaar: Astranaar Sentinel
        [442] = (6086, 0),       // Auberdine: Auberdine Sentinel
        [380] = (0, 3501),       // The Crossroads: Horde Guard
        [362] = (0, 5953),       // Razor Hill: Razor Hill Grunt
        [513] = (4979, 0),       // Theramore: Theramore Guard
        [1116] = (7939, 0),      // Feathermoon: Feathermoon Sentinel
        [1638] = (0, 3084),      // Thunder Bluff: Bluffwatcher
        [221] = (0, 7975),       // Camp Narache: Mulgore Protector
        [222] = (0, 7975),       // Bloodhoof Village
        [215] = (0, 7975),       // Mulgore
        [1099] = (0, 8147),      // Camp Mojache: Camp Mojache Brave
        [484] = (0, 9525),       // Freewind Post: Freewind Brave
        [367] = (0, 8017),       // Sen'jin Village: Sen'jin Guardian
        [108] = (8096, 0),       // Sentinel Hill: Protector of the People
        [144] = (8055, 0),       // Thelsamar: Thelsamar Mountaineer
        [130] = (0, 7489),       // Silverpine Forest: Silverpine Deathguard
        [1497] = (0, 7980),      // Undercity: Deathguard Elite
        [131] = (727, 0),        // Kharanos: Ironforge Mountaineer
        [1537] = (5595, 0),      // Ironforge: Ironforge Guard
        [1657] = (4262, 0),      // Darnassus: Darnassus Sentinel
        [1637] = (0, 3296),      // Orgrimmar: Orgrimmar Grunt
        [11] = (1475, 0),        // Wetlands: Menethil Guard
        [15] = (4979, 10036),    // Dustwallow Marsh: Theramore Guard, Brackenwall Enforcer
        [496] = (0, 10036),      // Brackenwall Village
        [45] = (10696, 2621),    // Arathi Highlands: Refuge Pointe Defender, Hammerfall Guardian
        [320] = (10696, 0),      // Refuge Pointe
        [159] = (0, 7980),       // Brill: Deathguard Elite
        [85] = (0, 7980),        // Tirisfal Glades
        [154] = (0, 7980),       // Deathknell
        [267] = (2386, 2405),    // Hillsbrad Foothills: Southshore Guard, Tarren Mill Deathguard
        [271] = (2386, 0),       // Southshore
        [272] = (0, 2405),       // Tarren Mill
        [406] = (0, 7730),       // Stonetalon Mountains: Stonetalon Grunt
        [117] = (0, 1064),       // Grom'gol Base Camp: Grom'gol Grunt
        [87] = (68, 0),          // Goldshire: Stormwind City Guard
        [75] = (0, 866),         // Stonard: Stonard Grunt
        [340] = (0, 8155),       // Kargath: Kargath Grunt
        [69] = (10037, 0),       // Lakeshire: Lakeshire Guard
        [42] = (10038, 0),       // Darkshire: Night Watch Guard
        [186] = (3571, 0),       // Dolanaar: Teldrassil Sentinel
        [141] = (3571, 0),       // Teldrassil
        [702] = (4262, 0),       // Rut'theran Village: Darnassus Sentinel
        [2361] = (11822, 11822), // Nighthaven: Moonglade Warden
        [3317] = (0, 14730),     // Revantusk Village: Revantusk Watcher
        [2268] = (16378, 16378), // Light's Hope Chapel: Argent Sentry
        [188] = (12160, 0),      // Shadowglen: Shadowglen Sentinel
        [9] = (1642, 0),         // Northshire Valley: Northshire Guard
        [132] = (853, 0),        // Coldridge Valley: Coldridge Mountaineer
        [363] = (0, 5952),       // Valley of Trials: Den Grunt
        [35] = (4624, 4624),     // Booty Bay: Booty Bay Bruiser
        [2255] = (11190, 11190), // Everlook: Everlook Bruiser
        [976] = (9460, 9460),    // Gadgetzan: Gadgetzan Bruiser
        [392] = (3502, 3502),    // Ratchet: Ratchet Bruiser
        [228] = (0, 15138),      // The Sepulcher: Silverpine Elite (patch 1.7+, GuardMgr.cpp:263-268)
        [150] = (15137, 0),      // Menethil Harbor: Menethil Elite
        [321] = (0, 15136),      // Hammerfall: Hammerfall Elite
    };

    private readonly object _gate = new();
    private readonly Dictionary<uint, PostState> _state = [];

    /// <summary>Whether <paramref name="areaId"/> has a guard post.</summary>
    public static bool HasPost(uint areaId) => Posts.ContainsKey(areaId);

    /// <summary>The guard entry a post sends against <paramref name="guardTeam"/>'s enemies, 0 for none (AreaGuardInfo::GetCreatureIdForTeam).</summary>
    public static uint GuardEntryFor(uint areaId, Team guardTeam)
        => Posts.TryGetValue(areaId, out (uint Alliance, uint Horde) post) ? (guardTeam == Team.Alliance ? post.Alliance : post.Horde) : 0;

    /// <summary>
    /// vmangos GuardMgr::SummonGuard, the bookkeeping half (GuardMgr.cpp:421-441): an area without a post answers
    /// <see cref="GuardPostUse.NoPost"/>; a post that is cooling down or out of charges answers <see cref="GuardPostUse.Unavailable"/>;
    /// otherwise a charge is spent, the 10 s cooldown starts and the guard entry for <paramref name="guardTeam"/> (null: none) comes back.
    /// <paramref name="nowMs"/> is the server clock.
    /// </summary>
    public GuardPostCall TryUse(uint areaId, Team? guardTeam, long nowMs)
    {
        if (!Posts.TryGetValue(areaId, out (uint Alliance, uint Horde) post))
        {
            return new GuardPostCall(GuardPostUse.NoPost, 0);
        }

        lock (_gate)
        {
            if (!_state.TryGetValue(areaId, out PostState? state))
            {
                _state[areaId] = state = new PostState { Charges = MaxCharges, RechargeEpoch = nowMs / RechargeMs };
            }

            long epoch = nowMs / RechargeMs;
            if (epoch > state.RechargeEpoch)
            {
                state.Charges = (uint)Math.Min(MaxCharges, state.Charges + (epoch - state.RechargeEpoch));
                state.RechargeEpoch = epoch;
            }

            if (nowMs < state.CooldownUntilMs || state.Charges == 0)
            {
                return new GuardPostCall(GuardPostUse.Unavailable, 0);
            }

            state.Charges--;
            state.CooldownUntilMs = nowMs + UseCooldownMs;
        }

        uint entry = guardTeam switch
        {
            Team.Alliance => post.Alliance,
            Team.Horde => post.Horde,
            _ => 0,
        };
        return new GuardPostCall(GuardPostUse.Used, entry);
    }

    /// <summary>The charges a post has left (for tests and GM inspection); <see cref="MaxCharges"/> for a post never used.</summary>
    public uint ChargesOf(uint areaId)
    {
        lock (_gate)
        {
            return _state.TryGetValue(areaId, out PostState? state) ? state.Charges : MaxCharges;
        }
    }

    /// <summary>
    /// vmangos GuardMgr::GetTextId (GuardMgr.cpp:299-404): what a civilian shouts. Razor Hill always says "Grunts! Attack!"; otherwise the
    /// text follows the civilian's model (<paramref name="modelId"/>, CreatureDisplayInfo.dbc ModelId; null when the metadata is not
    /// installed), else its faction template; 0 says nothing.
    /// </summary>
    public static uint GetTextId(uint factionTemplateId, uint areaId, uint? modelId)
    {
        if (areaId == AreaRazorHill)
        {
            return TextGuardOrc2;
        }

        switch (modelId)
        {
            case 49 or 50: return TextGuardHuman;      // MODEL_HUMAN_MALE / FEMALE (Objects/UnitDefines.h:816-831)
            case 51 or 52: return TextGuardOrc;
            case 53 or 54: return TextGuardDwarf;
            case 55 or 56: return TextGuardNightElf;
            case 57 or 58: return TextGuardUndead;
            case 59 or 60: return TextGuardTauren;
            case 182 or 183: return TextGuardGnome;
            case 185 or 186: return TextGuardTroll;
        }

        return factionTemplateId switch
        {
            // Stormwind, the Night Watch, Alliance generic, Theramore, League of Arathor, Silvermoon Remnant.
            11 or 12 or 123 or 1078 or 1575 or 53 or 56 or 84 or 210 or 534 or 1315 or 149 or 150 or 151 or 894 or 1075 or 1077 or 1096
                or 1577 or 371 or 1576 => TextGuardHuman,
            // Orgrimmar, Horde generic, Frostwolf Clan, Warsong Outriders.
            29 or 65 or 85 or 125 or 1074 or 1174 or 1595 or 1612 or 1619 or 83 or 106 or 714 or 1034 or 1314 or 1215 or 1515 => TextGuardOrc,
            // Ironforge, Wildhammer Clan, Stormpike Guard.
            55 or 57 or 122 or 1611 or 1618 or 694 or 1054 or 1055 or 1217 => TextGuardDwarf,
            // Gnomeregan Exiles.
            23 or 64 or 875 => TextGuardGnome,
            // Undercity, the Defilers.
            68 or 71 or 98 or 118 or 1134 or 1154 or 412 => TextGuardUndead,
            // Darnassus, Silverwing Sentinels.
            79 or 80 or 124 or 1076 or 1097 or 1594 or 1600 or 1514 => TextGuardNightElf,
            // Thunder Bluff.
            104 or 105 or 995 => TextGuardTauren,
            // Darkspear Trolls.
            126 or 876 or 877 => TextGuardTroll,
            _ => 0,
        };
    }

    private sealed class PostState
    {
        public uint Charges { get; set; }

        public long RechargeEpoch { get; set; }

        public long CooldownUntilMs { get; set; }
    }
}
