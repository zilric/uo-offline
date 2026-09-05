// =========================================================================
// FerryFleetSeeder.cs — SP-043/SP-044: seeds a PermanentCharterBoat +
// on-deck CharterCaptain at every FerryRouteRegistry stop, plus the
// [seedferries / [wipeferries GM commands. Replaces AmbientBoatSeeder.cs
// and IslandOutpostSeeder.cs — there is no separate "hub" vs "outpost"
// treatment anymore (no camps, no dockmaster styling variants); every
// stop gets exactly the same boat + captain pair.
//
// [seedferries wipes any existing fleet first, so re-running it is
// idempotent rather than requiring a manual [wipeferries first.
//
// SP-044: SeedStop now checks !boat.Deleted before placing the captain.
// This matters because of exactly the bug that motivated SP-044 in the
// first place — a boat that cascade-deletes itself via TillerMan (now
// fixed, see PermanentCharterBoat.cs) used to leave the captain stranded
// with nothing under it. That root cause is fixed, but the check stays
// as a guard: if boat placement or construction ever fails for some
// other reason in the future, a captain floating with no boat is a worse
// failure mode than no captain at all, so this skips it and logs why.
//
// Deleting a tracked PermanentCharterBoat cascades through BaseBoat's own
// OnAfterDelete (which deletes its Hold/PPlank/SPlank/TillerMan), so
// [wipeferries never orphans a plank or hold even though only the boat
// itself is tracked in FerrySystemAuthority.
//
// Decay: a real BaseBoat sinks 9 days after its last Refresh(). A
// maintenance timer here calls KeepFresh() on every tracked boat every
// 6 hours so that never happens in practice.
// =========================================================================

using System;
using Server;
using Server.Commands;
using Server.Multis;

namespace Server.Engines.FerrySystem;

public static class FerryFleetSeeder
{
    // How far from the boat's own deck-landing tile the captain stands,
    // so an arriving/departing player (who lands exactly on DeckLanding)
    // never stacks directly on top of them.
    private const int CaptainOffsetX = 1;

    private static readonly TimeSpan FreshenInterval = TimeSpan.FromHours(6);

    public static void Configure()
    {
        CommandSystem.Register("seedferries", AccessLevel.GameMaster, OnSeed);
        CommandSystem.Register("wipeferries", AccessLevel.GameMaster, OnWipe);
    }

    public static void Initialize()
    {
        Timer.DelayCall(FreshenInterval, FreshenInterval, KeepFleetFresh);
    }

    [Usage("seedferries")]
    [Description("Cleans up any existing charter fleet, then moors a permanent charter boat with an on-deck captain at every port and island.")]
    private static void OnSeed(CommandEventArgs e)
    {
        var from = e.Mobile;
        var authority = FerrySystemAuthority.Instance;
        if (authority == null)
        {
            from?.SendMessage("FerrySystemAuthority is not ready yet.");
            return;
        }

        if (authority.IsSeeded)
        {
            authority.WipeAll();
        }

        var count = SeedAll(authority);
        authority.IsSeeded = true;

        from?.SendMessage($"Ferry fleet seeded: {count} charter boat(s) moored with captains aboard.");
    }

    [Usage("wipeferries")]
    [Description("Removes every charter boat, plank, hold and captain seeded by [seedferries.")]
    private static void OnWipe(CommandEventArgs e)
    {
        var from = e.Mobile;
        var authority = FerrySystemAuthority.Instance;
        if (authority == null)
        {
            from?.SendMessage("FerrySystemAuthority is not ready yet.");
            return;
        }

        authority.WipeAll();
        from?.SendMessage("Ferry fleet wiped.");
    }

    private static int SeedAll(FerrySystemAuthority authority)
    {
        var count = 0;
        foreach (var stop in FerryRouteRegistry.Stops)
        {
            if (SeedStop(authority, stop))
            {
                count++;
            }
        }

        return count;
    }

    private static bool SeedStop(FerrySystemAuthority authority, FerryStop stop)
    {
        var map = stop.Map;

        var boat = new PermanentCharterBoat();
        boat.MoveToWorld(stop.BoatLocation, map);
        boat.Facing = stop.BoatFacing;

        if (boat.Deleted)
        {
            Console.WriteLine($"[FerryFleetSeeder] {stop.Name}: charter boat failed to spawn — skipping captain.");
            return false;
        }

        authority.Track(boat);

        var captainSpot = new Point3D(stop.DeckLanding.X + CaptainOffsetX, stop.DeckLanding.Y, stop.DeckLanding.Z);
        var captain = new CharterCaptain(stop.Name);
        captain.MoveToWorld(captainSpot, map);
        authority.Track(captain);

        return true;
    }

    private static void KeepFleetFresh()
    {
        var authority = FerrySystemAuthority.Instance;
        if (authority == null)
        {
            return;
        }

        foreach (var item in authority.TrackedItems)
        {
            if (item is PermanentCharterBoat boat && !boat.Deleted)
            {
                boat.KeepFresh();
            }
        }
    }
}
