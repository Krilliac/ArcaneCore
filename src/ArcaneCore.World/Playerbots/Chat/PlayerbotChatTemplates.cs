namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// The built-in provider's lines (<see cref="PlayerbotBuiltinChat"/>). Each key holds several phrasings; a phrasing is used only when
/// every placeholder in it has a value, and never twice in a row to the same player. Placeholders: <c>{player}</c> (the speaker),
/// <c>{name}</c>, <c>{level}</c>, <c>{race}</c>, <c>{class}</c>, <c>{zone}</c>, <c>{subzone}</c>, <c>{goal}</c> / <c>{Goal}</c>
/// (what the bot is doing, from its brain or party AI), <c>{quest}</c> (the title of its current quest), <c>{master}</c>.
/// <para>
/// Keys: the intent (<c>greeting</c>, <c>thanks</c>, <c>bye</c>, <c>doing</c>, <c>doing.idle</c>, <c>doing.quest</c>, <c>where</c>,
/// <c>where.zone</c>, <c>where.unknown</c>, <c>who</c>, <c>bot</c>, <c>abuse</c>, <c>group.yes</c>, <c>group.no</c>,
/// <c>group.already</c>, <c>group.together</c>, <c>command.notmaster</c>, <c>command.nogroup</c>, <c>help.other</c>,
/// <c>unknown</c>; and for a line the safety screening flagged, <c>safety.selfharm</c> and <c>safety.personal</c>), plus variants added to the base list: <c>&lt;key&gt;.race.&lt;Race&gt;</c>, <c>&lt;key&gt;.class.&lt;Class&gt;</c>,
/// <c>&lt;key&gt;.level.novice</c> (below 10), <c>&lt;key&gt;.level.veteran</c> (60). Add a line by adding a string; add a variant by
/// adding a key. Lines state only what the bot knows of itself and well-known game mechanics: no invented lore.
/// </para>
/// </summary>
internal static class PlayerbotChatTemplates
{
    internal static readonly IReadOnlyDictionary<string, string[]> Lines = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["greeting"] = ["Hey, {player}.", "Hello there, {player}.", "Hi {player}, need something?", "Oh, hey {player}."],
        ["greeting.race.Orc"] = ["Lok'tar, {player}."],
        ["greeting.race.NightElf"] = ["Ishnu-alah, {player}."],
        ["greeting.race.Dwarf"] = ["Well met, {player}!"],
        ["greeting.race.Human"] = ["Well met, {player}."],
        ["greeting.race.Gnome"] = ["Hello hello, {player}!"],
        ["greeting.race.Tauren"] = ["Greetings, {player}."],
        ["greeting.race.Troll"] = ["Hey there, mon."],
        ["greeting.race.Undead"] = ["Yes, {player}?"],

        ["thanks"] = ["Any time.", "No problem, {player}.", "Glad to help.", "Sure thing."],
        ["bye"] = ["Take care, {player}.", "Safe travels.", "See you around, {player}.", "Later!"],

        ["doing"] = ["Right now I'm {goal}.", "Just {goal}.", "{Goal}, mostly.", "{Goal} at the moment."],
        ["doing.quest"] = ["Working on {quest}.", "Trying to finish {quest}.", "Still on {quest}."],
        ["doing.idle"] = ["Not much, just looking around.", "Taking it easy for a moment.", "Nothing much right now."],

        ["where"] = ["I'm at {subzone}, in {zone}.", "{subzone}, in {zone}.", "Around {subzone}. That's {zone}."],
        ["where.zone"] = ["I'm in {zone}.", "Somewhere in {zone}.", "{zone}, last I checked."],
        ["where.unknown"] = ["Honestly, I'm not sure what this place is called.", "No idea what this spot is called, sorry."],

        ["who"] = ["Level {level} {race} {class}.", "I'm a level {level} {class}.", "{name}, level {level} {class}."],
        ["who.level.novice"] = ["Just a level {level} {class}, still learning the ropes.", "Level {level}. Still pretty new at this."],
        ["who.level.veteran"] = ["Level {level} {class}. Can't get any higher than that.", "Sixty, finally. {race} {class}."],
        ["who.class.Warrior"] = ["Level {level} warrior. I hit things until they stop moving."],
        ["who.class.Paladin"] = ["Level {level} paladin. Plate, a hammer and a few blessings."],
        ["who.class.Hunter"] = ["Level {level} hunter. Me, my bow and my pet."],
        ["who.class.Rogue"] = ["Level {level} rogue. You probably didn't see me coming."],
        ["who.class.Priest"] = ["Level {level} priest. Stay in range and I'll keep you standing."],
        ["who.class.Shaman"] = ["Level {level} shaman. Totems and lightning, mostly."],
        ["who.class.Mage"] = ["Level {level} mage. Need water?"],
        ["who.class.Warlock"] = ["Level {level} warlock. Don't mind my demon."],
        ["who.class.Druid"] = ["Level {level} druid. Bear, cat or tree, whatever the day needs."],

        ["bot"] = ["Yes, I'm a bot run by this server.", "I am, yes. One of the server's bots.", "Yep, a server bot. Still happy to chat."],
        ["abuse"] = ["No need for that.", "Whatever you say.", "Moving on.", "Okay then."],
        ["safety.selfharm"] =
        [
            "That sounds really heavy, {player}. Please talk to someone you trust, or a local crisis line, about it.",
            "Hey, that matters more than any game. Please reach out to someone you trust or a crisis line near you.",
        ],
        ["safety.personal"] = ["Better not share personal details in chat, {player}.", "Keep your personal info to yourself out here."],

        ["group.yes"] = ["Sure, send me an invite.", "Invite me and I'll come along.", "Happy to. Send the invite."],
        ["group.no"] = ["Thanks, but I'll pass for now.", "Sorry, not looking for a group right now.", "Maybe another time."],
        ["group.already"] = ["I'm already in a group.", "Sorry, already grouped up."],
        ["group.together"] = ["We're already grouped, {player}.", "I'm right here in your group."],

        ["command.notmaster"] = ["Sorry, I take orders from {master}.", "I follow {master}'s lead, sorry."],
        ["command.nogroup"] = ["We're not grouped, {player}.", "Invite me to a group first."],
        ["help.other"] = ["Sorry, I can't help right now.", "I'm busy {goal}, sorry.", "Can't right now, sorry."],

        ["unknown"] = ["Hm? Not sure what you mean, {player}.", "Sorry, didn't catch that.", "Can't chat long, I'm {goal}.", "Huh?"],
    };
}
