// =========================================================================
// SeedNodeRegistry.cs — SP-023's predefined trade-corridor anchor points.
//
// This server runs T2A-era, Felucca-only (see Configuration/expansion.json
// - Trammel was a Renaissance-expansion facet and doesn't exist here), so
// every node targets Map.Felucca.
//
// Coordinates below are approximate town/corridor reference points, not
// hand-surveyed exact road tiles - that precision isn't the job of a
// fixed anchor. WorldHouseSeeder's whole point is a radial search that
// walks outward from each anchor until it finds ground HousePlacement.Check
// actually accepts, so an anchor only needs to land in the right
// neighborhood (open land near the stated corridor, not literally on the
// paved tile) for the search to do its job.
// =========================================================================

namespace Server.Engines.OrganicMarket;

public sealed record SeedNode(
    string Name,
    Point3D Anchor,
    Map Map,
    int Radius,
    MarketHouseStyle Style,
    MarketArchetype Archetype
);

public static class SeedNodeRegistry
{
    // SP-027: expanded from the original 4 to 20 well-distributed trade
    // crossroads. The first four keep their exact original anchors and
    // radii - each was individually verified against this server's real
    // map data across SP-023/024/025/026's testing (see those files'
    // history), including which of the larger house styles a given spot
    // can and can't actually fit (Vesper-Minoc genuinely supports
    // LargePatio; Moonglow's island only ever fit SmallShop) - so style
    // assignment below respects what's already proven rather than
    // strictly round-robining archetype/style with no regard for terrain.
    // The 16 new nodes get a plain round-robin instead, since nothing is
    // proven about them yet; a search failure at any one of them is a
    // normal, silent skip (WorldHouseSeeder.SeedOne), not an error.
    public static readonly SeedNode[] Nodes =
    {
        // Northwest out of Britain, on the way toward Yew across the
        // forest - open country rather than the paved city blocks
        // immediately around Britain's own bank (1433,1689).
        new(
            "Britain-Yew Road Corridor",
            new Point3D(986, 1353, 0),
            Map.Felucca,
            45,
            MarketHouseStyle.SmallShop,
            MarketArchetype.Blacksmith
        ),

        // North of Britain, toward the Shrine of Compassion.
        new(
            "Britain North Farmland Crossroads",
            new Point3D(1297, 1274, 0),
            Map.Felucca,
            50,
            MarketHouseStyle.TwoStoryWoodPlaster,
            MarketArchetype.MageAlchemist
        ),

        // East out of Britain on the bridge road toward Cove and, beyond
        // it, Minoc.
        new(
            "Britain East Bridge Highway",
            new Point3D(1725, 1478, 0),
            Map.Felucca,
            50,
            MarketHouseStyle.LargePatio,
            MarketArchetype.CurioRares
        ),

        // South of Britain on the coast road down toward Trinsic.
        new(
            "Britain South Coast Road",
            new Point3D(1617, 2076, 0),
            Map.Felucca,
            50,
            MarketHouseStyle.SandStonePatio,
            MarketArchetype.TailorFletcher
        ),

        // Paws, the small farming village between Britain and Yew.
        new(
            "Paws Village Crossroads",
            new Point3D(927, 1053, 0),
            Map.Felucca,
            50,
            MarketHouseStyle.StoneAndPlasterHouse,
            MarketArchetype.Blacksmith
        ),

        // The court road running into Yew proper.
        new(
            "Yew Court Road Crossing",
            new Point3D(800, 1010, 0),
            Map.Felucca,
            50,
            MarketHouseStyle.LogCabin,
            MarketArchetype.MageAlchemist
        ),

        // Forest junction near Empath Abbey, west of Yew.
        new(
            "Yew Empath Abbey Forest Junction",
            new Point3D(712, 1088, 0),
            Map.Felucca,
            50,
            MarketHouseStyle.SmallShop,
            MarketArchetype.CurioRares
        ),

        // Mainland side of the Skara Brae ferry crossing.
        //
        // SP-030: was (769,2144) - in-game visual testing found a house
        // placed directly on top of an existing roadside tavern's deck at
        // (773,2144) (HousePlacement.Check's own static-collision rule
        // doesn't reject a walkable Background-flagged floor overlay - see
        // OrganicMarketSpawner.HasFootprintConflict for the actual fix).
        // Empirically re-verified via the same ring search this time WITH
        // that fix active (SP-030 diagnostic history): nearest genuinely
        // open meadow ground sits 37 tiles east-southeast, at (806,2149).
        new(
            "Skara Brae Mainland Ferry Crossroads",
            new Point3D(806, 2149, 0),
            Map.Felucca,
            50,
            MarketHouseStyle.TwoStoryWoodPlaster,
            MarketArchetype.TailorFletcher
        ),

        // Southern road junction near the hedge maze, between Britain and
        // Trinsic.
        new(
            "Hedge Maze Southern Road Junction",
            new Point3D(1721, 1475, 0),
            Map.Felucca,
            50,
            MarketHouseStyle.LargePatio,
            MarketArchetype.Blacksmith
        ),

        // North of Trinsic (bank ~1899,2688) on the road running up
        // toward Britain.
        new(
            "Trinsic North Gateway Crossroads",
            new Point3D(1882, 2516, 0),
            Map.Felucca,
            45,
            MarketHouseStyle.SandStonePatio,
            MarketArchetype.MageAlchemist
        ),

        // West of Trinsic, on the paladin road out past the guard gate.
        new(
            "Trinsic West Guard Gate",
            new Point3D(1777, 2718, 0),
            Map.Felucca,
            50,
            MarketHouseStyle.StoneAndPlasterHouse,
            MarketArchetype.CurioRares
        ),

        // Farmland crossroads on Jhelom's main island.
        new(
            "Jhelom Farmland Crossroads",
            new Point3D(1214, 3576, 0),
            Map.Felucca,
            50,
            MarketHouseStyle.LogCabin,
            MarketArchetype.TailorFletcher
        ),

        // South-west of the direct Vesper-Minoc line (which runs over the
        // lake between them) - solid ground genuinely wide enough for the
        // largest style this tool places.
        new(
            "Vesper-Minoc Northern Passage",
            new Point3D(2773, 775, 0),
            Map.Felucca,
            45,
            MarketHouseStyle.LargePatio,
            MarketArchetype.Blacksmith
        ),

        // The mountain pass road into Minoc - kept to the smallest style,
        // matching how tight mining-town terrain tends to run.
        new(
            "Minoc Mountain Pass Road",
            new Point3D(2664, 451, 0),
            Map.Felucca,
            50,
            MarketHouseStyle.SmallShop,
            MarketArchetype.MageAlchemist
        ),

        // West of Cove, on the road toward Britain.
        new(
            "Cove City West Crossroads",
            new Point3D(2067, 1168, 0),
            Map.Felucca,
            50,
            MarketHouseStyle.TwoStoryWoodPlaster,
            MarketArchetype.CurioRares
        ),

        // Border crossing on the arid stretch between Britain and Vesper,
        // near the Shrine of Compassion.
        new(
            "Compassion Desert Border Crossing",
            new Point3D(2084, 1016, 0),
            Map.Felucca,
            50,
            MarketHouseStyle.SandStonePatio,
            MarketArchetype.TailorFletcher
        ),

        // Outside Moonglow's own guard-zone perimeter (bank ~4471,1157) -
        // the island only has room for the smallest style this tool
        // places, not a coincidence given how little land Moonglow has
        // outside its own walls.
        new(
            "Moonglow Outskirts Crossroads",
            new Point3D(4390, 1244, 0),
            Map.Felucca,
            45,
            MarketHouseStyle.SmallShop,
            MarketArchetype.Blacksmith
        ),

        // The road to the Lycaeum, also on Moonglow's small island - same
        // space constraint as the outskirts node above.
        new(
            "Moonglow Lycaeum Road",
            new Point3D(4448, 1234, 0),
            Map.Felucca,
            45,
            MarketHouseStyle.SmallShop,
            MarketArchetype.MageAlchemist
        ),

        // Northern outskirts of Ocllo, the island Magincia sits on.
        new(
            "Ocllo Town Northern Outskirts",
            new Point3D(3711, 2511, 0),
            Map.Felucca,
            50,
            MarketHouseStyle.StoneAndPlasterHouse,
            MarketArchetype.CurioRares
        ),

        // SP-029: was "Nujel'm Mainland Trade Port Road" at (3200,1100) -
        // confirmed via direct probing (see this file's own SP-029
        // diagnostic history) that Nujel'm is a genuinely small, densely
        // built island with no open buildable ground anywhere within 1500
        // tiles of its own coastline in any direction; even the town's own
        // interior came up with zero placement-valid tiles among the 449
        // candidates that made it past the region check. No anchor near
        // Nujel'm was going to satisfy this node, so it's retargeted here
        // instead - Buccaneer's Den is a real trade-port town with actual
        // open coastal ground nearby.
        new(
            "Buccaneer's Den Trade Anchorage",
            new Point3D(2796, 2228, 0),
            Map.Felucca,
            50,
            MarketHouseStyle.LogCabin,
            MarketArchetype.TailorFletcher
        )
    };

    // SP-024: the "world inhabitation" catalog - dungeon entrances, virtue
    // shrines, trade crossways, and coastal/wilderness POIs. A separate
    // pool from Nodes above: [Seed World Crossroads] always places a
    // vendor at each of the 4 corridor nodes; [Seed World Inhabitation]
    // rolls each of these ~90/10 filler-vs-vendor (WorldHouseSeeder.
    // SeedInhabitation). Archetype only matters for whichever ~10% of a
    // given node actually rolls a vendor shop - a filler house ignores it.
    //
    // Coordinates are the same kind of approximate reference point as
    // Nodes above, not hand-surveyed tiles, and radius is wider (150) than
    // Nodes' 35-40: several of these (dungeon mouths, shrine clearings,
    // mountain passes) sit in genuinely tight terrain, so the search needs
    // more room to find the nearest buildable ground. Not every node is
    // guaranteed to find one - a dungeon entrance boxed in by solid rock
    // on every side within the radius is expected to occasionally come up
    // empty, and SeedInhabitation treats that as a normal, silent skip.
    public static readonly SeedNode[] InhabitationNodes = BuildInhabitationNodes();

    private static SeedNode[] BuildInhabitationNodes()
    {
        var raw = new (string Name, int X, int Y)[]
        {
            // Dungeons & Mountain Passes
            ("Despise Dungeon Entrance", 1147, 1423),
            ("Deceit Dungeon Entrance", 4111, 967),
            ("Covetous Dungeon Entrance", 2489, 441),
            ("Wrong Dungeon Entrance", 5062, 15),
            ("Shame Dungeon Entrance", 513, 1559),
            ("Hythloth Dungeon Entrance", 4721, 3823),
            ("Destard Dungeon Entrance", 1176, 2637),
            ("Fire Dungeon Entrance", 2530, 3709),
            ("Terathan Keep Approach", 1234, 2698),
            ("Orc Cave Entrance", 2610, 645),
            ("Ice Dungeon Entrance", 4076, 240),
            ("Wrong Valley Clearing", 5000, 100),
            ("Minoc Mountain Clearing", 2620, 480),
            ("Serpent's Spine Pass", 2450, 700),

            // Virtue Shrines & Sacred Circles
            ("Shrine of Compassion", 1421, 1379),
            ("Compassion Hedge Maze", 1440, 1400),
            ("Shrine of Honesty", 4398, 1300),
            ("Honesty Stone Circle", 4420, 1320),
            ("Shrine of Honor", 2666, 508),
            ("Honor Stone Circle", 2690, 530),
            ("Shrine of Humility", 721, 1442),
            ("Humility Hedge Maze", 740, 1460),
            ("Shrine of Justice", 1470, 1518),
            ("Justice Stone Circle", 1490, 1540),
            ("Shrine of Sacrifice", 686, 748),
            ("Sacrifice Hedge Maze", 706, 768),
            ("Shrine of Spirituality", 1866, 1339),
            ("Spirituality Stone Circle", 1886, 1359),
            ("Shrine of Valor", 1834, 1358),
            ("Valor Hedge Maze", 1854, 1378),

            // Trade Corridors & Crossways
            ("Britain North Thoroughfare", 1433, 1500),
            ("Britain South Thoroughfare", 1449, 1998),
            ("Britain East Thoroughfare", 1650, 1689),
            ("Yew Court Road", 790, 1000),
            ("Yew Forest Crossing", 771, 1213),
            ("Skara Brae Ferry Dock", 598, 2160),
            ("Skara Mainland Coast", 650, 2050),
            ("Trinsic-Britain Coast Road", 1700, 2300),
            ("Trinsic South Road", 1918, 2850),
            ("Jhelom Countryside", 1341, 3701),
            ("Cove Outskirts", 2226, 1148),
            ("Cove-Minoc Trail", 2400, 900),
            ("Vesper Farm Roads", 2950, 800),
            ("Minoc Mountain Pass", 2680, 452),
            ("Nujel'm Mainland Transit", 3400, 1200),
            ("Nujel'm Outskirts", 3455, 1245),
            ("Britain West Thoroughfare", 1250, 1689),
            ("Trinsic West Road", 1750, 2688),
            ("Vesper Harbor Road", 2900, 620),
            ("Minoc Ore Road", 2550, 620),
            ("Skara Brae Highland", 700, 1950),
            ("Jhelom Coastal Watch", 1450, 3600),

            // Islands & Wilderness Clearings
            ("Vesper Bay Coast", 2988, 702),
            ("Ice Island Outpost", 4162, 236),
            ("Dagger Isle Coastal Cabin", 4200, 300),
            ("Ocllo Wilderness", 3670, 2570),
            ("Ocllo Plains", 3700, 2620),
            ("Buccaneer's Den Outskirts", 2712, 2237),
            ("Serpent's Hold Approach", 2792, 3468),
            ("Papua Island Shore", 2588, 3620),
            ("Fire Island Outpost Perimeter", 2560, 3680),
            ("Delucia Outskirts", 1441, 3765),
            ("Magincia Countryside", 3697, 2508),
            ("Moonglow Forest Trail", 4350, 1350),
            ("Moonglow Coastal Path", 4500, 1100),
            ("Yew Swampland Trail", 850, 1300),
            ("Nujel'm Harbor District", 3580, 1230),
            ("Nujel'm Coast", 3540, 1287),

            // Additional countryside/farmland nodes - generic open ground
            // near well-established towns, deliberately unconstrained
            // (unlike the dungeon/shrine entries above, these aren't
            // pinned to a specific tight landmark) so the search has more
            // genuinely easy wins to find across the full catalog.
            ("Britain Countryside North", 1300, 1250),
            ("Britain Countryside South", 1550, 2100),
            ("Trinsic Farmland", 2050, 2600),
            ("Vesper Countryside", 2750, 550),
            ("Minoc Highland Farms", 2350, 400),
            ("Moonglow Island Farms", 4300, 1050),
            ("Yew Woodland Clearing", 900, 1100),
            ("Skara Brae Countryside", 750, 2250),
            ("Jhelom Southern Fields", 1250, 3550),
            ("Cove Farmland", 2100, 1250),
            ("Nujel'm Island Clearing", 3350, 1350),
            ("Magincia Shoreline", 3600, 2400),
            ("Buccaneer's Den Countryside", 2600, 2100),
            ("Delucia Farmland", 1550, 3650),
            ("Papua Coastal Clearing", 2650, 3550),
            ("Serpent's Hold Countryside", 2700, 3350),
            ("Britain Riverside", 1550, 1550),
            ("Trinsic Riverside", 1850, 2500),
            ("Vesper Woodland", 2700, 750),
            ("Minoc Riverside", 2450, 650),

            // SP-029: task 4 wants the catalog grown further toward a
            // 100-150+ total placement target (each of these, like every
            // node above, also gets WorldHouseSeeder.SeedInhabitation's
            // own cluster-mate attempt - one named node seeds a small
            // neighborhood, not exactly one house).

            // Farmland tracts
            ("Britain Farmland Tract East", 1750, 1450),
            ("Trinsic Farmland Tract North", 1950, 2400),
            ("Vesper Farmland Tract West", 2600, 700),
            ("Yew Farmland Tract South", 850, 1150),
            ("Minoc Farmland Tract East", 2500, 550),
            ("Jhelom Farmland Tract North", 1350, 3500),

            // Valley clearings, near (but not on top of) the virtue
            // shrines above
            ("Compassion Valley Clearing", 1350, 1450),
            ("Honor Valley Clearing", 2600, 600),
            ("Justice Valley Clearing", 1550, 1600),
            ("Spirituality Valley Clearing", 1800, 1250),
            ("Sacrifice Valley Clearing", 750, 850),
            ("Humility Valley Clearing", 800, 1350),

            // Coastline settlements
            ("Britain Coastal Hamlet", 1500, 2200),
            ("Trinsic Coastal Hamlet", 2000, 2950),
            ("Vesper Coastal Hamlet", 3050, 650),
            ("Skara Brae Coastal Hamlet", 550, 2100),
            // SP-030: was (4550,1300) - in-game visual testing found an
            // ambient house spawned inside Moonglow's cemetery grounds
            // among gravestones and undead spawns at (4552,1314). No
            // Region bounds that cemetery at all on this server (confirmed:
            // no "Moonglow Cemetery" entry in Distribution/Data/
            // regions.json - see WorldHouseSeeder.IsCemeteryRegion and
            // OrganicMarketSpawner.HasFootprintConflict's gravestone-static
            // scan, the actual fix). Empirically re-verified with that fix
            // active: nearest genuinely clear ground sits 51 tiles
            // northwest, at (4510,1269).
            ("Moonglow Coastal Hamlet", 4510, 1269),
            ("Nujel'm Coastal Hamlet", 3450, 1150),
            ("Ocllo Coastal Hamlet", 3750, 2450),

            // Forest hamlets
            ("Yew Forest Hamlet", 700, 950),
            ("Britain Forest Hamlet North", 1150, 1200),
            ("Deceit Forest Hamlet", 4050, 1050),
            ("Spine Forest Hamlet", 2350, 750),
            ("Compassion Forest Hamlet", 1350, 1300),
            ("Trinsic Forest Hamlet", 1850, 2200),
            ("Delucia Forest Hamlet", 1400, 3700)
        };

        // Cycled round-robin across all 32 nodes purely for visual variety
        // - no thematic tie between a given POI and the style/archetype it
        // lands on.
        var styles = new[]
        {
            MarketHouseStyle.SmallShop, MarketHouseStyle.SmallPlasterHouse, MarketHouseStyle.SmallStoneHouse,
            MarketHouseStyle.SmallWoodHouse, MarketHouseStyle.WoodAndPlasterHouse, MarketHouseStyle.StoneAndPlasterHouse,
            MarketHouseStyle.TwoStoryWoodPlaster, MarketHouseStyle.LargePatio, MarketHouseStyle.SandStonePatio,
            MarketHouseStyle.LogCabin, MarketHouseStyle.SmallTower
        };

        var archetypes = new[]
        {
            MarketArchetype.Blacksmith, MarketArchetype.MageAlchemist, MarketArchetype.CurioRares,
            MarketArchetype.TailorFletcher
        };

        var nodes = new SeedNode[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            nodes[i] = new SeedNode(
                raw[i].Name,
                new Point3D(raw[i].X, raw[i].Y, 0),
                Map.Felucca,
                150,
                styles[i % styles.Length],
                archetypes[i % archetypes.Length]
            );
        }

        return nodes;
    }
}
