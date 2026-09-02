// =========================================================================
// BotShopBrowsingManager.cs — opportunistic, cosmetic-only shop browsing
// detours for passing bot mobiles near a registered market vendor.
//
// SP-034: fixed two real bugs found diagnosing "zero bots ever browsed":
//
//   1. Under-detection - Tick() only ever checked proximity to
//      MerchantGuildAuthority.VendorAt(i), the single "primary" vendor
//      SP-028's registry schema tracks per house. Now walks every vendor
//      in house.PlayerVendors instead.
//
//   2. Rigid type lookup - a single Type.GetType("Server.CustomBots.
//      PlayerBot") call is one rename away from silently returning null
//      forever. BotTypes now scans every loaded assembly ONCE, caching
//      every Mobile-derived type whose name contains "Bot".
//
// SP-035: fixed two more, found watching bots actually trigger it live:
//
//   3. Red combat flash - Frozen/CantWalk block Mobile.Move (Mobile.
//      CanMove checks m_Paralyzed || m_Frozen), but do NOT block a pure
//      direction change: Mobile.Move's own early branch
//      (`if ((m_Direction & Direction.Mask) == (d & Direction.Mask))`)
//      only runs the Frozen-gated CanMove check when the mobile is
//      actually trying to STEP - turning in place skips that branch
//      entirely and always succeeds. So while frozen, the bot's own AI
//      kept trying (and failing) to resume its real route, and each
//      failed attempt still spun its facing around - read from outside as
//      "stopped, turned around confusedly, then left," and separately,
//      something in the bot framework's own danger/threat handling reacts
//      to a mobile going suddenly unresponsive by flipping it into a
//      visible combat-ready state (the reported "turns red"). Neither
//      Frozen nor CantWalk is set anywhere in this file any more.
//
//   4. No real approach - the old code did a single one-tile Math.Sign
//      hop toward the vendor's own tile (often standing behind a
//      counter/table the bot can't occupy) and then just sat there for
//      the whole dwell - "stops in place on the road" is exactly what a
//      1-tile nudge toward an unreachable tile looks like. Approach now
//      uses Server's own PathFollower (the same step-by-step, obstacle-
//      and-door-aware walker PlayerBot's real movement is built on -
//      see Engines/Pathing/PathFollower.cs) driven by a short repeating
//      timer, walking the bot to a real interior spot a couple of tiles
//      in front of the vendor (in the direction the vendor itself faces -
//      InteriorTileFinder places vendors facing the customer approach
//      area, so that direction reliably points at open floor).
//
// Since nothing here ever blocks the bot's own AI any more, this can't
// force a "resume" the way a freeze/unfreeze pair could - once the dwell
// ends, this code simply stops touching the bot, and whatever it was
// already doing before (never interrupted at the Move level) carries on
// on its own next decision tick.
// =========================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using CalcMoves = Server.Movement.Movement;
using Server.Logging;
using Server.Mobiles;

namespace Server.Engines.OrganicMarket;

public static class BotShopBrowsingManager
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(BotShopBrowsingManager));

    // "every 5-10 seconds" - a low-frequency ambient tick, not a per-game-
    // tick hook.
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(7);

    // Widened from an original 12 - a shop's various vendors aren't all
    // standing on the exact same tile, and a bot walking a road a few
    // tiles further out than a tight radius still reads as "passing the
    // shop."
    private const int ProximityRange = 16;

    // Raised from an original 12% so the behavior is actually observable
    // during testing instead of needing dozens of passes before one
    // triggers.
    private const double DetourChance = 0.30;

    // "per-bot cooldown of >= 30 minutes."
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);

    // SP-035: "dwell quietly for 5-8 seconds."
    private const int DwellMinSeconds = 5;
    private const int DwellMaxSeconds = 8;

    // How far in front of the vendor (in the direction it faces) the
    // approach targets - "1-2 tiles in front of the counter."
    private const int CounterApproachDistance = 2;

    // Safety cap on how many PathFollower steps one detour will spend
    // trying to reach the counter spot before giving up and dwelling
    // wherever it ended up - a locked door or an unreachable interior
    // shouldn't strand a bot mid-approach forever.
    private const int MaxApproachSteps = 20;

    private static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FaceReassertInterval = TimeSpan.FromSeconds(1);

    private static HashSet<Type> _botTypes;
    private static bool _typesResolved;

    // Serial-keyed rather than Mobile-keyed - safe to hold across a bot's
    // own deletion/respawn without pinning a stale reference, and Serial
    // is this engine's own standard entity-identity key throughout.
    private static readonly Dictionary<Serial, DateTime> _lastBrowsed = new();

    public static void Initialize()
    {
        Timer.DelayCall(TickInterval, TickInterval, Tick);
    }

    // Resolved lazily (first Tick, not at class load) and cached - the
    // assembly scan runs exactly once for this class's entire lifetime,
    // not once per tick. Scans every loaded assembly (not just the
    // "calling" one Type.GetType would implicitly search) for any type
    // that IS-A Mobile and whose simple name contains "Bot" - catches
    // Server.CustomBots.PlayerBot today, and keeps working if that class
    // is ever renamed or a second bot-flavored Mobile type is added,
    // without this file needing to change.
    private static HashSet<Type> BotTypes
    {
        get
        {
            if (!_typesResolved)
            {
                _typesResolved = true;
                _botTypes = ResolveBotTypes();
            }

            return _botTypes;
        }
    }

    private static HashSet<Type> ResolveBotTypes()
    {
        var found = new HashSet<Type>();
        var mobileType = typeof(Mobile);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // One misbehaving assembly (a missing optional dependency,
                // usually) shouldn't blank out every other assembly's
                // types - salvage whatever types DID load successfully.
                var loaded = ex.Types;
                types = new Type[loaded.Length];
                var count = 0;
                foreach (var t in loaded)
                {
                    if (t != null)
                    {
                        types[count++] = t;
                    }
                }

                Array.Resize(ref types, count);
            }

            foreach (var type in types)
            {
                if (mobileType.IsAssignableFrom(type) &&
                    type.Name.Contains("Bot", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(type);
                }
            }
        }

        if (VerboseConfig.Pathfinding)
        {
            var names = "";
            foreach (var t in found)
            {
                names += (names.Length > 0 ? ", " : "") + t.FullName;
            }

            logger.Information("[BotBrowsing] Resolved {Count} bot-like Mobile type(s): {Types}", found.Count, names);
        }

        return found;
    }

    private static bool IsBot(Mobile m)
    {
        var type = m.GetType();
        foreach (var botType in BotTypes)
        {
            if (botType.IsAssignableFrom(type))
            {
                return true;
            }
        }

        return false;
    }

    private static void Tick()
    {
        if (BotTypes.Count == 0)
        {
            return; // No bot-like Mobile type found in any loaded assembly - nothing to do.
        }

        var authority = MerchantGuildAuthority.Instance;
        if (authority == null)
        {
            return;
        }

        PruneStaleCooldowns();

        // Candidates are only COLLECTED while GetMobilesInRange's
        // enumerator is live, never acted on directly inside the loop -
        // StartBrowsing eventually moves the bot, which would modify the
        // map's own per-sector mobile lists out from under this same
        // enumeration ("collection was modified after the enumerator was
        // instantiated," confirmed live once already). All mutation
        // happens in the second loop below, strictly after every
        // enumerator from this scan has been fully walked and discarded.
        var candidates = new List<(Mobile Bot, PlayerVendor Vendor)>();

        for (var i = 0; i < authority.Count; i++)
        {
            var house = authority.HouseAt(i);
            if (house?.Deleted != false)
            {
                continue;
            }

            // Every real vendor in the shop, not just the single tracked
            // "primary."
            foreach (var vendor in house.PlayerVendors)
            {
                if (vendor?.Deleted != false || vendor.Map == null || vendor.Map == Map.Internal)
                {
                    continue;
                }

                foreach (var m in vendor.Map.GetMobilesInRange(vendor.Location, ProximityRange))
                {
                    // Skip anything already mid-detour/in combat - a
                    // fleeing or fighting bot shouldn't get cosmetically
                    // interrupted, and a bot another vendor's own scan
                    // already queued this same tick shouldn't be re-rolled.
                    if (m?.Deleted != false || m.Warmode || !IsBot(m))
                    {
                        continue;
                    }

                    if (_lastBrowsed.TryGetValue(m.Serial, out var last) && DateTime.UtcNow - last < Cooldown)
                    {
                        continue;
                    }

                    if (Utility.RandomDouble() >= DetourChance)
                    {
                        continue;
                    }

                    candidates.Add((m, vendor));
                }
            }
        }

        var started = 0;

        foreach (var (bot, vendor) in candidates)
        {
            // Re-check deleted/cooldown - two different vendors' scans
            // above could both have queued the same bot before either one
            // actually ran.
            if (bot.Deleted ||
                (_lastBrowsed.TryGetValue(bot.Serial, out var last) && DateTime.UtcNow - last < Cooldown))
            {
                continue;
            }

            StartBrowsing(bot, vendor);
            started++;
        }

        if (started > 0 && VerboseConfig.Pathfinding)
        {
            logger.Information("[BotBrowsing] Tick started {Count} browsing detour(s)", started);
        }
    }

    // Cheap, bounded cleanup so a long-running server doesn't accumulate
    // one dictionary entry per bot that has ever browsed, forever.
    private static void PruneStaleCooldowns()
    {
        if (_lastBrowsed.Count == 0)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - Cooldown;
        var stale = new List<Serial>();

        foreach (var (serial, last) in _lastBrowsed)
        {
            if (last < cutoff)
            {
                stale.Add(serial);
            }
        }

        foreach (var serial in stale)
        {
            _lastBrowsed.Remove(serial);
        }
    }

    private static void StartBrowsing(Mobile bot, PlayerVendor vendor)
    {
        _lastBrowsed[bot.Serial] = DateTime.UtcNow;

        if (VerboseConfig.Pathfinding)
        {
            logger.Information(
                "[BotBrowsing] Detected bot {Name} near vendor {VendorName} - initiating detour",
                bot.Name, vendor.Name
            );
        }

        // Strictly enforced, never toggled off elsewhere in this flow -
        // no criminal/aggression flag is ever touched here.
        bot.Warmode = false;

        var counterSpot = ComputeCounterSpot(vendor);
        var follower = new PathFollower(bot, counterSpot);

        WalkToCounter(bot, vendor, follower, MaxApproachSteps);
    }

    // A tile a couple of steps in front of the vendor, in the direction
    // the vendor itself faces - InteriorTileFinder.TryFindVendorSpots
    // orients every spawned vendor to face the customer approach area, so
    // that direction reliably points at open interior floor rather than
    // into a wall or the counter/table behind the vendor.
    private static Point3D ComputeCounterSpot(PlayerVendor vendor)
    {
        var spot = vendor.Location;
        CalcMoves.Offset(vendor.Direction, ref spot, CounterApproachDistance);
        return spot;
    }

    // One real pathing step per call, via the same PathFollower class
    // PlayerBot's own movement is built on - obstacle-aware and door-
    // aware (PlayerBot.Move already opens closed doors it walks into),
    // not a blind teleport-style hop. Re-invoked on a short timer rather
    // than in a single tight loop so the walk animates naturally instead
    // of snapping to the destination.
    private static void WalkToCounter(Mobile bot, PlayerVendor vendor, PathFollower follower, int stepsRemaining)
    {
        if (bot?.Deleted != false || vendor?.Deleted != false || bot.Map != vendor.Map)
        {
            return;
        }

        // Reasserted every step - a stray Warmode flip picked up mid-
        // approach (e.g. a passing red) shouldn't derail a friendly
        // shop-browsing detour.
        bot.Warmode = false;

        if (stepsRemaining <= 0)
        {
            // Ran out of approach budget (a locked door, an unreachable
            // interior, ...) - dwell wherever it ended up rather than
            // stranding it mid-walk forever. Matches the graceful-
            // fallback spirit: inspect from the porch, not the middle of
            // the road.
            BeginDwell(bot, vendor);
            return;
        }

        if (follower.Follow(1))
        {
            BeginDwell(bot, vendor);
            return;
        }

        Timer.DelayCall(StepInterval, () => WalkToCounter(bot, vendor, follower, stepsRemaining - 1));
    }

    private static void BeginDwell(Mobile bot, PlayerVendor vendor)
    {
        if (bot?.Deleted != false)
        {
            return;
        }

        bot.Direction = bot.GetDirectionTo(vendor);
        bot.Warmode = false;

        var dwellSeconds = Utility.RandomMinMax(DwellMinSeconds, DwellMaxSeconds);
        ReassertFacing(bot, vendor, dwellSeconds);
    }

    // Re-faces the vendor once a second for the rest of the dwell - the
    // bot's own AI is never blocked (no Frozen/CantWalk anywhere in this
    // file), so left alone its own decision loop could spin the bot's
    // facing back toward its real route mid-browse (a bare turn always
    // succeeds even when a step would fail - see this file's own header).
    // Continuously re-asserting keeps the bot visibly looking at the
    // vendor for the whole dwell instead of a single facing set that gets
    // silently overwritten a moment later.
    private static void ReassertFacing(Mobile bot, PlayerVendor vendor, int secondsRemaining)
    {
        if (bot?.Deleted != false)
        {
            return;
        }

        if (secondsRemaining <= 0)
        {
            if (VerboseConfig.Pathfinding)
            {
                logger.Information("[BotBrowsing] Bot {Name} completed browsing, resuming path", bot.Name);
            }

            return;
        }

        bot.Direction = bot.GetDirectionTo(vendor);

        Timer.DelayCall(FaceReassertInterval, () => ReassertFacing(bot, vendor, secondsRemaining - 1));
    }
}
