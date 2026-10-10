using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>
/// mangos-classic kalimdor/temple_of_ahnqiraj/temple_of_ahnqiraj.cpp at_temple_ahnqiraj / DoHandleTempleAreaTrigger, aIntroDialogue and
/// JustDidDialogueStep. 4047: while TYPE_TWINS_INTRO (slot 9) is NOT_STARTED, the Master's Eye (15963) faces the player and the intro
/// runs: the eye's emote (broadcast_text 11700) and its despawn 6 s later, the kneeling emperors stand 2 s on, then six lines alternating
/// Vek'lor / Vek'nilash (11702, 11706, 11707, 11708, 11709, 11710) 6, 6, 8, 3, 3 and 1 s apart; the last line sets slot 9 DONE. The
/// emperors kneel on creation until then. 4052: Sartura (15516) is pulled into zone combat while her encounter is NOT_STARTED or FAIL.
/// </summary>
public sealed partial class TempleOfAhnQirajInstance
{
    public const uint TwinsIntro = 9;
    public const uint AreaTriggerTwinEmperors = 4047, AreaTriggerSartura = 4052;
    public const uint MastersEye = 15963, Veklor = 15276, Veknilash = 15275, SarturaEntry = 15516;

    // (broadcast_text, speaker, delay to the next step); text 0 is the STAND_EMPERORS_INTRO step.
    private static readonly (int Text, uint Speaker, uint DelayMs)[] IntroDialogue =
    [
        (11700, MastersEye, 2000),
        (0, 0, 6000),
        (11702, Veklor, 6000),
        (11706, Veknilash, 8000),
        (11707, Veklor, 3000),
        (11708, Veknilash, 3000),
        (11709, Veklor, 1000),
        (11710, Veknilash, 0),
    ];

    private int _introStep = -1;
    private uint _introMs;

    /// <summary>The next dialogue step, or -1 when the intro is not running.</summary>
    public int TwinsIntroStep => _introStep;

    private void OnTempleCreatureCreate(Creature creature)
    {
        if (creature.Entry is MastersEye or SarturaEntry) StoreCreature(creature);
        if (creature.Entry is Veklor or Veknilash && GetData(TwinsIntro) != EncounterState.Done)
            creature.StandState = StandState.Kneel;
    }

    private bool HandleTempleAreaTrigger(Player player, uint triggerId)
    {
        if (triggerId == AreaTriggerTwinEmperors)
        {
            if (GetData(TwinsIntro) != EncounterState.NotStarted) return true;
            Encounters[TwinsIntro] = EncounterState.InProgress;
            if (GetSingleCreatureFromStorage(MastersEye) is { } eye)
                eye.Orientation = Creature.NormalizeOrientation(MathF.Atan2(player.Y - eye.Y, player.X - eye.X));
            _introStep = 0;
            _introMs = 0;
            return true;
        }

        if (triggerId == AreaTriggerSartura)
        {
            if (GetData(Sartura) is EncounterState.NotStarted or EncounterState.Fail
                && GetSingleCreatureFromStorage(SarturaEntry) is { IsAlive: true } sartura)
                sartura.System?.SetInCombatWithZone(sartura);
            return true;
        }

        return false;
    }

    private void UpdateTwinsIntro(uint diffMs)
    {
        while (_introStep >= 0)
        {
            if (_introMs > diffMs)
            {
                _introMs -= diffMs;
                return;
            }

            diffMs -= _introMs;
            var (text, speaker, delay) = IntroDialogue[_introStep];
            if (text != 0 && GetSingleCreatureFromStorage(speaker) is { } talker) talker.System?.SayText(talker, text);
            switch (_introStep)
            {
                case 0:
                    if (GetSingleCreatureFromStorage(MastersEye) is { } eye) eye.System?.ForcedDespawn(eye, 6000);
                    break;
                case 1:
                    foreach (uint twin in new[] { Veklor, Veknilash })
                        if (GetSingleCreatureFromStorage(twin) is { } emperor) emperor.StandState = StandState.Stand;
                    break;
                case 7:
                    Encounters[TwinsIntro] = EncounterState.Done;
                    SaveToDB();
                    break;
            }

            _introMs = delay;
            _introStep = _introStep == IntroDialogue.Length - 1 ? -1 : _introStep + 1;
        }
    }
}
