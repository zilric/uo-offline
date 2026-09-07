// =========================================================================
// WaypointView.cs — see the waypoint graph in the game world.
//
//   [showways            draw the graph around you (40 tiles)
//   [showways 80         ...out to 80 tiles
//   [showways nolinks    nodes only, no connection lines
//   [hideways            clear it
//
// WHY. The graph is the only thing standing between a bot and where it is
// going, and until now the only way to look at it was the web map editor or
// a wall of console text. Neither tells you the thing you actually need to
// know when a bot is pacing at a wall: is that node ON the walkable tile you
// think it is, and does the line from here to there cross something solid.
// Standing next to it and looking is the fastest way to know.
//
// WHAT YOU SEE. A recall rune at every node — hoverable, so its name and
// exact coordinates are one mouse-over away — and a trail of dots along
// every connection. The colours carry the diagnosis:
//
//   white   the node nearest you, the one a bot here would route from
//   green   an ordinary node in that same connected component
//   orange  a node in a DIFFERENT component — no route reaches it from
//           where you stand, however close it looks
//   red     a broken node: it names a neighbour that does not exist
//   blue    a normal connection
//   red     a connection longer than 38 tiles, which A* cannot walk
//
// NOTHING DRAWN HERE IS SOLID. This is a pure overlay, and getting that
// right is the whole trick. The first cut placed the art as real items and
// the link art (0xF9) is flagged Impassable, so the connection lines became
// walls that bots piled up against — the tool broke the thing it was built
// to debug.
//
// The engine already solved this for its own spawner-border overlay:
// ProjectedItem is an invisible, nodraw, non-blocking item that PULSES its
// art at nearby staff clients as an effect once a second. The art is never
// an ItemID, so it has no tile flags, so it cannot block anything. Every
// marker here is one of those, and carries SkipSerialization on top, so
// none of it reaches a world save and a restart clears any strays.
//
// The cost of an overlay is that there is nothing solid to mouse over, so
// names come two other ways: [showways prints the nodes it drew nearest
// you, and floats each nearby node's name over it once when you run it.
//
// Only GameMaster and above see any of it. The view redraws as you walk and
// expires on its own after twenty minutes.
//
// EDITING. Placement already exists and is unchanged: [MarkWay <name> puts a
// node where you stand and auto-connects it, [delway removes one. Both write
// waypoints.json; run [ReloadWaypoints and this view redraws on the new
// graph, so you can mark, reload, and see the result without moving.
// =========================================================================

using System;
using System.Collections.Generic;
using ModernUO.Serialization;
using Server;
using Server.Commands;
using Server.Items;

namespace Server.CustomBots
{
    // A marker. ProjectedItem does the work: the real item is an invisible
    // nodraw with no tile flags — so it blocks nothing, ever — and the art
    // is pulsed to staff clients as an effect instead. SkipSerialization
    // keeps it out of the world save.
    [SerializationGenerator(0)]
    public partial class WaypointMarkerItem : ProjectedItem
    {
        public override bool SkipSerialization => true;

        [Constructible]
        public WaypointMarkerItem(int effectItemId) : base(effectItemId) =>
            MinimumVisible = AccessLevel.GameMaster;
    }

    public static class WaypointView
    {
        // ---- Look. All of it is one line to change. ----

        // Art pulsed at the tile, never placed as an item — so tile flags
        // (0xF9 is Impassable) do not matter and nothing here blocks.
        // A recall rune for a node: small, flat, and it already means
        // "a place someone marked".
        private const int NodeArt = 0x1F14;

        // The line art the engine's own spawner-border overlay draws with.
        private const int LinkArt = 0xF9;

        private const int HueNearest      = 0x481; // white — you are here
        private const int HueNode         = 0x3F;  // green — ordinary node
        private const int HueOtherComp    = 0x35;  // orange — unroutable from here
        private const int HueBroken       = 0x21;  // red — names a missing neighbour
        private const int HueLink         = 0x59;  // blue — ordinary connection
        private const int HueLongLink     = 0x21;  // red — too long for A*

        // ---- Knobs ----

        private const int DefaultRadius = 40;
        private const int MaxRadius     = 200;

        // A dot every this many tiles along a connection. Close enough to
        // read as a line, far enough that a 38-tile leg is a dozen items.
        private const int LinkSpacing = 3;

        // Hard ceiling on markers in one view. Each one re-sends its art to
        // you once a second, so this is a packet budget as much as a visual
        // one. The densest 40-tile box on the shard draws under 300.
        private const int MaxItems = 1200;

        // Float a node's name over it (once, on an explicit [showways) when
        // it is this close. Every node at once is unreadable.
        private const int LabelRadius = 12;

        // Redraw once you have walked this far from where it was drawn.
        private const int RedrawAfterMoved = 8;

        private static readonly TimeSpan Expiry = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan Pulse  = TimeSpan.FromSeconds(2);

        private sealed class View
        {
            public readonly List<Item> Items = new();
            public Point3D DrawnFrom;
            public int Radius;
            public bool Links;
            public DateTime ExpiresAt;
        }

        private static readonly Dictionary<Mobile, View> _views = new();
        private static Timer _timer;

        public static void Configure()
        {
            CommandSystem.Register("showways", AccessLevel.GameMaster, OnShow);
            CommandSystem.Register("hideways", AccessLevel.GameMaster, OnHide);
        }

        // -----------------------------------------------------------------
        // Commands
        // -----------------------------------------------------------------

        [Usage("showways [radius] [nolinks]")]
        [Description("Draw the waypoint graph in the world around you.")]
        private static void OnShow(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null || from.Map == null || from.Map == Map.Internal)
            {
                return;
            }

            int radius = DefaultRadius;
            bool links = true;

            for (int i = 0; i < e.Length; i++)
            {
                string arg = e.GetString(i)?.Trim() ?? "";
                if (arg.Length == 0)
                {
                    continue;
                }
                if (arg.InsensitiveEquals("off") || arg.InsensitiveEquals("clear"))
                {
                    OnHide(e);
                    return;
                }
                if (arg.InsensitiveEquals("nolinks") || arg.InsensitiveEquals("nodes"))
                {
                    links = false;
                    continue;
                }
                if (int.TryParse(arg, out int r))
                {
                    radius = Math.Clamp(r, 5, MaxRadius);
                }
            }

            Draw(from, radius, links, announce: true);
        }

        [Usage("hideways")]
        [Description("Clear the waypoint view.")]
        private static void OnHide(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null)
            {
                return;
            }
            if (Clear(from))
            {
                from.SendMessage(0x3B2, "Waypoint view cleared.");
            }
            else
            {
                from.SendMessage(0x3B2, "No waypoint view up. [showways to draw one.");
            }
        }

        // -----------------------------------------------------------------
        // Drawing
        // -----------------------------------------------------------------

        private static void Draw(Mobile from, int radius, bool links, bool announce)
        {
            Clear(from);

            var map = from.Map;
            var graph = WaypointRegistry.Graph;
            var view = new View
            {
                DrawnFrom = from.Location,
                Radius = radius,
                Links = links,
                ExpiresAt = Core.Now + Expiry,
            };

            // Everything in the box, and the component the bot standing here
            // would actually be routing within.
            var shown = new Dictionary<string, WaypointNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in graph.AllNodes)
            {
                if (InBox(from.Location, n.Location, radius))
                {
                    shown[n.Name] = n;
                }
            }

            if (shown.Count == 0)
            {
                // Walking out of the graph ends the view rather than
                // following you across empty country repeating itself.
                from.SendMessage(0x22, announce
                    ? $"No waypoints within {radius} tiles. [MarkWay <name> puts one where you stand."
                    : $"Waypoint view: nothing within {radius} tiles of here — view cleared.");
                return;
            }

            var nearest = graph.FindNearestNode(from.Location);
            int myComp = nearest != null ? graph.ComponentOf(nearest.Name) : -1;

            int broken = 0, offComp = 0, longLinks = 0, drawnLinks = 0;
            var problems = new List<string>();

            // What got drawn, so the names can be read back — an overlay has
            // nothing solid to mouse over.
            var placed = new List<(WaypointNode Node, WaypointMarkerItem Marker, string Flag)>();

            // ---- Nodes ----
            foreach (var n in shown.Values)
            {
                bool bad = false;
                foreach (var c in n.Connects)
                {
                    if (graph.Get(c) == null)
                    {
                        bad = true;
                        problems.Add($"'{n.Name}' names a neighbour that does not exist: '{c}'");
                    }
                }

                int comp = graph.ComponentOf(n.Name);
                bool offIsland = myComp >= 0 && comp >= 0 && comp != myComp;

                int hue = bad ? HueBroken
                        : nearest != null && n.Name.InsensitiveEquals(nearest.Name) ? HueNearest
                        : offIsland ? HueOtherComp
                        : HueNode;

                if (bad) broken++;
                else if (offIsland) offComp++;

                string label = $"waypoint: {n.Name}";
                if (offIsland)
                {
                    label += "  [NO ROUTE FROM HERE]";
                }
                else if (bad)
                {
                    label += "  [BROKEN LINK]";
                }

                var marker = Place(view, map, n.Location, NodeArt, hue, label);
                placed.Add((n, marker, bad ? "BROKEN" : offIsland ? "no route" : ""));
            }

            // ---- Links ----
            if (links)
            {
                var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var n in shown.Values)
                {
                    foreach (var cName in n.Connects)
                    {
                        var other = graph.Get(cName);
                        if (other == null)
                        {
                            continue; // already reported on the node itself
                        }

                        // One line per pair, whichever end we reach first.
                        string key = string.CompareOrdinal(n.Name, other.Name) < 0
                            ? n.Name + "\0" + other.Name
                            : other.Name + "\0" + n.Name;
                        if (!done.Add(key))
                        {
                            continue;
                        }

                        int dist = Dist(n.Location, other.Location);
                        bool tooLong = dist > WaypointGraph.MaxLegDistance;
                        if (tooLong)
                        {
                            longLinks++;
                            problems.Add(
                                $"'{n.Name}' -> '{other.Name}' is {dist} tiles, over the " +
                                $"{WaypointGraph.MaxLegDistance}-tile A* limit — bots cannot walk it");
                        }

                        DrawLink(view, map, n.Location, other.Location,
                                 tooLong ? HueLongLink : HueLink,
                                 $"link: {n.Name} <-> {other.Name}  ({dist} tiles)");
                        drawnLinks++;

                        if (view.Items.Count >= MaxItems)
                        {
                            break;
                        }
                    }
                    if (view.Items.Count >= MaxItems)
                    {
                        break;
                    }
                }
            }

            _views[from] = view;
            StartTimer();

            if (!announce)
            {
                return;
            }

            from.SendMessage(0x35,
                $"Waypoint view: {shown.Count} node(s), {drawnLinks} connection(s) within {radius} tiles.");
            from.SendMessage(0x3B2,
                "  white = nearest to you   green = routable from here   " +
                "orange = other component   red = broken");
            if (!links)
            {
                from.SendMessage(0x3B2, "  Links hidden ([showways with no 'nolinks' shows them).");
            }

            if (broken > 0 || offComp > 0 || longLinks > 0)
            {
                from.SendMessage(0x22,
                    $"  PROBLEMS: {broken} broken node(s), {offComp} out of reach, " +
                    $"{longLinks} over-long link(s).");
                int n = 0;
                foreach (var p in problems)
                {
                    if (n++ >= 8)
                    {
                        from.SendMessage(0x22, $"  ...and {problems.Count - 8} more.");
                        break;
                    }
                    from.SendMessage(0x22, "  " + p);
                }
            }

            if (view.Items.Count >= MaxItems)
            {
                from.SendMessage(0x22,
                    $"  Stopped at {MaxItems} markers — use a smaller radius for the whole picture.");
            }

            // Names, since there is nothing solid to mouse over. Float the
            // close ones once — not on the silent redraws, or walking would
            // fill the journal — and print the nearest handful either way.
            placed.Sort((a, b) => Dist(from.Location, a.Node.Location)
                                  .CompareTo(Dist(from.Location, b.Node.Location)));

            foreach (var (node, marker, _) in placed)
            {
                if (marker == null || Dist(from.Location, node.Location) > LabelRadius)
                {
                    continue;
                }
                marker.LabelTo(from, node.Name);
            }

            from.SendMessage(0x3B2, "  Nearest nodes:");
            int listed = 0;
            foreach (var (node, _, flag) in placed)
            {
                if (listed++ >= 10)
                {
                    from.SendMessage(0x3B2, $"    ...and {placed.Count - 10} more.");
                    break;
                }
                string suffix = flag.Length > 0 ? $"  <- {flag}" : "";
                from.SendMessage(0x3B2,
                    $"    {Dist(from.Location, node.Location),3}t  {node.Name} " +
                    $"({node.Location.X},{node.Location.Y},{node.Location.Z}){suffix}");
            }

            from.SendMessage(0x3B2, "  [hideways to clear. It follows you and clears itself in 20 minutes.");
        }

        // Dots from a to b, skipping the ends so the node markers stay
        // readable. Each dot is snapped to a standable Z where one can be
        // found, so a line up a stairwell tracks the stairs.
        private static void DrawLink(View view, Map map, Point3D a, Point3D b, int hue, string label)
        {
            int dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < LinkSpacing * 2)
            {
                return; // neighbours close enough to touch; the nodes say it
            }

            int steps = (int)(len / LinkSpacing);
            for (int i = 1; i < steps; i++)
            {
                double t = (double)i / steps;
                int x = a.X + (int)Math.Round(dx * t);
                int y = a.Y + (int)Math.Round(dy * t);
                int z = a.Z + (int)Math.Round((b.Z - a.Z) * t);

                if (Walkable.TryFindSeedZ(map, x, y, z, out int sz))
                {
                    z = sz;
                }

                Place(view, map, new Point3D(x, y, z), LinkArt, hue, label);

                if (view.Items.Count >= MaxItems)
                {
                    return;
                }
            }
        }

        private static WaypointMarkerItem Place(
            View view, Map map, Point3D loc, int art, int hue, string name)
        {
            if (view.Items.Count >= MaxItems)
            {
                return null;
            }

            var item = new WaypointMarkerItem(art) { Hue = hue, Name = name };
            item.MoveToWorld(loc, map);
            view.Items.Add(item);
            return item;
        }

        // -----------------------------------------------------------------
        // Housekeeping
        // -----------------------------------------------------------------

        public static bool Clear(Mobile from)
        {
            if (from == null || !_views.Remove(from, out var view))
            {
                return false;
            }

            foreach (var item in view.Items)
            {
                item?.Delete();
            }

            if (_views.Count == 0)
            {
                _timer?.Stop();
                _timer = null;
            }
            return true;
        }

        // Redraw every view where it stands — for [ReloadWaypoints, so the
        // graph you are looking at is the graph the bots just picked up.
        public static void RefreshAll()
        {
            if (_views.Count == 0)
            {
                return;
            }

            var viewers = new List<Mobile>(_views.Keys);
            foreach (var m in viewers)
            {
                if (!_views.TryGetValue(m, out var v))
                {
                    continue;
                }
                int radius = v.Radius;
                bool links = v.Links;
                if (m.Deleted || m.NetState == null)
                {
                    Clear(m);
                    continue;
                }
                Draw(m, radius, links, announce: false);
            }
        }

        public static bool IsViewing(Mobile from) => from != null && _views.ContainsKey(from);

        private static void StartTimer()
        {
            _timer ??= Timer.DelayCall(Pulse, Pulse, OnTick);
        }

        private static void OnTick()
        {
            if (_views.Count == 0)
            {
                _timer?.Stop();
                _timer = null;
                return;
            }

            var viewers = new List<Mobile>(_views.Keys);
            foreach (var m in viewers)
            {
                if (!_views.TryGetValue(m, out var view))
                {
                    continue;
                }

                // Gone, or logged out — take the markers with them.
                if (m.Deleted || m.NetState == null || m.Map == null || m.Map == Map.Internal)
                {
                    Clear(m);
                    continue;
                }

                if (Core.Now >= view.ExpiresAt)
                {
                    Clear(m);
                    m.SendMessage(0x3B2, "Waypoint view expired. [showways to draw it again.");
                    continue;
                }

                if (Dist(m.Location, view.DrawnFrom) >= RedrawAfterMoved)
                {
                    Draw(m, view.Radius, view.Links, announce: false);
                }
            }
        }

        // -----------------------------------------------------------------

        private static bool InBox(Point3D a, Point3D b, int radius) =>
            Math.Abs(a.X - b.X) <= radius && Math.Abs(a.Y - b.Y) <= radius;

        private static int Dist(Point3D a, Point3D b)
        {
            int dx = a.X - b.X, dy = a.Y - b.Y;
            return (int)Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
