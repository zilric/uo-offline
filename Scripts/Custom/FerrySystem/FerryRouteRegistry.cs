// =========================================================================
// FerryRouteRegistry.cs — SP-043 through SP-045: static data for every
// charter stop.
//
// Per SP-043's architecture — permanent moored boats with an on-deck
// captain, no more standalone shore dockmasters — the thing that needs a
// coordinate is the BOAT: BoatLocation is where the boat multi's own
// pivot sits (its waterline Z), with BoatFacing chosen so the boat sits
// broadside to the shore (BaseBoat's Port/Starboard planks are
// perpendicular to its facing — see PermanentCharterBoat.cs).
//
// SP-045: the twelve non-Britain coordinates below are the user's own
// client-verified "deck tile directly behind the mast" spots — i.e. each
// one IS the exact value BaseBoat.GetMarkedLocation() would return for a
// correctly-placed boat, not the boat's own pivot tile. DeckLanding is
// therefore computed the same way GetMarkedLocation() itself does: take
// SmallBoat's own MarkOffset (local (0, 1, 3) — 1 tile toward local
// "south", 3 Z above the pivot) and rotate it by BoatFacing before adding
// it to BoatLocation (SP-044's version of this added the +3 to Z but
// never applied the +1 tile rotation, which is what this fixes).
// FerryStop's constructor runs that same rotation, so every stop's
// BoatLocation below is stored as the *inverse* of the user's given deck
// coordinate — solve GetMarkedLocation's rotation backward for the
// stop's own BoatFacing to recover where the boat's pivot must sit for
// the deck spot to land exactly on the user's coordinate. Britain's entry
// is untouched (already confirmed working before this fix existed).
//
// DeckLanding.Z: the user's coordinates didn't include a Z. Britain's
// already-confirmed pairing is waterline -2 / deck +1 (a +3 rise,
// matching MarkOffset's own Z), so the twelve new stops use the same
// +3 relationship rather than an independently-guessed flat value — this
// is the one thing about deck height that Britain actually confirmed
// works, so it's what everything else is built on instead of introducing
// a second, untested convention.
//
// Routing model: every stop can charter to every OTHER stop (a "any port
// sells passage to any other port" ferry network), so there is no
// adjacency table to maintain — GetDestinationsFrom just excludes the
// origin. Fare scales with tile distance between the two stops' boats,
// clamped into the 50-250gp range.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;

namespace Server.Engines.FerrySystem;

public sealed class FerryStop
{
    // SmallBoat's own MarkOffset — local (0, 1, 3): 1 tile "south" and 3 Z
    // up from the boat's own pivot, before rotation by facing. This is
    // the exact same constant BaseBoat.GetMarkedLocation() rotates to find
    // a boat's rune/recall spot; DeckLanding below reproduces that
    // rotation so it lands on the exact same tile a real GetMarkedLocation()
    // call against the seeded boat would return.
    private static readonly Point3D MarkOffset = new(0, 1, 3);

    public string Name { get; }
    public string Lore { get; }

    // Where the permanent charter boat itself is moored (waterline Z).
    public Point3D BoatLocation { get; }

    // Where a person actually stands on deck, directly behind the mast —
    // BoatLocation + MarkOffset, rotated by BoatFacing. Used for the
    // on-deck captain and for arriving/departing players/pets.
    public Point3D DeckLanding { get; }

    // North => Port/Starboard planks on the E/W sides (boat runs N/S,
    // broadside to a shore that's east or west of it).
    // East  => planks on the N/S sides (boat runs E/W, broadside to a
    // shore that's north or south of it).
    // South is the same E/W-plank axis as North, just the hull graphic
    // facing the opposite way — used where the pier slip needs the bow
    // pointed south rather than north.
    public Direction BoatFacing { get; }

    public Map Map { get; }

    public FerryStop(string name, string lore, Point3D boatLocation, Direction boatFacing)
    {
        Name = name;
        Lore = lore;
        BoatLocation = boatLocation;
        BoatFacing = boatFacing;
        Map = Map.Felucca;

        var (dx, dy) = RotateMarkOffset(boatFacing);
        DeckLanding = new Point3D(boatLocation.X + dx, boatLocation.Y + dy, boatLocation.Z + MarkOffset.Z);
    }

    // Rotates MarkOffset's (X, Y) by the same count BaseBoat.Rotate uses
    // ((int)facing / 2 quarter-turns) — reproduced here rather than called
    // on a live boat instance since this runs before any boat exists.
    private static (int dx, int dy) RotateMarkOffset(Direction facing) =>
        facing switch
        {
            Direction.North => (MarkOffset.X, MarkOffset.Y),
            Direction.East  => (-MarkOffset.Y, MarkOffset.X),
            Direction.South => (-MarkOffset.X, -MarkOffset.Y),
            Direction.West  => (MarkOffset.Y, -MarkOffset.X),
            _               => (MarkOffset.X, MarkOffset.Y)
        };
}

public static class FerryRouteRegistry
{
    private const int MinFare = 50;
    private const int MaxFare = 250;

    // Fare climbs roughly a gold piece every 6 tiles of distance beyond a
    // flat 50gp boarding fee, then clamps at 250 for the far side of the map.
    private const int BaseFare = 50;
    private const double TilesPerGold = 6.0;

    public static readonly FerryStop[] Stops =
    {
        // SP-044: client-verified mooring, facing South to match the pier
        // slip orientation shown in the screenshot.
        new(
            "Britain Harbor Docks",
            "Britannia's busiest port — boats crowd the bay day and night.",
            new Point3D(1457, 1766, -2), Direction.South
        ),
        // SP-045: user stood at deck coordinate (2091, 2857) behind the
        // mast. Facing North => DeckLanding = BoatLocation + (0, 1, 3), so
        // BoatLocation = (2091, 2856, -4).
        new(
            "Trinsic East Docks",
            "Trinsic's paladin fleet guards these honest trade waters.",
            new Point3D(2091, 2856, -4), Direction.North
        ),
        // SP-045: deck coordinate (3050, 837), facing North.
        new(
            "Vesper East Harbor",
            "A quiet harbor favored by traders on the northern coast.",
            new Point3D(3050, 836, -4), Direction.North
        ),
        // SP-045: deck coordinate (1379, 3902), facing East => DeckLanding
        // = BoatLocation + (-1, 0, 3), so BoatLocation = (1380, 3902, -4).
        new(
            "Jhelom Main Island Docks",
            "The warrior city's planks have outlasted a thousand storms.",
            new Point3D(1380, 3902, -4), Direction.East
        ),
        // SP-045: deck coordinate (4425, 1037), facing North.
        new(
            "Moonglow West Pier",
            "Where the mages' isle meets the mainland trade routes.",
            new Point3D(4425, 1036, -4), Direction.North
        ),
        // SP-045: deck coordinate (3641, 2679), facing North.
        new(
            "Ocllo Town Docks",
            "A sleepy dock town — the charter boats run reliably.",
            new Point3D(3641, 2678, -4), Direction.North
        ),
        // SP-045: deck coordinate (3661, 2309), facing North.
        new(
            "Magincia Harbor",
            "The gem-city's harbor, rebuilt again — still gleaming.",
            new Point3D(3661, 2308, -4), Direction.North
        ),
        // SP-045: deck coordinate (2533, 332), facing East.
        new(
            "Minoc Bay Docks",
            "Miners load ore barges for the run down to Britain.",
            new Point3D(2534, 332, -4), Direction.East
        ),
        // SP-045: deck coordinate (511, 800), facing East.
        new(
            "Yew North Coast Pier",
            "A quiet coastal pier below the Empath Abbey.",
            new Point3D(512, 800, -4), Direction.East
        ),
        // SP-045: deck coordinate (4269, 597), facing North. The
        // SP-044-era "captain standing in the sea" report at this stop was
        // the TillerMan cascade-delete bug (see PermanentCharterBoat.cs) —
        // the boat never actually existed. This is the user's own
        // hands-on-verified mooring, replacing the earlier estimate.
        new(
            "Dagger Isle",
            "A grim beach camp near Deceit's black cliffs.",
            new Point3D(4269, 596, -4), Direction.North
        ),
        // SP-045: deck coordinate (2387, 3912), facing North.
        new(
            "Isle of the Avatar",
            "A humble camp near the shrine paths of Humility.",
            new Point3D(2387, 3911, -4), Direction.North
        ),
        // SP-045: deck coordinate (2775, 3447), facing North.
        new(
            "Isle of Fire",
            "A smoldering camp overlooking the Hythloth approach.",
            new Point3D(2775, 3446, -4), Direction.North
        ),
        // SP-045: deck coordinate (667, 2226), facing North.
        new(
            "Skara Brae Ferry Bank",
            "The mainland crossing point to Skara Brae's island.",
            new Point3D(667, 2225, -4), Direction.North
        )
    };

    public static FerryStop GetStop(string name)
    {
        for (var i = 0; i < Stops.Length; i++)
        {
            if (string.Equals(Stops[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return Stops[i];
            }
        }

        return null;
    }

    public static List<FerryStop> GetDestinationsFrom(string originName)
    {
        var list = new List<FerryStop>();
        for (var i = 0; i < Stops.Length; i++)
        {
            if (!string.Equals(Stops[i].Name, originName, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(Stops[i]);
            }
        }

        return list;
    }

    public static int ComputeFare(FerryStop from, FerryStop to)
    {
        if (from == null || to == null)
        {
            return MinFare;
        }

        var dx = from.BoatLocation.X - to.BoatLocation.X;
        var dy = from.BoatLocation.Y - to.BoatLocation.Y;
        var tiles = Math.Sqrt(dx * dx + dy * dy);

        var fare = BaseFare + (int)(tiles / TilesPerGold);
        return Math.Clamp(fare, MinFare, MaxFare);
    }
}
