// =========================================================================
// BotPadDiscovery.cs — find dungeon teleporters that have NO destination
// record, so the missing stair records can be authored from what is
// actually in the world.
//
// This is the inverse of BotPadAudit. That one takes the records we have
// and checks them against reality; this one takes REALITY and reports what
// the records are missing.
//
// Why it exists: 41% of dungeon room points sit on a floor segment with no
// DungeonDescend record anywhere in their waypoint component, so a crawler
// that lands there can never roll a way deeper. It shuffles between two
// room points until its run timer expires. That is why the shard's crawlers
// live almost entirely on level 1 (L1 3376 : L2 95 : L3 3 over a 15-minute
// soak) and why nothing dies to anything dangerous.
//
// The stairs themselves are not missing — they are real Teleporter items,
// already in the world. Only the records are missing. So walk every
// Teleporter in the dungeon strip, resolve which floor it leaves from and
// which floor it lands on, and dump the facts. Classification into
// Descend/Ascend is deliberately NOT done here: a floor "segment" like
// 'Covetous lvl1g' is level 1 the same as 'Covetous lvl1', so direction has
// to come from a graph built over all the pads at once. That is a job for
// the offline tool, which gets this file as its input.
//
// Output: Data/Live/padmap_report.json. Run via [MapPads (GameMaster).
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Server;
using Server.Commands;
using Server.Items;

namespace Server.CustomBots
{
    public static class BotPadDiscovery
    {
        public static void Configure()
        {
            CommandSystem.Register("MapPads", AccessLevel.GameMaster, OnCommand);
        }

        // The dungeon strip. Felucca's dungeons all live east of this line,
        // which is what keeps the sweep off the 6144x4096 surface map.
        private const int DungeonMinX = 5000;

        // How far a pad may sit from a known interior point and still be
        // considered "on" that floor. Generous: a stair landing is often at
        // the end of a corridor with the nearest authored room some way off.
        private const int FloorResolveDist = 45;

        // A pad already covered by a record within this many tiles is not
        // missing, so it is reported as known rather than as a candidate.
        private const int CoveredDist = 2;

        public sealed class PadFact
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Z { get; set; }
            public int DestX { get; set; }
            public int DestY { get; set; }
            public int DestZ { get; set; }
            public bool Active { get; set; }
            public bool Covered { get; set; }
            public string CoveredBy { get; set; }
            public string FromDungeon { get; set; }
            public int FromLevel { get; set; }
            public int FromDist { get; set; }
            public string ToDungeon { get; set; }
            public int ToLevel { get; set; }
            public int ToDist { get; set; }
            public string NearestWaypoint { get; set; }
            public int WaypointDist { get; set; }
        }

        private static void OnCommand(CommandEventArgs e)
        {
            var facts = Run();
            int missing = 0;
            foreach (var f in facts)
            {
                if (!f.Covered) missing++;
            }
            e.Mobile.SendMessage(
                $"Pad discovery: {facts.Count} teleporter(s), {missing} with no record — see report.");
        }

        public static List<PadFact> Run()
        {
            var facts = new List<PadFact>();
            var map = Map.Felucca;
            if (map == null) return facts;

            var graph = WaypointRegistry.Graph;

            foreach (var item in World.Items.Values)
            {
                if (item is not Teleporter tele) continue;
                if (tele.Deleted || tele.Map != map) continue;
                if (tele.Location.X < DungeonMinX) continue;

                // A teleporter that sends you to another MAP is not a stair
                // between two floors of the same dungeon.
                if (tele.MapDest != null && tele.MapDest != map) continue;

                var f = new PadFact
                {
                    X = tele.X, Y = tele.Y, Z = tele.Z,
                    DestX = tele.PointDest.X,
                    DestY = tele.PointDest.Y,
                    DestZ = tele.PointDest.Z,
                    Active = tele.Active,
                };

                // Already have a record for this tile?
                foreach (var d in DestinationCatalog.All)
                {
                    if (d.Type != DestinationType.DungeonDescend &&
                        d.Type != DestinationType.DungeonAscend &&
                        d.Type != DestinationType.DungeonEntrance)
                    {
                        continue;
                    }
                    if (Cheb(d.Location, tele.Location) <= CoveredDist)
                    {
                        f.Covered = true;
                        f.CoveredBy = d.Name;
                        break;
                    }
                }

                // Which floor does it leave from, and which does it land on?
                // NearestPointOnFloor is component-scoped, so it will not
                // grab a point through a wall on the floor stacked beside
                // this one — which plain 2D proximity does constantly in the
                // dungeon strip.
                var from = DungeonRegistry.NearestPointOnFloor(tele.Location, FloorResolveDist);
                if (from != null)
                {
                    f.FromDungeon = from.Dungeon;
                    f.FromLevel = from.Level;
                    f.FromDist = Cheb(from.Location, tele.Location);
                }

                var landing = new Point3D(tele.PointDest.X, tele.PointDest.Y, tele.PointDest.Z);
                var to = DungeonRegistry.NearestPointOnFloor(landing, FloorResolveDist);
                if (to != null)
                {
                    f.ToDungeon = to.Dungeon;
                    f.ToLevel = to.Level;
                    f.ToDist = Cheb(to.Location, landing);
                }

                // The record needs a waypoint anchor or the crawler cannot
                // route to it — a pad nothing can walk to is no better than
                // no pad at all, and the offline tool needs to know which
                // ones those are.
                if (graph != null && graph.NodeCount > 0)
                {
                    var node = graph.FindNearestNode(tele.Location);
                    if (node != null)
                    {
                        f.NearestWaypoint = node.Name;
                        f.WaypointDist = Cheb(node.Location, tele.Location);
                    }
                }

                facts.Add(f);
            }

            Report(facts);
            return facts;
        }

        private static void Report(List<PadFact> facts)
        {
            int missing = 0, unanchored = 0, unresolved = 0;
            foreach (var f in facts)
            {
                if (!f.Covered) missing++;
                if (f.NearestWaypoint == null || f.WaypointDist > WaypointGraph.MaxLegDistance) unanchored++;
                if (f.FromDungeon == null || f.ToDungeon == null) unresolved++;
            }

            Console.WriteLine(
                $"[PadDiscovery] {facts.Count} dungeon teleporter(s): {missing} with no record, " +
                $"{unanchored} with no reachable waypoint, {unresolved} whose floors could not be resolved");

            try
            {
                var dir = Path.Combine(Core.BaseDirectory, "Data", "Live");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "padmap_report.json");
                var json = JsonSerializer.Serialize(
                    facts, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json, new UTF8Encoding(false));
                Console.WriteLine($"[PadDiscovery] wrote {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PadDiscovery] could not write report: {ex.Message}");
            }
        }

        private static int Cheb(Point3D a, Point3D b)
        {
            int dx = a.X - b.X; if (dx < 0) dx = -dx;
            int dy = a.Y - b.Y; if (dy < 0) dy = -dy;
            return dx > dy ? dx : dy;
        }
    }
}
