namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// The built-in provider's lines (<see cref="PlayerbotBuiltinChat"/>). Each key holds several phrasings; a phrasing is used only when
/// every placeholder in it has a value, and never twice in a row to the same player. The voice is a 2005 player's: short, mostly lower
/// case, the slang of the time (lfg, lfm, wts, wtb, inv, ty, np, gz, oom, pst, rip, brb), nothing from after 1.12.
/// <para>
/// Placeholders from the bot's persona: <c>{player}</c> (the one spoken to), <c>{name}</c>, <c>{level}</c>, <c>{race}</c>, <c>{class}</c>,
/// <c>{zone}</c>, <c>{subzone}</c>, <c>{goal}</c> / <c>{Goal}</c> (what it is doing), <c>{quest}</c> (its current quest), <c>{master}</c>.
/// From its facts (<see cref="BotChatFacts"/>): <c>{hp}</c>, <c>{mana}</c>, <c>{ilvl}</c>, <c>{item}</c> (its best worn item), <c>{spec}</c>,
/// <c>{role}</c> (tank, healer, dps), <c>{guild}</c>, <c>{objective}</c> (its quest's open objective and progress), <c>{need}</c> (what it
/// lacks most), <c>{surplus}</c> / <c>{surpluscount}</c> (goods it carries spare), <c>{gold}</c>, <c>{bagfree}</c>, <c>{instance}</c>,
/// <c>{mob}</c> (a notable creature near it), <c>{dungeon}</c> (the dungeon of its level), <c>{otherquest}</c> (another quest in its log),
/// <c>{place}</c> / <c>{placezone}</c> / <c>{placemap}</c> (a place a player asked about, from the area data). Any other placeholder
/// (<c>{who}</c>, <c>{boss}</c>, <c>{newlevel}</c>, <c>{wanted}</c>, <c>{count}</c>, <c>{price}</c>, <c>{askedquest}</c>, ...) is a value
/// of the line's topic (<see cref="BotChatTopic.Values"/>).
/// </para>
/// <para>
/// Keys: the answers to a player (<c>greeting</c>, <c>thanks</c>, <c>bye</c>, <c>doing*</c>, <c>where*</c>, <c>place*</c>, <c>who*</c>,
/// <c>spec*</c>, <c>gold*</c>, <c>needs*</c>, <c>gear*</c>, <c>surplus*</c>, <c>group*</c>, <c>helpquest*</c>, <c>trade*</c>, <c>share*</c>,
/// <c>bot</c>, <c>abuse</c>, <c>praise</c>, <c>followup.*</c>, <c>command.*</c>, <c>help.other</c>, <c>unknown</c>, <c>safety.*</c>); the
/// lines bots start (<c>post.lfg</c>, <c>post.wts</c>); the whispers to a world channel line (<c>react.lfm</c>, <c>react.wtb</c>); and the
/// conversation steps between two bots (<c>convo.&lt;topic&gt;.&lt;step&gt;</c>, <see cref="BotSocial.PlayerbotConversations"/>). Variants
/// added to the base list: <c>&lt;key&gt;.race.&lt;Race&gt;</c>, <c>&lt;key&gt;.class.&lt;Class&gt;</c>, <c>&lt;key&gt;.level.novice</c>
/// (below 10), <c>&lt;key&gt;.level.veteran</c> (60). Lines state only what the bot knows of itself and well-known game mechanics.
/// </para>
/// </summary>
internal static class PlayerbotChatTemplates
{
    internal static readonly IReadOnlyDictionary<string, string[]> Lines = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        // --- answers to a player --------------------------------------------------------------------------------------------
        ["greeting"] = ["Hey, {player}.", "Hello there, {player}.", "Hi {player}, need something?", "Oh, hey {player}.", "hey", "yo {player}", "sup {player}", "hi :)"],
        // A player the bot knows (met at least 3 times and liked, or disliked: BuiltinChatIntent.Greeting, PlayerbotBuiltinChat.GreetingVariant).
        ["greeting.back"] = ["Welcome back, {player}!", "{player}! Good to see you again.", "Oh hey {player}, back again?", "Hey {player}, good to see you."],
        ["greeting.curt"] = ["{player}.", "Yes?", "What do you want, {player}?", "Hm."],
        // A bot losing a fight calls out (Social memory: PlayerbotFightSocial).
        ["callhelp"] = ["Help!", "I need help over here!", "Someone, help me!", "A hand, please!", "Little help here?"],
        ["greeting.race.Orc"] = ["Lok'tar, {player}."],
        ["greeting.race.NightElf"] = ["Ishnu-alah, {player}."],
        ["greeting.race.Dwarf"] = ["Well met, {player}!"],
        ["greeting.race.Human"] = ["Well met, {player}."],
        ["greeting.race.Gnome"] = ["Hello hello, {player}!"],
        ["greeting.race.Tauren"] = ["Greetings, {player}."],
        ["greeting.race.Troll"] = ["Hey there, mon."],
        ["greeting.race.Undead"] = ["Yes, {player}?"],

        ["thanks"] = ["Any time.", "No problem, {player}.", "Glad to help.", "Sure thing.", "np", "np {player}", "yw", "no prob"],
        ["praise"] = ["ty!", "thanks :)", "heh ty", "aw ty {player}"],
        ["bye"] = ["Take care, {player}.", "Safe travels.", "See you around, {player}.", "Later!", "cya", "later {player}", "gl out there", "cya around"],

        ["doing"] = ["Right now I'm {goal}.", "Just {goal}.", "{Goal}, mostly.", "{Goal} at the moment.", "{goal} atm"],
        ["doing.quest"] = ["Working on {quest}.", "Trying to finish {quest}.", "Still on {quest}.", "doing {quest}", "on {quest} atm"],
        ["doing.objective"] = ["on {quest}, {objective} so far", "{quest}. got {objective}", "still need more for {quest}, {objective}"],
        ["doing.ready"] = ["just finished {quest}, gotta turn it in", "done with {quest}, heading back to turn in"],
        ["doing.idle"] = ["Not much, just looking around.", "Taking it easy for a moment.", "Nothing much right now.", "nm, just chillin", "not much tbh"],
        ["doing.combat"] = ["sec, in combat", "kinda busy, fighting atm", "one sec, fighting {mob}", "busy, brb"],
        ["doing.dead"] = ["dead lol, running back", "corpse run, brb", "died, walking back to my body"],
        ["doing.instance"] = ["in {instance} atm", "running {instance}", "in {instance} with my group"],

        ["where"] = ["I'm at {subzone}, in {zone}.", "{subzone}, in {zone}.", "Around {subzone}. That's {zone}.", "{subzone} in {zone}", "near {subzone}, {zone}"],
        ["where.zone"] = ["I'm in {zone}.", "Somewhere in {zone}.", "{zone}, last I checked.", "{zone}"],
        ["where.instance"] = ["in {instance}", "inside {instance} atm"],
        ["where.unknown"] = ["Honestly, I'm not sure what this place is called.", "No idea what this spot is called, sorry."],

        ["place.zone"] = ["{place} is a zone in {placemap}", "{place}? thats in {placemap}", "{place} is over in {placemap}"],
        ["place.area"] = ["{place} is in {placezone}", "{place}? its in {placezone}", "thats in {placezone}, {placemap}", "{placezone}, on {placemap}"],
        ["place.here"] = ["{place}? thats right here in {placezone}", "{place} is here in {placezone}, not far"],
        ["place.unknown"] = ["never heard of it tbh", "no clue where that is, sorry", "not sure, check your map?"],

        ["who"] = ["Level {level} {race} {class}.", "I'm a level {level} {class}.", "{name}, level {level} {class}.", "{level} {class}", "lvl {level} {race} {class}"],
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

        ["spec"] = ["{spec}", "{spec} {class}", "im {spec}, so mostly {role}", "{spec} spec, i can {role}", "went {spec} this time"],
        ["spec.none"] = ["no talents yet, only {level}", "too low for talents lol", "lvl {level}, no spec yet"],

        ["gold"] = ["got about {gold}", "{gold}, not much lol", "around {gold}", "{gold}. saving up"],
        ["gold.poor"] = ["broke lol, {gold}", "barely {gold}, training eats it all", "poor, {gold} to my name"],
        ["gold.ask"] = ["sorry, barely got {gold} myself", "cant spare any, sorry", "lol no, im broke too"],

        ["needs"] = ["could use {need}", "need {need} tbh", "{need} would be nice", "running low on {need}", "mostly {need}"],
        ["needs.none"] = ["nah im good, ty", "all set, thanks", "nothing atm, ty", "im ok, thanks tho"],

        ["gear"] = ["avg ilvl around {ilvl}, best thing is {item}", "{item} is the best i got", "decent, {item} mostly", "got {item}, rest is meh"],
        ["gear.none"] = ["mostly vendor stuff lol", "nothing special yet"],

        ["surplus"] = ["got {surpluscount} {surplus} spare if you need", "have some {surplus} spare", "could sell {surplus} x{surpluscount}"],
        ["surplus.none"] = ["nothing spare, sorry", "bags are mostly junk lol"],

        ["group.yes"] = ["Sure, send me an invite.", "Invite me and I'll come along.", "Happy to. Send the invite.", "sure, inv", "ya inv me"],
        ["group.inviting"] = ["sure, inv inc", "sending inv", "k, invited", "inv sent"],
        ["group.no"] = ["Thanks, but I'll pass for now.", "Sorry, not looking for a group right now.", "Maybe another time."],
        ["group.already"] = ["I'm already in a group.", "Sorry, already grouped up."],
        ["group.together"] = ["We're already grouped, {player}.", "I'm right here in your group."],
        ["group.dungeon"] = ["{dungeon}? im in, inv", "sure, i could use {dungeon}", "im {role}, inv for {dungeon}"],

        ["helpquest.same"] = ["im on {askedquest} too, lets group", "same quest! inv?", "ya need that one too, group up?"],
        ["helpquest.yes"] = ["sure, i can help with {askedquest}", "k, inv me and ill help", "sure, where at?"],
        ["helpquest.busy"] = ["cant right now, doing {quest}", "maybe after {quest}, sorry", "busy atm, sorry"],
        ["helpquest.low"] = ["thats too high for me, sorry", "im only {level}, cant help there"],

        ["trade.yes"] = ["sure, open trade", "k, trade inc", "opening trade", "sure, sec"],
        ["trade.far"] = ["come closer and ill trade", "too far, come to me"],
        ["trade.no"] = ["sorry, not trading rn", "nah, sorry"],
        ["share.yes"] = ["sharing what i got", "shared, check your log", "k, sharing"],
        ["share.no"] = ["got nothing to share, sorry", "only the leader can ask me that"],

        ["bot"] = ["Yes, I'm a bot run by this server.", "I am, yes. One of the server's bots.", "Yep, a server bot. Still happy to chat."],
        ["abuse"] = ["No need for that.", "Whatever you say.", "Moving on.", "Okay then.", "k", "ok", "whatever"],
        ["safety.selfharm"] =
        [
            "That sounds really heavy, {player}. Please talk to someone you trust, or a local crisis line, about it.",
            "Hey, that matters more than any game. Please reach out to someone you trust or a crisis line near you.",
        ],
        ["safety.personal"] = ["Better not share personal details in chat, {player}.", "Keep your personal info to yourself out here."],

        ["followup.you"] = ["same, {goal}", "me? {goal}", "{Goal}", "just doing {quest}"],
        ["followup.why"] = ["need {need}", "need the xp", "gotta level somehow", "for {quest}"],
        ["followup.agree"] = ["ya", "yeah", "true", "yep", "for sure"],
        ["followup.laugh"] = ["lol", "haha", ":)", "heh"],
        ["followup.ok"] = ["k", "ok", "cool", "alright"],

        ["command.notmaster"] = ["Sorry, I take orders from {master}.", "I follow {master}'s lead, sorry."],
        ["command.nogroup"] = ["We're not grouped, {player}.", "Invite me to a group first."],
        ["help.other"] = ["Sorry, I can't help right now.", "I'm busy {goal}, sorry.", "Can't right now, sorry."],

        ["unknown"] = ["Hm? Not sure what you mean, {player}.", "Sorry, didn't catch that.", "Can't chat long, I'm {goal}.", "Huh?", "?", "huh"],

        // --- lines a bot starts in a world channel ----------------------------------------------------------------------------
        ["post.lfg"] = ["LFG {dungeon}, {level} {class}", "lvl {level} {class} LFG {dungeon}", "{role} LFG {dungeon} pst", "LFG {dungeon}, can {role}"],
        ["post.wts"] = ["WTS {surplus} x{surpluscount}, pst", "selling {surplus} x{surpluscount}, pst w/ offers", "WTS {surplus}, cheap, pst"],

        // --- whispers to a real player's world channel line -------------------------------------------------------------------
        ["react.lfm"] = ["inv", "inv pls", "{level} {class} here, inv?", "can {role} if you still need, inv", "id come, {level} {class}", "{role} here, inv for {dungeon}?"],
        ["react.wtb"] = ["got {surplus} x{surpluscount}, want it?", "i have {surplus}, {price} each?", "have {surplus} x{surpluscount}, cod or meet?", "got some {surplus}, still need?"],

        // --- bot-to-bot conversations (PlayerbotConversations): {player} is the other bot -------------------------------------
        ["convo.need.ask"] = ["anyone got {wanted}? all out", "{player}, u got any {wanted}?", "out of {wanted} again lol", "need {wanted}, anyone?"],
        ["convo.need.offer"] = ["ya got {count} {wanted}", "have some {wanted}, how many?", "i got {count}, want a few?", "sure, got extra {wanted}"],
        ["convo.need.thanks"] = ["few is fine, ty!", "awesome ty", "ty {player}", "u rock, ty"],
        ["convo.need.close"] = ["np", "np, open trade", "np, gl", "anytime"],

        ["convo.quest.ask"] = ["u on {quest} too?", "{player}, u doing {quest}?", "anyone else on {quest}?"],
        ["convo.quest.same"] = ["ya, {objective}", "yep, still need more", "ya, slow going lol"],
        ["convo.quest.other"] = ["nah, doing {quest}", "no, im on {quest}", "did it already"],
        ["convo.quest.group"] = ["wanna group up for it?", "group?", "lets team up, faster that way"],
        ["convo.quest.close"] = ["sure", "k", "gl then", "ok gl"],

        ["convo.boss.cheer"] = ["{boss} down!", "gj all", "nice, {boss} dead", "gg on {boss}"],
        ["convo.boss.reply"] = ["gg", "nice one", "gj", "that went well"],
        ["convo.boss.loot"] = ["anything good drop?", "loot?", "any upgrades?"],
        ["convo.boss.answer"] = ["nothing for me", "nope", "not for me, gz to whoever"],
        ["convo.boss.answerloot"] = ["got {loot}!", "{loot} for me :)", "finally got {loot}"],

        ["convo.ding.grats"] = ["gz {player}!", "grats on {newlevel}!", "gz", "ding! nice {player}"],
        ["convo.ding.thanks"] = ["ty!", "ty ty", "thx :)", "finally lol"],
        ["convo.ding.more"] = ["{newlevel} already, nice", "catching up to me lol", "not long now"],
        ["convo.ding.close"] = ["slowly getting there lol", "ya, grind continues", "heh"],

        ["convo.loot.grats"] = ["nice {loot}", "grats on {loot}", "ooh {loot}, nice", "lucky, {loot}"],
        ["convo.loot.thanks"] = ["ty, finally", "ty :)", "heh ty, about time"],
        ["convo.loot.close"] = ["jealous lol", "gz", "nice"],

        ["convo.death.ask"] = ["rip", "u ok?", "rip lol", "ouch, u ok?"],
        ["convo.death.mob"] = ["ya, {mob} got me", "{mob} hits hard", "pulled {mob} by accident lol"],
        ["convo.death.plain"] = ["ya, pulled too many", "lagged lol", "fine, just a corpse run", "ya, my bad"],
        ["convo.death.close"] = ["happens", "lol gl", "careful out there"],

        ["convo.chat.ask"] = ["what u up to?", "how goes it {player}?", "{player}, whatcha doing?", "busy?"],
        ["convo.chat.answer"] = ["{goal}", "on {quest}", "{Goal}, u?", "not much, u?"],
        ["convo.chat.back"] = ["same, {goal}", "{Goal}", "on {quest}", "leveling lol"],
        ["convo.chat.close"] = ["gl", "nice, gl", "k gl", "have fun"],

        ["convo.dungeon.ask"] = ["anyone up for {dungeon}?", "{player}, wanna do {dungeon} later?", "need {dungeon} at some point"],
        ["convo.dungeon.yes"] = ["maybe, need a tank tho", "id go, im {role}", "sure, after {quest}"],
        ["convo.dungeon.no"] = ["maybe later, still on {quest}", "not rn, sorry", "too low still"],
        ["convo.dungeon.close"] = ["k ill ask around", "cool", "k lmk"],

        ["convo.greet.hello"] = ["hey all", "hi guild", "o/", "hey guild"],
        ["convo.greet.reply"] = ["hey {player}", "yo", "hi {player}", "o/"],
    };
}
