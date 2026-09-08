// =========================================================================
// BotGuildTest.cs — does recruiting a bot into a real guild take, and
// does "anyone wanna group up" in guild chat bring the guild bots?
//
// A throwaway REAL player founds a real guild at the Britain bank and
// recruits two live bots from across the shard the way the guildstone
// does (they land on the guild's Accepted list). BotGuildRecruits joins
// them. Then the player types the call in guild chat, through the same
// DoSpeech path a client uses, and the rest is the shard's own doing:
// the bots answer in guild chat, recall in, and take the party invite.
//
//   [TestGuild [linger] [keep] — run it, results to the caller + console.
//                            keep=1 leaves the guild and its bots in place
//                            and saves the world, to check a restart keeps
//                            them.
//   guild_request.txt      — headless: "token [linger] [keep]" -> guild_ack.json,
//                            "[TestGuild] RESULT ..." on the console.
// =========================================================================

using System;
using System.Collections.Generic;
using Server.Commands;
using Server.Engines.PartySystem;
using Server.Guilds;
using Server.Mobiles;
using Server.Network;

namespace Server.CustomBots
{
    public static class BotGuildTest
    {
        // Join (up to ~10s), the call, the answers, a recall with retries,
        // the invite and the walk over.
        public const int DefaultLinger = 120;

        private const int Recruits = 2;

        public static void Configure()
        {
            CommandSystem.Register("TestGuild", AccessLevel.GameMaster, OnCommand);
        }

        private static void OnCommand(CommandEventArgs e)
        {
            var linger = e.Length > 0 ? Math.Clamp(e.GetInt32(0), 0, 600) : DefaultLinger;
            var keep = e.Length > 1 && e.GetInt32(1) != 0;
            foreach (var line in Run(linger, keep))
            {
                e.Mobile.SendMessage(line.StartsWith("FAIL") ? 0x22 : 0x3F, line);
            }
        }

        public static List<string> Run(int lingerSeconds = DefaultLinger, bool keep = false)
        {
            var findings = new List<string>();

            int purged = PurgeLeftovers();
            if (purged > 0)
            {
                findings.Add($"OK   cleared {purged} leftover(s) from an earlier keep run");
            }

            BotDestination bank = null;
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type == DestinationType.Bank)
                {
                    bank ??= d;
                    if (string.Equals(d.City, "Britain", StringComparison.OrdinalIgnoreCase))
                    {
                        bank = d;
                        break;
                    }
                }
            }
            if (bank == null)
            {
                findings.Add("FAIL no bank destination in the catalog");
                return Report(findings);
            }

            var spot = bank.ArrivalPoint ?? bank.Location;
            var rig = new PlayerMobile
            {
                Name = "Guild Test",
                Body = 0x190,
                Hue = 0x83EA,
                Player = true,
                RawStr = 100,
                RawDex = 100,
                RawInt = 100,
            };
            rig.MoveToWorld(spot, Map.Felucca);
            rig.Hits = rig.HitsMax;

            var guild = new Guild(rig, "Test Guild", "TST");

            // A real guild has a stone, and the old guild system disbands
            // any guild without one on load. The deed would have made it.
            var stone = new Server.Items.Guildstone(guild);
            stone.MoveToWorld(spot, Map.Felucca);
            guild.Guildstone = stone;

            var recruits = PickRecruits(rig);
            if (recruits.Count == 0)
            {
                findings.Add("FAIL no live bot that could join a party and recall");
                Cleanup(rig, guild, recruits);
                return Report(findings);
            }

            foreach (var bot in recruits)
            {
                guild.Accepted.Add(bot);
                findings.Add(
                    $"OK   recruited {bot.Name} ({bot.Class}, {bot.SkillTier}) " +
                    $"{Cheb(bot.Location, rig.Location)} tiles away, " +
                    $"recall={(MagicTravel.CanTravel(bot) ? "yes" : "no")} " +
                    $"spawner={(bot.Spawner != null ? "yes" : "none")}");
            }

            if (lingerSeconds <= 0)
            {
                Cleanup(rig, guild, recruits);
                return Report(findings);
            }

            findings.Add(
                $"WATCH guild rig running for {lingerSeconds}s — expect \"[guild] ... joined [TST]\", " +
                "the call, \"[guild] [TST] ...: yeah\", recalls and \"[party] ... joined Guild Test's party\"");

            // Give the recruits time to join, then make the call.
            Timer.DelayCall(TimeSpan.FromSeconds(14), () => Call(rig, guild, recruits));

            Timer.DelayCall(TimeSpan.FromSeconds(lingerSeconds), () =>
            {
                Tally(rig, guild, recruits);
                if (keep)
                {
                    // Leave everything standing and save, so a restart can
                    // show the guild bots still there.
                    Console.WriteLine(
                        $"[TestGuild] KEEP guild [TST] and {recruits.Count} bot(s) left in place — saving the world");
                    try { Party.Get(rig)?.Disband(); } catch { }
                    World.Save();
                    return;
                }
                Cleanup(rig, guild, recruits);
            });

            return Report(findings);
        }

        private static void Call(PlayerMobile rig, Guild guild, List<PlayerBot> recruits)
        {
            int joined = 0;
            foreach (var bot in recruits)
            {
                if (!bot.Deleted && bot.Guild == guild)
                {
                    joined++;
                }
            }
            Console.WriteLine(
                $"[TestGuild] {(joined == recruits.Count ? "OK  " : "FAIL")} " +
                $"{joined}/{recruits.Count} recruit(s) joined [TST]; " +
                $"bound={CountBound(recruits)} detached={CountDetached(recruits)}");

            // The same path a client's guild-chat line takes.
            rig.DoSpeech("anyone wanna group up", Array.Empty<int>(), MessageType.Guild, 0x3B2);
            Console.WriteLine("[TestGuild] Guild Test said in guild chat: anyone wanna group up");
        }

        private static void Tally(PlayerMobile rig, Guild guild, List<PlayerBot> recruits)
        {
            var party = Party.Get(rig);
            int inParty = 0;
            var names = new List<string>();
            foreach (var bot in recruits)
            {
                if (!bot.Deleted && party != null && bot.Party == party)
                {
                    inParty++;
                    names.Add($"{bot.Name}@{Cheb(bot.Location, rig.Location)}t");
                }
            }
            Console.WriteLine(
                $"[TestGuild] RESULT {(inParty > 0 ? "OK  " : "FAIL")} " +
                $"{inParty}/{recruits.Count} guild bot(s) in Guild Test's party " +
                $"[{string.Join(", ", names)}] pendingSummons={BotGuildChat.Pending} " +
                $"guildBots={BotGuildRecruits.BotMembers(guild)}");
        }

        // Two live bots that would take a party invite and can recall,
        // far enough away that they have to.
        private static List<PlayerBot> PickRecruits(Mobile rig)
        {
            var far = new List<PlayerBot>();
            var near = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot bot || bot.Deleted || !bot.Alive || bot.LoggingOut ||
                    bot.Map != rig.Map || bot.Guild != null || bot.Party != null ||
                    bot.Murderer || bot.Criminal || bot.LifecycleExempt ||
                    DungeonRegistry.IsInDungeon(bot) ||
                    !BotPlayerParty.CanJoin(bot, out _) || !MagicTravel.CanCastRecall(bot))
                {
                    continue;
                }
                if (Cheb(bot.Location, rig.Location) > 150)
                {
                    far.Add(bot);
                }
                else
                {
                    near.Add(bot);
                }
                if (far.Count >= Recruits)
                {
                    break;
                }
            }
            var picks = new List<PlayerBot>(far);
            for (int i = 0; picks.Count < Recruits && i < near.Count; i++)
            {
                picks.Add(near[i]);
            }
            return picks;
        }

        private static int CountBound(List<PlayerBot> bots)
        {
            int n = 0;
            foreach (var b in bots)
            {
                if (!b.Deleted && b.GuildBound)
                {
                    n++;
                }
            }
            return n;
        }

        private static int CountDetached(List<PlayerBot> bots)
        {
            int n = 0;
            foreach (var b in bots)
            {
                if (!b.Deleted && b.Spawner == null)
                {
                    n++;
                }
            }
            return n;
        }

        // A keep run leaves "Test Guild" and its founder in the world on
        // purpose. The next run clears them.
        private static int PurgeLeftovers()
        {
            int n = 0;
            var guilds = new List<Guild>();
            foreach (var bg in World.Guilds.Values)
            {
                if (bg is Guild g && g.Name == "Test Guild")
                {
                    guilds.Add(g);
                }
            }
            foreach (var g in guilds)
            {
                var members = new List<Mobile>(g.Members);
                foreach (var m in members)
                {
                    if (m is PlayerBot bot)
                    {
                        try { g.RemoveMember(bot); } catch { }
                        n++;
                    }
                }
                try { g.Guildstone?.Delete(); } catch { }
                try { g.Disband(); } catch { }
            }

            var rigs = new List<Mobile>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerMobile && m is not PlayerBot && m.Name == "Guild Test")
                {
                    rigs.Add(m);
                }
            }
            foreach (var m in rigs)
            {
                try { Party.Get(m)?.Disband(); } catch { }
                try { m.Delete(); } catch { }
                n++;
            }
            return n;
        }

        private static int Cheb(Point3D a, Point3D b)
        {
            int dx = Math.Abs(a.X - b.X);
            int dy = Math.Abs(a.Y - b.Y);
            return dx > dy ? dx : dy;
        }

        private static List<string> Report(List<string> findings)
        {
            foreach (var line in findings)
            {
                Console.WriteLine($"[TestGuild] {line}");
            }
            return findings;
        }

        // The recruits go back to ordinary lives (leaving the guild clears
        // GuildBound); the party and the guild go away with the rig.
        private static void Cleanup(PlayerMobile rig, Guild guild, List<PlayerBot> recruits)
        {
            try { Party.Get(rig)?.Disband(); } catch { }

            foreach (var bot in recruits)
            {
                if (bot.Deleted)
                {
                    continue;
                }
                try
                {
                    if (bot.Party is Party p) { p.Remove(bot); }
                    bot.Party = null;
                    if (bot.Guild == guild) { guild.RemoveMember(bot); }
                    guild.Accepted.Remove(bot);
                    if (bot.Behavior is PlayerGroupBehavior)
                    {
                        bot.Behavior = new TravelerBehavior();
                    }
                }
                catch { }
            }

            try { guild.Guildstone?.Delete(); } catch { }
            try { guild.Disband(); } catch { }

            if (rig.Corpse is { Deleted: false } corpse)
            {
                corpse.Delete();
            }
            if (!rig.Deleted)
            {
                rig.Delete();
            }
        }
    }
}
