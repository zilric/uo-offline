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
    // will I look for a spot."
    private const int CoverageRange = 30;

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
                if (SeedOne(node, authority, asVendor))
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

    // Walks an expanding ring pattern out from the node's anchor (ring 0
    // is the anchor tile itself, then every tile at Chebyshev distance 1,
    // then 2, ...) so the FIRST valid spot found is always the one
    // closest to the intended corridor location, not an arbitrary one
    // from scanning a filled square in row-major order.
    //
    // asVendor picks which OrganicMarketSpawner entry point places the
    // house once a valid spot is found - true (SeedAll's only caller
    // today) always places a full vendor shop; SeedInhabitation passes its
    // own per-node coin flip.
    public static bool SeedOne(SeedNode node, MerchantGuildAuthority authority, bool asVendor = true)
    {
        var map = node.Map;
        if (map == null || map == Map.Internal)
        {
            return false;
        }

        var offset = OrganicMarketSpawner.PlacementOffset(node.Style);

        foreach (var p in RingPoints(node.Anchor, node.Radius))
        {
            // Ground terrain height at this column - there's no house here
            // yet, so this is exactly what a player's own placement
            // reticle would read on the same spot.
            var z = map.GetAverageZ(p.X, p.Y);
            var candidate = new Point3D(p.X, p.Y, z);

            var region = Region.Find(candidate, map);
            if (!region.AllowHousing(authority, candidate))
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

    private static IEnumerable<Point2D> RingPoints(Point3D anchor, int maxRadius)
    {
        yield return new Point2D(anchor.X, anchor.Y);

        for (var r = 1; r <= maxRadius; r++)
        {
            for (var dx = -r; dx <= r; dx++)
            {
                yield return new Point2D(anchor.X + dx, anchor.Y - r); // top edge
                yield return new Point2D(anchor.X + dx, anchor.Y + r); // bottom edge
            }

            for (var dy = -r + 1; dy <= r - 1; dy++)
            {
                yield return new Point2D(anchor.X - r, anchor.Y + dy); // left edge
                yield return new Point2D(anchor.X + r, anchor.Y + dy); // right edge
            }
        }
    }
}
