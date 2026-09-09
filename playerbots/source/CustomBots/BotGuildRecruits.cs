// =========================================================================
// BotGuildRecruits.cs — a player recruits a bot into a real guild.
//
// This shard runs the old guild system (Core.SE is off): a guildmaster
// stands at the guildstone, picks "Recruit someone into the guild" and
// targets a person. That person lands in the guild's Accepted list and
// joins by double-clicking the stone. A bot has no client, so nothing ever
// tells it it was recruited and it can never click. This watches the
// Accepted lists of every guild instead, and when a bot is on one it
// joins a few seconds later, the way the recruit would have walked over
// to the stone.
//
// A bot that joins a player's guild stops being a passer-by. PlayerBot
// flips GuildBound on the guild change, and that flag (saved with the
// bot) keeps it out of every path that deletes bots: the session logout,
// the boot purge, the regenerations. It keeps living its life; it just
// never leaves the shard, and it wears the player's tag.
//
// A recruit by an ordinary member lands in Candidates, and the era rule
// is that the guildmaster votes on those. That rule stands; only Accepted
// is handled here.
// =========================================================================

using System;
using System.Collections.Generic;
using Server.Guilds;

namespace Server.CustomBots
{
    public static class BotGuildRecruits
    {
        public static bool Enabled = true;

        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

        // bot -> when it gets round to the stone.
        private static readonly Dictionary<PlayerBot, DateTime> _pending = new();
        private static readonly List<PlayerBot> _due = new();

        public static void Configure()
        {
            Timer.DelayCall(Interval, Interval, OnTick);
        }

        private static void OnTick()
        {
            if (!Enabled)
            {
                return;
            }

            foreach (var bg in World.Guilds.Values)
            {
                if (bg is not Guild g || g.Disbanded)
                {
                    continue;
                }

                var accepted = g.Accepted;
                for (int i = accepted.Count - 1; i >= 0; i--)
                {
                    if (accepted[i] is PlayerBot bot && !bot.Deleted && !_pending.ContainsKey(bot))
                    {
                        _pending[bot] = Core.Now + TimeSpan.FromSeconds(2 + Utility.RandomDouble() * 4);
                    }
                }
            }

            if (_pending.Count == 0)
            {
                return;
            }

            _due.Clear();
            foreach (var (bot, at) in _pending)
            {
                if (Core.Now >= at)
                {
                    _due.Add(bot);
                }
            }

            foreach (var bot in _due)
            {
                _pending.Remove(bot);
                Join(bot);
            }
        }

        private static void Join(PlayerBot bot)
        {
            if (bot.Deleted)
            {
                return;
            }

            Guild guild = null;
            foreach (var bg in World.Guilds.Values)
            {
                if (bg is Guild g && !g.Disbanded && g.Accepted.Contains(bot))
                {
                    guild = g;
                    break;
                }
            }
            if (guild == null)
            {
                return; // withdrawn meanwhile
            }

            // A ghost cannot click. It stays accepted and joins once raised.
            if (!bot.Alive)
            {
                return;
            }

            guild.Accepted.Remove(bot);
            guild.AddMember(bot);
            bot.DisplayGuildTitle = true;

            Console.WriteLine(
                $"[guild] {bot.Name} joined [{guild.Abbreviation}] {guild.Name}" +
                $"{(bot.GuildBound ? " and is staying on the shard for good" : "")}");

            // "ty for the inv", to whoever is standing there.
            var line = ChatLibrary.PickRandom("guild_join");
            if (!string.IsNullOrEmpty(line) && SomeoneListening(bot))
            {
                Timer.DelayCall(TimeSpan.FromSeconds(1 + Utility.RandomDouble()), () =>
                {
                    if (!bot.Deleted && bot.Alive)
                    {
                        bot.Say(line);
                    }
                });
            }
        }

        private static bool SomeoneListening(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal)
            {
                return false;
            }
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, 22))
            {
                if (m is Mobiles.PlayerMobile && m is not PlayerBot)
                {
                    return true;
                }
            }
            return false;
        }

        // How many bots a guild holds, for the status page and the rig.
        public static int BotMembers(Guild g)
        {
            int n = 0;
            foreach (var m in g.Members)
            {
                if (m is PlayerBot)
                {
                    n++;
                }
            }
            return n;
        }
    }
}
