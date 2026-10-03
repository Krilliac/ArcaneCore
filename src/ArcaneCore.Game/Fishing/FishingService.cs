using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Fishing;

/// <summary>
/// Fishing on one map: the bobber a fishing spell's TRANS_DOOR effect summons (position, water and line-of-sight check, bite timing,
/// splash, expiry), the click that decides the catch and opens the loot, fishing holes, and the settlement when the loot window closes.
/// Behaviour re-implemented from vmangos Spell::EffectTransmitted (SpellEffects.cpp:5648-5790), GameObject::Update (GameObject.cpp:359-379,
/// 411-424), GameObject::Use (:1635-1731), Player::SendLoot (Player.cpp:7651-7703) and DoLootRelease (LootHandler.cpp:489-495); no code copied.
/// <para>
/// Times use this service's millisecond clock (advanced by <see cref="Update"/>) where vmangos compares whole wall-clock seconds, so a bite
/// can differ by up to one second from vmangos; the schedule itself (bite at <c>duration - lastSec</c>, five seconds to react) is the same.
/// </para>
/// World thread only; one service per map, attached with <see cref="Map.AddUpdater"/>.
/// </summary>
public sealed class FishingService : IMapUpdater, ILootReleaseHandler
{
    /// <summary>FISHING_BOBBER_READY_TIME (GameObjectDefines.h:197): seconds a bobber stays ready for the catch.</summary>
    public const long BobberReadyTimeMs = 5000;

    /// <summary>The bite happens <c>lastSec</c> seconds before the channel ends, one of these (SpellEffects.cpp: PickRandomValue(3, 7, 13, 17)).</summary>
    public static readonly IReadOnlyList<int> LastSecondsChoices = [3, 7, 13, 17];

    /// <summary>The splash sound (GameObject.cpp:364: PlayDistanceSound(3355)).</summary>
    public const uint SplashSoundId = 3355;

    private sealed class Bobber(GameObject go, Player owner, uint spellId, long readyAtMs, long expireAtMs)
    {
        public GameObject Go { get; } = go;
        public Player Owner { get; } = owner;
        public uint SpellId { get; } = spellId;
        public long ReadyAtMs { get; } = readyAtMs;
        public long ExpireAtMs { get; } = expireAtMs;
        public bool Ready { get; set; }
    }

    private readonly GameObjectMapSystem _objects;
    private readonly Func<SpellSystem?> _spells;
    private readonly Random _random;
    private readonly IFishingTerrain _terrain;
    private readonly Dictionary<ObjectGuid, Bobber> _bobbers = [];
    private long _clockMs;

    public FishingService(Map map, GameObjectMapSystem objects, FishingOptions options, Func<SpellSystem?> spells, Random? random = null, IFishingTerrain? terrain = null)
    {
        Map = map ?? throw new ArgumentNullException(nameof(map));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _spells = spells ?? throw new ArgumentNullException(nameof(spells));
        _random = random ?? new Random();
        _terrain = terrain ?? MapFishingTerrain.Instance;
    }

    public Map Map { get; }

    public FishingOptions Options { get; }

    /// <summary>
    /// Where a position lies (zone, sub-zone). Null reads the map's terrain (<see cref="Map.GetZoneAndAreaId"/>); a build with WMO area data
    /// or a test supplies its own.
    /// </summary>
    public Func<float, float, float, (uint ZoneId, uint AreaId)>? AreaOf { get; set; }

    /// <summary>Bobbers waiting for a bite or a click (a hooked bobber whose loot is open is the loot service's).</summary>
    public int ActiveBobbers => _bobbers.Count;

    private LootService? Loot => _objects.Loot;

    // --- the spell side ---------------------------------------------------------------------

    /// <summary>
    /// SPELL_EFFECT_TRANS_DOOR of a fishing spell (vmangos Spell::EffectTransmitted for a game object of type FISHINGNODE): find where the
    /// bobber lands, refuse land and shallow or unseen water with NOT_FISHABLE, summon the bobber owned by the caster, make it the
    /// channel object and schedule the bite. Other transmitted objects (rituals, traps) are not handled here.
    /// </summary>
    /// <returns>False when the transmitted object is not a fishing node (the caller hands the effect to the handler installed before it).</returns>
    public bool Transmit(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_objects.FindTemplate((uint)context.Effect.MiscValue) is not { } template
            || (GameObjectType)template.Type != GameObjectType.FishingNode)
        {
            return false;
        }

        if (context.Caster is not Player caster || !ReferenceEquals(caster.Map, Map))
        {
            return true;
        }

        SpellSystem system = context.System;
        SpellInfo spell = context.Spell;
        (float fx, float fy, float fz) = LandingPoint(context, caster);

        // GridMap IsSwimmable twice (the second time from the water level the first returned), then the literal vmangos guard.
        if (!_terrain.IsSwimmable(Map, fx, fy, caster.Z + 1.0f, 1.5f, out LiquidData liquid))
        {
            _terrain.IsSwimmable(Map, fx, fy, liquid.Level, 1.5f, out liquid);
        }

        if (MathF.Abs(liquid.DepthLevel) < 1 || !Map.Collision.IsInLineOfSight(caster.X, caster.Y, caster.Z + 2.0f, fx, fy, liquid.Level))
        {
            caster.Session.Send(WorldOpcode.SmsgCastResult, SpellPackets.BuildCastResult(spell.Id, SpellCastResult.NotFishable));
            system.FinishChannel(caster);
            return true;
        }

        fz = liquid.Level;
        if (_bobbers.Remove(caster.Guid, out Bobber? previous))
        {
            _objects.Remove(previous.Go);
        }

        GameObject? go = _objects.Summon(template.Entry, fx, fy, fz, caster.Orientation);
        if (go is null)
        {
            return true;
        }

        go.SetOwner(caster.Guid);
        go.SpellId = spell.Id;
        go.SetUInt32(UpdateFields.GameobjectLevel, caster.Level);
        go.LootState = GameObjectLootState.NotReady;
        caster.SetUInt64(UpdateFields.UnitFieldChannelObject, go.Guid.Value);

        // The bite comes lastSec before the channel ends; the click window is FISHING_BOBBER_READY_TIME long (a ready bobber that
        // nobody clicked ends at duration - lastSec + 5 s).
        int lastSec = LastSecondsChoices[system.Random.Next(LastSecondsChoices.Count)];
        long readyAt = _clockMs + Math.Max(0, context.Cast.Duration - (lastSec * 1000L));
        _bobbers[caster.Guid] = new Bobber(go, caster, spell.Id, readyAt, readyAt + BobberReadyTimeMs);
        return true;
    }

    /// <summary>vmangos EffectTransmitted position: the explicit destination, else the effect radius ahead of the caster, else a random point in the spell range.</summary>
    private (float X, float Y, float Z) LandingPoint(SpellEffectContext context, Player caster)
    {
        SpellCastTargets targets = context.Cast.Targets;
        SpellEffectInfo effect = context.Effect;
        SpellInfo spell = context.Spell;
        if (targets.HasDest)
        {
            return targets.Dest;
        }

        float fx;
        float fy;
        float fz = caster.Z;
        if (effect.Radius > 0 && spell.Speed == 0)
        {
            fx = caster.X + (effect.Radius * MathF.Cos(caster.Orientation));
            fy = caster.Y + (effect.Radius * MathF.Sin(caster.Orientation));
            LosHit(caster.X, caster.Y, caster.Z + 0.5f, ref fx, ref fy, ref fz);
        }
        else
        {
            float min = spell.Range.Min;
            float max = spell.Range.Max;
            float distance = (context.System.Random.NextSingle() * (max - min)) + min;
            float maxAngle = (max - min) / (max + caster.BoundingRadius);
            float angle = caster.Orientation + (maxAngle * (context.System.Random.NextSingle() - 0.5f));
            fx = caster.X + (distance * MathF.Cos(angle));
            fy = caster.Y + (distance * MathF.Sin(angle));
            LosHit(caster.X, caster.Y, caster.Z + 2.0f, ref fx, ref fy, ref fz);
        }

        return (fx, fy, fz);
    }

    /// <summary>vmangos Map::GetLosHitPosition(..., -1.5f): the first model hit on the way, pulled 1.5 yd back.</summary>
    private void LosHit(float x, float y, float z, ref float tx, ref float ty, ref float tz)
    {
        if (Map.Collision.LineOfSight.TryGetObjectHit(Map.MapId, new Vector3(x, y, z), new Vector3(tx, ty, tz), -1.5f, out Vector3 hit))
        {
            (tx, ty, tz) = (hit.X, hit.Y, hit.Z);
        }
    }

    /// <summary>The spell system ended <paramref name="caster"/>'s cast of <paramref name="spellId"/> (cancel, move, new cast, timeout): the bobber it owns goes (vmangos Unit::RemoveGameObject(spellId, true)).</summary>
    public void OnCastFinished(Unit caster, uint spellId)
    {
        if (_bobbers.TryGetValue(caster.Guid, out Bobber? bobber) && bobber.SpellId == spellId)
        {
            RemoveBobber(bobber);
        }
    }

    private void RemoveBobber(Bobber bobber)
    {
        _bobbers.Remove(bobber.Owner.Guid);
        _objects.Remove(bobber.Go);
    }

    // --- the clock --------------------------------------------------------------------------

    public void Update(Map map, uint diffMs)
    {
        _clockMs += diffMs;
        foreach (Bobber bobber in _bobbers.Values.ToArray())
        {
            if (!bobber.Go.IsSpawned)
            {
                _bobbers.Remove(bobber.Owner.Guid); // removed by someone else (GM command, grid unload)
                continue;
            }

            if (!bobber.Ready && _clockMs >= bobber.ReadyAtMs)
            {
                MakeReady(bobber);
            }

            if (bobber.Ready && bobber.Go.LootState == GameObjectLootState.Ready && _clockMs >= bobber.ExpireAtMs)
            {
                Expire(bobber);
            }
        }
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
    }

    /// <summary>GameObject.cpp:359-379: the bobber splashes (state active, sound, animation) and can be clicked.</summary>
    private void MakeReady(Bobber bobber)
    {
        bobber.Ready = true;
        GameObject go = bobber.Go;
        go.State = GameObjectState.Active;
        go.LootState = GameObjectLootState.Ready;
        Map.BroadcastToObservers(go, WorldOpcode.SmsgPlayObjectSound, FishingPackets.PlayObjectSound(SplashSoundId, go.Guid));
        Map.BroadcastToObservers(go, WorldOpcode.SmsgGameobjectCustomAnim, GameObjectPackets.CustomAnim(go.Guid, 0));
    }

    /// <summary>GameObject.cpp:411-424: nobody clicked in time: the channel ends, the fish is not hooked, the bobber goes.</summary>
    private void Expire(Bobber bobber)
    {
        _spells()?.FinishChannel(bobber.Owner);
        if (bobber.Owner.IsInWorld)
        {
            bobber.Owner.Session.Send(WorldOpcode.SmsgFishNotHooked, FishingPackets.NotHooked());
        }

        bobber.Go.LootState = GameObjectLootState.JustDeactivated;
        _bobbers.Remove(bobber.Owner.Guid);
    }

    // --- the click ---------------------------------------------------------------------------

    /// <summary>
    /// GameObject::Use for a bobber (GameObject.cpp:1635-1731), registered for <see cref="GameObjectType.FishingNode"/>: only the owner
    /// reacts. A ready bobber rolls the catch (a skill below the zone's base never catches; at the base 5%, one point more per skill point),
    /// searches a fishing hole, raises the skill and opens the loot (type 3 on the wire); a failure is "fish escaped" (or junk with
    /// <see cref="FishingOptions.FailLoot"/>). A bobber that was not ready yet answers "not hooked" and goes. The channel ends afterwards in every case.
    /// </summary>
    public GameObjectUseResult UseBobber(Player player, GameObject go)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(go);
        if (go.OwnerGuid != player.Guid)
        {
            return GameObjectUseResult.NotUsable; // vmangos: return without a word
        }

        switch (go.LootState)
        {
            case GameObjectLootState.Ready:
                Catch(player, go);
                break;
            case GameObjectLootState.JustDeactivated:
                break; // nothing to do, it is removed at the next update
            default:
                go.LootState = GameObjectLootState.JustDeactivated;
                player.Session.Send(WorldOpcode.SmsgFishNotHooked, FishingPackets.NotHooked());
                break;
        }

        _spells()?.FinishChannel(player);
        return GameObjectUseResult.Ok;
    }

    private void Catch(Player player, GameObject go)
    {
        LootService? loot = Loot;
        if (loot is null)
        {
            go.LootState = GameObjectLootState.JustDeactivated;
            return;
        }

        (uint zone, uint area) = AreaOf?.Invoke(go.X, go.Y, go.Z) ?? Map.GetZoneAndAreaId(go.X, go.Y, go.Z);
        int zoneSkill = FishingCatch.ZoneSkill(loot.Content, zone, area);
        int skill = (int)_objects.SkillValue(player, SkillIds.Fishing);
        int roll = _random.Next(1, 101); // irand(1, 100)
        bool success = FishingCatch.Succeeds(skill, zoneSkill, roll);
        GameObject? hole = null;
        if (!success)
        {
            if (!Options.FailPossibleFishingPool)
            {
                hole = FishingHoles.FindAround(_objects, go);
                success = hole is not null;
            }
        }
        else
        {
            hole = FishingHoles.FindAround(_objects, go);
        }

        if (success || Options.FailGain)
        {
            player.Skills?.UpdateFishing();
        }

        if (!success && !Options.FailLoot)
        {
            go.LootState = GameObjectLootState.JustDeactivated;
            player.Session.Send(WorldOpcode.SmsgFishEscaped, FishingPackets.Escaped());
            return;
        }

        // The bobber leaves the spell's care (vmangos player->RemoveGameObject(this, false)): the channel ending must not delete it now.
        _bobbers.Remove(player.Guid);
        if (hole is not null && OpenHole(player, hole))
        {
            go.LootState = GameObjectLootState.JustDeactivated;
            return;
        }

        OpenBobberLoot(player, go, success, zone, area, loot);
    }

    private void OpenBobberLoot(Player player, GameObject go, bool success, uint zone, uint area, LootService loot)
    {
        LootBag bag;
        if (!success)
        {
            // fishing_loot_template entry 0: the junk table of a failed cast (Player.cpp:7692).
            bag = loot.Generate(go.Guid, LootSourceKind.GameObject, LootType.FishingFail, LootTableKind.Fishing, 0, [player], zeroEntryIsATable: true);
        }
        else if (FishingCatch.IsHiddenLake(area, player.X, player.Y) || FishingCatch.LootEntry(loot.Content, zone, area) is not { } entry)
        {
            bag = loot.Generate(go.Guid, LootSourceKind.GameObject, LootType.Fishing, LootTableKind.Fishing, 0, [player]); // empty
        }
        else
        {
            bag = loot.Generate(go.Guid, LootSourceKind.GameObject, LootType.Fishing, LootTableKind.Fishing, entry, [player], zeroEntryIsATable: true);
        }

        bag.IgnoreDistance = true; // an owned bobber is exempt from the loot distance (LootHandler.cpp:56-70)
        bag.ReleaseHandler = this;
        go.Loot = bag;
        go.LootState = GameObjectLootState.Activated;
        loot.ShowSpecial(player, go, bag);
    }

    /// <summary>A successful catch near a hole loots the hole instead (GameObject::Use → fishingHole-&gt;Use → SendLoot(LOOT_FISHINGHOLE)); false when the hole cannot be used now.</summary>
    private bool OpenHole(Player player, GameObject hole)
    {
        LootService? loot = Loot;
        if (loot is null || hole.LootState != GameObjectLootState.Ready)
        {
            return false; // in use by another fisher: vmangos shows its shared loot, this implementation falls back to the zone loot
        }

        LootBag bag = loot.Generate(hole.Guid, LootSourceKind.GameObject, LootType.FishingHole, LootTableKind.GameObject, hole.Template.GetData(1), [player]);
        bag.IgnoreDistance = true; // every fishing hole is exempt (LootHandler.cpp:63-64)
        bag.ReleaseHandler = this;
        hole.Loot = bag;
        hole.LootState = GameObjectLootState.Activated;
        loot.ShowSpecial(player, hole, bag);
        return true;
    }

    // --- release -----------------------------------------------------------------------------

    /// <summary>DoLootRelease for a bobber or a hole (LootHandler.cpp:458-495).</summary>
    public void OnReleased(Player player, LootBag bag)
    {
        ArgumentNullException.ThrowIfNull(bag);
        if (_objects.Find(bag.Source) is not { } go)
        {
            return;
        }

        if (go.Type == GameObjectType.FishingHole)
        {
            if (!bag.IsEmpty)
            {
                go.LootState = GameObjectLootState.Activated; // vmangos keeps the leftovers and the hole activated
                return;
            }

            go.LootState = FishingHoles.AfterUse(go, _random);
        }
        else
        {
            go.LootState = GameObjectLootState.JustDeactivated; // the bobber is gone after its loot window
        }

        go.Loot = null;
        Loot?.RemoveSpecial(go);
    }
}