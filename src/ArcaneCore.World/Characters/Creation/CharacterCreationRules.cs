using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Characters.Creation;

/// <summary>What the creation rules need to know about the realm (each answer is read lazily, in pipeline order).</summary>
public interface ICharacterCreationFacts
{
    /// <summary>The name is already used by a character (vmangos GetPlayerGuidByName).</summary>
    Task<bool> IsNameTakenAsync(string name);

    /// <summary>The characters the account already has on this realm (vmangos m_charactersCount).</summary>
    Task<int> CountOnRealmAsync();

    /// <summary>
    /// The race of the account's first character (the lowest id: vmangos GetPlayerDataForAccount
    /// walks a std::map keyed by guid, ObjectMgr.h:464), or null when the account has none.
    /// </summary>
    Task<byte?> FirstCharacterRaceAsync();

    /// <summary>A start row (vmangos PlayerInfo) exists for the race/class pair.</summary>
    Task<bool> HasStartInfoAsync(byte race, byte cls);
}

/// <summary>The fields of CMSG_CHAR_CREATE the rules look at.</summary>
public readonly record struct CharacterCreationRequest(byte[] RawName, byte Race, byte Class, byte Gender);

/// <summary>The outcome of the rules: <see cref="Result"/> is <see cref="CharResult.CharCreateSuccess"/> and <see cref="Name"/> set when the character may be created.</summary>
public readonly record struct CharacterCreationDecision(CharResult Result, string? Name)
{
    public bool Accepted => Result == CharResult.CharCreateSuccess;
}

/// <summary>
/// The checks of vmangos WorldSession::HandleCharCreateOpcode (CharacterHandler.cpp:185-322), in
/// the same order, over the facts the handler supplies. Pure of any database or session.
/// </summary>
/// <remarks>
/// Order: CharactersCreatingDisabled team mask (player security only, :193-216) → unknown race or
/// class (CHAR_CREATE_FAILED, :218-228) → race flagged NOT_PLAYABLE (CHAR_CREATE_DISABLED, :230-237)
/// → name (normalizePlayerName NO_NAME, CheckPlayerName, :246-262) → name in use (:270-274) →
/// characters per realm (:276-280) → PvP-realm one faction per account (:282-307) → no start row
/// (Player::Create fails, CHAR_CREATE_ERROR, Player.cpp:408-413). The reserved_name table and
/// NamesProfanity/NamesReserved lists, the CharSections appearance checks and the cross-realm
/// account limit are not part of this port (see docs/areas/character-creation.md); an out-of-range
/// gender is still refused with CHAR_CREATE_FAILED at the appearance step (:239-244).
/// </remarks>
public static class CharacterCreationRules
{
    public static async Task<CharacterCreationDecision> EvaluateAsync(
        CharacterCreationRequest request,
        AccountSecurity security,
        CharacterCreationOptions options,
        int charactersPerRealm,
        ICharacterCreationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(facts);

        bool staff = security > AccountSecurity.Player;
        bool legacy = options.Mode == CharacterCreationMode.Legacy;

        // 1. CharactersCreatingDisabled: bit 0 Alliance, bit 1 Horde, ordinary players only.
        if (!legacy && !staff && options.CharactersCreatingDisabled != 0)
        {
            uint bit = RaceClassRules.TeamForRace(request.Race) == ArcaneCore.Game.Entities.Team.Alliance ? 1u : 2u;
            if ((options.CharactersCreatingDisabled & bit) != 0)
            {
                return Refuse(CharResult.CharCreateDisabled);
            }
        }

        // 2-3. Race and class rows; a race flagged NOT_PLAYABLE answers DISABLED.
        if (legacy)
        {
            // Earlier builds: valid means "a start row exists for the pair".
            if (!await facts.HasStartInfoAsync(request.Race, request.Class).ConfigureAwait(false))
            {
                return Refuse(CharResult.CharCreateFailed);
            }
        }
        else if (!RaceClassRules.RaceExists(request.Race) || !RaceClassRules.ClassExists(request.Class))
        {
            return Refuse(CharResult.CharCreateFailed);
        }

        if (!legacy && !RaceClassRules.IsPlayableRace(request.Race))
        {
            return Refuse(CharResult.CharCreateDisabled);
        }

        // 4. Appearance: only the gender can be checked without CharSections.dbc.
        if (request.Gender > 1)
        {
            return Refuse(CharResult.CharCreateFailed);
        }

        // 5. Name.
        if (CharacterNames.ValidateUtf8(request.RawName, NameRuleSettings.From(options, create: true), out string? name) is { } nameError)
        {
            return Refuse(nameError);
        }

        // 7. Name in use.
        if (await facts.IsNameTakenAsync(name!).ConfigureAwait(false))
        {
            return Refuse(CharResult.CharCreateNameInUse);
        }

        // 9. Characters per realm, clamped to 1..10 (World.cpp:633).
        if (await facts.CountOnRealmAsync().ConfigureAwait(false) >= (legacy ? charactersPerRealm : CharacterCreationOptions.EffectiveCharactersPerRealm(charactersPerRealm)))
        {
            return Refuse(CharResult.CharCreateServerLimit);
        }

        // 10. PvP realm, one faction per account. Faithful to vmangos: only the account's first
        // character is compared, and a first character of race 0 always violates (:296-304).
        bool allowTwoSide = legacy || !options.IsPvPRealm || options.AllowTwoSideAccounts || staff;
        if (!allowTwoSide && await facts.FirstCharacterRaceAsync().ConfigureAwait(false) is { } firstRace
            && (firstRace == 0 || RaceClassRules.TeamForRace(firstRace) != RaceClassRules.TeamForRace(request.Race)))
        {
            return Refuse(CharResult.CharCreatePvpTeamsViolation);
        }

        // 11. Player::Create fails without a PlayerInfo row → CHAR_CREATE_ERROR.
        if (!await facts.HasStartInfoAsync(request.Race, request.Class).ConfigureAwait(false))
        {
            return Refuse(CharResult.CharCreateError);
        }

        return new CharacterCreationDecision(CharResult.CharCreateSuccess, name);
    }

    private static CharacterCreationDecision Refuse(CharResult result) => new(result, null);
}
