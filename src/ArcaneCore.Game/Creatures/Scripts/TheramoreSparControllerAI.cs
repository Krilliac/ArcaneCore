using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// The Theramore sparring matches: mangos-classic ScriptDev2 <c>npc_theramore_spar_controller</c> (kalimdor/dustwallow_marsh.cpp:796-1221) on
/// Master Szigeti (5090). Every cycle it gathers the blue guards (Edward 4922, Kahil 5091, Narrisha 5093) and the red ones (Jarad 4923,
/// Lana 5092, Tark 5094) within 20 yd, puts them on faction 151 and finds the medics (Tamberlyn 5199 blue, Helaina 5200 red).
/// <list type="bullet">
/// <item><description>The first pair walks to the corners (Criton 4924 and both captains announce them); 5 s later they salute; 3 s
/// later Criton introduces them; 3 s after that he starts the bout. Each fighter takes its team faction (1621 blue, 1622 red) until combat
/// stops, and the two attack each other.</description></item>
/// <item><description>Every second the controller checks them: when one drops to 30% the other wins. Both get their faction back and stop
/// fighting, and the medics cast First Aid (7162). Szigeti points and names the winning team, the winner's teammates cheer, and the losing
/// captain names the loser. Criton sends them back to their corners and, when the losing team has no one left, names the overall winner;
/// 8 s later the captains say their motivation lines.</description></item>
/// <item><description>The pair runs to the corners; 5 s later the loser runs home and the next of the losing team steps up (5 s), or, with
/// none left, the winner goes home too and the teams are gathered again for a new round 20 s later.</description></item>
/// </list>
/// The source's controller looks for Szigeti and Criton within 15 yd; Szigeti is the controller itself, so it speaks when no other is found.
/// The source's HandleBlueWin reads <c>active[BLUE]</c>, read here as the blue team's fighter. A fighter's home is its corner while it is in
/// the ring (the bout's CombatStop sends it evading, and the evade then ends where the source moves it), and its post again when it goes home.
/// </summary>
public sealed class TheramoreSparControllerAI(Creature creature) : ScriptedAI(creature)
{
    public const uint NpcMasterSzigeti = 5090, NpcMasterCriton = 4924, NpcBlueCaptain = 5096, NpcRedCaptain = 5095;
    public const uint NpcGuardEdward = 4922, NpcGuardKahil = 5091, NpcGuardNarrisha = 5093, NpcGuardJarad = 4923, NpcGuardLana = 5092, NpcGuardTark = 5094;
    public const uint NpcMedicHelaina = 5200, NpcMedicTamberlyn = 5199;
    public const uint SpellFirstAid = 7162, BlueTeam = 1621, RedTeam = 1622, FactionTheramoreFriendly = 151;
    public const uint EmoteOneshotPoint = 25, EmoteOneshotCheer = 4, EmoteOneshotSalute = 66;
    public const int SayRedWinner = 1794, SayBlueWinner = 1795, SayRedWinnerOverall = 1773, SayBlueWinnerOverall = 1774, SayBackToCorners = 1636;
    public const int SayForTheRed = 1778, SayForTheBlue = 1777, SayFirstCombatants = 1771, SayFightIntro = 1643, SayFightStart = 1641;
    public const int SayEdwardLoss = 1760, SayKahilLoss = 1762, SayNarrishaLoss = 1763, SayJaradLoss = 1766, SayLanaLoss = 1767, SayTarkLoss = 1768;
    public const int SayRedMotivationWin = 1769, SayRedMotivationLoss = 1770, SayBlueMotivationLoss = 1765, SayBlueMotivationWin = 1764;
    public const int SayRedFirst = 1776, SayBlueFirst = 1775;
    public const float TeamRange = 20f, MasterRange = 15f, LosingHealthPercent = 30f;

    public static readonly uint[] BlueTeamEntries = [NpcGuardEdward, NpcGuardKahil, NpcGuardNarrisha];
    public static readonly uint[] RedTeamEntries = [NpcGuardJarad, NpcGuardLana, NpcGuardTark];
    public static readonly (float X, float Y, float Z, float O) BlueStart = (-3643.574707f, -4505.717773f, 9.462863f, 3.855592f);
    public static readonly (float X, float Y, float Z, float O) RedStart = (-3652.405273f, -4514.126953f, 9.462863f, 0.717925f);

    /// <summary>CombatantActions.</summary>
    public enum Step
    {
        CheckHp,
        BlueWin,
        RedWin,
        StartPosition,
        StartSpar,
        GetAll,
        Salute,
        Switch,
        Separate,
        Intro,
        Motivation,
    }

    private readonly Dictionary<Step, uint> _timers = new() { [Step.GetAll] = 2000, [Step.StartPosition] = 10_000 };
    private readonly Dictionary<uint, List<Creature>> _teams = new() { [BlueTeam] = [], [RedTeam] = [] };
    private readonly Dictionary<uint, Creature?> _medics = [];
    private readonly Dictionary<uint, Creature> _active = [];
    private readonly Dictionary<Creature, CreatureHome> _homes = [];
    private uint _winningTeam;

    public IReadOnlyDictionary<uint, Creature> Active => _active;

    public uint WinningTeam => _winningTeam;

    public IReadOnlyList<Creature> Waiting(uint team) => _teams[team];

    /// <summary>Whether <paramref name="step"/> is scheduled.</summary>
    public bool IsScheduled(Step step) => _timers.ContainsKey(step);

    protected override void Reset()
    {
    }

    private void Schedule(Step step, uint ms) => _timers[step] = ms;

    private Creature? Near(uint entry, float range)
        => System?.ClosestOfEntry(Me, entry, range, aliveOnly: true) ?? (Me.Template.Entry == entry && Me.IsAlive ? Me : null);

    private Creature? Szigeti => Near(NpcMasterSzigeti, MasterRange);

    private Creature? Criton => Near(NpcMasterCriton, MasterRange);

    private void Say(Creature? speaker, int text)
    {
        if (speaker is { IsAlive: true })
        {
            System?.SayText(speaker, text);
        }
    }

    private Creature? ActiveOf(uint team) => _active.TryGetValue(team, out Creature? c) && c.Map is not null ? c : null;

    private static (float X, float Y, float Z, float O) StartOf(uint team) => team == BlueTeam ? BlueStart : RedStart;

    private static uint Other(uint team) => team == BlueTeam ? RedTeam : BlueTeam;

    public override void OnUpdate(uint diffMs)
    {
        foreach (Step step in _timers.Keys.ToArray())
        {
            if (!_timers.TryGetValue(step, out uint ms))
            {
                continue;
            }

            if (ms > diffMs)
            {
                _timers[step] = ms - diffMs;
                continue;
            }

            _timers.Remove(step);
            Run(step);
        }

        UpdateVictim();
    }

    private void Run(Step step)
    {
        switch (step)
        {
            case Step.GetAll: GetCombatants(); break;
            case Step.CheckHp: CheckCombatantHp(); break;
            case Step.BlueWin: HandleTeamWin(BlueTeam); break;
            case Step.RedWin: HandleTeamWin(RedTeam); break;
            case Step.StartPosition: MoveIntoPosition(); break;
            case Step.Salute: CombatantSalute(); break;
            case Step.Intro: Say(Criton, SayFightIntro); Schedule(Step.StartSpar, 3000); break;
            case Step.StartSpar: StartSparring(); break;
            case Step.Separate: CombatantSeparate(); break;
            case Step.Switch: SwitchMembers(); break;
            case Step.Motivation: HandleMotivation(); break;
        }
    }

    /// <summary>GetCombatants.</summary>
    private void GetCombatants()
    {
        _active.Clear();
        foreach ((uint team, uint[] entries) in new[] { (RedTeam, RedTeamEntries), (BlueTeam, BlueTeamEntries) })
        {
            _teams[team].Clear();
            foreach (uint entry in entries)
            {
                if (System?.ClosestOfEntry(Me, entry, TeamRange, aliveOnly: true) is { } member)
                {
                    member.FactionTemplate = FactionTheramoreFriendly;
                    _teams[team].Add(member);
                }
            }
        }

        _medics[RedTeam] = System?.ClosestOfEntry(Me, NpcMedicHelaina, TeamRange, aliveOnly: false);
        _medics[BlueTeam] = System?.ClosestOfEntry(Me, NpcMedicTamberlyn, TeamRange, aliveOnly: false);
    }

    /// <summary>MoveIntoPosition: a new round takes the first of each team; the fighters walk to their corners.</summary>
    private void MoveIntoPosition()
    {
        if (_active.Count == 0)
        {
            if (_teams[RedTeam].Count == 0 || _teams[BlueTeam].Count == 0)
            {
                // The source would read an empty list here; with a team missing there is no bout: look again in 20 s.
                GetCombatants();
                Schedule(Step.StartPosition, 20_000);
                return;
            }

            foreach (uint team in (uint[])[RedTeam, BlueTeam])
            {
                _active[team] = _teams[team][0];
                _teams[team].RemoveAt(0);
            }

            Say(Criton, SayFirstCombatants);
            Say(Near(NpcRedCaptain, TeamRange), SayRedFirst);
            Say(Near(NpcBlueCaptain, TeamRange), SayBlueFirst);
        }

        if (ActiveOf(RedTeam) is { } red && ActiveOf(BlueTeam) is { } blue)
        {
            ToCorner(red, RedTeam, run: false);
            ToCorner(blue, BlueTeam, run: false);
        }

        Schedule(Step.Salute, 5000);
    }

    /// <summary>
    /// MovePoint(1, startingPositions[team]). The fighter's home is its corner while it is in the ring, so the evade that follows the
    /// CombatStop of a bout takes it there too; while it evades it is already on its way.
    /// </summary>
    private void ToCorner(Creature creature, uint team, bool run)
    {
        (float x, float y, float z, float o) = StartOf(team);
        _homes.TryAdd(creature, creature.Home);
        System?.SetHomePosition(creature, x, y, z, o);
        if (creature.IsInEvadeMode)
        {
            return;
        }

        creature.Motion.Clear();
        creature.Motion.MovePoint(1, x, y, z, run);
    }

    private void CombatantSalute()
    {
        if (ActiveOf(RedTeam) is { IsAlive: true } red && ActiveOf(BlueTeam) is { IsAlive: true } blue)
        {
            System?.PlayEmote(red, EmoteOneshotSalute);
            Say(red, SayForTheRed);
            System?.PlayEmote(blue, EmoteOneshotSalute);
            Say(blue, SayForTheBlue);
        }

        Schedule(Step.Intro, 3000);
    }

    private void StartSparring()
    {
        Say(Criton, SayFightStart);
        if (ActiveOf(RedTeam) is not { } red || ActiveOf(BlueTeam) is not { } blue)
        {
            return;
        }

        red.SetFactionTemporary(RedTeam, TemporaryFactionFlags.RestoreCombatStop);
        red.ReactState = CreatureReactState.Defensive;
        blue.SetFactionTemporary(BlueTeam, TemporaryFactionFlags.RestoreCombatStop);
        blue.ReactState = CreatureReactState.Defensive;
        red.AI?.AttackStart(blue);
        blue.AI?.AttackStart(red);
        Schedule(Step.CheckHp, 1000);
    }

    private static float HealthPercent(Creature c) => c.MaxHealth == 0 ? 0f : c.Health * 100f / c.MaxHealth;

    /// <summary>CheckCombatantHP.</summary>
    private void CheckCombatantHp()
    {
        if (ActiveOf(RedTeam) is { } red && ActiveOf(BlueTeam) is { } blue)
        {
            float redHealth = HealthPercent(red), blueHealth = HealthPercent(blue);
            if (redHealth <= LosingHealthPercent || blueHealth <= LosingHealthPercent)
            {
                Schedule(redHealth < blueHealth ? Step.BlueWin : Step.RedWin, 100);
                foreach (Creature fighter in (Creature[])[red, blue])
                {
                    // RestoreOriginalFaction and CombatStop(true); the threat goes too, so neither picks the other up again from its list.
                    fighter.ClearTemporaryFaction();
                    if (fighter.Combat.HasThreatList)
                    {
                        fighter.Combat.Threat.Clear();
                    }

                    fighter.Map?.Combat.CombatStop(fighter);
                }

                foreach ((uint team, Creature fighter) in new[] { (RedTeam, red), (BlueTeam, blue) })
                {
                    if (_medics.GetValueOrDefault(team) is { IsAlive: true } medic && fighter.IsAlive)
                    {
                        System?.CastSpell(medic, SpellFirstAid, fighter, triggered: false);
                    }
                }

                return;
            }
        }

        Schedule(Step.CheckHp, 1000);
    }

    /// <summary>HandleBlueWin / HandleRedWin.</summary>
    private void HandleTeamWin(uint winner)
    {
        uint loser = Other(winner);
        if (Szigeti is { } szigeti)
        {
            System?.PlayEmote(szigeti, EmoteOneshotPoint);
            Say(szigeti, winner == BlueTeam ? SayBlueWinner : SayRedWinner);
        }

        uint activeWinner = ActiveOf(winner)?.Template.Entry ?? 0;
        foreach (uint member in winner == BlueTeam ? BlueTeamEntries : RedTeamEntries)
        {
            if (member != activeWinner && System?.ClosestOfEntry(Me, member, TeamRange, aliveOnly: true) is { } mate)
            {
                System.PlayEmote(mate, EmoteOneshotCheer);
            }
        }

        if (Near(loser == RedTeam ? NpcRedCaptain : NpcBlueCaptain, TeamRange) is { } captain)
        {
            int? lossLine = ActiveOf(loser)?.Template.Entry switch
            {
                NpcGuardJarad => SayJaradLoss,
                NpcGuardLana => SayLanaLoss,
                NpcGuardTark => SayTarkLoss,
                NpcGuardEdward => SayEdwardLoss,
                NpcGuardKahil => SayKahilLoss,
                NpcGuardNarrisha => SayNarrishaLoss,
                _ => null,
            };
            if (lossLine is { } line)
            {
                Say(captain, line);
            }
        }

        // HandleWin.
        if (Criton is { } criton)
        {
            Say(criton, SayBackToCorners);
            if (_teams[loser].Count == 0)
            {
                Say(criton, winner == BlueTeam ? SayBlueWinnerOverall : SayRedWinnerOverall);
                Schedule(Step.Motivation, 8000);
            }
        }

        Schedule(Step.Separate, 100);
        _winningTeam = winner;
    }

    private void HandleMotivation()
    {
        if (Near(NpcRedCaptain, TeamRange) is not { } red || Near(NpcBlueCaptain, TeamRange) is not { } blue)
        {
            return;
        }

        if (_winningTeam == RedTeam)
        {
            Say(red, SayRedMotivationWin);
            Say(blue, SayBlueMotivationLoss);
        }
        else if (_winningTeam == BlueTeam)
        {
            Say(blue, SayBlueMotivationWin);
            Say(red, SayRedMotivationLoss);
        }
    }

    private void CombatantSeparate()
    {
        if (ActiveOf(RedTeam) is { } red && ActiveOf(BlueTeam) is { } blue)
        {
            ToCorner(red, RedTeam, run: true);
            red.ReactState = CreatureReactState.Passive;
            ToCorner(blue, BlueTeam, run: true);
            blue.ReactState = CreatureReactState.Passive;
        }

        Schedule(Step.Switch, 5000);
    }

    /// <summary>MovePoint(0, GetRespawnPosition(), run): back to its post, which is its home again.</summary>
    private void GoHome(Creature creature)
    {
        creature.ReactState = CreatureReactState.Passive;
        if (!_homes.Remove(creature, out CreatureHome home))
        {
            home = creature.Home;
        }

        System?.SetHomePosition(creature, home.X, home.Y, home.Z, home.Orientation);
        creature.Motion.Clear();
        creature.Motion.MoveTargetedHome(home);
    }

    /// <summary>SwitchMembers.</summary>
    private void SwitchMembers()
    {
        uint winner = _winningTeam, loser = Other(winner);
        if (ActiveOf(loser) is { IsAlive: true } losing)
        {
            GoHome(losing);
        }

        Creature? winning = ActiveOf(winner);
        if (winning is { IsAlive: true })
        {
            ToCorner(winning, winner, run: false);
        }

        if (_teams[loser].Count > 0)
        {
            _active[loser] = _teams[loser][0];
            _teams[loser].RemoveAt(0);
            Schedule(Step.StartPosition, 5000);
            return;
        }

        if (winning is { IsAlive: true })
        {
            GoHome(winning);
        }

        GetCombatants();
        Schedule(Step.StartPosition, 20_000);
    }
}
