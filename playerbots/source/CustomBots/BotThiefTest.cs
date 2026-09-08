// =========================================================================
// BotThiefTest.cs — does a thief bot really pick pockets?
//
// Stands a fresh Grandmaster thief next to two throwaway "players" with
// fat packs (gold, reagents, a gem, bandages) and lets ThiefBehavior do
// the rest. Everything after the spawn is the shard's own doing: the
// engine's Stealing skill decides success and whether the thief is
// noticed, BotGrayWatch and the guards decide what happens next.
//
// Out in the countryside by default, so the run measures lifting and the
// getaway rather than how fast a guard kills a criminal. town=1 stands the
// rig at the Britain bank instead, which is the whole 1999 experience:
// steal under the banker's nose and see how long you last.
//
//   [TestThief [linger] [town]   — run it, results to the caller + console.
//   thief_request.txt            — headless: "token [linger] [town]"
//                                  -> thief_ack.json, tally on the console
//                                  as "[TestThief] RESULT ..." when it ends.
// =========================================================================

using System;
using System.Collections.Generic;
using Server.Commands;
using Server.Items;
using Server.Mobiles;
using Server.Regions;

namespace Server.CustomBots
{
    public static class BotThiefTest
    {
        // Several looks (6-18s apart), approaches and the ten-second skill
        // cooldown between lifts. Two minutes sees three or four attempts.
        public const int DefaultLinger = 150;

        private const int CrowdRange = 10;
        private const int CrowdWanted = 3;

        // The throwaway marks. ThiefBehavior skips real players with no
        // connection unless they are one of these.
        private static readonly HashSet<Mobile> _rigs = new();

        public static bool IsRig(Mobile m) => m != null && _rigs.Contains(m);

        public static void Configure()
        {
            CommandSystem.Register("TestThief", AccessLevel.GameMaster, OnCommand);
        }

        private static void OnCommand(CommandEventArgs e)
        {
            var linger = e.Length > 0 ? Math.Clamp(e.GetInt32(0), 0, 600) : DefaultLinger;
            var town = e.Length > 1 && e.GetInt32(1) != 0;
            foreach (var line in Run(linger, town))
            {
                e.Mobile.SendMessage(line.StartsWith("FAIL") ? 0x22 : 0x3F, line);
            }
        }

        public static List<string> Run(int lingerSeconds = DefaultLinger, bool town = false)
        {
            var findings = new List<string>();

            Point3D spot;
            Map map = Map.Felucca;
            if (town)
            {
                var bank = FindBank();
                if (bank == null)
                {
                    findings.Add("FAIL no bank destination in the catalog");
                    return Report(findings);
                }
                spot = bank.ArrivalPoint ?? bank.Location;
            }
            else
            {
                var host = FindCountrysideBot();
                if (host == null)
                {
                    findings.Add("FAIL no bot standing outside a guarded region to host the rig");
                    return Report(findings);
                }
                spot = host.Location;
                map = host.Map;
            }

            var thief = new PlayerBot(BotClass.Thief, BotSkillTier.Grandmaster)
            {
                LifecycleExempt = true,
            };
            thief.MoveToWorld(spot, map);
            thief.Hits = thief.HitsMax;

            var marks = new List<PlayerMobile>
            {
                MakeMark("Mark Test", Offset(spot, map, 2, 0)),
                MakeMark("Mark Two",  Offset(spot, map, -1, 2)),
            };

            int attemptsBefore = ThiefBehavior.TotalAttempts;
            int liftsBefore    = ThiefBehavior.TotalLifts;
            int caughtBefore   = ThiefBehavior.TotalCaught;

            thief.Behavior = new ThiefBehavior { Fearless = town };

            var place = BotEventJournal.PlaceName(spot, map);
            findings.Add(
                $"{(thief.NpcGuild == NpcGuild.ThievesGuild ? "OK  " : "FAIL")} thief {thief.Name} " +
                $"stealing={thief.Skills.Stealing.Value:0} snooping={thief.Skills.Snooping.Value:0} " +
                $"hiding={thief.Skills.Hiding.Value:0} guild={thief.NpcGuild} " +
                $"tag={BotGuilds.Get(thief.BotGuildIndex)?.Tag ?? "none"} " +
                $"hands={(thief.FindItemOnLayer(Layer.OneHanded) == null && thief.FindItemOnLayer(Layer.TwoHanded) == null ? "free" : "FULL")}");
            findings.Add(
                $"OK   two marks with 400gp, 30 pearls, a gem and bandages each at {place} " +
                $"({(town ? "in town" : "countryside")}, crowd={CountCrowd(thief)})");

            if (lingerSeconds <= 0)
            {
                Cleanup(thief, marks);
                return Report(findings);
            }

            findings.Add(
                $"WATCH thief working for {lingerSeconds}s — expect \"[thief] {thief.Name} lifted ...\"");

            Timer.DelayCall(TimeSpan.FromSeconds(lingerSeconds), () =>
            {
                Tally(thief, marks, attemptsBefore, liftsBefore, caughtBefore);
                Cleanup(thief, marks);
            });

            return Report(findings);
        }

        private static void Tally(PlayerBot thief, List<PlayerMobile> marks,
            int attemptsBefore, int liftsBefore, int caughtBefore)
        {
            int attempts = ThiefBehavior.TotalAttempts - attemptsBefore;
            int lifts    = ThiefBehavior.TotalLifts - liftsBefore;
            int caught   = ThiefBehavior.TotalCaught - caughtBefore;

            int thiefGold = thief.Deleted ? -1 : thief.Backpack?.GetAmount(typeof(Gold)) ?? 0;
            int markGold = 0;
            foreach (var m in marks)
            {
                if (!m.Deleted)
                {
                    markGold += m.Backpack?.GetAmount(typeof(Gold)) ?? 0;
                }
            }

            var verdict = attempts == 0 ? "FAIL" : "OK  ";
            Console.WriteLine(
                $"[TestThief] RESULT {verdict} attempts={attempts} lifts={lifts} caught={caught} " +
                $"thief={(thief.Deleted ? "deleted" : thief.Alive ? "alive" : "DEAD")} " +
                $"criminal={(!thief.Deleted && thief.Criminal)} hidden={(!thief.Deleted && thief.Hidden)} " +
                $"brain={thief.Behavior?.SerializableName ?? "none"} " +
                $"thiefGold={thiefGold} marksGold={markGold} " +
                $"at {BotEventJournal.PlaceName(thief.Location, thief.Map)}");
        }

        private static PlayerMobile MakeMark(string name, Point3D at)
        {
            var mark = new PlayerMobile
            {
                Name = name,
                Body = 0x190,
                Hue = 0x83EA,
                Player = true,
                RawStr = 90,
                RawDex = 90,
                RawInt = 90,
            };

            var pack = new Backpack { Movable = false };
            mark.AddItem(pack);
            pack.DropItem(new Gold(400));
            pack.DropItem(new BlackPearl(30));
            pack.DropItem(new Diamond());
            pack.DropItem(new Bandage(20));

            mark.MoveToWorld(at, Map.Felucca);
            mark.Hits = mark.HitsMax;
            _rigs.Add(mark);
            return mark;
        }

        private static Point3D Offset(Point3D p, Map map, int dx, int dy)
        {
            int x = p.X + dx;
            int y = p.Y + dy;
            int z = map.GetAverageZ(x, y);
            var candidate = new Point3D(x, y, z);
            return map.CanSpawnMobile(candidate) ? candidate : p;
        }

        private static BotDestination FindBank()
        {
            BotDestination any = null;
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type != DestinationType.Bank)
                {
                    continue;
                }
                any ??= d;
                if (string.Equals(d.City, "Britain", StringComparison.OrdinalIgnoreCase))
                {
                    return d;
                }
            }
            return any;
        }

        // A live blue bot outside every guarded region, with company if
        // possible, so the getaway has somebody to get away from.
        private static PlayerBot FindCountrysideBot()
        {
            PlayerBot fallback = null;

            foreach (var m in World.Mobiles.Values)
            {
                if (m is not PlayerBot bot || bot.Deleted || !bot.Alive ||
                    bot.Map == null || bot.Map == Map.Internal ||
                    bot.Murderer || bot.Criminal || bot.Class == BotClass.Thief)
                {
                    continue;
                }

                if (bot.Behavior is PKBehavior or GhostBehavior or DungeonCrawlerBehavior)
                {
                    continue;
                }

                if (bot.Region?.GetRegion<GuardedRegion>() != null ||
                    DungeonRegistry.IsInDungeon(bot))
                {
                    continue;
                }

                // Not a battlefield. A host mid-fight, or with a murderer in
                // sight, gets the rig killed before it lifts anything.
                if (bot.Combatant != null || RedNearby(bot))
                {
                    continue;
                }

                fallback ??= bot;

                if (CountCrowd(bot) >= CrowdWanted)
                {
                    return bot;
                }
            }

            return fallback;
        }

        private static bool RedNearby(PlayerBot bot)
        {
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, 24))
            {
                if (m.Alive && !m.Deleted && (m.Murderer || m.Criminal ||
                    m is PlayerBot { Behavior: PKBehavior }))
                {
                    return true;
                }
            }
            return false;
        }

        private static int CountCrowd(PlayerBot bot)
        {
            var n = 0;
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, CrowdRange))
            {
                if (m is PlayerBot other && other != bot && other.Alive && !other.Deleted)
                {
                    n++;
                }
            }
            return n;
        }

        private static List<string> Report(List<string> findings)
        {
            foreach (var line in findings)
            {
                Console.WriteLine($"[TestThief] {line}");
            }
            return findings;
        }

        private static void Cleanup(PlayerBot thief, List<PlayerMobile> marks)
        {
            foreach (var m in marks)
            {
                _rigs.Remove(m);
                if (m.Corpse is { Deleted: false } c)
                {
                    c.Delete();
                }
                if (!m.Deleted)
                {
                    m.Delete();
                }
            }

            if (thief.Corpse is { Deleted: false } corpse)
            {
                corpse.Delete();
            }
            if (!thief.Deleted)
            {
                thief.Delete();
            }
        }
    }
}
