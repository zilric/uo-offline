// =========================================================================
// PlayerShopPatronageManager.cs — SP-036: procedural shopper visits for
// GENUINELY PLAYER-OWNED player-vendor shops (not the OrganicMarket's own
// ambient/seeded test houses - see IsCandidateHouse). Makes those shops
// feel alive and economically viable: a scheduler that scans every house
// in the world for one with real stock, a shopper bot that arrives by
// Moongate/Recall/on foot, appraises the vendor's stock against a real
// baseline price, buys what's a fair deal, and leaves cleanly.
//
// Architecture deliberately mirrors BotShopBrowsingManager (the same
// chained-Timer.DelayCall state machine, the same PathFollower-driven
// approach, the same "never touch Frozen/CantWalk" philosophy) since
// that file's own live-tested design already solved the "smooth
// approach, silent dwell, no rubberbanding" problem this ticket asks
// for again in a new context. Where the geometry/pacing is IDENTICAL
// (counter standoff, step cadence) this reuses that file's own internal
// methods rather than re-deriving them - see ComputeCounterSpot/
// ComputeStepDelay in BotShopBrowsingManager.cs.
//
// The one real architectural difference: BotShopBrowsingManager redirects
// an EXISTING roaming bot for a few seconds and hands it back exactly as
// it was. This spawns a brand-new, throwaway bot for the whole visit and
// deletes it at the end - there's no "prior journey" to cache/restore,
// so the shopper just gets a silent placeholder behavior (SilentShopper)
// for its entire (short) lifetime and never needs BotShopBrowsingManager's
// own PlayerBotBehavior-caching dance.
// =========================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using Server.CustomBots;
using Server.Items;
using Server.Logging;
using Server.Mobiles;
using Server.Multis;

namespace Server.Engines.OrganicMarket;

public static class PlayerShopPatronageManager
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(PlayerShopPatronageManager));

    // How often the scheduler re-scans every house in the world for one
    // that's now due a visit. Cheap per house (a couple of dictionary
    // lookups plus, only for real candidates, one pass over each
    // vendor's own backpack) - a 1-minute cadence gives fine enough
    // granularity to land the ticket's own "within the first 2-5 minutes
    // of server boot" catch-up window precisely, without being anywhere
    // near expensive enough to matter at that frequency.
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(1);

    // "Base visit interval: 30 to 60 minutes of active server uptime."
    private const int BaseIntervalMinMinutes = 30;
    private const int BaseIntervalMaxMinutes = 60;

    // "Well-stocked shops (>= 25 total items) reduce cooldown by up to
    // 35% (approaching ~20-25 minutes)." 1% off the rolled base interval
    // per item past the 25 threshold, capped at 35% off - a 25-item shop
    // barely moves off the base roll, a 60+-item shop always lands at
    // the full 35% discount.
    private const int WellStockedThreshold = 25;
    private const double WellStockedMaxReduction = 0.35;
    private const int WellStockedFloorMinutes = 20;

    // "Sparse shops (<= 3 items) incur increased cooldowns (>= 60
    // minutes)."
    private const int SparseThreshold = 3;
    private const int SparseMinMinutes = 60;
    private const int SparseMaxMinutes = 90;

    // "If >= 3 active vendors and >= 30 items, roll a 15% chance to
    // schedule a small multi-shopper group (2-3 bots arriving 15-30s
    // apart)."
    private const int GroupMinVendors = 3;
    private const int GroupMinItems = 30;
    private const double GroupChance = 0.15;
    private const int GroupMinSize = 2;
    private const int GroupMaxSize = 3;
    private const int GroupStaggerMinSeconds = 15;
    private const int GroupStaggerMaxSeconds = 30;

    // "Session-Burst Catch-Up ... whose last recorded visit timestamp
    // exceeds 45 active minutes (or first boot). Schedule a shopper
    // arrival within the first 2-5 minutes of server boot." _nextVisit
    // is never persisted (in-memory only, exactly like BotShopBrowsing
    // Manager's own _lastBrowsed) - so every house looks like "first
    // boot" the moment this dictionary is empty, which is every real
    // boot. That single fact is what makes the catch-up case and the
    // ongoing-operation case the same code path below: a house not yet
    // in the dictionary always gets a 2-5 minute due time the first time
    // this scan ever sees it, whether that's the literal first boot or
    // a house built five hours into a long session.
    private const int CatchUpMinMinutes = 2;
    private const int CatchUpMaxMinutes = 5;

    // house.Serial -> Core.Uptime (ms) this house is next due a visit.
    private static readonly Dictionary<Serial, long> _nextVisitDueUptime = new();

    // "Dwells for 8-15 seconds while silently browsing vendor stock."
    private const int DwellMinSeconds = 8;
    private const int DwellMaxSeconds = 15;

    // "purchase if vendorPrice <= (basePrice * 1.4)... GM-crafted,
    // exceptional, or high-durability items allow up to 2.0x markup."
    private const double FairPriceMultiplier = 1.4;
    private const double PremiumPriceMultiplier = 2.0;

    // Arrival vector roll: 35% Moongate, 35% Recall, 30% Walk-in.
    private const double MoongateChance = 0.35;
    private const double RecallChance = 0.35; // cumulative through 0.70

    // "Keep the 6-8 second lifespan timer" - rolled per gate rather than
    // one fixed number, same reasoning as every other timing figure in
    // this file (two houses' gates shouldn't read as metronome-identical).
    private const int GateLifetimeMinSeconds = 6;
    private const int GateLifetimeMaxSeconds = 8;
    private const int GateSoundId = 0x20E; // "Gate Travel sound."
    private const int RecallSoundId = 0x1FC; // "Recall sound."
    private const int ArrivalSparkleId = 0x3728; // "spell sparkle effect."

    // SP-039: "a random Britannia city bank/square" - a real, usable
    // moongate destination, not a decorative loop back to itself. Every
    // coordinate is the ticket's own named bank/town-square landmark for
    // that city.
    private static readonly (string Name, Point3D Loc)[] CityDestinations =
    {
        ("Britain", new Point3D(1425, 1696, 0)),
        ("Moonglow", new Point3D(4467, 1173, 0)),
        ("Trinsic", new Point3D(1844, 2772, 0)),
        ("Minoc", new Point3D(2466, 543, 0)),
        ("Yew", new Point3D(546, 991, 0)),
        ("Skara Brae", new Point3D(596, 2138, 0))
    };

    private const int WalkInMinTiles = 18;
    private const int WalkInMaxTiles = 24;
    // "Path 15 tiles away toward wilderness/roads" - a small band around
    // the ticket's own figure rather than one fixed distance, matching
    // FindNearbySpot's own "roll within [min, max]" shape.
    private const int WalkAwayMinTiles = 13;
    private const int WalkAwayMaxTiles = 17;

    // Same ~11-second real-world timeout budget SP-037/038 settled on for
    // BotShopBrowsingManager's own approach - divided by the shopper's
    // own dynamic step delay (ComputeStepDelay, reused from that file) to
    // get an actual step-count budget that stays a consistent wall-clock
    // window regardless of the shopper's pace.
    private static readonly TimeSpan ApproachTimeout = TimeSpan.FromSeconds(11);

    [CallPriority(10)]
    public static void Initialize()
    {
        Timer.DelayCall(ScanInterval, ScanInterval, RunScan);
    }

    // -------------------------------------------------------------------
    // Scheduling
    // -------------------------------------------------------------------

    private static void RunScan()
    {
        var authority = MerchantGuildAuthority.Instance;
        var liveSerials = new HashSet<Serial>();

        foreach (var house in BaseHouse.AllHouses)
        {
            if (!IsCandidateHouse(house, authority))
            {
                continue;
            }

            var serial = house.Serial;
            liveSerials.Add(serial);

            if (!_nextVisitDueUptime.TryGetValue(serial, out var due))
            {
                // Never seen before - this IS the "first boot" (or
                // first-time-stocked) case, scheduled within the
                // ticket's own 2-5 minute catch-up window.
                _nextVisitDueUptime[serial] = Core.Uptime + CatchUpDelayMs();
                continue;
            }

            if (Core.Uptime < due)
            {
                continue;
            }

            ScheduleVisit(house);
            _nextVisitDueUptime[serial] = Core.Uptime + (long)ComputeInterval(house).TotalMilliseconds;
        }

        // Bounded cleanup - drop entries for houses that no longer
        // qualify (deleted, sold off its vendors, etc.) so a long-running
        // server doesn't accumulate one dictionary entry per house that
        // has ever briefly had a vendor.
        if (_nextVisitDueUptime.Count > liveSerials.Count)
        {
            var stale = new List<Serial>();
            foreach (var serial in _nextVisitDueUptime.Keys)
            {
                if (!liveSerials.Contains(serial))
                {
                    stale.Add(serial);
                }
            }

            foreach (var serial in stale)
            {
                _nextVisitDueUptime.Remove(serial);
            }
        }
    }

    private static long CatchUpDelayMs() =>
        (long)TimeSpan.FromMinutes(Utility.RandomMinMax(CatchUpMinMinutes, CatchUpMaxMinutes)).TotalMilliseconds;

    // Genuinely player-owned: excludes every OrganicMarket-seeded ambient/
    // test house (MerchantGuildAuthority.IsRegistered) so this system
    // never buys from its own admin-test vendors, and requires at least
    // one real sellable item so an empty new shop doesn't get scheduled
    // (and re-scheduled every scan) for nothing.
    private static bool IsCandidateHouse(BaseHouse house, MerchantGuildAuthority authority)
    {
        if (house?.Deleted != false || house.Map == null || house.Map == Map.Internal)
        {
            return false;
        }

        if (authority != null && authority.IsRegistered(house))
        {
            return false;
        }

        if (house.Owner is not PlayerMobile)
        {
            return false;
        }

        if (house.PlayerVendors == null || house.PlayerVendors.Count == 0)
        {
            return false;
        }

        return CountSellableItems(house) > 0;
    }

    // "Count total valid sale items across all vendors in the house" -
    // every top-level backpack item with a real, currently-for-sale
    // VendorItem entry (skips display-container-priced-negative parents
    // and anything mid-teardown).
    private static int CountSellableItems(BaseHouse house)
    {
        var count = 0;

        foreach (var vendor in house.PlayerVendors)
        {
            if (vendor?.Deleted != false || vendor.Backpack == null)
            {
                continue;
            }

            foreach (var item in vendor.Backpack.Items)
            {
                var vi = vendor.GetVendorItem(item);
                if (vi is { Valid: true, IsForSale: true })
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int CountActiveVendors(BaseHouse house)
    {
        var count = 0;

        foreach (var vendor in house.PlayerVendors)
        {
            if (vendor?.Deleted == false)
            {
                count++;
            }
        }

        return count;
    }

    private static TimeSpan ComputeInterval(BaseHouse house)
    {
        var items = CountSellableItems(house);

        if (items <= SparseThreshold)
        {
            return TimeSpan.FromMinutes(Utility.RandomMinMax(SparseMinMinutes, SparseMaxMinutes));
        }

        var baseMinutes = Utility.RandomMinMax(BaseIntervalMinMinutes, BaseIntervalMaxMinutes);

        if (items >= WellStockedThreshold)
        {
            var reduction = Math.Min(WellStockedMaxReduction, (items - WellStockedThreshold) * 0.01);
            baseMinutes = Math.Max(WellStockedFloorMinutes, (int)(baseMinutes * (1 - reduction)));
        }

        return TimeSpan.FromMinutes(baseMinutes);
    }

    // Rolls the multi-shopper group chance and fires off 1-3 spawns,
    // staggered per the ticket's own "15-30s apart."
    private static void ScheduleVisit(BaseHouse house)
    {
        var vendors = CountActiveVendors(house);
        var items = CountSellableItems(house);

        var isGroup = vendors >= GroupMinVendors && items >= GroupMinItems && Utility.RandomDouble() < GroupChance;
        var shopperCount = isGroup ? Utility.RandomMinMax(GroupMinSize, GroupMaxSize) : 1;

        for (var i = 0; i < shopperCount; i++)
        {
            var delay = i == 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(Utility.RandomMinMax(GroupStaggerMinSeconds, GroupStaggerMaxSeconds) * i);

            Timer.DelayCall(delay, () => SpawnShopper(house, forcedPurchase: false, targetVendor: null));
        }

        if (isGroup && VerboseConfig.Pathfinding)
        {
            logger.Information(
                "[ShopPatronage] Scheduling a {Count}-shopper group visit for {House}", shopperCount, house.Sign?.Name ?? house.Serial.ToString()
            );
        }
    }

    internal static PlayerVendor PickActiveVendor(BaseHouse house)
    {
        if (house?.PlayerVendors is not { Count: > 0 } vendors)
        {
            return null;
        }

        var candidates = new List<PlayerVendor>(vendors.Count);
        foreach (var vendor in vendors)
        {
            if (vendor?.Deleted == false && vendor.Backpack != null)
            {
                candidates.Add(vendor);
            }
        }

        return candidates.Count == 0 ? null : candidates[Utility.Random(candidates.Count)];
    }

    // -------------------------------------------------------------------
    // Shopper lifecycle
    // -------------------------------------------------------------------

    // Threaded through the whole visit instead of a growing parameter
    // list - the same idea BotShopBrowsingManager expresses via raw
    // parameters, just heavier here since a purchase-and-two-styles-of-
    // departure pipeline carries more state than a browse-and-leave one.
    private sealed class ShopperVisit
    {
        public Mobile Bot;
        public BaseHouse House;
        public PlayerVendor Vendor;
        public bool ForcedPurchase;
        public bool ArrivedByWalking;
        public Point3D DepartureAnchor; // the porch spot - base point for a walk-away departure's fresh 15-tile target
    }

    // A deliberately silent, do-nothing PlayerBotBehavior for the
    // shopper's entire (short) lifetime - see BotShopBrowsingManager.
    // BrowsingBehavior for the identical reasoning (Tick left as
    // PlayerBotBehavior's own empty virtual default guarantees zero
    // speech/emote/sound for the whole visit). A fresh throwaway bot
    // never has a "previous" behavior worth caching, unlike a redirected
    // roaming one, so this is simpler than that file's own version -
    // just attach once at spawn and never touch it again.
    private sealed class SilentShopperBehavior : PlayerBotBehavior
    {
        public SilentShopperBehavior()
        {
            ChatCategories = Array.Empty<string>();
            ChatChance = 0.0;
        }

        public override string SerializableName => "Shop Patron";

        public override string GetStatusLine(PlayerBot bot) => "shopping at a player vendor";
    }

    // Entry point for both the scheduler (targetVendor: null - picks one)
    // and [spawnbuyer (targetVendor: the GM's own target).
    internal static bool SpawnShopper(BaseHouse house, bool forcedPurchase, PlayerVendor targetVendor)
    {
        if (house?.Deleted != false || house.Map == null || house.Map == Map.Internal)
        {
            return false;
        }

        var vendor = targetVendor ?? PickActiveVendor(house);
        if (vendor == null)
        {
            return false;
        }

        var map = house.Map;

        // SP-039: a real door is the best reference point when one
        // exists, but a customizable HouseFoundation with no doors
        // placed yet (or any other multi with zero house.Doors entries)
        // shouldn't just silently drop the visit - see
        // FindArrivalReference for the sign/bounding-box fallback chain.
        if (FindArrivalReference(house, map) is not { } reference)
        {
            return false;
        }

        if (FindPorchSpot(house, reference, map) is not { } porch)
        {
            return false;
        }

        var roll = Utility.RandomDouble();
        var walkIn = roll >= MoongateChance + RecallChance;

        Point3D spawnLoc;
        if (walkIn)
        {
            spawnLoc = FindNearbySpot(porch, map, WalkInMinTiles, WalkInMaxTiles) ?? porch;
        }
        else
        {
            spawnLoc = porch;
        }

        var bot = new PlayerBot();
        bot.MoveToWorld(spawnLoc, map);
        bot.Warmode = false;

        if (bot is PlayerBot playerBot)
        {
            playerBot.Behavior = new SilentShopperBehavior();
        }

        if (!walkIn)
        {
            if (roll < MoongateChance)
            {
                CreateArrivalGate(porch, map);
            }

            PlayArrivalEffect(bot, roll < MoongateChance ? GateSoundId : RecallSoundId);
        }

        if (VerboseConfig.Pathfinding)
        {
            logger.Information(
                "[ShopPatronage] Spawned shopper {Bot} at {House} via {Vector}{Forced}",
                bot.Name, house.Sign?.Name ?? house.Serial.ToString(),
                walkIn ? "walk-in" : roll < MoongateChance ? "moongate" : "recall",
                forcedPurchase ? " (forced)" : ""
            );
        }

        var visit = new ShopperVisit
        {
            Bot = bot,
            House = house,
            Vendor = vendor,
            ForcedPurchase = forcedPurchase,
            ArrivedByWalking = walkIn,
            DepartureAnchor = porch
        };

        var stairZones = InteriorTileFinder.ComputeStairExclusionZones(house);
        var counterSpot = BotShopBrowsingManager.ComputeCounterSpot(vendor, stairZones);
        var follower = new PathFollower(bot, counterSpot);
        var stepDelay = BotShopBrowsingManager.ComputeStepDelay(bot);
        var maxSteps = Math.Max(1, (int)(ApproachTimeout.TotalMilliseconds / stepDelay.TotalMilliseconds));

        ApproachCounter(visit, follower, maxSteps, stepDelay);
        return true;
    }

    // SP-039: resolves the point FindPorchSpot searches outward from -
    // the real front door when the house has one, or the fallback chain
    // the ticket asks for when it doesn't (a customizable HouseFoundation
    // with no doors placed yet, or any other doorless multi):
    //   1. house.Sign - "the area immediately adjacent to or in front of
    //      the house sign is almost universally walkable front-ground
    //      access." SetSign runs for every BaseHouse at construction
    //      (classic styles AND HouseFoundation alike - HouseFoundation's
    //      own constructor computes its sign position from Components.
    //      Min/Center, always facing the same direction the stairs do),
    //      so this covers the doorless case in practice almost every
    //      time without needing any HouseFoundation-specific geometry.
    //   2. The multi's own bounding-box edge (FindFoundationFrontEdge) -
    //      true last resort, only reachable if the sign itself is
    //      somehow gone too.
    private static Point3D? FindArrivalReference(BaseHouse house, Map map)
    {
        if (InteriorTileFinder.FrontDoorLocation(house) is { } door)
        {
            return door;
        }

        if (house.Sign?.Deleted == false)
        {
            return house.Sign.Location;
        }

        return FindFoundationFrontEdge(house, map);
    }

    // Scans the south, then east, edge of the multi's own bounding box
    // (Components.Min/Max - covers a classic style's fixed footprint and
    // a HouseFoundation's own customized one identically) for the first
    // tile that's real, walkable exterior ground. "Scan the front edge
    // of the multi's bounding box facing south/east" per the ticket -
    // only ever reached if a house somehow has neither a door nor a sign
    // to anchor from.
    private static Point3D? FindFoundationFrontEdge(BaseHouse house, Map map)
    {
        var mcl = house.Components;
        var southY = house.Y + mcl.Max.Y + 1;
        var eastX = house.X + mcl.Max.X + 1;

        for (var x = house.X + mcl.Min.X; x <= house.X + mcl.Max.X; x++)
        {
            var z = map.GetAverageZ(x, southY);
            if (map.CanSpawnMobile(x, southY, z))
            {
                return new Point3D(x, southY, z);
            }
        }

        for (var y = house.Y + mcl.Min.Y; y <= house.Y + mcl.Max.Y; y++)
        {
            var z = map.GetAverageZ(eastX, y);
            if (map.CanSpawnMobile(eastX, y, z))
            {
                return new Point3D(eastX, y, z);
            }
        }

        return null;
    }

    // The porch/front-steps spot: the first candidate - all 8 compass
    // directions from `reference` (a real door, or FindArrivalReference's
    // own sign/bounding-box fallback for a doorless house), at distances
    // 2 through 5 - that's genuinely OUTSIDE the house's own floor plan
    // and still real, walkable ground. "On or directly outside the house
    // porch/front steps." Widened from an original cardinals-only/
    // distance-2-only version that failed outright on a real SmallShop in
    // live testing - a wide porch/patio overhang or a diagonally-facing
    // reference point can easily need more than 2 straight-cardinal tiles
    // to genuinely clear the building, so this uses the same distance/
    // direction candidate-ladder philosophy the rest of this subsystem
    // already leans on rather than assuming one exact offset always
    // works.
    //
    // Z handling also found wanting in that same live test: a style like
    // SmallShop has a genuinely raised floor with real steps down to
    // street level, so the reference point's own Z can sit well outside
    // any narrow window around it - an original reference.Z +/- 12 check
    // failed on EVERY exterior candidate for exactly that house. Each
    // candidate now tries the real terrain height first (map.
    // GetAverageZ, the same technique FindNearbySpot already uses
    // successfully for the walk-in/walk-away spots), then falls back to a
    // much more generous +/- 20 window around the reference point for a
    // raised porch platform that isn't bare terrain either.
    private static Point3D? FindPorchSpot(BaseHouse house, Point3D reference, Map map)
    {
        var directions = new (int Dx, int Dy)[]
        {
            (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)
        };

        for (var distance = 2; distance <= 5; distance++)
        {
            foreach (var (dx, dy) in directions)
            {
                var x = reference.X + dx * distance;
                var y = reference.Y + dy * distance;

                if (house.IsInside(new Point3D(x, y, reference.Z), 16))
                {
                    continue; // still inside the house - wrong direction out
                }

                if (map.CanSpawnMobile(x, y, map.GetAverageZ(x, y)))
                {
                    return new Point3D(x, y, map.GetAverageZ(x, y));
                }

                if (map.CanSpawnMobile(x, y, reference.Z - 20, reference.Z + 20, canSwim: false, cantWalk: false, out var z))
                {
                    return new Point3D(x, y, z);
                }
            }
        }

        return null;
    }

    // Shared by the walk-in arrival ("Spawn the bot 18-24 tiles away
    // along an accessible ... terrain tile") and the walk-away departure
    // ("path 15 tiles away toward wilderness/roads") - both just want
    // "a real walkable tile roughly N tiles out in some direction," at
    // two different distance bands. Tries every compass direction at a
    // randomly rolled distance within [minTiles, maxTiles] first, then
    // degrades in steps of 2 down to minTiles/2 before giving up
    // entirely - the same candidate-ladder philosophy this whole
    // subsystem already leans on rather than ever assuming pure geometry
    // is walkable.
    private static Point3D? FindNearbySpot(Point3D from, Map map, int minTiles, int maxTiles)
    {
        var directions = new (int Dx, int Dy)[]
        {
            (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)
        };

        for (var distance = Utility.RandomMinMax(minTiles, maxTiles); distance >= minTiles / 2; distance -= 2)
        {
            foreach (var (dx, dy) in directions)
            {
                var x = from.X + dx * distance;
                var y = from.Y + dy * distance;
                var z = map.GetAverageZ(x, y);

                if (map.CanSpawnMobile(x, y, z))
                {
                    return new Point3D(x, y, z);
                }
            }
        }

        return null;
    }

    // SP-039: a genuinely functional moongate - "should function as
    // real, usable moongates linking back to a random Britannia city
    // bank/square before expiring," not the decorative self-targeting
    // loop this used to be. Target/TargetMap are set via the constructor
    // (Moongate(target, targetMap, dispellable) - Items/Skill Items/
    // Magical/Misc/Moongate.cs), which is exactly equivalent to setting
    // gate.Target/gate.TargetMap on the instance afterward. Still
    // dispellable and still deleted after its own short lifespan, so a
    // real player who happens to step through gets a real, useful trip
    // to a city bank, but the gate itself doesn't linger as a permanent
    // fixture.
    private static void CreateArrivalGate(Point3D loc, Map map)
    {
        var destination = CityDestinations[Utility.Random(CityDestinations.Length)];
        var gate = new Moongate(destination.Loc, map, dispellable: true);
        gate.MoveToWorld(loc, map);

        var lifespan = Utility.RandomMinMax(GateLifetimeMinSeconds, GateLifetimeMaxSeconds);

        if (VerboseConfig.Pathfinding)
        {
            logger.Information(
                "[ShopPatronage] Opened a moongate to {City} {Target} for {Lifespan}s",
                destination.Name, destination.Loc, lifespan
            );
        }

        Timer.DelayCall(TimeSpan.FromSeconds(lifespan), () =>
        {
            if (gate?.Deleted == false)
            {
                gate.Delete();
            }
        });
    }

    private static void PlayArrivalEffect(Mobile bot, int soundId)
    {
        bot.PlaySound(soundId);
        bot.FixedParticles(ArrivalSparkleId, 9, 32, 5008, EffectLayer.Waist);
    }

    // One real pathing step per call - identical shape to BotShopBrowsing
    // Manager.WalkToCounter, just with a purchase-capable dwell at the
    // end instead of a pure cosmetic one, and no cached behavior to
    // restore on early exit (a throwaway shopper just deletes itself).
    private static void ApproachCounter(ShopperVisit visit, PathFollower follower, int stepsRemaining, TimeSpan stepDelay)
    {
        var bot = visit.Bot;

        if (bot?.Deleted != false || visit.House?.Deleted != false || visit.Vendor?.Deleted != false ||
            bot.Map != visit.House.Map)
        {
            bot?.Delete();
            return;
        }

        bot.Warmode = false;

        if (stepsRemaining <= 0)
        {
            // Couldn't reach the counter in time (blocked door, packed
            // shop, ...) - skip the dwell/purchase and go straight to a
            // graceful departure rather than standing wherever it got
            // stuck.
            Depart(visit);
            return;
        }

        if (follower.Follow(1))
        {
            BeginDwell(visit);
            return;
        }

        Timer.DelayCall(stepDelay, () => ApproachCounter(visit, follower, stepsRemaining - 1, stepDelay));
    }

    private static void BeginDwell(ShopperVisit visit)
    {
        var bot = visit.Bot;
        if (bot?.Deleted != false || visit.Vendor?.Deleted != false)
        {
            bot?.Delete();
            return;
        }

        bot.Direction = bot.GetDirectionTo(visit.Vendor);
        bot.Warmode = false;

        var dwellSeconds = Utility.RandomMinMax(DwellMinSeconds, DwellMaxSeconds);
        Timer.DelayCall(TimeSpan.FromSeconds(dwellSeconds), () => TryPurchase(visit));
    }

    // -------------------------------------------------------------------
    // Appraisal & purchase
    // -------------------------------------------------------------------

    private static void TryPurchase(ShopperVisit visit)
    {
        var vendor = visit.Vendor;

        if (visit.Bot?.Deleted != false || vendor?.Deleted != false || vendor.Backpack == null)
        {
            Depart(visit);
            return;
        }

        // "Evaluate candidate items in the vendor's root pack" - top-
        // level only, matching this system's own established "a bundle/
        // display container is one purchasable unit" convention (see
        // OrganicMarketSpawner/StockTemplateEngine). Shuffled so this
        // isn't always the same first-in-pack item on every visit.
        var candidates = new List<(Item Item, VendorItem Vi)>();
        foreach (var item in vendor.Backpack.Items)
        {
            var vi = vendor.GetVendorItem(item);
            if (vi is { Valid: true, IsForSale: true })
            {
                candidates.Add((item, vi));
            }
        }

        candidates.Shuffle();

        foreach (var (item, vi) in candidates)
        {
            if (!visit.ForcedPurchase && !IsFairPrice(item, vi.Price))
            {
                continue;
            }

            CompletePurchase(visit, item, vi);
            break; // one purchase per visit
        }

        Depart(visit);
    }

    // "Determine baseline core price (from GenericBuyInfo, item factory
    // baseline, or default template cost)." Two real, non-guessed
    // sources, tried in order:
    //   1. IShopSellInfo.GetBuyPriceFor - the SAME pricing oracle every
    //      real NPC vendor category (SBBlacksmith, SBTailor, ...) uses,
    //      already quality/durability/damage-aware on its own.
    //   2. StockTemplateEngine.BasePrices - this system's own default
    //      template cost for the resources/tools it generates, for
    //      anything no SBInfo category happens to stock.
    // An item neither source recognizes has no fair baseline to judge
    // against, so it's skipped (never bought) unless ForcedPurchase.
    private static bool IsFairPrice(Item item, int vendorPrice)
    {
        if (vendorPrice <= 0)
        {
            return true; // free or a display-priced item slipping through - always a deal
        }

        var basePrice = AppraiseBaseline(item);
        if (basePrice <= 0)
        {
            return false;
        }

        var allowance = IsPremiumQuality(item) ? PremiumPriceMultiplier : FairPriceMultiplier;
        return vendorPrice <= basePrice * allowance;
    }

    private static int AppraiseBaseline(Item item)
    {
        if (TryGetShopSellInfo(item.GetType(), out var sellInfo))
        {
            var price = sellInfo.GetBuyPriceFor(item);
            if (price > 0)
            {
                return price;
            }
        }

        return StockTemplateEngine.BasePrices.GetValueOrDefault(item.GetType(), 0);
    }

    // "GM-crafted, exceptional, or high-durability items" - Quality ==
    // Exceptional covers "GM-crafted" (that's what high-skill crafting
    // actually produces), and any non-Regular durability/damage/
    // protection rune also counts.
    private static bool IsPremiumQuality(Item item) => item switch
    {
        BaseWeapon w => w.Quality == WeaponQuality.Exceptional ||
                        w.DurabilityLevel != WeaponDurabilityLevel.Regular ||
                        w.DamageLevel != WeaponDamageLevel.Regular,
        BaseArmor a => a.Quality == ArmorQuality.Exceptional ||
                        a.Durability != ArmorDurabilityLevel.Regular ||
                        a.ProtectionLevel != ArmorProtectionLevel.Regular,
        _ => false
    };

    // Lazily built, cached forever - one reflection scan across every
    // loaded SBInfo subclass (the same "IS-A, scan every assembly once"
    // technique BotShopBrowsingManager.ResolveBotTypes already uses for
    // bot-type detection), mapping each Type an NPC shop category stocks
    // to that category's own IShopSellInfo. First SBInfo to claim a Type
    // wins; several categories legitimately overlap (both a Provisioner
    // and a Tailor might sell cloth) and any of their prices is an
    // equally real baseline.
    private static Dictionary<Type, IShopSellInfo> _sellInfoByType;

    private static bool TryGetShopSellInfo(Type itemType, out IShopSellInfo info)
    {
        EnsureSellInfoCache();
        return _sellInfoByType.TryGetValue(itemType, out info);
    }

    private static void EnsureSellInfoCache()
    {
        if (_sellInfoByType != null)
        {
            return;
        }

        var cache = new Dictionary<Type, IShopSellInfo>();
        var sbInfoType = typeof(SBInfo);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var loaded = ex.Types;
                var salvaged = new List<Type>(loaded.Length);
                foreach (var t in loaded)
                {
                    if (t != null)
                    {
                        salvaged.Add(t);
                    }
                }

                types = salvaged.ToArray();
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type == sbInfoType || !sbInfoType.IsAssignableFrom(type))
                {
                    continue;
                }

                SBInfo instance;
                try
                {
                    instance = (SBInfo)Activator.CreateInstance(type);
                }
                catch
                {
                    // A handful of SBInfo subclasses may have non-default
                    // constructors or side effects this reflection scan
                    // can't safely satisfy - skip, don't crash the whole
                    // pricing cache over one category.
                    continue;
                }

                var sellInfo = instance?.SellInfo;
                if (sellInfo?.Types == null)
                {
                    continue;
                }

                foreach (var t in sellInfo.Types)
                {
                    cache.TryAdd(t, sellInfo);
                }
            }
        }

        _sellInfoByType = cache;

        if (VerboseConfig.VendorStock)
        {
            logger.Information("[ShopPatronage] Built appraisal price cache covering {Count} item type(s)", cache.Count);
        }
    }

    // Mirrors PlayerVendorBuyGump.OnResponse's own real purchase sequence
    // (Mobiles/Vendors/PlayerVendorGumps.cs) - PlaceInBackpack moves the
    // item out of the vendor's pack (and out of _sellItems, via the same
    // hook a real player's purchase triggers) into the shopper's, and
    // HoldGold is credited directly rather than drawn from any wallet the
    // shopper doesn't have - this is a simulated visit, not a full
    // player-parity transaction, exactly per the ticket's own "deposit
    // directly into vendor.HoldGold."
    private static void CompletePurchase(ShopperVisit visit, Item item, VendorItem vi)
    {
        var bot = visit.Bot;
        var vendor = visit.Vendor;

        if (!vi.Valid || item.Deleted || !item.IsChildOf(vendor.Backpack))
        {
            return;
        }

        if (!bot.PlaceInBackpack(item))
        {
            return; // shouldn't normally happen - a fresh bot always has room
        }

        vendor.HoldGold += vi.Price;

        if (VerboseConfig.VendorStock)
        {
            logger.Information(
                "[ShopPatronage] {Bot} bought {Item} from {Vendor} for {Price}gp (HoldGold now {HoldGold})",
                bot.Name, item.GetType().Name, vendor.Name, vi.Price, vendor.HoldGold
            );
        }
    }

    // -------------------------------------------------------------------
    // Departure
    // -------------------------------------------------------------------

    // "Recall Exit (Primary) ... Walk-Away Exit (Fallback)." A shopper
    // that arrived by Moongate or Recall departs the same way it came
    // (sound, sparkle, a short beat, gone); one that walked in walks back
    // out toward the spot it walked in from before deleting, so it never
    // just vanishes mid-shop.
    private static void Depart(ShopperVisit visit)
    {
        if (visit.Bot?.Deleted != false)
        {
            return;
        }

        if (visit.ArrivedByWalking)
        {
            DepartByWalking(visit);
        }
        else
        {
            DepartByRecall(visit.Bot);
        }
    }

    private static void DepartByRecall(Mobile bot)
    {
        PlayArrivalEffect(bot, RecallSoundId);
        Timer.DelayCall(TimeSpan.FromSeconds(2), () =>
        {
            if (bot?.Deleted == false)
            {
                bot.Delete();
            }
        });
    }

    private static void DepartByWalking(ShopperVisit visit)
    {
        var bot = visit.Bot;

        // A fresh target roughly 15 tiles out from the porch, not a walk
        // all the way back to wherever this particular shopper happened
        // to spawn (which could be a full 24 tiles for a walk-in arrival)
        // - the departure distance is its own figure in the ticket,
        // independent of the arrival one.
        var exitSpot = FindNearbySpot(visit.DepartureAnchor, bot.Map, WalkAwayMinTiles, WalkAwayMaxTiles);
        if (exitSpot is not { } spot)
        {
            // No open ground found anywhere nearby (a tightly boxed-in
            // plot) - fall back to the recall-style vanish rather than
            // never leaving at all.
            DepartByRecall(bot);
            return;
        }

        var follower = new PathFollower(bot, spot);
        var stepDelay = BotShopBrowsingManager.ComputeStepDelay(bot);
        var maxSteps = Math.Max(1, (int)(ApproachTimeout.TotalMilliseconds / stepDelay.TotalMilliseconds));

        WalkAway(bot, follower, maxSteps, stepDelay);
    }

    private static void WalkAway(Mobile bot, PathFollower follower, int stepsRemaining, TimeSpan stepDelay)
    {
        if (bot?.Deleted != false)
        {
            return;
        }

        bot.Warmode = false;

        if (stepsRemaining <= 0 || follower.Follow(1))
        {
            bot.Delete();
            return;
        }

        Timer.DelayCall(stepDelay, () => WalkAway(bot, follower, stepsRemaining - 1, stepDelay));
    }
}
