namespace ArcaneCore.Game.Pets;

/// <summary>vmangos CommandStates (UnitDefines.h:701-706; wow_messages PetCommandState).</summary>
public enum CommandState : byte
{
    Stay = 0,
    Follow = 1,
    Attack = 2,
    Dismiss = 3,
}

/// <summary>vmangos ReactStates (UnitDefines.h:680-685; wow_messages PetReactState).</summary>
public enum ReactState : byte
{
    Passive = 0,
    Defensive = 1,
    Aggressive = 2,
}

/// <summary>
/// vmangos ActiveStates (UnitDefines.h:670-677): the type byte of a pet action button.
/// </summary>
public enum ActionType : byte
{
    /// <summary>ACT_DECIDE: custom, never sent.</summary>
    Decide = 0x00,

    /// <summary>ACT_PASSIVE: a passive spell.</summary>
    Passive = 0x01,

    /// <summary>ACT_REACTION: passive / defensive / aggressive.</summary>
    Reaction = 0x06,

    /// <summary>ACT_COMMAND: attack / follow / stay.</summary>
    Command = 0x07,

    /// <summary>ACT_DISABLED: a castable spell with autocast off.</summary>
    Disabled = 0x81,

    /// <summary>ACT_ENABLED: a castable spell with autocast on.</summary>
    Enabled = 0xC1,
}

/// <summary>
/// One pet action button: the packed word <c>action | type &lt;&lt; 24</c> (vmangos
/// MAKE_UNIT_ACTION_BUTTON, UNIT_ACTION_BUTTON_ACTION and UNIT_ACTION_BUTTON_TYPE, Unit.h:173-176).
/// </summary>
public readonly record struct ActionButton(uint Packed)
{
    /// <summary>An empty slot (vmangos UnitActionBarEntry: ACT_DISABLED, action 0).</summary>
    public static ActionButton Empty => Make(0, ActionType.Disabled);

    public uint Action => Packed & 0x00FFFFFF;

    public byte Type => (byte)(Packed >> 24);

    /// <summary>vmangos UnitActionBarEntry::IsActionBarForSpell.</summary>
    public bool IsForSpell => Type is (byte)ActionType.Disabled or (byte)ActionType.Enabled or (byte)ActionType.Passive;

    public static ActionButton Make(uint action, ActionType type) => Make(action, (byte)type);

    public static ActionButton Make(uint action, byte type) => new((action & 0x00FFFFFF) | ((uint)type << 24));

    public ActionButton WithType(ActionType type) => Make(Action, type);

    public ActionButton WithAction(uint action) => new((Packed & 0xFF000000) | (action & 0x00FFFFFF));
}

/// <summary>
/// vmangos CharmInfo (Unit.h:211-295, Unit.cpp:8338-8650): what a pet remembers for its owner: the
/// command and react state, the ten-slot action bar, the pet number and the flags the pet AI keeps
/// while it obeys a command. Held in <see cref="SummonLinks.Charm"/> of every pet, guardian and
/// mini pet; <see cref="PetSpells"/> is the pet's spell list (vmangos <c>m_petSpells</c>/<c>m_spells</c>).
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class CharmInfo
{
    /// <summary>vmangos ACTION_BAR_INDEX_PET_SPELL_START: the first spell slot (slots 0-2 are the commands).</summary>
    public const int SpellSlotStart = 3;

    /// <summary>vmangos ACTION_BAR_INDEX_PET_SPELL_END: one past the last spell slot (slots 7-9 are the reactions).</summary>
    public const int SpellSlotEnd = 7;

    /// <summary>vmangos MAX_UNIT_ACTION_BAR_INDEX.</summary>
    public const int ActionBarSize = 10;

    private readonly ActionButton[] _bar = new ActionButton[ActionBarSize];
    private readonly Dictionary<uint, ActionType> _spells = [];

    internal CharmInfo(ReactState react)
    {
        ReactState = react;
        InitPetActionBar();
    }

    /// <summary>vmangos CharmInfo::GetPetNumber (the name query key); 0 until one is assigned.</summary>
    public uint PetNumber { get; internal set; }

    public string Name { get; internal set; } = "Pet";

    public uint NameTimestamp { get; internal set; }

    public bool RenameAllowed { get; internal set; }

    /// <summary>vmangos Pet::m_loyaltyPoints (hunter pets; see <see cref="PetLoyalty"/>).</summary>
    public int LoyaltyPoints { get; internal set; }

    /// <summary>vmangos Pet::m_trainingPoints: minus the training points spent, plus those earned (shown through UNIT_TRAINING_POINTS).</summary>
    public int TrainingPoints { get; internal set; }

    /// <summary>vmangos m_commandState: a new pet follows (CharmInfo constructor).</summary>
    public CommandState CommandState { get; internal set; } = CommandState.Follow;

    public ReactState ReactState { get; internal set; }

    /// <summary>vmangos Pet::IsEnabled: false greys the pet's bar out (a mounted owner); the packets then carry 0x8.</summary>
    public bool Enabled { get; internal set; } = true;

    public bool IsCommandAttack { get; internal set; }

    public bool IsCommandFollow { get; internal set; }

    public bool IsAtStay { get; internal set; }

    public bool IsFollowing { get; internal set; }

    public bool IsReturning { get; internal set; }

    /// <summary>Where the pet stood when it was told to stay (vmangos CharmInfo::SaveStayPosition).</summary>
    public (float X, float Y, float Z) StayPosition { get; internal set; }

    /// <summary>The ten action buttons in slot order.</summary>
    public IReadOnlyList<ActionButton> ActionBar => _bar;

    /// <summary>The spells the pet knows with their state (vmangos PetSpell::active: passive, disabled or enabled autocast).</summary>
    public IReadOnlyDictionary<uint, ActionType> SpellStates => _spells;

    /// <summary>The spells the pet knows and whether each is on autocast.</summary>
    public IReadOnlyDictionary<uint, bool> PetSpells => _spells.ToDictionary(s => s.Key, s => s.Value == ActionType.Enabled);

    public ActionButton GetButton(int slot) => (uint)slot < ActionBarSize ? _bar[slot] : ActionButton.Empty;

    /// <summary>vmangos CharmInfo::SetActionBar.</summary>
    public void SetActionBar(int slot, uint action, byte type)
    {
        if ((uint)slot < ActionBarSize)
        {
            _bar[slot] = ActionButton.Make(action, type);
        }
    }

    /// <summary>
    /// vmangos CharmInfo::InitPetActionBar (Unit.cpp:8347-8360): attack, follow and stay, four empty
    /// spell slots, then aggressive, defensive and passive.
    /// </summary>
    public void InitPetActionBar()
    {
        for (int i = 0; i < SpellSlotStart; i++)
        {
            _bar[i] = ActionButton.Make((uint)((int)CommandState.Attack - i), ActionType.Command);
        }

        for (int i = SpellSlotStart; i < SpellSlotEnd; i++)
        {
            _bar[i] = ActionButton.Make(0, ActionType.Disabled);
        }

        for (int i = SpellSlotEnd; i < ActionBarSize; i++)
        {
            _bar[i] = ActionButton.Make((uint)((int)ReactState.Aggressive - (i - SpellSlotEnd)), ActionType.Reaction);
        }
    }

    /// <summary>
    /// Teach the pet a spell and put it on the bar (vmangos Pet::AddSpell / CharmInfo::AddSpellToActionBar,
    /// Pet.cpp:1887-1975, Unit.cpp:8461-8488). A spell already on the bar keeps its slot, a passive spell is
    /// known but never on the bar. Rank chains (<c>GetFirstSpellInChain</c>) are not modelled, so a new rank
    /// takes its own slot until the spell chain data exists. Returns false when the pet already knew it.
    /// </summary>
    public bool LearnSpell(uint spellId, ActionType state)
    {
        if (spellId == 0 || !_spells.TryAdd(spellId, state == ActionType.Decide ? ActionType.Disabled : state))
        {
            return false;
        }

        if (state != ActionType.Passive)
        {
            AddSpellToActionBar(spellId, state);
        }

        return true;
    }

    /// <summary>A castable spell, with autocast off (vmangos ACT_DECIDE) or on.</summary>
    public bool LearnSpell(uint spellId, bool autocast = false) => LearnSpell(spellId, autocast ? ActionType.Enabled : ActionType.Disabled);

    /// <summary>vmangos Pet::removeSpell.</summary>
    public bool UnlearnSpell(uint spellId)
    {
        if (!_spells.Remove(spellId))
        {
            return false;
        }

        RemoveSpellFromActionBar(spellId);
        return true;
    }

    public bool HasSpell(uint spellId) => _spells.ContainsKey(spellId);

    /// <summary>vmangos CharmInfo::AddSpellToActionBar: the first empty spell slot.</summary>
    public bool AddSpellToActionBar(uint spellId, ActionType state)
    {
        foreach (ActionButton button in _bar)
        {
            if (button.Action == spellId && button.IsForSpell)
            {
                return true;
            }
        }

        for (int i = 0; i < ActionBarSize; i++)
        {
            if (_bar[i].Action == 0 && _bar[i].IsForSpell)
            {
                _bar[i] = ActionButton.Make(spellId, state == ActionType.Decide ? ActionType.Disabled : state);
                return true;
            }
        }

        return false;
    }

    /// <summary>vmangos CharmInfo::RemoveSpellFromActionBar.</summary>
    public bool RemoveSpellFromActionBar(uint spellId)
    {
        for (int i = 0; i < ActionBarSize; i++)
        {
            if (_bar[i].Action == spellId && _bar[i].IsForSpell)
            {
                _bar[i] = ActionButton.Make(0, ActionType.Disabled);
                return true;
            }
        }

        return false;
    }

    /// <summary>vmangos CharmInfo::SetSpellAutocast (the bar) together with Pet::ToggleAutocast (the spell list); a passive spell has no autocast.</summary>
    public void SetSpellAutocast(uint spellId, bool state)
    {
        if (!_spells.TryGetValue(spellId, out ActionType current) || current == ActionType.Passive)
        {
            return;
        }

        _spells[spellId] = state ? ActionType.Enabled : ActionType.Disabled;
        for (int i = 0; i < ActionBarSize; i++)
        {
            if (_bar[i].Action == spellId && _bar[i].IsForSpell)
            {
                _bar[i] = _bar[i].WithType(state ? ActionType.Enabled : ActionType.Disabled);
                break;
            }
        }
    }

    /// <summary>vmangos PetAI::ClearCharmInfoFlags.</summary>
    public void ClearFlags()
    {
        IsAtStay = false;
        IsCommandAttack = false;
        IsCommandFollow = false;
        IsFollowing = false;
        IsReturning = false;
    }
}
