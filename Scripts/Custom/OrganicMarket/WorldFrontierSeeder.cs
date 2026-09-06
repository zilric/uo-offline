// =========================================================================
// WorldFrontierSeeder.cs — SP-047: a fast-batched bounding-box scatter
// seeder for the entire Felucca mainland (originally adapted from the
// now-removed DaggerIsleSeeder.cs's own island-scale version of the same
// architecture).
//
// SP-050: this is now the ONLY world/region house seeder in
// Scripts/Custom/OrganicMarket/ — WorldHouseSeeder.cs (the original
// anchor/ring-search engine behind "Seed World Crossroads" and "Seed
// World Inhabitation") has been deleted outright, its admin gump buttons
// removed, and PassesFastTerrainFilter/IsCemeteryRegion below (originally
// duplicated from it because that file was off-limits to modify) are now
// the only surviving copies of that logic anywhere in this codebase.
//
// Architectural notes:
//   - Terrain filter: no single "valid" land ID range exists for the
//     whole mainland — grass, forest, desert, and dozens of other
//     passable land tiles are all legitimate ground here (unlike Dagger
//     Isle's own snow/ice whitelist, which doubled as its water/shoreline
//     exclusion for free). PassesFastTerrainFilter rejects
//     TileFlag.Impassable | TileFlag.Wet land tiles generally instead.
//   - Dungeon exclusion: reuses Server.CustomBots.BotPanelActions.
//     DungeonCoords (an independently-verified dictionary covering all
//     nine major dungeon entrances) as a read-only reference, at the same
//     40-tile exclusion radius established for Dagger Isle's own single
//     Deceit coordinate.
//   - Cemetery guard: IsCemeteryRegion (a named-region substring check)
//     omits the old PoisonedCemeteryRegion type check — that region is
//     Ilshenar-specific, and this seeder only ever targets Map.Felucca, so
//     a candidate could never actually be inside one.
//     HasFootprintConflict's own gravestone-static-ID scan (reused via
//     CheckPlacement) separately covers real graveyards with no bounding
//     Region at all, which is this server's actual Moonglow-cemetery
//     case.
//   - Ambient homeowner occupancy: no extra code is needed to make placed
//     filler houses eligible for AmbientHouseManager's ~10% rotation -
//     that system already scans MerchantGuildAuthority for every house
//     registered under OrganicMarketSpawner.AmbientResidenceArchetype,
//     regardless of which seeder placed it. Calling the same
//     PlaceFillerHouse pipeline is sufficient.
//
// Facet note: Felucca only - this server is T2A-era with Trammel disabled
// (Configuration/expansion.json's MapSelectionFlags.Trammel: false).
// =========================================================================

using System;
using Server.Commands;
using Server.CustomBots;
using Server.Logging;
using Server.Multis;
using Server.Regions;

namespace Server.Engines.OrganicMarket;

public static class WorldFrontierSeeder
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(WorldFrontierSeeder));

    // Full Britannia landmass per the ticket.
    private const int MinX = 0;
    private const int MaxX = 5120;
    private const int MinY = 0;
    private const int MaxY = 4096;

    // Passed explicitly per call to CheckPlacement, never touching the
    // shared HousePlacementConfig constants (Scripts/Custom/Housing/
    // HousePlacementConfig.cs) other placement paths still default to.
    private const int SideBuffer = 2;
    private const int FrontBackBuffer = 4;

    // SP-048: lowered from a flat 13.5% - rolled fresh per Seed() call
    // (not per-candidate) into a random 5-8% for that whole pass, so
    // vendor density varies a little run to run instead of every pass
    // converging on the exact same ratio. See Seed()'s own local
    // vendorRollChance.
    private const int VendorRollChanceMinPerMille = 50; // 5.0%
    private const int VendorRollChanceMaxPerMille = 80; // 8.0%

    // SP-048: raised from 400/70,000 for a much higher-yield pass.
    private const int TargetHouseCount = 1500;

    // SP-050: back to a single fixed ceiling instead of a per-pass random
    // range (225,000-275,000) - a predictable, reproducible attempt budget
    // that milestone reporting can express as a clean "X0% complete"
    // percentage without needing to know or display a per-pass-varying
    // denominator.
    private const int MaxAttempts = 250000;

    // SP-050: every 10% of MaxAttempts (25,000 attempts at the current
    // ceiling) triggers a progress broadcast - see Seed()'s own
    // nextMilestone tracking.
    private const int MilestoneCount = 10;
    private const int ReportStep = MaxAttempts / MilestoneCount;

    // At 50 candidates/35ms tick, 250,000 attempts needs at most
    // ceil(250000/50) = 5000 ticks - 175 seconds worst case. Still a
    // bounded run of cheap per-tick work (50 point checks/tick, nowhere
    // near CLAUDE.md's definition of "heavy"), not an open-ended one.
    private const int BatchSize = 50;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(35);

    private static bool _running;

    // Same exclusion radius DaggerIsleSeeder's own verified Deceit
    // coordinate uses, applied here to every entry in the real dungeon
    // registry instead of just the one.
    private const int DungeonExclusionRadius = 40;

    // Full catalog, grand structures kept rare rather than banned — same
    // reasoning as DaggerIsleSeeder's own RollStyle: this seeder checks
    // one random point at a time, not a per-node ring search, so a 31x31
    // Castle footprint has a low chance of fitting at any given roll
    // regardless of how many mainland attempts are spent on it. Weighting
    // it evenly with a SmallShop would burn a full 1/8 of every attempt on
    // a style that usually can't land, undercutting this same ticket's
    // own speed/yield goals - GrandStructureChance mirrors
    // WorldHouseSeeder's own already-proven 4% rate for exactly this.
    private static readonly MarketHouseStyle[] AllStyles =
        (MarketHouseStyle[])Enum.GetValues(typeof(MarketHouseStyle));

    private static readonly MarketHouseStyle[] CommonStyles = Array.FindAll(
        AllStyles,
        s => s is not (MarketHouseStyle.LargeTower or MarketHouseStyle.Keep or MarketHouseStyle.Castle)
    );

    private const double GrandStructureChance = 0.04;

    private static MarketHouseStyle RollStyle()
    {
        if (Utility.RandomDouble() < GrandStructureChance)
        {
            return Utility.Random(3) switch
            {
                0 => MarketHouseStyle.LargeTower,
                1 => MarketHouseStyle.Keep,
                _ => MarketHouseStyle.Castle
            };
        }

        return CommonStyles[Utility.Random(CommonStyles.Length)];
    }

    // Full archetype catalog - archetype never affects placement/footprint
    // validity, only interior stock/theming after a house already fits,
    // so there's no performance reason to weight this pool unevenly.
    private static readonly MarketArchetype[] AllArchetypes =
        (MarketArchetype[])Enum.GetValues(typeof(MarketArchetype));

    public static void Configure()
    {
        CommandSystem.Register("seedworldfrontier", AccessLevel.GameMaster, OnSeedWorldFrontier);
    }

    [Usage("seedworldfrontier")]
    [Description("Fast-batched frontier-density scatter seeding across the entire Felucca mainland - any house style, any vendor archetype, at tight 2/4-tile buffers. Broadcasts a progress milestone every 10% of the attempt budget.")]
    private static void OnSeedWorldFrontier(CommandEventArgs e) => Seed(e.Mobile);

    // Fire-and-forget - the "Placed X houses..." summary arrives as its
    // own message once the attempt budget (or the house target) is
    // spent, not synchronously here. SP-050: also broadcasts a progress
    // milestone (to the initiator's own overhead/chat and the server
    // console log) every time attempts cross a 10% boundary of
    // MaxAttempts, so a GM watching a run that can take up to ~3 minutes
    // isn't left guessing whether it's still working.
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
            from?.SendMessage("World Frontier: a seeding pass is already in progress.");
            return;
        }

        var map = Map.Felucca;

        // Rolled once for the whole pass, not per-candidate - see the
        // constant's own comment for why.
        var vendorRollChance = Utility.RandomMinMax(VendorRollChanceMinPerMille, VendorRollChanceMaxPerMille) / 1000.0;

        _running = true;
        var placed = 0;
        var vendors = 0;
        var ambient = 0;
        var attempt = 0;

        // The next attempt count that should trigger a milestone
        // broadcast - advances by ReportStep each time it fires, and
        // deliberately never reaches MaxAttempts itself (that 10% would
        // just be "100%", already covered by the completion message
        // below rather than a redundant near-duplicate milestone).
        var nextMilestone = ReportStep;

        from?.SendMessage($"World Frontier: scanning the mainland ({MinX}-{MaxX}, {MinY}-{MaxY})...");

        var ticksNeeded = (MaxAttempts + BatchSize - 1) / BatchSize;

        Timer timer = null;
        timer = Timer.DelayCall(TimeSpan.Zero, TickInterval, ticksNeeded, () =>
        {
            for (var i = 0; i < BatchSize && attempt < MaxAttempts && placed < TargetHouseCount; i++)
            {
                attempt++;

                if (TryPlaceOne(map, authority, vendorRollChance, out var wasVendor))
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

                if (nextMilestone < MaxAttempts && attempt >= nextMilestone)
                {
                    var pct = 100 * nextMilestone / MaxAttempts;
                    var progressMessage =
                        $"World Frontier Seeding: {pct}% complete ({attempt:N0}/{MaxAttempts:N0} attempts, {placed} houses placed)...";

                    from?.SendMessage(0x35, progressMessage);
                    logger.Information(progressMessage);

                    nextMilestone += ReportStep;
                }
            }

            if (placed >= TargetHouseCount || attempt >= MaxAttempts)
            {
                timer?.Stop();
                _running = false;
                from?.SendMessage(
                    0x59,
                    $"World Frontier: Placed {placed} houses ({vendors} vendors, {ambient} ambient dwellings)."
                );
            }
        });
    }

    private static bool TryPlaceOne(Map map, MerchantGuildAuthority authority, double vendorRollChance, out bool wasVendor)
    {
        wasVendor = false;

        var x = Utility.RandomMinMax(MinX, MaxX);
        var y = Utility.RandomMinMax(MinY, MaxY);

        if (x < 0 || y < 0 || x >= map.Width || y >= map.Height)
        {
            return false;
        }

        // General "is this real, dry, walkable ground" filter - see file
        // header for why this replaces DaggerIsleSeeder's snow-ID
        // whitelist for a mainland-wide pass.
        if (!PassesFastTerrainFilter(map, x, y))
        {
            return false;
        }

        // Keep every major dungeon's entrance clearing (and its immediate
        // approach) free of houses - see file header for why this reuses
        // BotPanelActions.DungeonCoords instead of a hand-picked list.
        foreach (var dungeonPoint in BotPanelActions.DungeonCoords.Values)
        {
            if (Utility.InRange(x, y, dungeonPoint.X, dungeonPoint.Y, DungeonExclusionRadius))
            {
                return false;
            }
        }

        var z = map.GetAverageZ(x, y);
        var candidate = new Point3D(x, y, z);

        // Region.AllowHousing already rejects guard zones, roads, and any
        // other "no housing" region.
        var region = Region.Find(candidate, map);
        if (!region.AllowHousing(authority, candidate) || IsCemeteryRegion(region))
        {
            return false;
        }

        var style = RollStyle();
        var offset = OrganicMarketSpawner.PlacementOffset(style);
        var center = new Point3D(candidate.X - offset.X, candidate.Y - offset.Y, candidate.Z - offset.Z);

        // HousePlacement.Check (via CheckPlacement) validates door swing
        // clearance, walkable surface, and overlap against every real
        // structure/multi already on the map (including anything
        // DaggerIsleSeeder placed, since Dagger Isle's own coordinates
        // fall inside this seeder's full-mainland bounding box too) -
        // the same authoritative check every placement path in this
        // codebase relies on. Only the footprint-conflict margin is
        // overridden, to this pass's own tighter 2/4 buffers instead of
        // the mainland's default 3/5.
        var result = OrganicMarketSpawner.CheckPlacement(map, center, style, out var toMove, SideBuffer, FrontBackBuffer);
        if (result != HousePlacementResult.Valid)
        {
            return false;
        }

        int index;
        if (Utility.RandomDouble() < vendorRollChance)
        {
            var archetype = AllArchetypes[Utility.Random(AllArchetypes.Length)];
            index = OrganicMarketSpawner.PlaceTestHouse(map, center, style, archetype, toMove);
            wasVendor = index >= 0;
        }
        else
        {
            // Automatically eligible for AmbientHouseManager's ~10%
            // homeowner rotation - that system scans MerchantGuildAuthority
            // for AmbientResidenceArchetype houses generically, regardless
            // of which seeder placed them. Nothing further is needed here.
            index = OrganicMarketSpawner.PlaceFillerHouse(map, center, style, toMove);
        }

        return index >= 0;
    }

    // Rejects open water and other impassable land outright, before
    // paying for the far more expensive Region.Find/CheckPlacement calls
    // below. Originally duplicated from the now-deleted WorldHouseSeeder's
    // own PassesFastTerrainFilter (see file header) - this is now the
    // only copy of this logic.
    private static bool PassesFastTerrainFilter(Map map, int x, int y)
    {
        var landTile = map.Tiles.GetLandTile(x, y);
        var landFlags = TileData.LandTable[landTile.ID & TileData.MaxLandValue].Flags;
        return (landFlags & (TileFlag.Impassable | TileFlag.Wet)) == 0;
    }

    // Originally duplicated from the now-deleted WorldHouseSeeder's own
    // IsCemeteryRegion (see file header), minus its PoisonedCemeteryRegion
    // type check - that region is Ilshenar-specific and this seeder only
    // ever targets Map.Felucca, so it could never actually match.
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
