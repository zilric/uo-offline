// =========================================================================
// BotGuildChat.cs — guild chat, and "anyone wanna group up".
//
// Guild chat does not work on a T2A shard. The client sends it with the
// guild message type, and PlayerMobile.DoSpeech only routes that to the
// guild when the new guild system is on (Core.SE). Here it falls through
// to ordinary speech: a local shout nobody in the guild hears. So this
// hooks the speech event, delivers the line to the guild itself, and
// blocks the local echo.
//
// Then it reads the line. A guild member asking for a group ("anyone
// wanna group up", "lfg", "wanna hunt") gets an answer from the bots in
// the guild: a few of them say "yeah" in guild chat, recall to the asker
// (a real Recall, so it needs the magery, the regs or a scroll), and
// join the party. One that cannot recall says so and walks. One that is
// already close just walks over.
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

        // What counts as asking for a group.
        private static readonly string[] GroupPhrases =
        {
            "group up", "party up", "wanna group", "wanna party", "want to group",
            "want to party", "lfg", "anyone want to hunt", "wanna hunt", "lets hunt",
            "lets go hunt", "anyone up for", "who wants to hunt", "group?", "party?",
            "anyone coming", "who's coming", "whos coming", "need a group",
        };

        // How many guildmates answer one call.
        private const int MaxResponders = 4;

        // Close enough to just walk over and be invited.
        private const int WalkRange = 60;

        // Close enough to be invited after a recall or a walk.
        private const int InviteRange = 40;

        // How long a recall has to land, and a walk to arrive.
        private static readonly TimeSpan RecallDeadline = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan WalkDeadline = TimeSpan.FromMinutes(6);

        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

        private sealed class Summons
        {
            public PlayerBot Bot;
            public Mobile Player;
            public Guild Guild;
            public DateTime Deadline;
            public DateTime NextInvite;
            public bool Arrived;
        }

        private static readonly List<Summons> _summons = new();

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

            var lower = e.Speech.ToLowerInvariant();
            if (!MatchesAny(lower, GroupPhrases))
            {
                return;
            }

            Rally(g, from);
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

        // -------------------------------------------------------------------
        // The call goes out. A few guild bots answer, staggered like people
        // reading the line.
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
                Timer.DelayCall(delay, () => Answer(bot, player, g));
            }
        }

        private static bool Eligible(PlayerBot bot, Mobile player)
        {
            if (bot == null || bot.Deleted || !bot.Alive || bot.LoggingOut ||
                bot.Map != player.Map || bot.Map == null || bot.Map == Map.Internal ||
                bot.Party != null)
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

            return BotPlayerParty.CanJoin(bot, out _);
        }

        private static void Answer(PlayerBot bot, Mobile player, Guild g)
        {
            if (player.Deleted || !Eligible(bot, player) || bot.Guild != g)
            {
                return;
            }

            var s = new Summons { Bot = bot, Player = player, Guild = g };

            int dist = Cheb(bot.Location, player.Location);
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
                    if (bot.Deleted || !bot.Alive || player.Deleted)
                    {
                        return;
                    }
                    if (!MagicTravel.TryBeginTrip(bot, null, player.Location,
                            DestinationType.CityCenter, required: true))
                    {
                        // Fizzled out of the gate; walking is the answer.
                        SayGuild(bot, g, "guild_group_cant");
                        StartWalking(bot, player);
                        s.Deadline = Core.Now + WalkDeadline;
                    }
                });
                return;
            }

            // No way to recall. Say so and walk.
            SayGuild(bot, g, "guild_group_cant");
            s.Deadline = Core.Now + WalkDeadline;
            _summons.Add(s);
            StartWalking(bot, player);
        }

        // Walk toward the asker: the nearest catalog destination to where
        // they are standing. The party follow takes over once invited.
        private static void StartWalking(PlayerBot bot, Mobile player)
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
                var dist = Math.Max(Math.Abs(d.Location.X - player.X), Math.Abs(d.Location.Y - player.Y));
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
        // not going to make it.
        // -------------------------------------------------------------------
        private static void OnTick()
        {
            for (int i = _summons.Count - 1; i >= 0; i--)
            {
                var s = _summons[i];
                var bot = s.Bot;
                var player = s.Player;

                if (bot.Deleted || !bot.Alive || player.Deleted || player.Map != bot.Map ||
                    bot.Guild != s.Guild)
                {
                    _summons.RemoveAt(i);
                    continue;
                }

                // In the party: done, once it has said hello.
                if (bot.Party is Party p && p.Members.Count > 0 && Party.Get(player) == p)
                {
                    if (!s.Arrived && bot.InRange(player.Location, 12))
                    {
                        s.Arrived = true;
                        var line = ChatLibrary.PickRandom("guild_group_arrive");
                        if (!string.IsNullOrEmpty(line))
                        {
                            bot.Say(line);
                        }
                        Console.WriteLine($"[guild] {bot.Name} grouped up with {player.Name}");
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
                    Console.WriteLine($"[guild] {bot.Name} never made it to {player.Name}");
                    _summons.RemoveAt(i);
                    if (bot.Behavior is IdleBehavior)
                    {
                        bot.Behavior = RedTerritory.TravelBrain(bot);
                    }
                    continue;
                }

                // Close enough: the invite. BotPlayerParty accepts it on the
                // bot's next tick and hands it the party brain.
                if (bot.Party == null && Core.Now >= s.NextInvite &&
                    bot.InRange(player.Location, InviteRange) && !bot.Hidden)
                {
                    s.NextInvite = Core.Now + TimeSpan.FromSeconds(10);
                    BotPlayerParty.TryRecruitToPlayer(player, bot, 0.5);
                }
            }
        }

        // MagicTravel asks this when a bot lands. A summoned bot that came
        // down near the asker stands still for the invite; one that landed
        // somewhere else (a recall that never took) travels as usual.
        public static bool OnLanded(PlayerBot bot)
        {
            foreach (var s in _summons)
            {
                if (s.Bot != bot || s.Player.Deleted)
                {
                    continue;
                }
                if (bot.Map != s.Player.Map || !bot.InRange(s.Player.Location, InviteRange))
                {
                    return false;
                }
                bot.Behavior = new IdleBehavior();
                s.NextInvite = Core.Now;
                return true;
            }
            return false;
        }

        // A line in guild chat. Bots have no client, so the console gets a
        // copy for anyone watching headless.
        private static void SayGuild(PlayerBot bot, Guild g, string category)
        {
            var line = ChatLibrary.PickRandom(category);
            if (string.IsNullOrEmpty(line) || bot.Deleted)
            {
                return;
            }
            g.GuildChat(bot, bot.SpeechHue, line);
            Console.WriteLine($"[guild] [{g.Abbreviation}] {bot.Name}: {line}");
        }

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
    }
}
