using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// "Triage" (quests 6624 Alliance / 6622 Horde): mangos-classic ScriptDev2 <c>npc_doctor</c> (Gustaf VanHowzen 12939, Gregory Victor 12920),
/// world/npcs_special.cpp at 3e8597afe7. Accepting the quest starts the event: every 10 s an injured, badly injured or critically injured
/// soldier is summoned on a free bunk (7 Alliance, 6 Horde; TEMPSPAWN_TIMED_OOC_OR_CORPSE, 5 s). Saving 15 credits the player, a sixth
/// death fails the quest, and the event resets after 21 summons.
/// Difference: the source's quest-status checks are left to the quest system, which only credits or fails an incomplete quest.
/// </summary>
public sealed class TriageDoctorAI(Creature creature) : CreatureAI(creature), IQuestScriptAI
{
    public const uint DoctorAlliance = 12939, DoctorHorde = 12920, QuestTriageA = 6624, QuestTriageH = 6622;
    private static readonly uint[] s_alliance = [12938, 12936, 12937], s_horde = [12923, 12924, 12925];
    private static readonly (float X, float Y, float Z, float O)[] s_allianceBunks =
    [
        (-3757.38f, -4533.05f, 14.16f, 3.62f), (-3754.36f, -4539.13f, 14.16f, 5.13f), (-3749.54f, -4540.25f, 14.28f, 3.34f),
        (-3742.10f, -4536.85f, 14.28f, 3.64f), (-3755.89f, -4529.07f, 14.05f, 0.57f), (-3749.51f, -4527.08f, 14.07f, 5.26f),
        (-3746.37f, -4525.35f, 14.16f, 5.22f),
    ];
    private static readonly (float X, float Y, float Z, float O)[] s_hordeBunks =
    [
        (-1013.75f, -3492.59f, 62.62f, 4.34f), (-1017.72f, -3490.92f, 62.62f, 4.34f), (-1015.77f, -3497.15f, 62.82f, 4.34f),
        (-1019.51f, -3495.49f, 62.82f, 4.34f), (-1017.25f, -3500.85f, 62.98f, 4.34f), (-1020.95f, -3499.21f, 62.98f, 4.34f),
    ];

    private readonly List<int> _freeBunks = [];
    private readonly List<Creature> _patients = [];
    private ObjectGuid _player;
    private uint _summonMs, _summoned, _died, _saved;

    public bool InProgress { get; private set; }
    public uint Saved => _saved;
    public uint Died => _died;
    public IReadOnlyList<Creature> Patients => _patients;
    private bool Alliance => Me.Entry == DoctorAlliance;
    private uint Quest => Alliance ? QuestTriageA : QuestTriageH;

    private void Reset()
    {
        _player = default;
        _summonMs = 10_000;
        _summoned = _died = _saved = 0;
        _patients.Clear();
        _freeBunks.Clear();
        InProgress = false;
        Me.UnitFlags &= ~UnitFlags.NotSelectable;
    }

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId is not (QuestTriageA or QuestTriageH))
        {
            return;
        }

        Reset();
        _player = player.Guid;
        _freeBunks.AddRange(Enumerable.Range(0, (Alliance ? s_allianceBunks : s_hordeBunks).Length));
        InProgress = true;
        Me.UnitFlags |= UnitFlags.NotSelectable;
    }

    internal void PatientDied(int bunk)
    {
        if (System?.Map.FindPlayer(_player) is not { } player)
        {
            Reset();
            return;
        }

        if (++_died > 5)
        {
            System.QuestFailed(player, Quest);
            Reset();
            return;
        }

        _freeBunks.Add(bunk);
    }

    internal void PatientSaved(Player player, int bunk)
    {
        if (player.Guid != _player || System is not { } system)
        {
            return;
        }

        if (++_saved == 15)
        {
            foreach (Creature patient in _patients.Where(p => p.IsAlive && p.IsInWorld).ToList())
            {
                Me.Map?.Combat.Kill(null, patient);
            }

            system.RewardGroupEventExplored(player, Quest, Me);
            Reset();
            return;
        }

        _freeBunks.Add(bunk);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (InProgress && _summoned >= 21)
        {
            Reset(); // the worst case: 5 dead and 15 saved
            return;
        }

        if (!InProgress || _freeBunks.Count == 0 || System is not { } system)
        {
            return;
        }

        if (_summonMs > diffMs)
        {
            _summonMs -= diffMs;
            return;
        }

        int pick = system.RandomInt(0, _freeBunks.Count - 1);
        int bunk = _freeBunks[pick];
        (float x, float y, float z, float o) = (Alliance ? s_allianceBunks : s_hordeBunks)[bunk];
        uint entry = (Alliance ? s_alliance : s_horde)[system.RandomInt(0, 2)];
        if (system.SummonAt(Me, entry, x, y, z, o, null, 5000, oocOrCorpse: true) is { } patient)
        {
            patient.UnitFlags |= UnitFlags.Pvp;
            _patients.Add(patient);
            if (patient.AI is InjuredPatientAI patientAi)
            {
                patientAi.Assign(this, bunk);
                _freeBunks.RemoveAt(pick);
            }
        }

        _summonMs = 10_000;
        ++_summoned;
    }
}

/// <summary>
/// mangos-classic <c>npc_injured_patient</c>: an injured soldier lying face down at 75, 50 or 25% health, losing 0.05 health per millisecond.
/// First Aid's Triage (spell 20804) from a player saves him: he stands, thanks the player, tells the doctor and runs out. At 1 health he dies
/// and the doctor counts it.
/// </summary>
public sealed class InjuredPatientAI(Creature creature) : CreatureAI(creature)
{
    public const uint SpellTriage = 20804;
    public static readonly uint[] Entries = [12923, 12924, 12925, 12936, 12937, 12938];
    private static readonly int[] s_thanks = [-1000201, -1000202, -1000203];
    private static readonly (float X, float Y, float Z) s_allianceExit = (-3742.96f, -4531.52f, 11.91f), s_hordeExit = (-1016.44f, -3508.48f, 62.96f);

    private TriageDoctorAI? _doctor;
    private int _bunk = -1;
    private bool _initialised;
    private float _drain;

    public bool IsSaved { get; private set; }

    internal void Assign(TriageDoctorAI doctor, int bunk)
    {
        _doctor = doctor;
        _bunk = bunk;
    }

    private void Init()
    {
        _initialised = true;
        Me.UnitFlags &= ~UnitFlags.NotSelectable;
        Me.UnitFlags |= UnitFlags.InCombat; // no regen
        Me.StandState = StandState.Dead;
        uint percent = Me.Entry switch { 12923 or 12938 => 75, 12924 or 12936 => 50, _ => 25 };
        Me.Health = Math.Max(1u, (uint)((ulong)Me.MaxHealth * percent / 100));
    }

    public override void OnEvade()
    {
        if (IsSaved)
        {
            base.OnEvade();
        }
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.Id == SpellTriage && caster is Player player)
        {
            Save(player);
        }
    }

    public void Save(Player player)
    {
        if (!Me.IsAlive || IsSaved || System is not { } system)
        {
            return;
        }

        _doctor?.PatientSaved(player, _bunk);
        Me.UnitFlags |= UnitFlags.NotSelectable;
        Me.UnitFlags &= ~UnitFlags.InCombat;
        Me.StandState = StandState.Stand;
        system.SayText(Me, s_thanks[system.RandomInt(0, 2)]);
        IsSaved = true;
        (float x, float y, float z) = Me.Entry is 12923 or 12924 or 12925 ? s_hordeExit : s_allianceExit;
        system.MoveTo(Me, x, y, z, run: true, finalOrientation: null);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!_initialised)
        {
            Init();
        }

        if (IsSaved || !Me.IsAlive)
        {
            return;
        }

        _drain += 0.05f * diffMs;
        uint lose = (uint)_drain;
        _drain -= lose;
        if (Me.Health > 1 + lose)
        {
            Me.Health -= lose;
            return;
        }

        Me.UnitFlags &= ~UnitFlags.InCombat;
        Me.UnitFlags |= UnitFlags.NotSelectable;
        TriageDoctorAI? doctor = _doctor;
        _doctor = null;
        Me.Map?.Combat.Kill(null, Me);
        doctor?.PatientDied(_bunk);
    }
}
