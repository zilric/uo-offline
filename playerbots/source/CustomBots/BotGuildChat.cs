// =========================================================================
// BotGuildChat.cs — guild chat, and the bots that live in it.
//
// Guild chat does not work on a T2A shard. The client sends it with the
// guild message type, and PlayerMobile.DoSpeech only routes that to the
// guild when the new guild system is on (Core.SE). Here it falls through
// to ordinary speech: a local shout nobody in the guild hears. So this
// hooks the speech event, delivers the line to the guild itself, and
// blocks the local echo.
//
// Then the bots in the guild use the channel like people do, whenever a
// real guildmate is online to read it:
//
//   - random chatter ("lag", "anyone got spare regs", "just died lol");
//   - "what is everyone doing", answered by a few guildmates with what
//     they are really doing and where ("hunting near Yew", "sitting at
//     the Britain bank", "in Despise L1");
//   - "anyone wanna group up", from the player or from a bot. Guildmates
//     say "yeah", recall to the asker with a real Recall (or walk if
//     close, or say they cannot), stand where they land, and join: the
//     player's party if the player asked, a guild hunting crew if a bot
//     asked. If the player answers a bot's call, the player leads.
//   - a hello gets a hello back.
// =========================================================================

using System;
using System.Collections.Generic;
using Server.Engines.PartySystem;
using Server.Guilds;
using Server.Mobiles;
using Server.Network;

namespace Server.CustomBots
{
    public static class BotGuildChat
    {
        public static bool Enabled = true;

        // ---- Phrases ----

        // Asking for a group.
        private static readonly string[] GroupPhrases =
        {
            "group up", "party up", "wanna group", "wanna party", "want to group",
            "want to party", "lfg", "anyone want to hunt", "wanna hunt", "lets hunt",
            "lets go hunt", "anyone up for", "who wants to hunt", "group?", "party?",
            "anyone coming", "who's coming", "whos coming", "need a group", "dungeon run",
        };

        // Asking what everyone is doing.
        private static readonly string[] DoingPhrases =
        {
            "what is everyone doing", "whats everyone doing", "what's everyone doing",
            "what are you guys doing", "whats everyone up to", "what's everyone up to",
            "where is everyone", "where's everyone", "wheres everyone", "where you all at",
            "where are you guys", "anyone doing anything", "whos doing what", "who's doing what",
            "what are you doing", "what r u doing", "what u doing", "whats up guys",
        };

        // Saying yes to somebody's call.
        private static readonly string[] YesPhrases =
        {
            "yeah", "yea", "ya", "yes", "sure", "me", "omw", "im in", "i'm in", "ill come",
            "i'll come", "coming", "ok", "k", "yep", "yup", "on my way", "lets go", "let's go",
        };

        // A hello.
        private static readonly string[] GreetPhrases =
        {
            "hey", "hi", "hello", "sup", "yo", "hail", "whats up", "what's up", "anyone on", "anyone here",
        };

        // ---- Knobs ----

        // How many guildmates answer one group call.
        private const int MaxResponders = 4;

        // How many answer "what is everyone doing".
        private const int MaxStatusReplies = 4;

        // Close enough to just walk over and be invited.
        private const int WalkRange = 60;

        // Close enough to be invited after a recall or a walk.
        private const int InviteRange = 40;

        // How long a recall has to land, and a walk to arrive.
        private static readonly TimeSpan RecallDeadline = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan WalkDeadline = TimeSpan.FromMinutes(6);

        // How long a bot-led crew waits for the guildmates recalling in.
        private static readonly TimeSpan CrewHold = TimeSpan.FromSeconds(100);

        // Chatter cadence per guild, while a real guildmate is online.
        private static readonly TimeSpan ChatterMin = TimeSpan.FromSeconds(25);
        private static readonly TimeSpan ChatterMax = TimeSpan.FromSeconds(80);

        // What a chatter slot turns into.
        private const double AskDoingShare = 0.18;
        private const double AskGroupShare = 0.12;

        // A bot that just spoke waits this long before its next line.
        private static readonly TimeSpan BotCooldown = TimeSpan.FromSeconds(45);

        // A player's "yeah" counts as an answer to a bot's call this long.
        private static readonly TimeSpan OpenCallWindow = TimeSpan.FromSeconds(30);

        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

        // ---- State ----

        private sealed class Summons
        {
            public PlayerBot Bot;
            public Mobile Target;        // who to get to: the player, or the asking bot
            public Guild Guild;
            public BotParty Crew;        // set when a bot asked
            public DateTime Deadline;
            public DateTime NextInvite;
            public bool Arrived;
        }

        private sealed class Crew
        {
            public PlayerBot Leader;
            public BotParty Party;
            public Guild Guild;
            public DateTime HoldUntil;
            public int Coming;
            public int Arrived;
            public int Resolved;   // answered and either arrived or failed
        }

        private static readonly List<Summons> _summons = new();
        private static readonly List<Crew> _crews = new();
        private static readonly Dictionary<Guild, DateTime> _nextChatter = new();
        private static readonly Dictionary<PlayerBot, DateTime> _cooldown = new();

        // A bot's open call: a player's "yeah" inside the window sends the
        // crew to the player instead.
        private static Guild _openCallGuild;
        private static PlayerBot _openCallBy;
        private static DateTime _openCallAt;

        // Spam guard: one rally per asker per few seconds.
        private static Serial _lastAsker;
        private static DateTime _lastRally;

        public static void Configure()
        {
            EventSink.Speech += OnSpeech;
            Timer.DelayCall(TickInterval, TickInterval, OnTick);
        }

        // -------------------------------------------------------------------
        // Every line said on the shard passes here. Only guild-typed lines
        // from real players matter.
        // -------------------------------------------------------------------
        private static void OnSpeech(SpeechEventArgs e)
        {
            if (!Enabled || e == null || e.Type != MessageType.Guild)
            {
                return;
            }

            var from = e.Mobile;
            if (from is not PlayerMobile || from is PlayerBot || from.Deleted)
            {
                return;
            }

            if (from.Guild is not Guild g || g.Disbanded)
            {
                from.SendLocalizedMessage(1063142); // You are not in a guild!
                e.Blocked = true;
                return;
            }

            // Deliver it to the guild, which this era's server never does.
            g.GuildChat(from, e.Hue, e.Speech);
            e.Blocked = true;

            if (string.IsNullOrWhiteSpace(e.Speech))
            {
                return;
            }

            var lower = e.Speech.Trim().ToLowerInvariant();

            if (MatchesAny(lower, GroupPhrases))
            {
                Rally(g, from);
                return;
            }

            if (MatchesAny(lower, DoingPhrases))
            {
                AnswerDoing(g, from);
                return;
            }

            // "yeah" to a bot's open call: the player leads instead.
            if (_openCallGuild == g && Core.Now - _openCallAt < OpenCallWindow &&
                IsYes(lower))
            {
                Console.WriteLine($"[guild] {from.Name} answered {_openCallBy?.Name}'s call — the crew goes to {from.Name}");
                _openCallGuild = null;
                CancelCrewOf(_openCallBy);
                Rally(g, from);
                return;
            }

            if (IsGreeting(lower))
            {
                AnswerGreeting(g, from);
            }
        }

        private static bool MatchesAny(string lower, string[] phrases)
        {
            foreach (var p in phrases)
            {
                if (lower.Contains(p, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsYes(string lower)
        {
            var t = lower.TrimEnd('!', '.', ' ');
            foreach (var y in YesPhrases)
            {
                if (t == y || t.StartsWith(y + " ", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsGreeting(string lower)
        {
            var t = lower.TrimEnd('!', '.', '?', ' ');
            foreach (var h in GreetPhrases)
            {
                if (t == h || t.StartsWith(h + " ", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // -------------------------------------------------------------------
        // The chatter clock. Each guild with a real member online gets a
        // line from one of its bots now and then.
        // -------------------------------------------------------------------
        private static void TickChatter()
        {
            foreach (var bg in World.Guilds.Values)
            {
                if (bg is not Guild g || g.Disbanded)
                {
                    continue;
                }

                if (!_nextChatter.TryGetValue(g, out var at))
                {
                    _nextChatter[g] = Core.Now + Random(ChatterMin, ChatterMax);
                    continue;
                }
                if (Core.Now < at)
                {
                    continue;
                }
                _nextChatter[g] = Core.Now + Random(ChatterMin, ChatterMax);

                if (!SomeoneOnline(g))
                {
                    continue;
                }

                var bots = GuildBots(g);
                if (bots.Count == 0)
                {
                    continue;
                }

                var speaker = PickSpeaker(bots);
                if (speaker == null)
                {
                    continue;
                }

                double roll = Utility.RandomDouble();
                if (roll < AskGroupShare && speaker.Party == null &&
                    BotPartyManager.CanLeadHunt(speaker) &&
                    bots.Count >= 2 && _crews.Count == 0)
                {
                    BotAsksForGroup(g, speaker);
                }
                else if (roll < AskGroupShare + AskDoingShare && bots.Count >= 2)
                {
                    SayGuild(speaker, g, "guild_ask_doing");
                    AnswerDoing(g, speaker);
                }
                else
                {
                    SayGuild(speaker, g, "guild_chatter");
                }
            }

            if (_nextChatter.Count > 64)
            {
                var gone = new List<Guild>();
                foreach (var (g, _) in _nextChatter)
                {
                    if (g.Disbanded)
                    {
                        gone.Add(g);
                    }
                }
                foreach (var g in gone)
                {
                    _nextChatter.Remove(g);
                }
            }
        }

        // A real member with a client connected, or the test rig.
        private static bool SomeoneOnline(Guild g)
        {
            foreach (var m in g.Members)
            {
                if (m is PlayerMobile && m is not PlayerBot && !m.Deleted &&
                    (m.NetState != null || BotGuildTest.IsRig(m)))
                {
                    return true;
                }
            }
            return false;
        }

        private static List<PlayerBot> GuildBots(Guild g)
        {
            var list = new List<PlayerBot>();
            foreach (var m in g.Members)
            {
                if (m is PlayerBot bot && !bot.Deleted && bot.Alive && !bot.LoggingOut &&
                    bot.Map != null && bot.Map != Map.Internal)
                {
                    list.Add(bot);
                }
            }
            return list;
        }

        private static PlayerBot PickSpeaker(List<PlayerBot> bots)
        {
            Shuffle(bots);
            foreach (var b in bots)
            {
                if (!OnCooldown(b) && b.Combatant == null)
                {
                    return b;
                }
            }
            return null;
        }

        // -------------------------------------------------------------------
        // "what is everyone doing" — a few guildmates say what they are up
        // to and where. Works for a player asking and a bot asking.
        // -------------------------------------------------------------------
        private static void AnswerDoing(Guild g, Mobile asker)
        {
            var bots = GuildBots(g);
            Shuffle(bots);
            int said = 0;
            foreach (var bot in bots)
            {
                if (bot == asker || OnCooldown(bot) || said >= MaxStatusReplies)
                {
                    continue;
                }
                var line = Doing(bot);
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }
                SetCooldown(bot);
                var delay = TimeSpan.FromSeconds(1.5 + said * 2.2 + Utility.RandomDouble() * 2);
                said++;
                var b = bot;
                Timer.DelayCall(delay, () =>
                {
                    if (!b.Deleted && b.Alive && b.Guild == g)
                    {
                        SayGuildLine(b, g, line);
                    }
                });
            }
        }

        // What a bot is really doing, in its own words.
        private static string Doing(PlayerBot bot)
        {
            var place = BotEventJournal.PlaceName(bot.Location, bot.Map);
            if (!bot.Alive)
            {
                return Pick($"dead at {place} lol", $"ghost. {place}", $"need a res at {place}");
            }
            if (bot.Combatant is Mobile foe && !foe.Deleted && foe.Alive)
            {
                return Pick($"fighting {foe.Name} at {place}", $"busy, {foe.Name} on me", $"in a fight at {place}");
            }

            switch (bot.Behavior)
            {
                case BankSitterBehavior:
                    return Pick($"at the {place} bank", $"bank sitting in {place}", $"sitting at {place}", $"{place} bank, bored");
                case DungeonCrawlerBehavior dc:
                    var where = string.IsNullOrEmpty(dc.DungeonName) ? place : $"{dc.DungeonName} L{dc.Level}";
                    return Pick($"in {where}", $"crawling {where}", $"{where}, its ok loot", $"clearing {where}");
                case ThiefBehavior:
                    return Pick($"just chilling at {place}", $"around {place}", $"{place}, nothing much", $"at {place} minding my business");
                case GathererBehavior:
                    return bot.Class == BotClass.Miner
                        ? Pick($"mining at {place}", $"getting ore near {place}", $"mining, {place}")
                        : Pick($"chopping wood at {place}", $"getting logs near {place}", $"lumberjacking at {place}");
                case CrafterBehavior:
                    return Pick($"crafting in {place}", $"at the shop in {place}", $"working, {place}", $"making stuff in {place}");
                case TravelerBehavior t:
                    return string.IsNullOrEmpty(t.DestinationName)
                        ? Pick($"on the road near {place}", $"walking, near {place}")
                        : Pick($"heading to {Humanize(t.DestinationName)}", $"on my way to {Humanize(t.DestinationName)}", $"walking to {Humanize(t.DestinationName)} from {place}");
                case PlayerGroupBehavior:
                    return Pick($"with a group at {place}", $"grouped up near {place}");
                case PartyMemberBehavior:
                    return Pick($"with the crew near {place}", $"heading out with a group, {place}");
                case ShopperBehavior:
                    return Pick($"shopping in {place}", $"at the vendors in {place}", $"buying regs in {place}");
                case VisitorBehavior:
                    return Pick($"in {place}", $"hanging around {place}", $"at {place}");
                case TamerBehavior:
                    return Pick($"taming near {place}", $"out with the pets by {place}");
                case TreasureHunterBehavior:
                    return Pick($"digging a map near {place}", $"treasure hunting, {place}");
                case GhostExitBehavior:
                case GhostBehavior:
                    return Pick($"dead at {place}", $"looking for a res, {place}");
                // Last: most fighting brains derive from it.
                case AdventurerBehavior:
                    return Pick($"hunting near {place}", $"killing stuff around {place}", $"out by {place}", $"farming near {place}");
                default:
                    return Pick($"at {place}", $"around {place}", $"nothing, {place}", $"{place}");
            }
        }

        // "Britain Provisioner 11" -> "britain provisioner", "Despise L1
        // Entrance" -> "despise". Enough to read as a person typing.
        private static string Humanize(string dest)
        {
            var s = dest.ToLowerInvariant();
            var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var keep = new List<string>();
            foreach (var w in words)
            {
                if (w.Length == 0 || char.IsDigit(w[0]) || w == "entrance" || w == "l1" || w == "l2" || w == "l3" || w == "l4")
                {
                    continue;
                }
                keep.Add(w);
            }
            return keep.Count == 0 ? s : string.Join(' ', keep);
        }

        private static string Pick(params string[] lines) => lines[Utility.Random(lines.Length)];

        // -------------------------------------------------------------------
        // A hello gets a hello or two.
        // -------------------------------------------------------------------
        private static void AnswerGreeting(Guild g, Mobile from)
        {
            var bots = GuildBots(g);
            Shuffle(bots);
            int said = 0;
            foreach (var bot in bots)
            {
                if (said >= 2 || OnCooldown(bot))
                {
                    continue;
                }
                var line = ChatLibrary.PickRandom("guild_greet");
                if (string.IsNullOrEmpty(line))
                {
                    return;
                }
                line = line.Replace("{name}", FirstName(from.Name), StringComparison.Ordinal);
                SetCooldown(bot);
                var delay = TimeSpan.FromSeconds(1.0 + said * 1.8 + Utility.RandomDouble() * 1.5);
                said++;
                var b = bot;
                Timer.DelayCall(delay, () =>
                {
                    if (!b.Deleted && b.Alive && b.Guild == g)
                    {
                        SayGuildLine(b, g, line);
                    }
                });
            }
        }

        private static string FirstName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name;
            }
            int space = name.IndexOf(' ');
            return space > 0 ? name[..space] : name;
        }

        // -------------------------------------------------------------------
        // The player calls for a group. A few guild bots answer, staggered
        // like people reading the line.
        // -------------------------------------------------------------------
        private static void Rally(Guild g, Mobile player)
        {
            if (_lastAsker == player.Serial && Core.Now - _lastRally < TimeSpan.FromSeconds(8))
            {
                return;
            }
            _lastAsker = player.Serial;
            _lastRally = Core.Now;

            // The asker leads. Somebody else's party is theirs to fill.
            var party = Party.Get(player);
            if (party != null && party.Leader != player)
            {
                return;
            }
            int slots = Party.Capacity - (party?.Members.Count ?? 1) - (party?.Candidates.Count ?? 0);
            if (slots <= 0)
            {
                return;
            }

            var cands = new List<PlayerBot>();
            foreach (var m in g.Members)
            {
                if (m is PlayerBot bot && Eligible(bot, player))
                {
                    cands.Add(bot);
                }
            }
            if (cands.Count == 0)
            {
                return;
            }

            Shuffle(cands);
            int take = Math.Min(Math.Min(MaxResponders, slots), cands.Count);

            Console.WriteLine(
                $"[guild] {player.Name} called for a group in [{g.Abbreviation}] — " +
                $"{take} of {cands.Count} guild bot(s) answering");

            for (int i = 0; i < take; i++)
            {
                var bot = cands[i];
                var delay = TimeSpan.FromSeconds(1.2 + i * 1.6 + Utility.RandomDouble() * 1.5);
                Timer.DelayCall(delay, () => Answer(bot, player, g, null));
            }
        }

        // -------------------------------------------------------------------
        // A bot calls for a group. It leads a guild hunting crew; the
        // guildmates recall to it and are added as they land. If the player
        // says "yeah" within the window, the player leads instead.
        // -------------------------------------------------------------------
        public static bool BotAsksForGroup(Guild g, PlayerBot asker)
        {
            if (asker == null || asker.Deleted || !asker.Alive || asker.Guild != g ||
                asker.Party != null || !BotPartyManager.CanLeadHunt(asker))
            {
                return false;
            }

            var cands = new List<PlayerBot>();
            foreach (var m in g.Members)
            {
                if (m is PlayerBot bot && bot != asker && Eligible(bot, asker))
                {
                    cands.Add(bot);
                }
            }

            SayGuild(asker, g, "guild_ask_group");
            SetCooldown(asker);

            _openCallGuild = g;
            _openCallBy = asker;
            _openCallAt = Core.Now;

            if (cands.Count == 0)
            {
                return true; // the call stands; only the player can answer
            }

            var party = BotPartyManager.FormGuildCrew(asker, CrewHold);
            if (party == null)
            {
                return false;
            }

            var crew = new Crew
            {
                Leader = asker,
                Party = party,
                Guild = g,
                HoldUntil = Core.Now + CrewHold,
            };
            _crews.Add(crew);

            // The leader waits where it stands for the crew.
            asker.Behavior = new IdleBehavior();

            Shuffle(cands);
            int take = Math.Min(Math.Min(MaxResponders, cands.Count), Utility.RandomMinMax(2, 4));
            crew.Coming = take;
            Console.WriteLine(
                $"[guild] {asker.Name} called for a group in [{g.Abbreviation}] — " +
                $"{take} of {cands.Count} guild bot(s) answering");

            for (int i = 0; i < take; i++)
            {
                var bot = cands[i];
                var delay = TimeSpan.FromSeconds(1.5 + i * 1.7 + Utility.RandomDouble() * 1.5);
                Timer.DelayCall(delay, () => Answer(bot, asker, g, party));
            }
            return true;
        }

        private static void CancelCrewOf(PlayerBot leader)
        {
            if (leader == null)
            {
                return;
            }
            for (int i = _crews.Count - 1; i >= 0; i--)
            {
                if (_crews[i].Leader == leader)
                {
                    _crews.RemoveAt(i);
                }
            }
            for (int i = _summons.Count - 1; i >= 0; i--)
            {
                if (_summons[i].Target == leader)
                {
                    _summons.RemoveAt(i);
                }
            }
            BotPartyManager.DisbandInvolving(leader);
            if (leader.Behavior is IdleBehavior)
            {
                leader.Behavior = RedTerritory.TravelBrain(leader);
            }
        }

        private static bool Eligible(PlayerBot bot, Mobile target)
        {
            if (bot == null || bot.Deleted || !bot.Alive || bot.LoggingOut ||
                bot.Map != target.Map || bot.Map == null || bot.Map == Map.Internal ||
                bot.Party != null || BotPartyManager.IsInParty(bot))
            {
                return false;
            }

            foreach (var s in _summons)
            {
                if (s.Bot == bot)
                {
                    return false;
                }
            }
            foreach (var c in _crews)
            {
                if (c.Leader == bot)
                {
                    return false;
                }
            }

            return BotPlayerParty.CanJoin(bot, out _);
        }

        private static void Answer(PlayerBot bot, Mobile target, Guild g, BotParty crew)
        {
            if (target.Deleted || !Eligible(bot, target) || bot.Guild != g)
            {
                return;
            }
            if (crew != null && !StillMustering(crew))
            {
                return;
            }

            var s = new Summons { Bot = bot, Target = target, Guild = g, Crew = crew };

            int dist = Cheb(bot.Location, target.Location);
            if (dist <= WalkRange)
            {
                SayGuild(bot, g, "guild_group_yes");
                s.Deadline = Core.Now + WalkDeadline;
                _summons.Add(s);
                return;
            }

            if (MagicTravel.CanTravel(bot))
            {
                SayGuild(bot, g, "guild_group_yes");
                s.Deadline = Core.Now + RecallDeadline;
                _summons.Add(s);

                // The cast comes a beat after the "yeah".
                Timer.DelayCall(TimeSpan.FromSeconds(1.5 + Utility.RandomDouble()), () =>
                {
                    if (bot.Deleted || !bot.Alive || target.Deleted)
                    {
                        return;
                    }
                    if (!MagicTravel.TryBeginTrip(bot, null, target.Location,
                            DestinationType.CityCenter, required: true))
                    {
                        // Fizzled out of the gate; walking is the answer.
                        SayGuild(bot, g, "guild_group_cant");
                        StartWalking(bot, target);
                        s.Deadline = Core.Now + WalkDeadline;
                    }
                });
                return;
            }

            // No way to recall. Say so and walk.
            SayGuild(bot, g, "guild_group_cant");
            s.Deadline = Core.Now + WalkDeadline;
            _summons.Add(s);
            StartWalking(bot, target);
        }

        private static bool StillMustering(BotParty party)
        {
            foreach (var c in _crews)
            {
                if (c.Party == party)
                {
                    return true;
                }
            }
            return false;
        }

        // Walk toward the asker: the nearest catalog destination to where
        // they are standing. The party follow takes over once invited.
        private static void StartWalking(PlayerBot bot, Mobile target)
        {
            BotDestination best = null;
            double bestDist = double.MaxValue;
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type is DestinationType.DungeonRoom or DestinationType.DungeonDescend
                    or DestinationType.DungeonAscend or DestinationType.TreasureSite)
                {
                    continue;
                }
                var dist = Math.Max(Math.Abs(d.Location.X - target.X), Math.Abs(d.Location.Y - target.Y));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = d;
                }
            }
            if (best == null)
            {
                return;
            }
            bot.Behavior = new TravelerBehavior { DestinationName = best.Name };
        }

        // -------------------------------------------------------------------
        // Every two seconds: who has arrived, who gets the invite, who is
        // not going to make it; and the chatter clock.
        // -------------------------------------------------------------------
        private static void OnTick()
        {
            if (!Enabled)
            {
                return;
            }

            TickChatter();
            TickSummons();
            TickCrews();
        }

        private static void TickSummons()
        {
            for (int i = _summons.Count - 1; i >= 0; i--)
            {
                var s = _summons[i];
                var bot = s.Bot;
                var target = s.Target;

                if (bot.Deleted || !bot.Alive || target.Deleted || target.Map != bot.Map ||
                    bot.Guild != s.Guild || (s.Crew != null && !StillMustering(s.Crew)))
                {
                    _summons.RemoveAt(i);
                    Resolve(s);
                    Standing(bot);
                    continue;
                }

                // ---- Bot-led: join the crew on arrival ----
                if (s.Crew != null)
                {
                    if (bot.InRange(target.Location, InviteRange) && !bot.Hidden)
                    {
                        if (BotPartyManager.AddToCrew(s.Crew, bot))
                        {
                            Console.WriteLine($"[guild] {bot.Name} got to {target.Name} and joined the crew");
                            foreach (var c in _crews)
                            {
                                if (c.Party == s.Crew)
                                {
                                    c.Arrived++;
                                }
                            }
                        }
                        _summons.RemoveAt(i);
                        Resolve(s);
                        continue;
                    }
                    if (Core.Now >= s.Deadline)
                    {
                        SayGuild(bot, s.Guild, "guild_group_cant");
                        Console.WriteLine($"[guild] {bot.Name} never made it to {target.Name}");
                        _summons.RemoveAt(i);
                        Resolve(s);
                        Standing(bot);
                    }
                    continue;
                }

                // ---- Player-led: the party invite ----
                if (bot.Party is Party p && p.Members.Count > 0 && Party.Get(target) == p)
                {
                    if (!s.Arrived && bot.InRange(target.Location, 12))
                    {
                        s.Arrived = true;
                        var line = ChatLibrary.PickRandom("guild_group_arrive");
                        if (!string.IsNullOrEmpty(line))
                        {
                            bot.Say(line);
                        }
                        Console.WriteLine($"[guild] {bot.Name} grouped up with {target.Name}");
                        _summons.RemoveAt(i);
                    }
                    else if (Core.Now >= s.Deadline)
                    {
                        _summons.RemoveAt(i);
                    }
                    continue;
                }

                if (Core.Now >= s.Deadline)
                {
                    SayGuild(bot, s.Guild, "guild_group_cant");
                    Console.WriteLine($"[guild] {bot.Name} never made it to {target.Name}");
                    _summons.RemoveAt(i);
                    Standing(bot);
                    continue;
                }

                // Close enough: the invite. BotPlayerParty accepts it on the
                // bot's next tick and hands it the party brain.
                if (bot.Party == null && Core.Now >= s.NextInvite &&
                    bot.InRange(target.Location, InviteRange) && !bot.Hidden)
                {
                    s.NextInvite = Core.Now + TimeSpan.FromSeconds(10);
                    BotPlayerParty.TryRecruitToPlayer(target, bot, 0.5);
                }
            }
        }

        // A bot-led crew: march once everyone who said yes has landed, or
        // give up if nobody came.
        private static void TickCrews()
        {
            for (int i = _crews.Count - 1; i >= 0; i--)
            {
                var c = _crews[i];
                var leader = c.Leader;

                if (leader.Deleted || !leader.Alive || BotPartyManager.PartyOf(leader) != c.Party)
                {
                    _crews.RemoveAt(i);
                    continue;
                }

                // Everyone who said yes has landed or given up, or the
                // hold ran out.
                if (c.Resolved >= c.Coming || Core.Now >= c.HoldUntil)
                {
                    if (c.Party.Members.Count == 0)
                    {
                        SayGuild(leader, c.Guild, "guild_group_nvm");
                        Console.WriteLine($"[guild] nobody made it to {leader.Name}'s call");
                        BotPartyManager.DisbandInvolving(leader);
                        if (leader.Behavior is IdleBehavior)
                        {
                            leader.Behavior = RedTerritory.TravelBrain(leader);
                        }
                    }
                    else
                    {
                        Console.WriteLine(
                            $"[guild] {leader.Name}'s crew of {c.Party.Members.Count + 1} sets out for " +
                            $"{c.Party.Target?.Dungeon}");
                        BotPartyManager.ReleaseHold(c.Party);
                    }
                    if (_openCallBy == leader)
                    {
                        _openCallGuild = null;
                    }
                    _crews.RemoveAt(i);
                }
            }
        }

        private static void Resolve(Summons s)
        {
            if (s.Crew == null)
            {
                return;
            }
            foreach (var c in _crews)
            {
                if (c.Party == s.Crew)
                {
                    c.Resolved++;
                }
            }
        }

        // A summoned bot left standing idle goes back to its life.
        private static void Standing(PlayerBot bot)
        {
            if (!bot.Deleted && bot.Alive && bot.Behavior is IdleBehavior &&
                !BotPartyManager.IsInParty(bot) && bot.Party == null)
            {
                bot.Behavior = RedTerritory.TravelBrain(bot);
            }
        }

        // MagicTravel asks this when a bot lands. A summoned bot that came
        // down near its target stands still for the invite; one that landed
        // somewhere else (a recall that never took) travels as usual.
        public static bool OnLanded(PlayerBot bot)
        {
            foreach (var s in _summons)
            {
                if (s.Bot != bot || s.Target.Deleted)
                {
                    continue;
                }
                if (bot.Map != s.Target.Map || !bot.InRange(s.Target.Location, InviteRange))
                {
                    return false;
                }
                bot.Behavior = new IdleBehavior();
                s.NextInvite = Core.Now;
                return true;
            }
            return false;
        }

        // ---- Speaking ----

        // A line in guild chat. Bots have no client, so the console gets a
        // copy for anyone watching headless.
        private static void SayGuild(PlayerBot bot, Guild g, string category)
        {
            var line = ChatLibrary.PickRandom(category);
            SayGuildLine(bot, g, line);
        }

        private static void SayGuildLine(PlayerBot bot, Guild g, string line)
        {
            if (string.IsNullOrEmpty(line) || bot.Deleted || g == null)
            {
                return;
            }
            if (Utility.RandomDouble() < 0.30 && line.Length > 0 && char.IsLower(line[0]))
            {
                line = char.ToUpper(line[0]) + line[1..];
            }
            g.GuildChat(bot, bot.SpeechHue, line);
            Console.WriteLine($"[guild] [{g.Abbreviation}] {bot.Name}: {line}");
        }

        private static bool OnCooldown(PlayerBot bot) =>
            _cooldown.TryGetValue(bot, out var until) && Core.Now < until;

        private static void SetCooldown(PlayerBot bot)
        {
            if (_cooldown.Count > 500)
            {
                _cooldown.Clear();
            }
            _cooldown[bot] = Core.Now + BotCooldown;
        }

        private static TimeSpan Random(TimeSpan min, TimeSpan max) =>
            TimeSpan.FromSeconds(min.TotalSeconds + Utility.RandomDouble() * (max - min).TotalSeconds);

        private static void Shuffle(List<PlayerBot> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Utility.Random(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private static int Cheb(Point3D a, Point3D b)
        {
            int dx = Math.Abs(a.X - b.X);
            int dy = Math.Abs(a.Y - b.Y);
            return dx > dy ? dx : dy;
        }

        public static int Pending => _summons.Count;
        public static int Crews => _crews.Count;
    }
}
