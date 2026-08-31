// =========================================================================
// WorldHouseSeeder.cs — SP-023's automated placement engine. Walks each
// SeedNodeRegistry entry outward in an expanding ring search until it
// finds ground the SAME validation a manual "Place Test House" click uses
// (Region.AllowHousing + HousePlacement.Check, both run against
// MerchantGuildAuthority so no rule gets a staff-access bypass - see
// OrganicMarketSpawner.CheckPlacement and MarketHousePlacementTarget,
// which this mirrors exactly instead of re-deriving its own rules), then
// hands off to the same OrganicMarketSpawner.PlaceTestHouse pipeline a
// manual placement uses - clutter, unlocked doors, stocked vendor, and
// MerchantGuildAuthority registration all happen there, identically
// either way.
//
// SP-025: one node's own search is still a cheap, bounded, synchronous
// geometry check (Region.Find + HousePlacement.Check, no world I/O) -
// nowhere near what CLAUDE.md's threading rules mean by "heavy work", and
// still runs fully on the main loop like everything else here has to
// (Region/BaseHouse/MerchantGuildAuthority state can't be touched off it).
// What doesn't scale is running EVERY node's placement - and every
// SendMessage progress line - back-to-back inside one synchronous method
// call on the GUMP response packet handler: at InhabitationNodes' current
// size that's up to 100 house/clutter/vendor spawns' worth of update
// packets, plus up to 100 individual text packets to the same NetState,
// all queued before the network layer gets a single tick to drain the
// socket - the exact shape of a "send buffer exhausted" disconnect. Both
// seeders below now process one node per Timer.DelayCall tick instead, so
// packet output spreads across real wall-clock time at the same cadence
// the game loop already drains sockets at.
//
// SP-026: 75ms plus a text packet on EVERY tick still wasn't enough headroom
// - a GM standing anywhere near a cluster of newly-built houses (their own
// multi/item/mobile update packets, not the chat text) could still exhaust
// a still-default 256KB send buffer, and the per-tick SendMessage calls
// added their own steady drip of packets on top of that regardless of the
// GM's location. Two changes: the network.sendBufferSize floor is now
// raised server-side (install-server.sh's ensure_send_buffer_size, run on
// every install/update), and per-node chatter is gone entirely - each
// seeder now sends exactly two packets to `from` for an entire run (a
// kickoff acknowledgement, a completion summary), with everything in
// between going to the server console log instead.
// =========================================================================

using System;
using System.Collections.Generic;
using Server.Logging;
using Server.Multis;
using Server.Regions;

namespace Server.Engines.OrganicMarket;

public static class WorldHouseSeeder
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(WorldHouseSeeder));

    // 200ms/node - up from SP-025's 75ms - gives the network layer a full
    // extra tick's worth of room to drain sector/world update packets
    // between nodes, on top of the per-node chat packets this version
    // removes outright (see the file-header SP-026 note).
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);

    // Re-entrancy guards: a GM impatiently double-clicking a button that
    // now visibly takes several seconds shouldn't start a second
    // overlapping run - not unsafe (each tick's own IsAlreadyFulfilled
    // check would just route around whatever the other run already
    // placed), just confusing progress output for no benefit.
    private static bool _crossroadsRunning;
    private static bool _inhabitationRunning;

    // How close an already-registered market house has to be to a node's
    // anchor to count as "this node is already covered" - the node's own
    // search radius, so a house the search itself would have placed there
    // reads as fulfilled on a second run instead of the search routing
    // around it and creeping a second house in next door.
    //
    // SP-029: was 30 - independently verified against how close two
    // genuinely different POIs sit (still the same problem the SP-025
    // comment above describes), but 30 was ALSO overshooting into "one
    // early house silently blocks the next several nodes trying to
    // cluster nearby", which fought directly against this same ticket's
    // goal of dense neighborhood clusters. 18 keeps enough spacing to stop
    // one lucky placement from satisfying a dozen unrelated named nodes,
    // while letting SeedInhabitation's own cluster-mate attempt (see
    // ClusterJitterMin/Max below) land close enough to its primary to read
    // as neighbors, not a duplicate-detection collision.
    private const int CoverageRange = 18;

    // Fire-and-forget: the actual placements happen across many timer
    // ticks after this returns, so there's no synchronous result to
    // hand back to the caller any more - the final "seeded X/Total"
    // message is sent to `from` itself once the last tick completes.
    public static void SeedAll(Mobile from)
    {
        var authority = MerchantGuildAuthority.Instance;
        var nodes = SeedNodeRegistry.Nodes;

        if (authority == null)
        {
            from?.SendMessage("Merchant Guild Authority is not initialized.");
            return;
        }

        if (_crossroadsRunning)
        {
            from?.SendMessage("OrganicMarket: a crossroads seeding pass is already in progress.");
            return;
        }

        if (nodes.Length == 0)
        {
            from?.SendMessage("OrganicMarket: Successfully seeded 0/0 trade corridor houses.");
            return;
        }

        _crossroadsRunning = true;
        var fulfilled = 0;
        var index = 0;

        from?.SendMessage($"OrganicMarket: Starting trade corridor scan across {nodes.Length} POIs...");

        Timer.DelayCall(TimeSpan.Zero, TickInterval, nodes.Length, () =>
        {
            var node = nodes[index++];

            if (IsAlreadyFulfilled(node, authority))
            {
                fulfilled++;
                logger.Information("SeedAll: {Node} already fulfilled", node.Name);
            }
            else if (SeedOne(node, authority))
            {
                fulfilled++;
                logger.Information("SeedAll: seeded {Node}", node.Name);
            }
            else
            {
                logger.Information("SeedAll: no valid ground found for {Node}", node.Name);
            }

            if (index >= nodes.Length)
            {
                _crossroadsRunning = false;
                from?.SendMessage($"OrganicMarket: Successfully seeded {fulfilled}/{nodes.Length} trade corridor houses.");
            }
        });
    }

    // Deliberately NOT node.Radius: that's how far the search is allowed
    // to roam looking for a NEW spot, which for the wider InhabitationNodes
    // catalog runs into the hundreds of tiles. Reusing it here as "how
    // close counts as already covering this POI" was a real bug found
    // during SP-025 verification - several named nodes sit only a few
    // hundred tiles apart (e.g. every Britain-area entry), so a large
    // search radius made ONE lucky placement near a town silently satisfy
    // a dozen unrelated nodes at once, and most of the catalog reported
    // "already fulfilled" without a single real house ever having been
    // attempted for it. A fixed, much smaller coverage distance keeps
    // "is this specific POI covered" meaningfully distinct from "how far
    // will I look for a spot." (Value itself now lives up in the file next
    // to SeedAll - see the SP-029 comment there.)

    private static bool IsAlreadyFulfilled(SeedNode node, MerchantGuildAuthority authority)
    {
        for (var i = 0; i < authority.Count; i++)
        {
            var house = authority.HouseAt(i);
            if (house?.Deleted == false && house.Map == node.Map &&
                Utility.InRange(house.X, house.Y, node.Anchor.X, node.Anchor.Y, CoverageRange))
            {
                return true;
            }
        }

        return false;
    }

    // SP-024/SP-025: the world-inhabitation pass. Same ring search as
    // SeedAll, over the much larger InhabitationNodes catalog (now
    // 60-100+ POIs), with each node independently rolled ~10% vendor shop
    // / ~90% ambient filler home (Utility.RandomDouble() < 0.10) - see
    // OrganicMarketSpawner.PlaceHouse for what that split actually changes
    // (Public/doors/clutter/vendor). A node already covered by an earlier
    // pass (either seeder - this one and SeedAll share the same "is there
    // already a house near here" check) is skipped rather than re-rolled,
    // so repeat clicks don't creep duplicate houses into the same POI.
    //
    // Fire-and-forget, same as SeedAll above - see the file-header comment
    // for why this can no longer place everything and return a result
    // synchronously. The final "Inhabitation complete" message is sent to
    // `from` once the last tick runs.
    public static void SeedInhabitation(Mobile from)
    {
        var authority = MerchantGuildAuthority.Instance;
        var nodes = SeedNodeRegistry.InhabitationNodes;

        if (authority == null)
        {
            from?.SendMessage("Merchant Guild Authority is not initialized.");
            return;
        }

        if (_inhabitationRunning)
        {
            from?.SendMessage("OrganicMarket: a world inhabitation pass is already in progress.");
            return;
        }

        if (nodes.Length == 0)
        {
            from?.SendMessage("OrganicMarket: Inhabitation complete. Placed 0 vendor shops and 0 ambient filler houses across Britannia.");
            return;
        }

        _inhabitationRunning = true;
        var vendors = 0;
        var fillers = 0;
        var index = 0;

        from?.SendMessage($"OrganicMarket: Starting world inhabitation scan across {nodes.Length} POIs...");

        Timer.DelayCall(TimeSpan.Zero, TickInterval, nodes.Length, () =>
        {
            var node = nodes[index++];

            if (!IsAlreadyFulfilled(node, authority))
            {
                var asVendor = Utility.RandomDouble() < 0.10;
                var placed = asVendor ? SeedOne(node, authority) : PlaceFillerAttempt(node, authority);

                if (placed)
                {
                    if (asVendor)
                    {
                        vendors++;
                        logger.Information("SeedInhabitation: opened a vendor shop at {Node}", node.Name);
                    }
                    else
                    {
                        fillers++;
                        logger.Information("SeedInhabitation: settled an ambient home at {Node}", node.Name);
                    }
                }
                else
                {
                    logger.Information("SeedInhabitation: no valid ground found for {Node}", node.Name);
                }
            }
            else
            {
                logger.Information("SeedInhabitation: {Node} already fulfilled", node.Name);
            }

            // SP-029: independently-anchored filler attempts near the same
            // POI - see ClusterAttempt below for why this exists and why
            // it's always filler, never a vendor. Empirically (see this
            // file's own SP-029 diagnostics history) a single fixed
            // anchor's own ring search misses plenty of genuinely close,
            // genuinely valid ground - RingStep 2's search pattern only
            // ever samples even-tile offsets from wherever it starts, so
            // an odd-offset valid spot right next to a bad anchor is
            // invisible to it no matter the budget. Each cluster-mate
            // attempt starts its OWN fresh ring search from its OWN
            // independently jittered anchor, which sidesteps that blind
            // spot by construction rather than needing every one of
            // InhabitationNodes' 100+ entries hand-tuned the way the much
            // shorter Nodes crossroads list was.
            //
            // SP-030: three, not two - the new cemetery/building-footprint
            // exclusion checks (WorldHouseSeeder.IsCemeteryRegion,
            // OrganicMarketSpawner.HasFootprintConflict) correctly reject
            // more candidates than before, which is the whole point of
            // this ticket, but it also pulled total placements from
            // SP-029's ~144 down to ~125 - under the 130-150+ target. A
            // third independent attempt recovers that headroom the same
            // way the second one raised the floor in SP-029, without
            // touching either exclusion check's own correctness.
            for (var cluster = 0; cluster < 3; cluster++)
            {
                if (ClusterAttempt(node, authority))
                {
                    fillers++;
                    logger.Information("SeedInhabitation: settled a cluster home near {Node}", node.Name);
                }
            }

            if (index >= nodes.Length)
            {
                _inhabitationRunning = false;
                from?.SendMessage(
                    $"OrganicMarket: Inhabitation complete. Placed {vendors} vendor shops and {fillers} " +
                    "ambient filler houses across Britannia."
                );
            }
        });
    }

    // SP-029: how far from a node's own anchor the cluster-mate attempt
    // jitters its own independent anchor - far enough that CoverageRange's
    // 18-tile "already covered" radius won't just reject it as a
    // duplicate of whatever the primary attempt placed, close enough to
    // still read as the same neighborhood/POI rather than a wholly
    // unrelated location.
    private const int ClusterJitterMin = 20;
    private const int ClusterJitterMax = 55;

    // Task 4 targets 100-150+ total ambient filler homes; growing
    // InhabitationNodes' own catalog only gets partway there without
    // either an unreasonably long node list or diminishing returns from
    // packing already-crowded areas even denser. A second, always-filler
    // attempt per node - independently anchored nearby rather than
    // reusing the exact same spot - turns each named POI into the seed of
    // a small cluster instead of exactly one house, which also just reads
    // as a more organic settlement pattern than one isolated building at
    // every point. Always filler, never a vendor: crossroads vendor
    // density is [Seed World Crossroads]'s own job (SeedAll above); this
    // pass's vendor share stays governed entirely by the 10% roll in
    // SeedInhabitation's primary attempt.
    private static bool ClusterAttempt(SeedNode node, MerchantGuildAuthority authority)
    {
        var jittered = node with { Anchor = JitterAnchor(node.Anchor) };
        return !IsAlreadyFulfilled(jittered, authority) && PlaceFillerAttempt(jittered, authority);
    }

    private static Point3D JitterAnchor(Point3D anchor)
    {
        var dx = Utility.RandomMinMax(ClusterJitterMin, ClusterJitterMax) * (Utility.RandomBool() ? 1 : -1);
        var dy = Utility.RandomMinMax(ClusterJitterMin, ClusterJitterMax) * (Utility.RandomBool() ? 1 : -1);
        return new Point3D(anchor.X + dx, anchor.Y + dy, anchor.Z);
    }

    // The three grandest classic structures ModernUO ships (see
    // MarketHouseStyle) - filler-only, and even then a deliberately rare
    // roll. Castle's 31x31 footprint alone fails far more searches than
    // it passes; rolling it as the common case would tank this node's -
    // and its cluster-mate's - own placement rate rather than help it.
    private const double GrandStructureChance = 0.04;

    // SP-029: confirmed empirically (this file's own SP-029 testing
    // history) that Tower/Keep/Castle routinely need tens to hundreds of
    // thousands of ring candidates before HousePlacement.Check finds room
    // for one, even in open countryside - Castle alone took ~370,000 at
    // RingStep 1. At RingStep 2's ~4-evaluations-per-radius-unit cost,
    // 60,000 reaches a comparable ~300+ tile radius for a fraction of the
    // work, and Radius 400 gives the ring search room to actually walk
    // that far (a normal filler node's own Radius, 45-150, would cut the
    // search off long before reaching realistically-available open
    // ground for a footprint this size).
    private const int GrandStructureSearchRadius = 400;
    private const int GrandStructureMaxEvaluations = 60000;

    // Rolls this filler attempt's style (RARE grand-structure substitution
    // included) and runs it through SeedOne with whatever budget that
    // roll needs. The one and only place a grand structure can appear -
    // both SeedInhabitation's primary attempt and ClusterAttempt route
    // through here, never the vendor path (SeedAll, or asVendor: true
    // above), matching the task's own "ambient filler pool only" intent.
    private static bool PlaceFillerAttempt(SeedNode node, MerchantGuildAuthority authority)
    {
        var style = RollFillerStyle(node.Style);
        var isGrand = style is MarketHouseStyle.LargeTower or MarketHouseStyle.Keep or MarketHouseStyle.Castle;

        var placeNode = node with
        {
            Style = style,
            Radius = isGrand ? Math.Max(node.Radius, GrandStructureSearchRadius) : node.Radius
        };

        return SeedOne(
            placeNode, authority, asVendor: false,
            isGrand ? GrandStructureMaxEvaluations : MaxCandidateEvaluations
        );
    }

    private static MarketHouseStyle RollFillerStyle(MarketHouseStyle baseStyle)
    {
        if (Utility.RandomDouble() >= GrandStructureChance)
        {
            return baseStyle;
        }

        return Utility.Random(3) switch
        {
            0 => MarketHouseStyle.LargeTower,
            1 => MarketHouseStyle.Keep,
            _ => MarketHouseStyle.Castle
        };
    }

    // SP-028: hard ceiling on how many candidate tiles a single node's
    // search will evaluate before giving up, regardless of how much
    // radius is left unexplored. Without this, a node whose entire
    // configured radius is bad ground (deep water, a dungeon interior
    // boxed in by rock) walks its FULL ring pattern every time - at
    // radius 150 that's on the order of 90,000 candidate evaluations
    // inside one single-threaded Timer tick, long enough to be a real,
    // measurable stall of the main loop that delays every connected
    // client's own packet processing for that tick - not what a "send
    // buffer exhausted" warning looks like at first glance, but a
    // legitimate contributing cause of one under real load. Paired with
    // RingStep below, this trades search thoroughness for a guaranteed
    // bounded per-node cost.
    //
    // SP-029: 300 was too aggressive - combined with the slope/static
    // pre-filter this replaced (see PassesFastTerrainFilter below), whole
    // nodes were aborting before the ring search ever reached genuinely
    // open ground, collapsing the overall placement hit-rate. 1000 keeps
    // the same bounded-per-tick guarantee (each check is now cheaper too -
    // one land-tile flag lookup instead of four GetAverageZ calls plus a
    // static-tile scan) while giving RingStep=2 below enough budget to
    // walk a full ~45-50 tile radius ring pattern to exhaustion - covering
    // the entire configured Radius on every crossroads node instead of
    // giving up partway through it.
    private const int MaxCandidateEvaluations = 1000;

    // Step 2 tiles per candidate instead of every integer coordinate.
    // Every style this tool places has a floor plan several tiles wide in
    // each direction, so skipping every other candidate essentially never
    // costs a genuinely valid spot - it just means roughly 4x fewer
    // evaluations to cover the same area.
    //
    // SP-029: kept at 2 (not dropped to 1) deliberately - at the raised
    // 1000-evaluation budget, step 2's ~4 evaluations/ring-radius-unit
    // reaches a full ~45-tile radius (matching crossroads nodes' actual
    // configured Radius), where step 1's ~8 evaluations/unit would only
    // reach ~15 tiles out before the budget ran out, silently under-
    // exploring every node whose nearest open ground sits farther than
    // that - worse for hit-rate, not better, despite sampling more finely
    // near the anchor.
    private const int RingStep = 2;

    // Walks an expanding ring pattern out from the node's anchor (ring 0
    // is the anchor tile itself, then every tile at Chebyshev distance
    // RingStep, then 2*RingStep, ...) so the FIRST valid spot found is
    // always the one closest to the intended corridor location, not an
    // arbitrary one from scanning a filled square in row-major order.
    //
    // asVendor picks which OrganicMarketSpawner entry point places the
    // house once a valid spot is found - true (SeedAll's only caller
    // today) always places a full vendor shop; SeedInhabitation passes its
    // own per-node coin flip.
    //
    // SP-029: maxEvaluations defaults to the standard per-node budget
    // above, but SeedInhabitation's grand-structure roll (see
    // RollFillerStyle) passes a much larger one-off budget instead -
    // Tower/Keep/Castle's footprints are big enough that even wide-open
    // countryside routinely needs tens of thousands of candidates checked
    // before HousePlacement.Check finds room for one (confirmed
    // empirically: Castle alone took ~370,000 candidates at RingStep 1 in
    // testing), far beyond what every other style needs. At the standard
    // 1,000-evaluation budget these three would never actually place in
    // practice - a roll that always silently fails isn't the "rare grand
    // structure" this ticket asked for, just dead code that looks alive.
    // Spending that much search on a single node is only affordable
    // because the 4% roll chance keeps it rare.
    public static bool SeedOne(SeedNode node, MerchantGuildAuthority authority, bool asVendor = true, int maxEvaluations = MaxCandidateEvaluations)
    {
        var map = node.Map;
        if (map == null || map == Map.Internal)
        {
            return false;
        }

        var offset = OrganicMarketSpawner.PlacementOffset(node.Style);
        var evaluated = 0;

        foreach (var p in RingPoints(node.Anchor, node.Radius, RingStep))
        {
            if (evaluated++ >= maxEvaluations)
            {
                break;
            }

            // SP-029: a ring point can walk past the map's own edge (a
            // negative X/Y, or past Width/Height) once its radius grows
            // past however close the anchor happens to sit to that edge -
            // RingPoints is pure integer arithmetic with no idea where the
            // map actually ends, and several InhabitationNodes anchors
            // (e.g. "Wrong Valley Clearing" at Y=100, "Ice Dungeon
            // Entrance" at Y=240) sit close enough to Y=0 that this was
            // reachable at their own configured Radius. Directory entry
            // #375's void spawn was exactly this: a negative server-side Y
            // sailing straight through every downstream check (Point3D's
            // X/Y are plain int - nothing here rejects a negative value on
            // its own) and only reading as a huge positive number once it
            // hit the client, whose own network packets encode X/Y as
            // unsigned 16-bit. Reject before any of that ever happens.
            if (p.X < 0 || p.Y < 0 || p.X >= map.Width || p.Y >= map.Height)
            {
                continue;
            }

            // Cheap reject BEFORE the two expensive checks below
            // (Region.Find's spatial lookup, and especially
            // HousePlacement.Check's full multi-footprint scan): open
            // water or otherwise-impassable land directly on this column.
            // Neither could ever pass HousePlacement.Check anyway - this
            // just stops paying its much higher cost to find that out.
            if (!PassesFastTerrainFilter(map, p.X, p.Y))
            {
                continue;
            }

            // Ground terrain height at this column - there's no house here
            // yet, so this is exactly what a player's own placement
            // reticle would read on the same spot.
            var z = map.GetAverageZ(p.X, p.Y);
            var candidate = new Point3D(p.X, p.Y, z);

            // MerchantGuildAuthority.Instance is the ONLY Mobile ever used
            // for either check below - never the calling GM (`from` isn't
            // even in scope here; SeedAll/SeedInhabitation never pass it
            // into SeedOne). Region.AllowHousing and HousePlacement.Check
            // both auto-pass for AccessLevel.GameMaster+, so evaluating
            // against a real GM would silently bypass every rule this
            // search exists to enforce - and, just as important for an
            // automated pass touching POIs scattered across the whole
            // map, would tie each candidate to a live NetState's own
            // viewport/mobile state for no reason, rather than to a
            // Mobile that only ever exists on the internal map.
            var region = Region.Find(candidate, map);
            if (!region.AllowHousing(authority, candidate) || IsCemeteryRegion(region))
            {
                continue;
            }

            var center = new Point3D(candidate.X - offset.X, candidate.Y - offset.Y, candidate.Z - offset.Z);

            var result = OrganicMarketSpawner.CheckPlacement(map, center, node.Style, out var toMove);
            if (result != HousePlacementResult.Valid)
            {
                continue;
            }

            var index = asVendor
                ? OrganicMarketSpawner.PlaceTestHouse(map, center, node.Style, node.Archetype, toMove)
                : OrganicMarketSpawner.PlaceFillerHouse(map, center, node.Style, toMove);

            if (index >= 0)
            {
                return true;
            }
        }

        return false;
    }

    // Fast, purely-arithmetic check against land tile data only - no
    // region tree lookup, no multi-footprint scan. Rejecting obviously bad
    // ground here is what keeps a fully-exhausted search's cost down to
    // MaxCandidateEvaluations cheap checks instead of that many
    // Region.Find + HousePlacement.Check calls.
    //
    // SP-029: this used to also reject on cardinal-neighbor slope variance
    // and any blocking static/multi tile on the column - both individually
    // reasonable-sounding, but between them they were the real cause of
    // the placement-rate collapse this ticket exists to fix: real
    // buildable ground is FULL of single-static clutter (a bush, a rock,
    // a fence post) that HousePlacement.Check's own full footprint scan
    // already knows how to route a house's corners around, and enough of
    // Britannia's terrain has gentle-but->2-Z-unit variance that the slope
    // check was rejecting genuinely valid spots right along with the
    // actual cliffs. Water is the one case where letting the real check
    // run instead is pure wasted cost - HousePlacement.Check will reject
    // every water tile anyway, just far more expensively - so that's the
    // only thing still short-circuited here.
    private static bool PassesFastTerrainFilter(Map map, int x, int y)
    {
        var landTile = map.Tiles.GetLandTile(x, y);
        var landFlags = TileData.LandTable[landTile.ID & TileData.MaxLandValue].Flags;
        return (landFlags & (TileFlag.Impassable | TileFlag.Wet)) == 0;
    }

    // SP-030: cheap (no tile scan, just walking a handful of Region.Parent
    // references) so it runs on every candidate right next to the existing
    // region.AllowHousing() check, unlike the gravestone/building-static
    // scans below (OrganicMarketSpawner.HasFootprintConflict), which are
    // deliberately deferred to the one candidate per search that actually
    // reaches HousePlacementResult.Valid - a per-tile static scan on every
    // one of a node's up-to-1000 evaluated candidates is exactly the kind
    // of synchronous stall SP-026/SP-028 already had to fix once.
    //
    // Region.IsPartOf<T>() alone isn't enough here: PoisonedCemeteryRegion
    // is the only cemetery-flavored Region subclass this server ships, and
    // it's specific to Ilshenar's Poisoned Cemetery quest content - it
    // doesn't cover a classic OSI-style graveyard like Moonglow's, which on
    // this server's region data isn't its own Region at all (confirmed:
    // no "Moonglow Cemetery" entry exists in Distribution/Data/regions.json
    // - the grounds are dressed with gravestone statics directly, nothing
    // more). IsPartOf(string) also wouldn't help even if one existed - it's
    // an exact-name match, not a substring one. So this walks the region's
    // own Parent chain by hand, doing a substring match against every
    // ancestor's Name - catching a real "___ Cemetery"/"___ Graveyard"
    // region wherever this server (or a future one) happens to define one,
    // on top of the PoisonedCemeteryRegion type check for the one case that
    // already exists.
    private static bool IsCemeteryRegion(Region region)
    {
        if (region.IsPartOf<PoisonedCemeteryRegion>())
        {
            return true;
        }

        for (var r = region; r != null; r = r.Parent)
        {
            if (r.Name != null &&
                (r.Name.Contains("Cemetery", StringComparison.OrdinalIgnoreCase) ||
                 r.Name.Contains("Graveyard", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<Point2D> RingPoints(Point3D anchor, int maxRadius, int step)
    {
        yield return new Point2D(anchor.X, anchor.Y);

        for (var r = step; r <= maxRadius; r += step)
        {
            for (var dx = -r; dx <= r; dx += step)
            {
                yield return new Point2D(anchor.X + dx, anchor.Y - r); // top edge
                yield return new Point2D(anchor.X + dx, anchor.Y + r); // bottom edge
            }

            for (var dy = -r + step; dy <= r - step; dy += step)
            {
                yield return new Point2D(anchor.X - r, anchor.Y + dy); // left edge
                yield return new Point2D(anchor.X + r, anchor.Y + dy); // right edge
            }
        }
    }
}
