// =========================================================================
// HomeownerBehavior.cs — SP-042: the "brain" a borrowed PlayerBot runs
// while dwelling in an ambient house (see Scripts/Custom/OrganicMarket/
// AmbientHouseManager.cs, which is what assigns/removes this behavior —
// nothing in this file spawns, deletes, or otherwise creates a bot).
//
// Modeled directly on the established "timed destination visit" pattern
// every other visit-style behavior in this codebase already uses
// (PlayerBotBehavior.VisitExpiresAt/CheckVisitExpired, documented in that
// base class's own header comment): Setup() records how long the dwell
// lasts, Tick() calls CheckVisitExpired() first, and when the window
// closes the base class itself swaps the bot back to a fresh Traveler
// (RedTerritory.TravelBrain) and returns true — this file never has to
// implement "go back to the world scheduler" itself.
//
// Domestic movement is a real pathfinding walk (Server.PathFollower, the
// same core class TravelerBehavior drives via its own _stepTimer/StepOnce
// pattern — mirrored here at a much smaller scale), not a teleport. The
// only teleport in this file is the initial dispatch in OnAttached; every
// domestic relocation after that steps there tile by tile, and passes
// through PlayerBot.Move -> DoorHelper.TryOpenAhead exactly the way a
// real player would, so an interior door still opens automatically.
//
// This class is declared in namespace Server.CustomBots (matching every
// other PlayerBotBehavior, e.g. WanderBehavior/BankSitterBehavior) even
// though the file lives under Scripts/Custom/PlayerBot/ — that's a
// deploy-location choice per the ticket's folder restriction, not a
// change to which namespace behaviors actually live in.
//
// Note: this behavior is intentionally NOT registered in the core
// BehaviorRegistry (that file lives outside Scripts/Custom/ and is out of
// this ticket's scope) — so its SerializableName won't round-trip through
// a world save/load. That's an acceptable gap: BotStartupManager purges
// and respawns the entire PlayerBot population on every normal boot
// already, so no bot's Behavior is expected to survive a restart
// regardless of this behavior's existence.
// =========================================================================

using System;
using Server;
using Server.Engines.OrganicMarket;
using Server.Multis;
using MoveDelays = Server.Movement.Movement;

namespace Server.CustomBots
{
    public class HomeownerBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Homeowner";

        // Read-only so external code (AmbientHouseManager's [leavehome
        // handler, GM diagnostics) can identify which house a bot is
        // currently resident in without this class exposing a setter
        // anyone outside Setup() could use to swap it out from under a
        // live visit.
        public BaseHouse House => _house;

        // Rendered as a real emote, not spoken chat — PlayerBotBehavior's
        // internal SpeakLine treats any line starting with '*' as
        // Mobile.Emote rather than Say.
        private static readonly string[] FlavorLines =
        {
            "*arranges scrolls on the shelf*",
            "*stirs the hearth*",
            "*dusts the mantle*",
            "*straightens a chair*",
            "*hums quietly while tidying up*",
            "*inspects the bookshelf*",
            "*polishes the table*"
        };

        private const int BowAnimation = 32;

        private static readonly TimeSpan MinActionInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MaxActionInterval = TimeSpan.FromSeconds(60);

        // How long a single domestic walk is allowed to keep stepping
        // before giving up in place — guards against an unreachable target
        // (e.g. the last-resort fallback point in FindInteriorSpot, which
        // is not IsInside/floor-validated) spinning _stepTimer forever.
        private static readonly TimeSpan WalkTimeout = TimeSpan.FromSeconds(20);

        private BaseHouse _house;
        private DateTime _nextActionAt;
        private PathFollower _follower;
        private Timer _stepTimer;
        private DateTime _walkDeadline;

        // Called BEFORE this instance is assigned to bot.Behavior (see
        // AmbientHouseManager.BeginVisit), so OnAttached below always sees
        // a fully-initialized house/dwell window.
        public void Setup(BaseHouse house, TimeSpan dwellDuration)
        {
            _house = house;
            VisitExpiresAt = Core.Now + dwellDuration;
            _nextActionAt = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax((int)MinActionInterval.TotalSeconds, (int)MaxActionInterval.TotalSeconds));
        }

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);

            if (_house?.Deleted != false)
            {
                return;
            }

            // FindInteriorSpot is guaranteed to return a usable point (worst
            // case, the house's own origin) — relocation must never be
            // silently skipped the way the old TryXxx pattern allowed.
            bot.MoveToWorld(FindInteriorSpot(), _house.Map);
            bot.ProcessDelta();
        }

        public override void OnDetached(PlayerBot bot)
        {
            StopWalk();
            AmbientHouseManager.ReleaseOccupant(_house, bot);
            base.OnDetached(bot);
        }

        public override void Tick(PlayerBot bot)
        {
            // Logged BEFORE CheckVisitExpired runs the actual transition,
            // since that call (base class, PlayerBotBehavior) both swaps
            // bot.Behavior to a fresh Traveler AND — via the Behavior
            // setter's OnDetached call — already runs StopWalk() and
            // AmbientHouseManager.ReleaseOccupant(_house, bot) (which
            // revokes house.Friends membership) by the time it returns.
            // This is the natural "dwell ran out" departure, distinct
            // from a GM-forced [leavehome checkout (AmbientHouseManager
            // logs that case itself, with its own [LeaveHome] message).
            if (VisitExpiresAt.HasValue && Core.Now >= VisitExpiresAt.Value)
            {
                Console.WriteLine(
                    $"[Homeowner] {bot.Name} completed dwell at " +
                    $"{AmbientHouseManager.DescribeHouse(_house)} and returned to TravelerBehavior.");
            }

            // Must run first — if the dwell window has closed, this
            // already swapped bot.Behavior back to a Traveler and we must
            // not touch any more state.
            if (CheckVisitExpired(bot))
            {
                return;
            }

            if (_house?.Deleted != false)
            {
                // House vanished (wiped or purchased) out from under an
                // active visit — hand the bot back immediately rather
                // than let it idle in a house that no longer exists.
                bot.Behavior = RedTerritory.TravelBrain(bot);
                return;
            }

            // Hard boundary check: if the bot is ever found outside the
            // house (a shove, a mid-visit house relocation, etc.), pull it
            // straight back instead of letting it wander loose. This is a
            // correction for an external interference, not a domestic
            // idle action, so it stays a teleport — any in-progress
            // domestic walk's cached path is now stale regardless and
            // must not keep stepping against the corrected position.
            if (!_house.IsInside(bot))
            {
                StopWalk();
                bot.Location = FindInteriorSpot();
            }

            if (Core.Now < _nextActionAt)
            {
                return;
            }

            _nextActionAt = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax((int)MinActionInterval.TotalSeconds, (int)MaxActionInterval.TotalSeconds));
            DoDomesticAction(bot);
        }

        private void DoDomesticAction(PlayerBot bot)
        {
            var spot = FindInteriorSpot();

            // Already there (or the room is too small to offer a second
            // point) — just play the flavor action in place rather than
            // starting a zero-length "walk."
            if (spot == bot.Location)
            {
                bot.Animate(BowAnimation, 5, 1, true, false, 0);
                TrySpeakLine(bot, FlavorLines[Utility.Random(FlavorLines.Length)]);
                return;
            }

            BeginWalk(bot, spot);
        }

        // ---- Domestic walk state machine ----
        //
        // A lightweight version of the same PathFollower + repeating-Timer
        // pattern TravelerBehavior's own _stepTimer/StepOnce uses for real
        // point-to-point walking: BehaviorTickManager only calls Tick()
        // once every 2 seconds (far too coarse to look like walking), so
        // this drives its own timer at the engine's real walk-step cadence
        // (Movement.WalkFootDelay) independent of the outer Tick.

        private void BeginWalk(PlayerBot bot, Point3D target)
        {
            StopWalk();

            _follower = new PathFollower(bot, target);
            _walkDeadline = Core.Now + WalkTimeout;

            // A mounted homeowner (any bot that happens to own/ride a
            // mount) covers ground roughly twice as fast as one on foot —
            // stepping it at the flat foot delay made every mounted bot
            // crawl indoors at half its real pace. Decided once here, not
            // re-checked mid-walk: nothing in this behavior mounts or
            // dismounts a bot, so it cannot change during a single walk.
            var delayMs = bot.Mounted ? MoveDelays.WalkMountDelay : MoveDelays.WalkFootDelay;
            var interval = TimeSpan.FromMilliseconds(delayMs);
            _stepTimer = Timer.DelayCall(interval, interval, () => StepWalk(bot));
        }

        private void StepWalk(PlayerBot bot)
        {
            if (bot.Deleted || !bot.Alive || bot.Map == null || bot.Map == Map.Internal ||
                _house?.Deleted != false || _follower == null)
            {
                StopWalk();
                return;
            }

            bool arrived;
            try
            {
                arrived = _follower.Follow(0);
            }
            catch (Exception)
            {
                // A failed/impossible path must not wedge the timer —
                // give up in place and let the next domestic cycle retry
                // with a fresh target.
                StopWalk();
                return;
            }

            if (arrived)
            {
                StopWalk();
                bot.Animate(BowAnimation, 5, 1, true, false, 0);
                TrySpeakLine(bot, FlavorLines[Utility.Random(FlavorLines.Length)]);
                return;
            }

            if (Core.Now >= _walkDeadline)
            {
                StopWalk();
            }
        }

        private void StopWalk()
        {
            _stepTimer?.Stop();
            _stepTimer = null;
            _follower = null;
        }

        // How close a domestic candidate can sit to a door or the house
        // sign — same 1-tile clearance InteriorTileFinder.TryFindVendorSpots
        // already uses to keep a vendor off the threshold.
        private const int DoorClearance = 1;
        private const int SignClearance = 1;

        // Random-samples the house's own Area rectangles for a genuinely
        // walkable ground-floor interior point.
        //
        // Porch/front-step fix: only area[0] is ever sampled. Surveying
        // every classic house's own AreaArray (Multis/Houses/Houses.cs)
        // shows a completely consistent shape: index 0 is always the
        // house's real enclosed room (its full bounding footprint), and
        // every additional rectangle is a 1-tile-deep exterior strip - a
        // patio, porch, or balcony sitting just outside it (e.g.
        // SandStonePatio's own AreaArray is exactly
        // [(-5,-4,12,8) main room, (-2,4,3,1) the 1-tile-deep patio]).
        // Sampling every rectangle equally, as this method used to, gave
        // the exterior patio the same odds as the real room - which is
        // exactly the reported "loitering on the front steps" bug. The
        // door/sign clearance check below additionally keeps a candidate
        // off the threshold tile itself, which sits on the edge of
        // area[0] for most styles.
        //
        // Root-cause fix (the actual Z=0-on-every-house bug): BaseHouse.Area
        // rectangles are relative to the house's own placement, NOT world
        // coordinates — confirmed against the engine's own canonical
        // conversion in Server.Regions.HouseRegion.GetArea, which builds
        // its region rectangles as `house.X + rect.X, house.Y + rect.Y`.
        // Every previous version of this method used rect.X/rect.Y
        // directly as world coordinates, so every sampled candidate was
        // offset by (-house.X, -house.Y) from the real house — nowhere
        // near it in world space for any house not sitting at the map
        // origin. Every attempt failed, every fallback failed the same
        // way, and the method bottomed out at the house's own raw
        // placement point every single time. If that placement's own Z
        // happens to be 0 (as it evidently is for these ambient/seeded
        // houses), the bot lands at Z=0 on every house, which is exactly
        // what was observed. A previous attempt at this fix (manually
        // re-scanning Z via Map.CanFit) never had a chance to run
        // correctly, because it was still being fed those same bogus
        // (x, y) columns.
        //
        // Now reuses InteriorTileFinder.IsGroundFloorInterior — the same
        // engine-native Map.CanSpawnMobile-based surface lookup the
        // vendor-placement system (TryFindVendorSpots) already relies on
        // and has proven correct — instead of re-deriving floor detection
        // by hand a third time.
        private Point3D FindInteriorSpot()
        {
            if (_house?.Deleted != false)
            {
                return Point3D.Zero;
            }

            var map = _house.Map;
            var area = _house.Area;
            if (map != null && map != Map.Internal && area is { Length: > 0 })
            {
                var rect = area[0];
                if (rect.Width > 0 && rect.Height > 0)
                {
                    for (var attempt = 0; attempt < 40; attempt++)
                    {
                        var x = _house.X + rect.X + Utility.Random(rect.Width);
                        var y = _house.Y + rect.Y + Utility.Random(rect.Height);

                        if (InteriorTileFinder.IsNearDoor(_house, x, y, DoorClearance) ||
                            InteriorTileFinder.IsNearSign(_house, x, y, SignClearance))
                        {
                            continue;
                        }

                        if (InteriorTileFinder.IsGroundFloorInterior(_house, map, x, y, out var candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }

            if (_house.BanLocation != Point3D.Zero && _house.IsInside(_house.BanLocation, 16))
            {
                return _house.BanLocation;
            }

            // Unconditional last resort — not IsInside/floor-validated,
            // but guarantees the bot is at least dropped at the house's
            // own placement point rather than left wherever it was before
            // [sendhome was used.
            return new Point3D(_house.X, _house.Y, _house.Z);
        }
    }
}
