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
// SP-037 ("Bot Approach Vectoring, Counter Standoff & Pathing State
// Restoration"): three more refinements, all found wanting under closer
// live inspection of the SP-035 approach:
//
//   5. Approximate standoff, not a real one - ComputeCounterSpot assumed
//      "2 tiles in the vendor's facing direction" always lands on open
//      customer floor. True for a properly counter-anchored vendor (SP-
//      034's own geometry guarantees exactly that), but never actually
//      confirmed against the REAL locked-down counter, and never
//      re-validated the candidate tile's own walkability/floor elevation
//      or checked it against the house's stair/ladder exclusion zones
//      (SP-036). ComputeCounterSpot now locates the actual LargeTable one
//      tile in front of the vendor, computes the tile one step past IT,
//      and validates every candidate through InteriorTileFinder.
//      IsGroundFloorInterior (the same real-surface check every other
//      placement decision in this system trusts) plus DynamicClutter
//      Generator's stair exclusion set, falling back through progressively
//      closer candidates rather than trusting pure geometry.
//
//   6. No real state caching, no timeout distinction - the bot's own
//      PlayerBotBehavior (the live "brain" BehaviorTickManager ticks
//      every 2 seconds, independent of anything this file does) was
//      never paused, only ever relied on for never being blocked at the
//      Move() level. That worked, but left the bot's own AI free to keep
//      ticking - and potentially issuing its OWN Move() calls toward its
//      real destination - at the same time this file's PathFollower was
//      driving it toward the counter, a real (if rare) source of
//      rubberbanding neither system knew about the other. StartBrowsing
//      now caches the bot's live PlayerBotBehavior instance (which
//      carries its own destination/waypoint state internally - caching
//      the reference IS caching that state) and swaps in a silent do-
//      nothing BrowsingBehavior for the whole detour, restoring the exact
//      same cached instance afterward so the bot's journey resumes from
//      exactly where its own behavior's internal state already says it
//      is. A bot that times out approaching (MaxApproachSteps exhausted)
//      now aborts and restores immediately, skipping the dwell entirely,
//      instead of standing around wherever the timeout left it.
//
//   7. Cooldown asserted only at the START of a detour - a bot that spent
//      several seconds approaching/dwelling had its 30-minute window
//      measured from BEFORE any of that happened. Re-asserted again now
//      at actual completion (success OR abort), so "bots that complete a
//      detour respect the 30-minute cooldown" holds from when they
//      actually finish, not when they started.
//
// Since the bot's own AI is now genuinely paused (its real Behavior
// detached, not just left un-frozen) for the exact duration of the
// detour, resumption is a plain reference restore - no separate "resume"
// signal needed.
//
// SP-038 ("Bot Approach Stepping Cadence & Movement Smoothing"): the
// approach's own step timer was still a single fixed 500ms interval for
// EVERY bot regardless of how it was actually moving - fine-ish on foot,
// but a mounted bot rendered at 500ms/step stutters badly, since the
// client's own mount-animation interpolation expects position updates
// roughly every 200ms at a walk. ComputeStepDelay now picks the real
// per-mobile pace (CalcMoves.WalkMountDelay/WalkFootDelay - this
// engine's own live-configured movement-speed settings, not guessed
// numbers) keyed off Mobile.Mounted, and ApproachTimeout (still ~11s,
// unchanged from SP-037) is now divided by that pace to get the actual
// step-count budget, so a fast-stepping mounted bot and a slower on-foot
// one both still get roughly the same REAL abort timeout instead of the
// mounted one running out in a fifth of the time. Walk pace, deliberately
// not run, for both cases - see ComputeStepDelay's own header for why.
// =========================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using CalcMoves = Server.Movement.Movement;
using Server.CustomBots;
using Server.Items;
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

    // SP-037: "dwell quietly for 5-10 seconds" (widened from SP-035's own
    // 5-8, per this ticket's explicit range).
    private const int DwellMinSeconds = 5;
    private const int DwellMaxSeconds = 10;

    // How far in front of the vendor (in the direction it faces) the
    // geometric fallback approach targets, when ComputeCounterSpot can't
    // confirm a real counter - "1-2 tiles in front of the counter."
    private const int CounterApproachDistance = 2;

    // SP-037's explicit "10-12 second timeout abort," now expressed as a
    // wall-clock budget rather than a fixed step count - SP-038 made the
    // per-step delay dynamic (see ComputeStepDelay), so a fixed step
    // COUNT would give a fast-stepping mounted bot a much SHORTER real
    // timeout than a slow on-foot one. StartBrowsing instead derives the
    // actual step count from this timeout divided by that bot's own step
    // delay, so every bot gets roughly the same ~11 real seconds to reach
    // the counter regardless of its own pace - "scaled appropriately so
    // faster bots do not linger," per the ticket.
    private static readonly TimeSpan ApproachTimeout = TimeSpan.FromSeconds(11);

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

    // SP-037: a deliberately silent, do-nothing PlayerBotBehavior swapped
    // in for the whole duration of a shop-browsing detour - see
    // StartBrowsing/RestoreBehavior. Tick is left as PlayerBotBehavior's
    // own empty virtual default (never overridden here), which is exactly
    // what guarantees zero speech/emote/sound for the whole approach and
    // dwell: nothing in this class runs TrySpeak or touches the bot at
    // all, and the ORIGINAL behavior it temporarily replaces simply never
    // ticks while this is attached (BehaviorTickManager only ever calls
    // bot.Behavior?.Tick(bot) - the cached original isn't "bot.Behavior"
    // again until RestoreBehavior puts it back).
    private sealed class BrowsingBehavior : PlayerBotBehavior
    {
        public BrowsingBehavior()
        {
            // Belt-and-suspenders - Tick above never calls TrySpeak, but
            // zeroing these documents the intent even against a future
            // base-class change that might consult them from somewhere
            // other than Tick.
            ChatCategories = Array.Empty<string>();
            ChatChance = 0.0;
        }

        public override string SerializableName => "Shop Browsing";

        public override string GetStatusLine(PlayerBot bot) => "browsing a shop";
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
        // SP-037: set at the START too, not just at completion - a bot
        // shouldn't become a candidate for a SECOND vendor's proximity
        // scan while already mid-approach to a first one this same tick
        // batch. CompleteBrowsing/AbortApproach re-assert this again once
        // the detour actually finishes, which is what makes the real
        // 30-minute window measure from completion rather than start -
        // see this file's own SP-037 header note.
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

        // SP-037: cache the bot's own live AI state and pause it for the
        // detour - see BrowsingBehavior's own header for why. Only
        // PlayerBot exposes a Behavior to cache; the other bot-like types
        // this file also matches (BotPackHorse, BotPackLlama, Faction
        // BottleVendor) have no real travel state to preserve, so they
        // fall straight through to the same move/dwell/release flow with
        // a null previousBehavior (RestoreBehavior below is then simply a
        // no-op for them).
        PlayerBotBehavior previousBehavior = null;
        if (bot is PlayerBot playerBot)
        {
            previousBehavior = playerBot.Behavior;
            playerBot.Behavior = new BrowsingBehavior();
        }

        var stairZones = vendor.House is { } house
            ? InteriorTileFinder.ComputeStairExclusionZones(house)
            : new HashSet<Point2D>();

        var counterSpot = ComputeCounterSpot(vendor, stairZones);
        var follower = new PathFollower(bot, counterSpot);

        // SP-038: computed once, up front, and threaded through the whole
        // approach chain unchanged - a bot's Mounted status is not going
        // to flip mid-detour, and keeping one fixed delay for the whole
        // approach is what makes ApproachTimeout's own step-count math
        // (see below) stay a real, consistent wall-clock budget.
        var stepDelay = ComputeStepDelay(bot);
        var maxSteps = Math.Max(1, (int)(ApproachTimeout.TotalMilliseconds / stepDelay.TotalMilliseconds));

        WalkToCounter(bot, vendor, follower, maxSteps, stepDelay, previousBehavior);
    }

    // SP-038: real per-mobile step cadence instead of the one fixed
    // 500ms interval every bot used to get regardless of how it was
    // actually moving - a mounted bot stepped once every 500ms stutters
    // badly (the client's own mount-animation interpolation expects
    // position updates roughly every 200ms at a walk; anything much
    // slower reads as a visible hitch between each little hop), and even
    // an unmounted bot at 500ms is noticeably slower than genuine
    // walking pace (400ms).
    //
    // Deliberately WALK pace, not run, for both cases - PathFollower.
    // Follow always computes its own step Direction via Mobile.
    // GetDirectionTo(goal) (Server/Engines/Pathing/PathFollower.cs),
    // whose `run` parameter defaults to false and is never overridden
    // there, so the Running bit is never actually set on the direction
    // this file's approach steps send the client. Pacing the SERVER's
    // own step timer to match what the DIRECTION packets already tell
    // the client (walking, not running) is what keeps position updates
    // and the client's own animation speed honestly in sync - forcing a
    // faster run-paced timer here would just trade one mismatch (too
    // slow) for another (positions arriving faster than a walking
    // animation can visibly keep up with).
    //
    // CalcMoves.WalkMountDelay/WalkFootDelay (Server.Movement.Movement,
    // already aliased CalcMoves in this file) are this engine's own
    // live-configured movement-speed settings (movement.delay.walkMount/
    // walkFoot), not guessed constants - they default to exactly the
    // ticket's own "~200ms mounted / ~380-400ms foot" walking figures.
    private static TimeSpan ComputeStepDelay(Mobile bot) =>
        TimeSpan.FromMilliseconds(bot.Mounted ? CalcMoves.WalkMountDelay : CalcMoves.WalkFootDelay);

    // SP-037: the real customer-side standoff tile, not just an assumed
    // offset. Locates the actual LargeTable the vendor is anchored behind
    // (DynamicClutterGenerator.PlaceCounter always stands a properly
    // counter-anchored vendor exactly 1 tile behind the counter's own
    // center, facing back out over it - so the counter, if one exists, is
    // always exactly 1 step from the vendor in vendor.Direction), then
    // targets the tile one step PAST that real counter. Every candidate -
    // the real-counter one first, then progressively looser geometric
    // fallbacks for a vendor that never got a counter anchor (a very
    // small floor plan, or InteriorTileFinder.TryFindVendorSpots'
    // fallback path) - is validated through InteriorTileFinder.
    // IsGroundFloorInterior (rejects anything that isn't genuinely
    // walkable ground-floor surface, including a tile that would clip
    // onto the counter's own raised tabletop or resolve to a porch/
    // exterior Z) and the house's own stair/ladder exclusion zone before
    // being accepted.
    private static Point3D ComputeCounterSpot(PlayerVendor vendor, HashSet<Point2D> stairZones)
    {
        var map = vendor.Map;
        var house = vendor.House;

        var counterTile = vendor.Location;
        CalcMoves.Offset(vendor.Direction, ref counterTile, 1);

        var hasRealCounter = false;
        if (house != null)
        {
            foreach (var item in house.LockDowns)
            {
                if (item is LargeTable && item.Deleted == false &&
                    item.X == counterTile.X && item.Y == counterTile.Y)
                {
                    hasRealCounter = true;
                    break;
                }
            }
        }

        var candidates = new List<Point3D>(3);

        if (hasRealCounter)
        {
            var beyondCounter = counterTile;
            CalcMoves.Offset(vendor.Direction, ref beyondCounter, 1);
            candidates.Add(beyondCounter);
        }

        var twoTiles = vendor.Location;
        CalcMoves.Offset(vendor.Direction, ref twoTiles, CounterApproachDistance);
        candidates.Add(twoTiles);

        var oneTile = vendor.Location;
        CalcMoves.Offset(vendor.Direction, ref oneTile, 1);
        candidates.Add(oneTile);

        foreach (var candidate in candidates)
        {
            if (map == null || house == null)
            {
                return candidate; // no house context to validate against - best effort.
            }

            if (!InteriorTileFinder.IsGroundFloorInterior(house, map, candidate.X, candidate.Y, out var validated))
            {
                continue;
            }

            if (stairZones.Contains(new Point2D(validated.X, validated.Y)))
            {
                continue;
            }

            return validated;
        }

        // Every candidate failed real validation - last resort. Follower.
        // Follow(1) will report immediate arrival against the vendor's
        // own tile, so this just dwells there rather than stranding the
        // bot mid-search.
        return vendor.Location;
    }

    // One real pathing step per call, via the same PathFollower class
    // PlayerBot's own movement is built on - obstacle-aware and door-
    // aware (PlayerBot.Move already opens closed doors it walks into),
    // not a blind teleport-style hop. Re-invoked on `stepDelay` (SP-038:
    // the mobile's own real walk cadence - see ComputeStepDelay) rather
    // than in a single tight loop, so the walk animates at a pace the
    // client's own movement interpolation actually expects instead of
    // either snapping to the destination or stuttering against it.
    private static void WalkToCounter(
        Mobile bot, PlayerVendor vendor, PathFollower follower, int stepsRemaining, TimeSpan stepDelay,
        PlayerBotBehavior previousBehavior
    )
    {
        if (bot?.Deleted != false || vendor?.Deleted != false || bot.Map != vendor.Map)
        {
            RestoreBehavior(bot, previousBehavior);
            return;
        }

        // Reasserted every step - a stray Warmode flip picked up mid-
        // approach (e.g. a passing red) shouldn't derail a friendly
        // shop-browsing detour.
        bot.Warmode = false;

        if (stepsRemaining <= 0)
        {
            // SP-037: "gracefully abort and resume prior journey" - ran
            // out of approach budget (a locked door, an unreachable
            // interior, ...) without actually reaching the counter, so
            // this skips the dwell entirely rather than standing around
            // wherever it got stuck.
            AbortApproach(bot, vendor, previousBehavior);
            return;
        }

        if (follower.Follow(1))
        {
            BeginDwell(bot, vendor, previousBehavior);
            return;
        }

        Timer.DelayCall(
            stepDelay, () => WalkToCounter(bot, vendor, follower, stepsRemaining - 1, stepDelay, previousBehavior)
        );
    }

    // SP-037: restores the bot's cached PlayerBotBehavior (a no-op for
    // any bot-like type that never had one to begin with, or if the bot
    // itself is gone). Since the original behavior instance is the exact
    // same object reference the whole time - never rebuilt, never reset -
    // its own internal destination/waypoint state is restored perfectly
    // intact, letting the bot's normal decision loop pick up exactly
    // where it left off on its very next tick.
    private static void RestoreBehavior(Mobile bot, PlayerBotBehavior previousBehavior)
    {
        if (previousBehavior != null && bot is PlayerBot playerBot && bot.Deleted == false)
        {
            playerBot.Behavior = previousBehavior;
        }
    }

    private static void AbortApproach(Mobile bot, PlayerVendor vendor, PlayerBotBehavior previousBehavior)
    {
        RestoreBehavior(bot, previousBehavior);

        if (bot?.Deleted == false)
        {
            // SP-037: re-assert from the actual end of this attempt - an
            // aborted approach still counts as "completed a detour" for
            // cooldown purposes, otherwise a bot stuck behind the same
            // locked door would retry it every single tick this class
            // runs instead of waiting the full 30 minutes.
            _lastBrowsed[bot.Serial] = DateTime.UtcNow;
        }

        if (VerboseConfig.Pathfinding)
        {
            logger.Information(
                "[BotBrowsing] Bot {Name} could not reach {VendorName}'s counter in time - aborting detour",
                bot?.Name, vendor?.Name
            );
        }
    }

    private static void BeginDwell(Mobile bot, PlayerVendor vendor, PlayerBotBehavior previousBehavior)
    {
        if (bot?.Deleted != false)
        {
            RestoreBehavior(bot, previousBehavior);
            return;
        }

        bot.Direction = bot.GetDirectionTo(vendor);
        bot.Warmode = false;

        var dwellSeconds = Utility.RandomMinMax(DwellMinSeconds, DwellMaxSeconds);
        ReassertFacing(bot, vendor, dwellSeconds, previousBehavior);
    }

    // Re-faces the vendor once a second for the rest of the dwell. Purely
    // cosmetic now that the bot's own AI is genuinely paused (Behavior
    // swapped to the silent BrowsingBehavior, which never ticks) rather
    // than just left un-frozen - nothing could spin the facing back on
    // its own mid-dwell any more - but kept as-is since a single facing
    // set at BeginDwell plus a steady re-assert reads identically either
    // way and there's no reason to remove a harmless belt-and-suspenders.
    private static void ReassertFacing(
        Mobile bot, PlayerVendor vendor, int secondsRemaining, PlayerBotBehavior previousBehavior
    )
    {
        if (bot?.Deleted != false)
        {
            RestoreBehavior(bot, previousBehavior);
            return;
        }

        if (secondsRemaining <= 0)
        {
            CompleteBrowsing(bot, vendor, previousBehavior);
            return;
        }

        bot.Direction = bot.GetDirectionTo(vendor);

        Timer.DelayCall(
            FaceReassertInterval, () => ReassertFacing(bot, vendor, secondsRemaining - 1, previousBehavior)
        );
    }

    // SP-037: the real completion point - restores the bot's cached AI
    // state (letting its own decision loop walk it back out of the shop
    // and resume its interrupted journey on the very next tick) and
    // re-asserts the 30-minute cooldown from NOW, not from when the
    // detour started.
    private static void CompleteBrowsing(Mobile bot, PlayerVendor vendor, PlayerBotBehavior previousBehavior)
    {
        RestoreBehavior(bot, previousBehavior);

        if (bot?.Deleted == false)
        {
            _lastBrowsed[bot.Serial] = DateTime.UtcNow;
        }

        if (VerboseConfig.Pathfinding)
        {
            logger.Information("[BotBrowsing] Bot {Name} completed browsing, resuming path", bot?.Name);
        }
    }
}
