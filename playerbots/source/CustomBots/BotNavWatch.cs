// =========================================================================
// BotNavWatch.cs — the fleet-wide "is this bot actually getting anywhere"
// watchdog.
//
// THE GAP THIS FILLS. Every stuck detector in this codebase belongs to one
// behavior and counts that behavior's own attempts: Traveler's leg cycles,
// the crawler's hard-stuck anchor, the pad timeout, the ghost walk stall.
// They all share a blind spot. A bot that keeps RETRYING resets them.
// TravelerBehavior already has the note in its own words — a hard-blocked
// bot repaths, instantly "reaches" hop-0 (the node it is already standing
// on), the cycle counter resets, and it paces at 'cycle 1/3' for the rest
// of its session. Twelve thousand such events in one soak, and the stuck
// ledger recorded none of them, because nothing ever gave up.
//
// So the ledger only ever held the failures that ENDED. The ones that
// never end — a bot pressing a wall for an hour, walking a doorway it can
// never pass, re-picking the same unreachable coordinate — were exactly
// the ones missing from it.
//
// This watches every bot in the world from outside its brain, and judges
// it on the one thing a retry cannot fake: distance to where it says it is
// going. A behavior declares that with PlayerBotBehavior.NavGoal; one that
// answers null is standing still on purpose and is never judged.
//
// What it does when it catches one:
//   1. Records `nav_stall` with the behavior, the status line, the goal
//      and the gap — so stuck_report.json finally shows the silent jams
//      alongside the loud ones, and the status page names the spots.
//   2. Tries the cheap physical escapes in order: open a door it may be
//      pressing, sidestep, and only for a bot the engine says is wedged on
//      every side, the extraction teleport.
//   3. Keeps watching. A bot that stalls again at the same spot is logged
//      as a REPEAT, which is the signature of a bad waypoint or a doorway
//      the graph believes in and the geometry does not — a data fix, not a
//      code one.
//
// It never re-brains a bot and never moves one that is merely slow. The
// point is to make the invisible visible first.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;

namespace Server.CustomBots
{
    public static class BotNavWatch
    {
        public static bool Enabled = true;

        // Chatty console line for every stall, on top of the ledger entry.
        // Off by default; the report is the interface.
        public static bool Verbose;

        // ---- Knobs ----

        // A bot has to fail to gain this much ground before anything is
        // said. Generous on purpose: legs are long, doors take a beat, and
        // a crowd at a bank shuffles for a while without being stuck.
        private static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(3);

        // Getting this many tiles closer to the goal counts as progress and
        // re-arms the clock. Small enough that a bot inching along a wall
        // still reads as moving; big enough that jitter in place does not.
        private const int ProgressTiles = 3;

        // Re-arm after acting, so one jam produces one report every few
        // minutes rather than one per tick.
        private static readonly TimeSpan ReportEvery = TimeSpan.FromMinutes(3);

        // A goal within this range is close enough that the bot is arriving,
        // not stalling — final-approach drift is somebody else's business.
        private const int ArrivedSlack = 2;

        private sealed class Track
        {
            public Point3D Goal;
            public int BestDist;
            public DateTime BestAt;
            public Point3D StallAt;   // where it jammed, for the repeat test
            public int Strikes;
            public DateTime NextReport;
        }

        private static readonly Dictionary<Serial, Track> _tracks = new();

        // Called once per bot from BehaviorTickManager, after the brain has
        // had its tick. Cheap: one dictionary lookup and two subtractions
        // for a bot that is travelling, nothing at all for one that is not.
        public static void Observe(PlayerBot bot)
        {
            if (!Enabled || bot == null || bot.Deleted ||
                bot.Map == null || bot.Map == Map.Internal)
            {
                return;
            }

            var behavior = bot.Behavior;
            var goal = behavior?.NavGoal(bot);

            // Not going anywhere (or fighting, which owns its own movement).
            if (goal == null || bot.Combatant != null)
            {
                _tracks.Remove(bot.Serial);
                return;
            }

            int dist = Dist(bot.Location, goal.Value);
            if (dist <= ArrivedSlack)
            {
                _tracks.Remove(bot.Serial);
                return;
            }

            if (!_tracks.TryGetValue(bot.Serial, out var t))
            {
                _tracks[bot.Serial] = new Track
                {
                    Goal = goal.Value,
                    BestDist = dist,
                    BestAt = Core.Now,
                    NextReport = Core.Now + StallAfter,
                };
                return;
            }

            // A new goal is a fresh trip — the old one's clock says nothing
            // about this one.
            if (t.Goal != goal.Value)
            {
                t.Goal = goal.Value;
                t.BestDist = dist;
                t.BestAt = Core.Now;
                return;
            }

            if (dist + ProgressTiles <= t.BestDist)
            {
                t.BestDist = dist;
                t.BestAt = Core.Now;
                return;
            }

            if (Core.Now - t.BestAt < StallAfter || Core.Now < t.NextReport)
            {
                return;
            }

            OnStalled(bot, behavior, t, goal.Value, dist);
        }

        private static void OnStalled(
            PlayerBot bot, PlayerBotBehavior behavior, Track t, Point3D goal, int dist)
        {
            // Same spot as last time? That is the signature worth chasing:
            // the bot is not merely slow, it is meeting the same wall.
            bool repeat = t.Strikes > 0 && Dist(bot.Location, t.StallAt) <= ProgressTiles;
            t.Strikes++;
            t.StallAt = bot.Location;
            t.BestAt = Core.Now;
            t.BestDist = dist;
            t.NextReport = Core.Now + ReportEvery;

            string what = behavior?.GetStatusLine(bot) ?? behavior?.SerializableName ?? "?";

            // How many other bots are pressed up against this one? A jam in
            // a shop doorway and a bot wedged in a wall look identical in
            // the ledger without this, and they need opposite fixes.
            int crowd = 0;
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, 1))
            {
                if (m != bot && m is PlayerBot && m.Alive)
                {
                    crowd++;
                }
            }

            string detail =
                $"{behavior?.SerializableName ?? "?"} x{t.Strikes}" +
                (repeat ? " REPEAT" : "") +
                (crowd > 0 ? $" crowd={crowd}" : "") +
                $" — {dist} tiles from ({goal.X},{goal.Y}) — {what}";

            // On a repeat, say WHAT is in the way. "Stuck at (5704,1416)" is
            // a coordinate to go look at by hand; "blocked N by a stone wall,
            // E by a 12-tile drop" is a fix.
            if (repeat)
            {
                detail += " | " + ProbeBlockers(bot, goal);
            }

            StuckTelemetry.Record(bot, repeat ? "nav_stall_repeat" : "nav_stall", detail);

            if (Verbose || repeat)
            {
                Console.WriteLine(
                    $"[nav] {bot.Name} has not gained ground in " +
                    $"{(int)StallAfter.TotalMinutes} min at ({bot.X},{bot.Y}): {detail}");
            }

            TryUnstick(bot);
        }

        // Ask the engine, direction by direction, why this bot cannot move.
        // Everything here is a read: CheckMovement is the same call Move
        // makes, so the answer is the truth and not a guess.
        private static string ProbeBlockers(PlayerBot bot, Point3D goal)
        {
            Direction[] dirs =
            {
                Direction.North, Direction.Right, Direction.East, Direction.Down,
                Direction.South, Direction.Left,  Direction.West, Direction.Up,
            };

            // Named by hand: Direction.Up and Direction.Mask share the value
            // 0x7, so ToString() on a northwest step prints "Mask".
            string[] names =
            {
                "N", "NE", "E", "SE", "S", "SW", "W", "NW",
            };

            var open = new List<string>();
            var shut = new List<string>();
            var toward = bot.GetDirectionTo(goal) & Direction.Mask;

            for (int i = 0; i < dirs.Length; i++)
            {
                var d = dirs[i];
                bool ok;
                try
                {
                    ok = Movement.Movement.CheckMovement(bot, d, out _);
                }
                catch
                {
                    continue;
                }

                string tag = names[i];
                if (d == toward)
                {
                    tag += "*"; // the way it actually wants to go
                }
                (ok ? open : shut).Add(tag);
            }

            // What is sitting on the tile it wants? Name the first blocker;
            // a wall, a table and a locked door are three different jobs.
            string onTile = "";
            int gx = bot.X, gy = bot.Y;
            Movement.Movement.Offset(toward, ref gx, ref gy);
            foreach (var item in bot.Map.GetItemsAt(gx, gy))
            {
                if (item.ItemData.Impassable || item is Server.Items.BaseDoor)
                {
                    onTile = $" ahead='{item.ItemData.Name ?? item.GetType().Name}'" +
                             (item is Server.Items.BaseDoor bd
                                 ? bd.Locked ? " LOCKED door" : " door"
                                 : "");
                    break;
                }
            }

            return $"open=[{string.Join(",", open)}] blocked=[{string.Join(",", shut)}]{onTile}";
        }

        // The cheap physical escapes, in the order a player would try them.
        // Nothing here re-picks a goal or re-brains the bot — the behavior
        // still owns its plan; this only tries to get the feet free.
        private static void TryUnstick(PlayerBot bot)
        {
            // Pressing a closed door is the single most common way to spend
            // an hour going nowhere, and it is free to rule out.
            if (DoorHelper.TryOpenAdjacent(bot))
            {
                return;
            }

            if (BotStuckEscape.SidestepAny(bot))
            {
                return;
            }

            // Every one of the eight directions refused: the engine says
            // this bot is inside something. That is the one case no amount
            // of goal-picking ever frees.
            BotStuckEscape.TryExtract(bot, "nav watch");
        }

        // Bots are transient; drop their tracks when they go.
        public static void Forget(PlayerBot bot)
        {
            if (bot != null)
            {
                _tracks.Remove(bot.Serial);
            }
        }

        private static int Dist(Point3D a, Point3D b)
        {
            int dx = a.X - b.X; if (dx < 0) dx = -dx;
            int dy = a.Y - b.Y; if (dy < 0) dy = -dy;
            return dx > dy ? dx : dy;
        }
    }
}
