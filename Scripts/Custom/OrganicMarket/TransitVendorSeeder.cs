// =========================================================================
// TransitVendorSeeder.cs — SP-050: a fast-batched compact-shop seeder for
// the 8 classic public moongates and the road network connecting them,
// modeled directly on WorldFrontierSeeder.cs's own reuse-first design
// (same BatchSize/TickInterval batching, same OrganicMarketSpawner.
// CheckPlacement/PlaceTestHouse/PlaceFillerHouse pipeline, same
// PassesFastTerrainFilter/dungeon-exclusion/AllowHousing/cemetery guards) -
// only the candidate-selection strategy and house-style pool differ.
//
// Moongate coordinates are the real Felucca PMList.Felucca table (Items/
// Misc/PublicMoongate.cs) - X/Y only, since Z is discarded immediately in
// favor of map.GetAverageZ(x, y) for the actual candidate anyway (Magincia's
// own real Z is itself computed at runtime for the same reason, so there
// was never a literal Z worth carrying over).
//
// Road corridors: this codebase has no existing road-tile classification
// anywhere (checked CustomBots/Nav, every Behaviors/*.cs file, and all of
// Scripts/Custom/ - WaypointGraph is a hand-authored named-node graph, not
// tile-ID based). IsRoadTile below is net-new, built from the ticket's own
// three classic paved/dirt land-tile-ID ranges. Rather than pre-scanning
// the full 5120x4096 mainland for road tiles (a second expensive batched
// pass on top of the placement search itself), this uses plain rejection
// sampling: roll a random mainland point, keep it only if it's already
// sitting on a road tile, then jitter 8-20 tiles off of it for the actual
// house candidate. WorldFrontierSeeder's own full-mainland pass already
// tolerates a well-under-1% per-attempt success rate over a 250,000-attempt
// budget (see its own MaxAttempts comment) - the same tolerance covers a
// low per-attempt road-tile hit rate here without needing a second
// architecture.
//
// Standard placement clearance (HousePlacement.Check, via CheckPlacement)
// already rejects any footprint overlapping PublicMoongate's own dynamic
// Item, exactly like it rejects overlap with any other placed Item/multi -
// no moongate-specific collision code is added here.
//
// Style pool: "Small Tower and smaller" per the ticket - the four
// SmallOldHouse trim variants it names (one shared C# class; MultiId(style)
// picks the art - see OrganicMarketSpawner.MultiId's own note on why
// SmallPlasterHouse is the "Thatched Roof" one), SmallShop, and SmallTower.
// Sandstone Patio, every TwoStory* variant, Keep, and Castle are never
// rolled here. The ticket's "SmallShop (Blacksmith, Woodworker)" doesn't
// map to two distinct house classes in this codebase - SmallShop is one
// class, themed entirely by MarketArchetype at vendor-conversion time, and
// there's no "Woodworker" archetype (verified against MerchantGuildAuthority
// .cs's MarketArchetype enum). SmallShopArchetypes below narrows a
// SmallShop's vendor-roll archetype to BlacksmithArmory (the literal match)
// and TinkerCarpenter (its remit already covers carpentry/hardware - the
// closest real analog to "Woodworker") instead of the full 7-archetype
// pool every other allowed style still uses.
// =========================================================================

using System;
using Server.Commands;
using Server.CustomBots;
using Server.Logging;
using Server.Multis;
using Server.Regions;

namespace Server.Engines.OrganicMarket;

public static class TransitVendorSeeder
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(TransitVendorSeeder));

    // Full Britannia landmass, same bounds WorldFrontierSeeder uses - only
    // the road phase's initial random point needs the full range; the
    // moongate phase stays within MoongateRadius of each named anchor.
    private const int MinX = 0;
    private const int MaxX = 5120;
    private const int MinY = 0;
    private const int MaxY = 4096;

    // Same tight buffers WorldFrontierSeeder already established for
    // regional (non-mainland-default) seeding passes, and literally the
    // exact values this ticket asks for.
    private const int SideBuffer = 2;
    private const int FrontBackBuffer = 4;

    // Same per-tick cadence WorldFrontierSeeder already validated in
    // production - see that file's own BatchSize comment for the math on
    // why 50 candidates/35ms tick stays well clear of "heavy" main-thread
    // work.
    private const int BatchSize = 50;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(35);

    private const int DungeonExclusionRadius = 40;

    private static bool _running;

    // Real Felucca coordinates (Items/Misc/PublicMoongate.cs, PMList.
    // Felucca) for the 8 classic named-city gates - Buccaneer's Den is in
    // that same table but isn't one of the ticket's 8 named cities, so it's
    // excluded here.
    private static readonly (string Name, int X, int Y)[] MoongateAnchors =
    {
        ("Britain", 1336, 1997),
        ("Moonglow", 4467, 1283),
        ("Yew", 771, 752),
        ("Jhelom", 1499, 3771),
        ("Skara Brae", 643, 2067),
        ("Trinsic", 1828, 2948),
        ("Minoc", 2701, 692),
        ("Magincia", 3563, 2139)
    };

    // 101x101-tile box per gate (50 tiles each direction). Most classic
    // city centers are a GuardedRegion (AllowHousing => false, by design -
    // see Region.cs/GuardedRegion.cs), so a meaningful share of every
    // gate's own attempt budget is expected to fail near its center and
    // only land toward the outer edge, just outside the guard line - the
    // same "shops springing up outside the gate" read the ticket is going
    // for, not a bug to work around.
    private const int MoongateRadius = 50;
    private const int MoongateAttemptsPerGate = 800;
    private const double MoongateVendorChance = 0.70;

    // Rejection-sampled road tiles get jittered 8-20 tiles off the found
    // tile for the actual house candidate - approximates "flanking a
    // 20-tile band" without needing real road-direction/centerline vector
    // math this codebase has no existing support for computing from a
    // land-tile ID alone.
    private const int RoadOffsetMin = 8;
    private const int RoadOffsetMax = 20;
    private const double RoadVendorChance = 0.25;
    private const int RoadMaxAttempts = 60000;
    private const int RoadTargetHouseCount = 300;

    // Classic paved/dirt road land-tile ID ranges, per the ticket. No
    // existing road-tile classification exists anywhere in this codebase
    // to cross-check against (see file header) - these three ranges are
    // exactly what the ticket specified, no additional ranges added.
    private static readonly (int Min, int Max)[] RoadTileRanges =
    {
        (0x071, 0x078),
        (0x0E5, 0x0EA),
        (0x01F4, 0x01FB)
    };

    // "Small Tower and smaller" - see file header for the full reasoning.
    private static readonly MarketHouseStyle[] AllowedStyles =
    {
        MarketHouseStyle.WoodAndPlasterHouse,
        MarketHouseStyle.StoneAndPlasterHouse,
        MarketHouseStyle.FieldStoneHouse,
        MarketHouseStyle.SmallPlasterHouse,
        MarketHouseStyle.SmallShop,
        MarketHouseStyle.SmallTower
    };

    // Archetype never affects placement/footprint validity, only interior
    // stock/theming after a house already fits - same reasoning
    // WorldFrontierSeeder's own AllArchetypes comment gives.
    private static readonly MarketArchetype[] AllArchetypes =
        (MarketArchetype[])Enum.GetValues(typeof(MarketArchetype));

    private static readonly MarketArchetype[] SmallShopArchetypes =
    {
        MarketArchetype.BlacksmithArmory,
        MarketArchetype.TinkerCarpenter
    };

    private static MarketArchetype RollArchetype(MarketHouseStyle style) =>
        style == MarketHouseStyle.SmallShop
            ? SmallShopArchetypes[Utility.Random(SmallShopArchetypes.Length)]
            : AllArchetypes[Utility.Random(AllArchetypes.Length)];

    public static void Configure()
    {
        CommandSystem.Register("seedtransitvendors", AccessLevel.GameMaster, OnSeedTransitVendors);
    }

    [Usage("seedtransitvendors")]
    [Description("Fast-batched compact-shop seeding around the 8 classic moongates (70% vendor rate) and along road corridors (25% vendor rate). Small Tower and smaller footprints only.")]
    private static void OnSeedTransitVendors(CommandEventArgs e) => Seed(e.Mobile);

    // Fire-and-forget, same as WorldFrontierSeeder.Seed - runs the moongate
    // phase to completion, then chains directly into the road phase, then
    // broadcasts the ticket's exact required completion message.
    public static void Seed(Mobile from)
    {
        var authority = MerchantGuildAuthority.Instance;
        if (authority == null)
        {
            from?.SendMessage("Merchant Guild Authority is not initialized.");
            return;
        }

        if (_running)
        {
            from?.SendMessage("Transit Seeder: a seeding pass is already in progress.");
            return;
        }

        _running = true;
        var map = Map.Felucca;
        var placed = 0;
        var vendors = 0;
        var ambient = 0;

        void Finish()
        {
            _running = false;
            var message = $"Transit Seeder Complete: Placed {placed} houses ({vendors} Moongate/Road Vendors, {ambient} Ambient Homes)";
            from?.SendMessage(0x59, message);
            logger.Information(message);
        }

        void RunRoadPhase()
        {
            from?.SendMessage("Transit Seeder: scanning road corridors for compact shop sites...");

            var attempt = 0;
            var roadPlaced = 0;
            var ticksNeeded = (RoadMaxAttempts + BatchSize - 1) / BatchSize;

            Timer roadTimer = null;
            roadTimer = Timer.DelayCall(TimeSpan.Zero, TickInterval, ticksNeeded, () =>
            {
                for (var i = 0; i < BatchSize && attempt < RoadMaxAttempts && roadPlaced < RoadTargetHouseCount; i++)
                {
                    attempt++;

                    if (TryPlaceRoadCandidate(map, authority, out var wasVendor))
                    {
                        placed++;
                        roadPlaced++;
                        if (wasVendor)
                        {
                            vendors++;
                        }
                        else
                        {
                            ambient++;
                        }
                    }
                }

                if (roadPlaced >= RoadTargetHouseCount || attempt >= RoadMaxAttempts)
                {
                    roadTimer?.Stop();
                    Finish();
                }
            });
        }

        void RunMoongatePhase()
        {
            var gateIndex = 0;
            var gateAttempt = 0;
            var ticksPerGate = (MoongateAttemptsPerGate + BatchSize - 1) / BatchSize;
            var ticksNeeded = ticksPerGate * MoongateAnchors.Length;

            Timer gateTimer = null;
            gateTimer = Timer.DelayCall(TimeSpan.Zero, TickInterval, ticksNeeded, () =>
            {
                var gate = MoongateAnchors[gateIndex];

                for (var i = 0; i < BatchSize && gateAttempt < MoongateAttemptsPerGate; i++)
                {
                    gateAttempt++;

                    if (TryPlaceMoongateCandidate(map, authority, gate.X, gate.Y, out var wasVendor))
                    {
                        placed++;
                        if (wasVendor)
                        {
                            vendors++;
                        }
                        else
                        {
                            ambient++;
                        }
                    }
                }

                if (gateAttempt >= MoongateAttemptsPerGate)
                {
                    gateIndex++;
                    gateAttempt = 0;

                    // Transition the instant the final gate's own budget is
                    // spent, in the same tick - not deferred to a check at
                    // the top of a "one more" tick, since ticksNeeded above
                    // is sized to exactly cover every gate's budget with no
                    // spare tick left for that deferred check to ever run
                    // (an earlier version of this method had exactly that
                    // bug: Timer.DelayCall's own count cap silently stopped
                    // the timer one tick before the check could fire,
                    // leaving the road phase - and the whole completion
                    // broadcast - never invoked. Caught via a temporary
                    // runtime self-test, not by the build.).
                    if (gateIndex >= MoongateAnchors.Length)
                    {
                        gateTimer?.Stop();
                        RunRoadPhase();
                    }
                }
            });
        }

        from?.SendMessage($"Transit Seeder: seeding compact shops around {MoongateAnchors.Length} moongates...");
        RunMoongatePhase();
    }

    private static bool TryPlaceMoongateCandidate(Map map, MerchantGuildAuthority authority, int gateX, int gateY, out bool wasVendor)
    {
        var x = gateX + Utility.RandomMinMax(-MoongateRadius, MoongateRadius);
        var y = gateY + Utility.RandomMinMax(-MoongateRadius, MoongateRadius);

        return TryPlaceAt(map, authority, x, y, MoongateVendorChance, out wasVendor);
    }

    private static bool TryPlaceRoadCandidate(Map map, MerchantGuildAuthority authority, out bool wasVendor)
    {
        wasVendor = false;

        var roadX = Utility.RandomMinMax(MinX, MaxX);
        var roadY = Utility.RandomMinMax(MinY, MaxY);

        if (roadX < 0 || roadY < 0 || roadX >= map.Width || roadY >= map.Height || !IsRoadTile(map, roadX, roadY))
        {
            return false;
        }

        var offsetX = Utility.RandomMinMax(RoadOffsetMin, RoadOffsetMax) * (Utility.RandomBool() ? 1 : -1);
        var offsetY = Utility.RandomMinMax(RoadOffsetMin, RoadOffsetMax) * (Utility.RandomBool() ? 1 : -1);

        return TryPlaceAt(map, authority, roadX + offsetX, roadY + offsetY, RoadVendorChance, out wasVendor);
    }

    // Shared tail end of both candidate sources - same pipeline
    // WorldFrontierSeeder.TryPlaceOne uses (terrain filter, dungeon
    // exclusion, AllowHousing/cemetery, CheckPlacement at the 2/4 buffers,
    // vendor-vs-ambient roll), just with a caller-supplied vendor chance
    // and the restricted AllowedStyles pool instead of the full catalog.
    private static bool TryPlaceAt(Map map, MerchantGuildAuthority authority, int x, int y, double vendorChance, out bool wasVendor)
    {
        wasVendor = false;

        if (x < 0 || y < 0 || x >= map.Width || y >= map.Height || !PassesFastTerrainFilter(map, x, y))
        {
            return false;
        }

        foreach (var dungeonPoint in BotPanelActions.DungeonCoords.Values)
        {
            if (Utility.InRange(x, y, dungeonPoint.X, dungeonPoint.Y, DungeonExclusionRadius))
            {
                return false;
            }
        }

        var z = map.GetAverageZ(x, y);
        var candidate = new Point3D(x, y, z);

        var region = Region.Find(candidate, map);
        if (!region.AllowHousing(authority, candidate) || IsCemeteryRegion(region))
        {
            return false;
        }

        var style = AllowedStyles[Utility.Random(AllowedStyles.Length)];
        var offset = OrganicMarketSpawner.PlacementOffset(style);
        var center = new Point3D(candidate.X - offset.X, candidate.Y - offset.Y, candidate.Z - offset.Z);

        var result = OrganicMarketSpawner.CheckPlacement(map, center, style, out var toMove, SideBuffer, FrontBackBuffer);
        if (result != HousePlacementResult.Valid)
        {
            return false;
        }

        int index;
        if (Utility.RandomDouble() < vendorChance)
        {
            var archetype = RollArchetype(style);
            index = OrganicMarketSpawner.PlaceTestHouse(map, center, style, archetype, toMove);
            wasVendor = index >= 0;
        }
        else
        {
            index = OrganicMarketSpawner.PlaceFillerHouse(map, center, style, toMove);
        }

        return index >= 0;
    }

    private static bool IsRoadTile(Map map, int x, int y)
    {
        var landTile = map.Tiles.GetLandTile(x, y);
        var id = landTile.ID & TileData.MaxLandValue;

        foreach (var (min, max) in RoadTileRanges)
        {
            if (id >= min && id <= max)
            {
                return true;
            }
        }

        return false;
    }

    // Identical to WorldFrontierSeeder's own copy - see that file's header
    // for why each seeder keeps its own rather than sharing one.
    private static bool PassesFastTerrainFilter(Map map, int x, int y)
    {
        var landTile = map.Tiles.GetLandTile(x, y);
        var landFlags = TileData.LandTable[landTile.ID & TileData.MaxLandValue].Flags;
        return (landFlags & (TileFlag.Impassable | TileFlag.Wet)) == 0;
    }

    private static bool IsCemeteryRegion(Region region)
    {
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
}
