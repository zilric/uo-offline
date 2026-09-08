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
// SP-055: reworked from one flat scatter pass into 4 sequential macro
// phases so mega-structures get deliberately spaced out across the whole
// mainland instead of clustering wherever a rare RNG roll happened to
// land first:
//   Phase 1 - Castle  (cap 5,  >=500 tiles from any other placed Castle)
//   Phase 2 - Keep    (cap 10, >=200 tiles from any Castle or Keep)
//   Phase 3 - Tower   (cap 15, >=100 tiles from any Castle, Keep, or Tower)
//   Phase 4 - General residential/commercial fill up to the world house
//             cap, medium-first: try a Midsize footprint (two-story
//             homes, villas, log cabins, large patios) before falling
//             back to a Small classic footprint at the same spot if the
//             medium one fails terrain/slope/collision validation.
// Mega-structure placements accumulate in one shared placedMegas list
// across phases 1-3 and are never cleared between phases, so "any
// Castle, Keep, or Tower" for the Phase 3 check falls out for free from
// just testing every entry already in the list against that phase's own
// exclusion radius — no separate per-tier bookkeeping needed, since by
// the time Phase 3 runs, phases 1 and 2 have already finished adding
// their own entries. The distance check runs before PlacementOffset/
// CheckPlacement's real multi-tile elevation/slope/collision sweep, so a
// too-close candidate is rejected for the cost of a couple of
// subtractions instead of a full placement probe.
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
//   - Size-tier categorization (GetSizeTier): SmallTower is deliberately
//     bucketed into Small despite its name — its footprint and deed are
//     a classic small house's, not a mega Tower's. Only MarketHouseStyle.
//     LargeTower (this codebase's own "Tower" mega style) counts toward
//     the Tower tier. See GetSizeTier's own comment.
//
// Facet note: Felucca only - this server is T2A-era with Trammel disabled
// (Configuration/expansion.json's MapSelectionFlags.Trammel: false).
// =========================================================================

using System;
using System.Collections.Generic;
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

    // SP-055: overall cap across all 4 phases combined - mega structures
    // placed in phases 1-3 count toward this exactly the same as any
    // Midsize/Small filler phase 4 places.
    private const int TargetHouseCount = 1500;

    // SP-055: hard per-style placement caps and spatial-exclusion radii
    // for the 3 mega-structure phases. Exclusion is Chebyshev (chessboard)
    // distance, same style of check Utility.InRange already uses for the
    // dungeon-exclusion filter below.
    private const int CastleCap = 5;
    private const int KeepCap = 10;
    private const int TowerCap = 15;

    private const int CastleExclusionRadius = 500;
    private const int KeepExclusionRadius = 200;
    private const int TowerExclusionRadius = 100;

    // SP-055: per-phase attempt budgets, so a phase that runs out of
    // fitting ground (e.g. a full mainland pass without 5 valid Castle
    // spots left) advances cleanly to the next phase instead of spending
    // the whole run's time budget chasing one increasingly-impossible
    // cap. Phase 4 (General) keeps the flat MaxAttempts ceiling below -
    // it's still the dominant cost of a full run, same as pre-SP-055.
    private const int CastlePhaseMaxAttempts = 4_000;
    private const int KeepPhaseMaxAttempts = 6_000;
    private const int TowerPhaseMaxAttempts = 10_000;

    // SP-050: Phase 4 (General)'s own attempt ceiling - a single fixed
    // budget instead of a per-pass random range, so milestone reporting
    // can express progress as a clean "X0% complete" percentage without
    // needing to know or display a per-pass-varying denominator.
    private const int MaxAttempts = 250000;

    // Every 10% of a phase's own attempt budget triggers a progress
    // broadcast for that phase - see Seed()'s own phaseNextMilestone
    // tracking.
    private const int MilestoneCount = 10;

    // At 50 candidates/35ms tick, the worst-case combined budget across
    // all 4 phases (4,000 + 6,000 + 10,000 + 250,000 = 270,000 attempts)
    // needs at most ceil(270000/50) = 5400 ticks - about 189 seconds
    // worst case. Still a bounded run of cheap per-tick work (50 point
    // checks/tick, nowhere near CLAUDE.md's definition of "heavy"), not
    // an open-ended one - and in practice phases 1-3 finish in a small
    // fraction of their own budget, since their caps (5/10/15) are so low.
    private const int BatchSize = 50;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(35);

    private static bool _running;

    // Same exclusion radius DaggerIsleSeeder's own verified Deceit
    // coordinate uses, applied here to every entry in the real dungeon
    // registry instead of just the one.
    private const int DungeonExclusionRadius = 40;

    // SP-055: Phase 4's medium-first fallback pools. Midsize covers
    // two-story homes, villas, log cabins, and large patios. Small covers
    // the classic SmallOldHouse family (SmallPlasterHouse/SmallStoneHouse/
    // SmallWoodHouse/WoodAndPlasterHouse/StoneAndPlasterHouse/
    // SmallBrickHouse/FieldStoneHouse all share one deed offset - see
    // OrganicMarketSpawner.PlacementOffset's own comment), SmallShop, the
    // two workshops, and SmallTower.
    private static readonly MarketHouseStyle[] MidsizeStyles =
    {
        MarketHouseStyle.TwoStoryWoodPlaster,
        MarketHouseStyle.LargePatio,
        MarketHouseStyle.TwoStoryStoneAndPlaster,
        MarketHouseStyle.TwoStoryVilla,
        MarketHouseStyle.TwoStoryLogCabin,
        MarketHouseStyle.LogCabin,
        MarketHouseStyle.SandStonePatio,
        MarketHouseStyle.SandstoneHouseWithPatio,
        MarketHouseStyle.MarbleHouseWithPatio,
        MarketHouseStyle.ThreeRoomBrickHouse
    };

    private static readonly MarketHouseStyle[] SmallStyles =
    {
        MarketHouseStyle.SmallShop,
        MarketHouseStyle.SmallPlasterHouse,
        MarketHouseStyle.SmallStoneHouse,
        MarketHouseStyle.SmallWoodHouse,
        MarketHouseStyle.WoodAndPlasterHouse,
        MarketHouseStyle.StoneAndPlasterHouse,
        MarketHouseStyle.SmallTower,
        MarketHouseStyle.SmallBrickHouse,
        MarketHouseStyle.FieldStoneHouse,
        MarketHouseStyle.StoneWorkshop,
        MarketHouseStyle.MarbleWorkshop
    };

    // Full archetype catalog - archetype never affects placement/footprint
    // validity, only interior stock/theming after a house already fits,
    // so there's no performance reason to weight this pool unevenly.
    private static readonly MarketArchetype[] AllArchetypes =
        (MarketArchetype[])Enum.GetValues(typeof(MarketArchetype));

    // SP-055: the 4 sequential macro phases (plus a terminal Done
    // sentinel so the tick loop below has a single, obvious stop
    // condition). Order matters - General must run last so its Midsize/
    // Small fill only ever competes for ground the 3 mega phases didn't
    // already claim.
    private enum Phase
    {
        Castle,
        Keep,
        Tower,
        General,
        Done
    }

    // SP-055: tags one entry in the shared mega-structure exclusion list
    // tracked across phases 1-3.
    private enum MegaTier
    {
        Castle,
        Keep,
        Tower
    }

    // SP-055: the 5 reporting buckets for the completion summary.
    // Deliberately a separate type from MegaTier - SmallTower resolves
    // to Small here despite the name, unlike a true mega Tower.
    private enum SizeTier
    {
        Castle,
        Keep,
        Tower,
        Midsize,
        Small
    }

    // Maps any style to its completion-summary bucket. Only reached from
    // Phase 4 (General) in practice, since phases 1-3 already know their
    // own tier without needing to look it up - kept general-purpose
    // anyway so the size-tier rule lives in exactly one place.
    private static SizeTier GetSizeTier(MarketHouseStyle style) => style switch
    {
        MarketHouseStyle.Castle => SizeTier.Castle,
        MarketHouseStyle.Keep => SizeTier.Keep,
        MarketHouseStyle.LargeTower => SizeTier.Tower,
        MarketHouseStyle.TwoStoryWoodPlaster
            or MarketHouseStyle.LargePatio
            or MarketHouseStyle.TwoStoryStoneAndPlaster
            or MarketHouseStyle.TwoStoryVilla
            or MarketHouseStyle.TwoStoryLogCabin
            or MarketHouseStyle.LogCabin
            or MarketHouseStyle.SandStonePatio
            or MarketHouseStyle.SandstoneHouseWithPatio
            or MarketHouseStyle.MarbleHouseWithPatio
            or MarketHouseStyle.ThreeRoomBrickHouse => SizeTier.Midsize,
        // Everything else - the SmallOldHouse family, SmallShop, the two
        // workshops, and SmallTower - is Small.
        _ => SizeTier.Small
    };

    private static int PhaseNumber(Phase p) => p switch
    {
        Phase.Castle => 1,
        Phase.Keep => 2,
        Phase.Tower => 3,
        _ => 4
    };

    private static string PhaseName(Phase p) => p switch
    {
        Phase.Castle => "Castles",
        Phase.Keep => "Keeps",
        Phase.Tower => "Towers",
        Phase.General => "General Residential & Commercial",
        _ => "Complete"
    };

    private static int PhaseMaxAttempts(Phase p) => p switch
    {
        Phase.Castle => CastlePhaseMaxAttempts,
        Phase.Keep => KeepPhaseMaxAttempts,
        Phase.Tower => TowerPhaseMaxAttempts,
        _ => MaxAttempts
    };

    private static int ReportStepFor(int phaseMaxAttempts) => Math.Max(1, phaseMaxAttempts / MilestoneCount);

    public static void Configure()
    {
        CommandSystem.Register("seedworldfrontier", AccessLevel.GameMaster, OnSeedWorldFrontier);
    }

    [Usage("seedworldfrontier")]
    [Description("Phased hierarchical frontier seeding across the entire Felucca mainland: Castles (cap 5, >=500 tile spacing), then Keeps (cap 10, >=200), then Towers (cap 15, >=100), then a medium-first General residential/commercial fill up to the world house cap. Broadcasts a progress milestone every 10% of each phase's own attempt budget.")]
    private static void OnSeedWorldFrontier(CommandEventArgs e) => Seed(e.Mobile);

    // Fire-and-forget - the size-tier completion summary arrives as its
    // own message once every phase has either hit its cap/budget or the
    // overall TargetHouseCount is reached, not synchronously here.
    // SP-055: also broadcasts a progress milestone (to the initiator's
    // own overhead/chat and the server console log) every time a phase's
    // own attempt count crosses a 10% boundary of that phase's budget,
    // plus a transition message every time one phase hands off to the
    // next, so a GM watching a run that can take a few minutes isn't left
    // guessing whether it's still working or which phase it's in.
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

        var placedMegas = new List<(Point2D Loc, MegaTier Tier)>();

        var totalPlaced = 0;
        var vendors = 0;
        var castles = 0;
        var keeps = 0;
        var towers = 0;
        var midsize = 0;
        var small = 0;

        var phase = Phase.Castle;
        var phaseAttempt = 0;
        var phaseMax = PhaseMaxAttempts(phase);
        var phaseReportStep = ReportStepFor(phaseMax);
        var phaseNextMilestone = phaseReportStep;

        from?.SendMessage($"World Frontier: beginning phased seeding pass ({MinX}-{MaxX}, {MinY}-{MaxY})...");
        from?.SendMessage(
            0x35,
            $"World Frontier: Phase {PhaseNumber(phase)}/4 - {PhaseName(phase)} (cap {CastleCap}, >= {CastleExclusionRadius} tiles apart)..."
        );
        logger.Information($"World Frontier Seeding: starting Phase {PhaseNumber(phase)}/4 ({PhaseName(phase)}).");

        // One phase step: places (at most) one structure for whichever
        // phase is currently active, updates that phase's own counters,
        // fires a 10%-of-phase-budget milestone if one was just crossed,
        // and hands off to the next phase once the current one's cap is
        // reached or its attempt budget is spent.
        void StepOnce()
        {
            if (totalPlaced >= TargetHouseCount)
            {
                phase = Phase.Done;
                return;
            }

            switch (phase)
            {
                case Phase.Castle:
                {
                    if (TryPlaceMega(map, authority, MarketHouseStyle.Castle, MegaTier.Castle, CastleExclusionRadius, placedMegas, vendorRollChance, out var wasVendor))
                    {
                        castles++;
                        totalPlaced++;
                        if (wasVendor)
                        {
                            vendors++;
                        }
                    }

                    break;
                }
                case Phase.Keep:
                {
                    if (TryPlaceMega(map, authority, MarketHouseStyle.Keep, MegaTier.Keep, KeepExclusionRadius, placedMegas, vendorRollChance, out var wasVendor))
                    {
                        keeps++;
                        totalPlaced++;
                        if (wasVendor)
                        {
                            vendors++;
                        }
                    }

                    break;
                }
                case Phase.Tower:
                {
                    if (TryPlaceMega(map, authority, MarketHouseStyle.LargeTower, MegaTier.Tower, TowerExclusionRadius, placedMegas, vendorRollChance, out var wasVendor))
                    {
                        towers++;
                        totalPlaced++;
                        if (wasVendor)
                        {
                            vendors++;
                        }
                    }

                    break;
                }
                default:
                {
                    if (TryPlaceGeneral(map, authority, vendorRollChance, out var wasVendor, out var tier))
                    {
                        totalPlaced++;
                        if (wasVendor)
                        {
                            vendors++;
                        }

                        if (tier == SizeTier.Midsize)
                        {
                            midsize++;
                        }
                        else
                        {
                            small++;
                        }
                    }

                    break;
                }
            }

            phaseAttempt++;

            if (phaseNextMilestone < phaseMax && phaseAttempt >= phaseNextMilestone)
            {
                var pct = 100 * phaseNextMilestone / phaseMax;
                var progressMessage =
                    $"World Frontier Seeding: Phase {PhaseNumber(phase)}/4 ({PhaseName(phase)}) {pct}% complete ({phaseAttempt:N0}/{phaseMax:N0} phase attempts, {totalPlaced:N0} total houses placed)...";

                from?.SendMessage(0x35, progressMessage);
                logger.Information(progressMessage);

                phaseNextMilestone += phaseReportStep;
            }

            var capReached = phase switch
            {
                Phase.Castle => castles >= CastleCap,
                Phase.Keep => keeps >= KeepCap,
                Phase.Tower => towers >= TowerCap,
                _ => false
            };
            var budgetSpent = phaseAttempt >= phaseMax;

            if (capReached || budgetSpent)
            {
                AdvancePhase();
            }
        }

        // Retires the current phase (logging how many it placed) and
        // moves on to the next one, resetting the per-phase attempt/
        // milestone tracking for whatever comes next.
        void AdvancePhase()
        {
            var finished = phase;
            var finishedCount = finished switch
            {
                Phase.Castle => castles,
                Phase.Keep => keeps,
                Phase.Tower => towers,
                _ => totalPlaced
            };

            phase = finished switch
            {
                Phase.Castle => Phase.Keep,
                Phase.Keep => Phase.Tower,
                Phase.Tower => Phase.General,
                _ => Phase.Done
            };

            if (phase == Phase.Done)
            {
                return;
            }

            var transitionMessage =
                $"World Frontier: Phase {PhaseNumber(finished)}/4 ({PhaseName(finished)}) complete - {finishedCount} placed. Advancing to Phase {PhaseNumber(phase)}/4 ({PhaseName(phase)}).";

            from?.SendMessage(0x35, transitionMessage);
            logger.Information(transitionMessage);

            phaseAttempt = 0;
            phaseMax = PhaseMaxAttempts(phase);
            phaseReportStep = ReportStepFor(phaseMax);
            phaseNextMilestone = phaseReportStep;
        }

        var totalTicksBudget =
            (CastlePhaseMaxAttempts + KeepPhaseMaxAttempts + TowerPhaseMaxAttempts + MaxAttempts + BatchSize - 1) / BatchSize;

        Timer timer = null;
        timer = Timer.DelayCall(TimeSpan.Zero, TickInterval, totalTicksBudget, () =>
        {
            for (var i = 0; i < BatchSize && phase != Phase.Done; i++)
            {
                StepOnce();
            }

            if (phase == Phase.Done)
            {
                timer?.Stop();
                _running = false;

                var summary =
                    $"World Frontier Seeding complete: {totalPlaced} total houses ({castles} Castle, {keeps} Keep, {towers} Tower, {midsize} Midsize, {small} Small | {vendors} Vendors).";

                from?.SendMessage(0x59, summary);
                logger.Information(summary);
            }
        });
    }

    // Locates one raw, terrain/region-valid mainland candidate point,
    // shared by every phase before it goes on to spend a style-specific
    // offset + CheckPlacement probe on it. Style-independent: dungeon
    // exclusion, terrain, and region/cemetery checks don't depend on
    // what's eventually going to be built there.
    private static bool TryFindBaseCandidate(Map map, MerchantGuildAuthority authority, out Point3D candidate)
    {
        candidate = Point3D.Zero;

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
        var point = new Point3D(x, y, z);

        // Region.AllowHousing already rejects guard zones, roads, and any
        // other "no housing" region.
        var region = Region.Find(point, map);
        if (!region.AllowHousing(authority, point) || IsCemeteryRegion(region))
        {
            return false;
        }

        candidate = point;
        return true;
    }

    // Chebyshev (chessboard) distance - the same style of check
    // Utility.InRange already uses for the dungeon-exclusion filter
    // above, just returning the raw distance instead of a bool so
    // per-phase exclusion radii can compare against it directly.
    private static int ChebyshevDistance(int x1, int y1, int x2, int y2) =>
        Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));

    // Phases 1-3: places one instance of a single fixed mega style,
    // rejecting the candidate early (before the expensive CheckPlacement
    // elevation/slope/collision sweep) if it's too close to any
    // mega-structure already placed by this or an earlier phase.
    private static bool TryPlaceMega(
        Map map,
        MerchantGuildAuthority authority,
        MarketHouseStyle style,
        MegaTier tier,
        int exclusionRadius,
        List<(Point2D Loc, MegaTier Tier)> placedMegas,
        double vendorRollChance,
        out bool wasVendor
    )
    {
        wasVendor = false;

        if (!TryFindBaseCandidate(map, authority, out var candidate))
        {
            return false;
        }

        foreach (var existing in placedMegas)
        {
            if (ChebyshevDistance(candidate.X, candidate.Y, existing.Loc.X, existing.Loc.Y) < exclusionRadius)
            {
                return false;
            }
        }

        var offset = OrganicMarketSpawner.PlacementOffset(style);
        var center = new Point3D(candidate.X - offset.X, candidate.Y - offset.Y, candidate.Z - offset.Z);

        if (OrganicMarketSpawner.CheckPlacement(map, center, style, out var toMove, SideBuffer, FrontBackBuffer) != HousePlacementResult.Valid)
        {
            return false;
        }

        var index = PlaceRolled(map, style, center, toMove, vendorRollChance, out wasVendor);
        if (index < 0)
        {
            return false;
        }

        placedMegas.Add((new Point2D(candidate.X, candidate.Y), tier));
        return true;
    }

    // Phase 4: medium-first fallback. Tries a random Midsize footprint at
    // a fresh candidate point first; if it fails terrain/slope/collision
    // validation, retries a random Small classic footprint at that same
    // point (recomputed with the small style's own PlacementOffset)
    // instead of discarding the whole attempt.
    private static bool TryPlaceGeneral(
        Map map,
        MerchantGuildAuthority authority,
        double vendorRollChance,
        out bool wasVendor,
        out SizeTier tier
    )
    {
        wasVendor = false;
        tier = SizeTier.Small;

        if (!TryFindBaseCandidate(map, authority, out var candidate))
        {
            return false;
        }

        var mediumStyle = MidsizeStyles[Utility.Random(MidsizeStyles.Length)];
        var mediumOffset = OrganicMarketSpawner.PlacementOffset(mediumStyle);
        var mediumCenter = new Point3D(
            candidate.X - mediumOffset.X, candidate.Y - mediumOffset.Y, candidate.Z - mediumOffset.Z
        );

        if (OrganicMarketSpawner.CheckPlacement(map, mediumCenter, mediumStyle, out var mediumToMove, SideBuffer, FrontBackBuffer)
            == HousePlacementResult.Valid)
        {
            var mediumIndex = PlaceRolled(map, mediumStyle, mediumCenter, mediumToMove, vendorRollChance, out wasVendor);
            if (mediumIndex >= 0)
            {
                tier = GetSizeTier(mediumStyle);
                return true;
            }
        }

        var smallStyle = SmallStyles[Utility.Random(SmallStyles.Length)];
        var smallOffset = OrganicMarketSpawner.PlacementOffset(smallStyle);
        var smallCenter = new Point3D(
            candidate.X - smallOffset.X, candidate.Y - smallOffset.Y, candidate.Z - smallOffset.Z
        );

        if (OrganicMarketSpawner.CheckPlacement(map, smallCenter, smallStyle, out var smallToMove, SideBuffer, FrontBackBuffer)
            != HousePlacementResult.Valid)
        {
            return false;
        }

        var smallIndex = PlaceRolled(map, smallStyle, smallCenter, smallToMove, vendorRollChance, out wasVendor);
        if (smallIndex < 0)
        {
            return false;
        }

        tier = GetSizeTier(smallStyle);
        return true;
    }

    // Shared vendor/ambient-filler roll, used by every phase once a
    // candidate has already cleared CheckPlacement for some style.
    private static int PlaceRolled(
        Map map, MarketHouseStyle style, Point3D center, List<IEntity> toMove, double vendorRollChance, out bool wasVendor
    )
    {
        wasVendor = false;

        if (Utility.RandomDouble() < vendorRollChance)
        {
            var archetype = AllArchetypes[Utility.Random(AllArchetypes.Length)];
            var index = OrganicMarketSpawner.PlaceTestHouse(map, center, style, archetype, toMove);
            wasVendor = index >= 0;
            return index;
        }

        // Automatically eligible for AmbientHouseManager's ~10% homeowner
        // rotation - that system scans MerchantGuildAuthority for
        // AmbientResidenceArchetype houses generically, regardless of
        // which seeder placed them. Nothing further is needed here.
        return OrganicMarketSpawner.PlaceFillerHouse(map, center, style, toMove);
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
