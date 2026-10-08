using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>ScriptDev2 instance_shadowfang_keep::OnCreatureCreate/Death, DoSpeech and Fenrus dialogue
/// (mangos-classic shadowfang_keep/instance_shadowfang_keep.cpp:65-125,158-169,266-339).</summary>
public sealed partial class ShadowfangKeepInstance
{
    public const uint NpcAda = 3849, NpcAsh = 3850, NpcFenrus = 4274, NpcArchmageArugal = 4275, NpcNandos = 3927;
    private readonly HashSet<ObjectGuid> _nandosPack = [];
    private Creature? _eventArugal;
    private uint _dialogueTimer;
    private int _dialogueStep = -1;
    private uint _introTimer;
    private int _introStep = -1;
    private bool _introVisible;
    private bool _fenrusVisible;

    public override void Initialize()
    {
        base.Initialize();
        _nandosPack.Clear();
        _dialogueStep = -1;
        _dialogueTimer = 0;
        _introStep = -1;
        _introTimer = 0;
        _introVisible = false;
        _fenrusVisible = false;
        Instance.AddVisibilityRule(new ArugalEventVisibility(this));
        if (Instance.FindUpdater<CreatureMapSystem>() is { } creatures)
        {
            creatures.RegisterEntryAi(NpcAda, c => new ShadowfangPrisonerAi(c, this));
            creatures.RegisterEntryAi(NpcAsh, c => new ShadowfangPrisonerAi(c, this));
            creatures.RegisterEntryAi(NpcArchmageArugal, c => new ArugalAi(c));
            creatures.RegisterEntryAi(4627, c => new ArugalVoidwalkerAi(c, this));
            creatures.RegisterEntryAi(4444, c => new DeathstalkerVincentAi(c, this));
        }
    }

    public override void OnCreatureCreate(Creature creature)
    {
        switch (creature.Entry)
        {
            case NpcAda or NpcAsh or NpcFenrus or NpcNandos or NpcArchmageArugal or 4444 or 10000:
                StoreCreature(creature);
                if (creature.Entry == 4444 && Encounters[4] == EncounterState.Done)
                {
                    creature.StandState = StandState.Dead; // Vincent after the intro
                }
                else if (creature.Entry == 4444 && _introStep < 0)
                {
                    _introStep = 0; // StartNextDialogueText(NPC_VINCENT)
                    _introTimer = 3_000;
                }

                break;
            case 3863 or 5058 or 3861 or 3862:
                if (creature.Z > 151.91f && creature.Spawn is not null)
                {
                    _nandosPack.Add(creature.Guid);
                }

                break;
        }
    }

    public override void OnCreatureDeath(Creature creature)
    {
        if (!_nandosPack.Remove(creature.Guid) || _nandosPack.Count != 0
            || GetSingleCreatureFromStorage(NpcNandos) is not { IsAlive: true } nandos || nandos.Combat.IsInCombat)
        {
            return;
        }

        Instance.FindUpdater<CreatureMapSystem>()?.SayText(nandos, -1033020);
        nandos.Motion.MovePoint(0, -170.6f, 2182.45f, 151.91f, run: true);
    }

    private void PrisonersSpeak()
    {
        Creature? ada = GetSingleCreatureFromStorage(NpcAda);
        Creature? ash = GetSingleCreatureFromStorage(NpcAsh);
        if (ada is { IsAlive: true } && ash is { IsAlive: true } && Instance.FindUpdater<CreatureMapSystem>() is { } creatures)
        {
            creatures.SayText(ada, -1033007);
            creatures.SayText(ash, -1033008);
        }
    }

    private void SummonArugalForFenrus()
    {
        if (GetSingleCreatureFromStorage(NpcFenrus) is null || Instance.FindUpdater<CreatureMapSystem>() is not { } creatures
            || creatures.Content.FindTemplate(NpcArchmageArugal) is not { } template)
        {
            return;
        }

        _eventArugal = creatures.SpawnTemporary(template, -136.89f, 2169.17f, 136.58f, 2.794f);
        _fenrusVisible = false;
        creatures.ForcedDespawn(_eventArugal, 30_000);
        _dialogueStep = 0;
        _dialogueTimer = 100;
    }

    public override void Update(uint diffMs)
    {
        UpdateIntro(diffMs);
        if (_dialogueStep < 0)
        {
            return;
        }

        if (_dialogueTimer > diffMs)
        {
            _dialogueTimer -= diffMs;
            return;
        }

        // aArugalDialogue's Fenrus tail: YELL_FENRUS, FIRE, LIGHTNING, INVIS, VOIDWALKERS.
        CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
        switch (_dialogueStep++)
        {
            case 0:
                _fenrusVisible = true;
                if (_eventArugal is not null)
                {
                    Instance.RefreshVisibility(_eventArugal);
                    creatures?.SayText(_eventArugal, -1033013);
                }
                _dialogueTimer = 2_000;
                break;
            case 1:
                if (_eventArugal is not null) creatures?.CastSpell(_eventArugal, 6422, _eventArugal, false);
                _dialogueTimer = 5_000;
                break;
            case 2:
                if (_eventArugal is not null && GetSingleGameObjectFromStorage(GoArugalFocus) is { } focus)
                    Instance.FindUpdater<GameObjectMapSystem>()?.UseByUnit(_eventArugal, focus);
                _dialogueTimer = 5_000;
                break;
            case 3:
                _fenrusVisible = false;
                if (_eventArugal is not null) Instance.RefreshVisibility(_eventArugal);
                _dialogueTimer = 500;
                break;
            case 4:
                SpawnArugalVoidwalkers();
                _dialogueStep = -1;
                break;
        }
    }

    private void UpdateIntro(uint diffMs)
    {
        if (_introStep < 0)
        {
            return;
        }

        if (_introTimer > diffMs)
        {
            _introTimer -= diffMs;
            return;
        }

        Creature? vincent = GetSingleCreatureFromStorage(4444);
        Creature? arugal = GetSingleCreatureFromStorage(10000);
        CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
        // aArugalDialogue's Vincent intro, instance_shadowfang_keep.cpp:31-44,266-311.
        switch (_introStep++)
        {
            case 0: if (vincent is not null) vincent.StandState = StandState.Dead; _introTimer = 8_000; break;
            case 1:
                _introVisible = true;
                if (arugal is not null) Instance.RefreshVisibility(arugal);
                _introTimer = 100;
                break;
            case 2: if (arugal is not null) creatures?.CastSpell(arugal, 7741, arugal, false); _introTimer = 3_200; break;
            case 3:
                if (arugal is not null && vincent is not null)
                    creatures?.SetFacingTo(arugal, MathF.Atan2(vincent.Y - arugal.Y, vincent.X - arugal.X));
                _introTimer = 100;
                break;
            case 4: if (arugal is not null) creatures?.SayText(arugal, -1033009); _introTimer = 3_500; break;
            case 5: if (arugal is not null) creatures?.PlayEmote(arugal, 25); _introTimer = 2_000; break;
            case 6: if (arugal is not null) creatures?.SayText(arugal, -1033010); _introTimer = 1_500; break;
            case 7: if (arugal is not null) creatures?.PlayEmote(arugal, 5); _introTimer = 3_000; break;
            case 8: if (arugal is not null) creatures?.SayText(arugal, -1033011); _introTimer = 3_000; break;
            case 9: if (arugal is not null) creatures?.PlayEmote(arugal, 11); _introTimer = 2_500; break;
            case 10: if (arugal is not null) creatures?.SayText(arugal, -1033012); _introTimer = 2_500; break;
            case 11: if (arugal is not null) creatures?.CastSpell(arugal, 7136, arugal, false); _introTimer = 2_000; break;
            case 12:
                SetData(TypeIntro, EncounterState.Done);
                _introVisible = false;
                if (arugal is not null) Instance.RefreshVisibility(arugal);
                if (arugal is not null) creatures?.ForcedDespawn(arugal, 0);
                _introStep = -1;
                break;
        }
    }

    private sealed class ArugalEventVisibility(ShadowfangKeepInstance instance) : IVisibilityRule
    {
        public bool CanSee(Player viewer, WorldObject target, bool alreadyVisible, bool detect)
        {
            if (target is not Creature creature) return true;
            return creature.Entry switch
            {
                10000 => instance._introVisible,
                NpcArchmageArugal when creature.Z < 140f => instance._fenrusVisible,
                _ => true,
            };
        }
    }

    private void SpawnArugalVoidwalkers()
    {
        if (_eventArugal is null || Instance.FindUpdater<CreatureMapSystem>() is not { } creatures)
        {
            return;
        }

        (float x, float y, float z, float o)[] points =
        [
            (-155.352f, 2172.780f, 128.448f, 4.679f), (-147.059f, 2163.193f, 128.696f, 0.128f),
            (-148.869f, 2180.859f, 128.448f, 1.814f), (-140.203f, 2175.263f, 128.448f, 0.373f),
        ];
        foreach ((float x, float y, float z, float o) in points)
        {
            Creature? walker = creatures.SummonCorpseDespawn(_eventArugal, 4627, x, y, z, o);
            if (walker is not null)
            {
                creatures.SetHomePosition(walker, -146.06f, 2172.84f, 127.953f, o);
            }
        }
    }
}
