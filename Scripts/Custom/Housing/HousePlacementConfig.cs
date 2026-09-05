// =========================================================================
// HousePlacementConfig.cs — SP-043: centralizes the clearance buffer
// OrganicMarketSpawner.HasFootprintConflict uses to reject a candidate
// house spot that sits too close to an existing building's walls/roof/
// doors (or a cemetery's gravestones — see that method's own header for
// both cases this same margin covers).
//
// Was a single isotropic FootprintConflictMargin = 10 applied equally on
// every side. Per this ticket, ambient houses should be allowed to
// cluster much more tightly side-by-side (like a real streetscape row of
// houses) while still keeping enough room in front of a house for a
// player to actually walk up to and use its front door — every classic
// house style this catalog places puts its primary entrance on the SOUTH
// wall (confirmed by surveying every AddSouthDoor(s) call in Multis/
// Houses/Houses.cs; secondary balcony doors are the only ones that ever
// face east, and never instead of a south entrance), so "front-to-back"
// maps onto the North/South (Y) axis and "side-to-side" onto the East/
// West (X) axis for every style this system knows how to place.
//
// This only affects OrganicMarketSpawner's own supplemental conflict
// scan. The core engine's HousePlacement.Check has a separate YardSize
// buffer (Multis/Houses/HousePlacement.cs), but that rule is gated on
// `hasFoundation` — it only ever applies to the customizable
// HouseFoundation system, which nothing in this catalog uses (every
// MarketHouseStyle maps to a classic, pre-built multi house). So this
// config is the ONLY house-to-house spacing buffer in play for ambient
// houses, and shrinking it is sufficient on its own to let houses cluster
// tighter — no core file needed touching.
// =========================================================================

namespace Server.Engines.Housing;

public static class HousePlacementConfig
{
    // How close another building's wall/roof/door (or a neighboring
    // ambient house's own footprint) may sit to a candidate house's own
    // EAST or WEST edge.
    public const int SideToSideBuffer = 3;

    // How close another building's wall/roof/door may sit to a candidate
    // house's own NORTH or SOUTH edge — wider than the side buffer so the
    // ground in front of (and behind) a house's south-facing front door
    // stays clear enough for a real approach path, not just a doorway
    // wedged flush against a neighbor's wall.
    public const int FrontToBackBuffer = 5;
}
