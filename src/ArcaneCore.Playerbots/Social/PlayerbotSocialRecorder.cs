using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// The one seam the coordinator, party AI, brain and chat record social events through. It knows which characters are managed bots
/// (a player guid carries the character id, so the map is keyed by it), keeps the <see cref="PlayerbotSocialMemory"/>, and never throws
/// into game logic: a failure is recorded at the fault site <c>playerbot.social-memory</c> and logged. Any thread.
/// </summary>
internal sealed class PlayerbotSocialRecorder(PlayerbotSocialMemory memory, ILogger logger)
{
    public const string FaultSite = "playerbot.social-memory";

    private readonly ConcurrentDictionary<uint, (Guid BotId, int CharacterId)> _managed = new();
    private int _faultsLogged;

    public PlayerbotSocialMemory Memory { get; } = memory;

    public bool Enabled => Memory.Enabled;

    /// <summary>A managed bot logged in (or was adopted by this feature): its events are now recorded against its id.</summary>
    public void Track(Guid botId, int characterId)
    {
        if (characterId <= 0) return;
        _managed[(uint)characterId] = (botId, characterId);
        Memory.SetSelf(botId, ObjectGuid.Player((uint)characterId).Value);
    }

    /// <summary>A managed bot left this feature.</summary>
    public void Untrack(int characterId) => _managed.TryRemove((uint)characterId, out _);

    /// <summary>The managed bot a player guid belongs to.</summary>
    public bool TryBot(ObjectGuid guid, out Guid botId, out int characterId)
    {
        if (guid.IsPlayer && _managed.TryGetValue(guid.Low, out (Guid BotId, int CharacterId) bot))
        {
            botId = bot.BotId;
            characterId = bot.CharacterId;
            return true;
        }

        botId = Guid.Empty;
        characterId = 0;
        return false;
    }

    public bool IsManaged(ObjectGuid guid) => guid.IsPlayer && _managed.ContainsKey(guid.Low);

    /// <summary>Record <paramref name="e"/> in <paramref name="bot"/>'s memory toward <paramref name="other"/>; nothing if the first is not a managed bot or the second not a player.</summary>
    public void Note(ObjectGuid bot, ObjectGuid other, PlayerbotSocialEvent e, bool otherFaction = false)
    {
        if (!Enabled || !other.IsPlayer || !TryBot(bot, out Guid botId, out int characterId)) return;
        Guarded(() => Memory.Note(botId, characterId, other.Value, IsManaged(other), e, otherFaction));
    }

    /// <summary>Record <paramref name="e"/> for a bot whose id is already known (chat, the party AI).</summary>
    public void NoteById(Guid botId, ObjectGuid botGuid, ObjectGuid other, PlayerbotSocialEvent e, bool otherFaction = false)
    {
        if (!Enabled || !other.IsPlayer || other == botGuid) return;
        Guarded(() => Memory.Note(botId, (int)botGuid.Low, other.Value, IsManaged(other), e, otherFaction));
    }

    /// <summary>Record <paramref name="e"/> both ways between two characters (each side that is a managed bot remembers the other).</summary>
    public void NoteBoth(ObjectGuid a, ObjectGuid b, PlayerbotSocialEvent e, bool crossFaction = false)
    {
        if (a == b) return;
        Note(a, b, e, crossFaction);
        Note(b, a, e, crossFaction);
    }

    /// <summary>How long a helper's credit toward one victim lasts (one fight; a victim bot that leaves combat ends it sooner, <see cref="EndFight"/>).</summary>
    internal const long HelpWindowMs = 60_000;

    private readonly Dictionary<(ulong Helper, ulong Victim), long> _helped = [];
    private readonly object _helpGate = new();
    private Func<long> _now = static () => Environment.TickCount64;

    /// <summary>The clock of the help window (tests).</summary>
    internal Func<long> Clock { set => _now = value; }

    /// <summary>
    /// <paramref name="helper"/> helped <paramref name="victim"/> in the current fight (an assist on a tagged mob, a heal, a hit on the mob that fights
    /// the victim): the victim, if a managed bot, records HelpedBy and the helper, if a managed bot, records HelpedThem. Once per helper and
    /// victim per fight, whichever source reports it first, so a single assist is never counted twice.
    /// </summary>
    public void NoteHelp(Unit helper, Unit victim)
    {
        if (!Enabled || helper.Guid == victim.Guid || !helper.Guid.IsPlayer || !victim.Guid.IsPlayer) return;
        if (!IsManaged(helper.Guid) && !IsManaged(victim.Guid)) return;
        Guarded(() =>
        {
            long now = _now();
            lock (_helpGate)
            {
                if (_helped.Count > 2048)
                    foreach (var stale in _helped.Where(pair => now - pair.Value > HelpWindowMs).Select(pair => pair.Key).ToArray()) _helped.Remove(stale);
                if (_helped.TryGetValue((helper.Guid.Value, victim.Guid.Value), out long at) && now - at <= HelpWindowMs) return;
                _helped[(helper.Guid.Value, victim.Guid.Value)] = now;
            }

            bool crossFaction = helper is Player a && victim is Player b && a.Team != b.Team;
            Note(helper.Guid, victim.Guid, PlayerbotSocialEvent.HelpedThem, crossFaction);
            Note(victim.Guid, helper.Guid, PlayerbotSocialEvent.HelpedBy, crossFaction);
        });
    }

    /// <summary>
    /// A player died to another player (or to a player's pet or totem): the victim, if a managed bot, records KilledBy and the killer, if a managed
    /// bot, KilledThem, each marking the other as of the other faction when the teams differ. Duels never count: a duel ends at 1 hp without a kill,
    /// and a death inside a started duel is skipped all the same. No record for an environmental death or a suicide.
    /// </summary>
    public void NotePvpKill(Unit? killer, Unit victim) => Guarded(() => NotePvpKillCore(killer, victim));

    private void NotePvpKillCore(Unit? killer, Unit victim)
    {
        if (!Enabled || killer is null || victim is not Player dead) return;
        Player? slayer = DuelRules.ControllingPlayer(killer);
        if (slayer is null || ReferenceEquals(slayer, dead) || DuelRules.IsInDuelWith(slayer, dead) || DuelRules.IsInDuelWith(dead, slayer)) return;
        if (!IsManaged(slayer.Guid) && !IsManaged(dead.Guid)) return;
        bool crossFaction = slayer.Team != dead.Team;
        Note(dead.Guid, slayer.Guid, PlayerbotSocialEvent.KilledBy, crossFaction);
        Note(slayer.Guid, dead.Guid, PlayerbotSocialEvent.KilledThem, crossFaction);
    }

    /// <summary>The victim left combat: its helpers may earn credit again in its next fight.</summary>
    public void EndFight(ObjectGuid victim)
    {
        lock (_helpGate)
            if (_helped.Count > 0)
                foreach (var key in _helped.Keys.Where(k => k.Victim == victim.Value).ToArray()) _helped.Remove(key);
    }

    /// <summary>Set the last topic and the bot-side summary of an existing relation.</summary>
    public void SetTopic(Guid botId, ObjectGuid other, string topicKey, string summary)
    {
        if (!Enabled || !other.IsPlayer) return;
        Guarded(() => Memory.SetTopic(botId, other.Value, topicKey, summary));
    }

    /// <summary>Run a recording step so that it can never throw into game logic.</summary>
    public void Guarded(Action step)
    {
        try
        {
            step();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record(FaultSite, error);
            if (Interlocked.Increment(ref _faultsLogged) <= 20)
                logger.LogWarning(error, "Playerbot social memory: a recording step failed");
        }
    }
}
