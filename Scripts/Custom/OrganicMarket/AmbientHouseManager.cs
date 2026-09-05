// =========================================================================
// AmbientHouseManager.cs — SP-042: keeps roughly 10% of non-vendor ambient
// houses occupied by a borrowed PlayerBot at any given time (the "Ambient
// Homeowner Bot Rotation"), plus persistent resident binding and the
// [sendhome / [leavehome GM commands.
//
// [leavehome is deliberately its own command rather than an overload of
// [sendhome's targeting flow: [sendhome always means "put a resident
// here," and folding an opposite "take the resident away" meaning onto
// the same command based on subtle targeting differences would make both
// harder to use correctly under pressure. It shares SendHome's own
// eviction one-liner (bot.Behavior = RedTerritory.TravelBrain(bot)) —
// the Behavior setter's OnDetached call already does everything a clean
// checkout needs (StopWalk, ReleaseOccupant revoking house.Friends) — so
// this command is only new targeting/messaging on top of existing,
// already-verified cleanup.
//
// Houses to draw from come straight out of MerchantGuildAuthority — it
// already tracks every ambient house this subsystem places (vendor and
// filler alike), and a purchased house is automatically deregistered from
// it, so filtering ArchetypeAt(i) == OrganicMarketSpawner.
// AmbientResidenceArchetype naturally excludes both vendor shops and
// anything a real player already owns. No new house-tracking structure
// was needed.
//
// Zero mobile bloat: no new mobiles are ever constructed here. A "resident"
// is an existing PlayerBot borrowed from the live population by swapping
// its Behavior to HomeownerBehavior (Scripts/Custom/PlayerBot/
// HomeownerBehavior.cs) for the dwell window, then handed back
// (RedTerritory.TravelBrain) exactly the way every other timed "visit"
// behavior in this codebase already works (BankSitterBehavior,
// AdventurerBehavior, etc. — see PlayerBotBehavior.VisitExpiresAt/
// CheckVisitExpired).
//
// Persistence note: bindings and per-house cooldowns below are tracked in
// a plain in-memory static dictionary, deliberately NOT serialized.
// PlayerBots themselves are purged and respawned fresh on every server
// boot (BotStartupManager) — a "persistent" bot Serial would go stale on
// the very next restart regardless, so there is nothing real to gain by
// persisting this bookkeeping and a real cost (a whole new persistent
// authority Mobile) to adding it. A binding lasts for the current boot
// session; [sendhome re-establishes it after a restart in one click.
// =========================================================================

using System;
using System.Collections.Generic;
using Server.Commands;
using Server.CustomBots;
using Server.Multis;
using Server.Targeting;

namespace Server.Engines.OrganicMarket;

public static class AmbientHouseManager
{
    // Target fraction of non-vendor ambient houses occupied at any time.
    private const double OccupancyTarget = 0.10;

    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(90);

    private sealed class HouseState
    {
        public Serial? OccupantSerial;
        public Serial? BoundSerial;
        public DateTime CooldownUntil = DateTime.MinValue;
    }

    private static readonly Dictionary<BaseHouse, HouseState> _states = new();

    private static HouseState GetOrCreateState(BaseHouse house)
    {
        if (!_states.TryGetValue(house, out var state))
        {
            _states[house] = state = new HouseState();
        }

        return state;
    }

    public static void Configure()
    {
        CommandSystem.Register("sendhome", AccessLevel.GameMaster, OnSendHome);
        CommandSystem.Register("leavehome", AccessLevel.GameMaster, OnLeaveHome);
    }

    public static void Initialize()
    {
        Timer.DelayCall(MaintenanceInterval, MaintenanceInterval, MaintainOccupancy);
    }

    [Usage("sendhome")]
    [Description("Target an ambient house sign to send its resident home (auto-assigning one if unbound), or target a PlayerBot then a house sign to bind that bot as the house's permanent resident.")]
    private static void OnSendHome(CommandEventArgs e)
    {
        var from = e.Mobile;
        from?.SendMessage("Target a PlayerBot to bind as resident, or target an ambient house sign directly to send its resident home.");
        from.Target = new SendHomeTarget(null);
    }

    private class SendHomeTarget : Target
    {
        private readonly PlayerBot _boundBot;

        public SendHomeTarget(PlayerBot boundBot) : base(-1, false, TargetFlags.None) => _boundBot = boundBot;

        protected override void OnTarget(Mobile from, object o)
        {
            if (_boundBot == null && o is PlayerBot bot)
            {
                from.SendMessage($"{bot.Name} selected — now target the house sign to bind it as resident.");
                from.Target = new SendHomeTarget(bot);
                return;
            }

            if (o is AmbientHouseSign sign)
            {
                var house = sign.Owner;
                if (house?.Deleted != false)
                {
                    from.SendMessage("That sign has no live house attached.");
                    return;
                }

                if (_boundBot != null)
                {
                    BindResident(house, _boundBot);
                    SendHome(house, _boundBot);
                    var landing = _boundBot.Location;
                    from.SendMessage(0x35, $"[SendHome] Dispatched {_boundBot.Name} to interior ({landing.X}, {landing.Y}, {landing.Z}).");
                    return;
                }

                var resident = GetOrAssignResident(house);
                if (resident == null)
                {
                    from.SendMessage(0x22, "[SendHome] No eligible idle/traveling/bank-sitting PlayerBot is available to borrow right now — sign was not modified.");
                    return;
                }

                SendHome(house, resident);
                var residentLanding = resident.Location;
                from.SendMessage(0x35, $"[SendHome] Dispatched {resident.Name} to interior ({residentLanding.X}, {residentLanding.Y}, {residentLanding.Z}).");
                return;
            }

            from.SendMessage("That is not a PlayerBot or an ambient house sign.");
        }
    }

    [Usage("leavehome")]
    [Description("Target an active homeowner PlayerBot (or the sign of the house it's currently resident in) to immediately end its dwell, revoke house.Friends, and send it back to TravelerBehavior.")]
    private static void OnLeaveHome(CommandEventArgs e)
    {
        var from = e.Mobile;
        from?.SendMessage("Target an active homeowner PlayerBot, or the sign of the house it's residing in.");
        from.Target = new LeaveHomeTarget();
    }

    private class LeaveHomeTarget : Target
    {
        public LeaveHomeTarget() : base(-1, false, TargetFlags.None)
        {
        }

        protected override void OnTarget(Mobile from, object o)
        {
            var bot = o switch
            {
                PlayerBot b => b,
                AmbientHouseSign sign => GetActiveResident(sign.Owner),
                _ => null
            };

            if (bot == null)
            {
                from.SendMessage(
                    o is AmbientHouseSign
                        ? "That house has no active resident to check out."
                        : "That is not a PlayerBot or an ambient house sign."
                );
                return;
            }

            if (bot.Behavior is not HomeownerBehavior homeowner)
            {
                from.SendMessage(0x22, $"{bot.Name} is not currently an active homeowner.");
                return;
            }

            var label = DescribeHouse(homeowner.House);

            // The Behavior setter's own OnDetached call already does the
            // rest: StopWalk() cancels any in-progress domestic walk, and
            // AmbientHouseManager.ReleaseOccupant clears the occupant slot
            // and removes the bot from house.Friends — the exact same
            // path SendHome's own eviction branch already relies on.
            bot.Behavior = RedTerritory.TravelBrain(bot);

            from.SendMessage(0x35, $"[LeaveHome] Checked out {bot.Name} from {label}; returned to Traveler.");
        }
    }

    // The house's own display name if a GM ever set one, else its real
    // house-style type name (SandStonePatio, TwoStoryVilla, ...) — shared
    // by [leavehome's GM feedback and HomeownerBehavior's own natural-
    // dwell-expiry departure log, so both describe a house the same way.
    public static string DescribeHouse(BaseHouse house) =>
        house?.Deleted != false
            ? "an unknown house"
            : !string.IsNullOrEmpty(house.Name) ? house.Name : house.GetType().Name;

    private static PlayerBot GetActiveResident(BaseHouse house)
    {
        if (house?.Deleted != false || !_states.TryGetValue(house, out var state) ||
            state.OccupantSerial is not { } serial)
        {
            return null;
        }

        return World.Mobiles.TryGetValue(serial, out var mobile) && mobile is PlayerBot bot && !bot.Deleted
            ? bot
            : null;
    }

    // ---- Public API used by [sendhome ----

    public static void BindResident(BaseHouse house, PlayerBot bot)
    {
        if (house?.Deleted != false || bot?.Deleted != false)
        {
            return;
        }

        var state = GetOrCreateState(house);
        state.BoundSerial = bot.Serial;

        if (house.Sign is AmbientHouseSign sign)
        {
            sign.Name = $"Home of {bot.Name}";
        }
    }

    public static PlayerBot GetOrAssignResident(BaseHouse house)
    {
        if (house?.Deleted != false)
        {
            return null;
        }

        var bound = TryGetBoundEligibleBot(house);
        if (bound != null)
        {
            return bound;
        }

        var candidate = FindRandomEligibleBot();
        if (candidate != null)
        {
            BindResident(house, candidate);
        }

        return candidate;
    }

    public static void SendHome(BaseHouse house, PlayerBot bot)
    {
        if (house?.Deleted != false || bot?.Deleted != false)
        {
            return;
        }

        var state = GetOrCreateState(house);

        // Evict whoever else is currently there, if anyone.
        if (state.OccupantSerial is { } occupantSerial && occupantSerial != bot.Serial &&
            World.Mobiles.TryGetValue(occupantSerial, out var occupantMobile) &&
            occupantMobile is PlayerBot occupant && occupant.Behavior is HomeownerBehavior)
        {
            occupant.Behavior = RedTerritory.TravelBrain(occupant);
        }

        state.CooldownUntil = DateTime.MinValue;
        BeginVisit(house, bot, state);
    }

    // Called by HomeownerBehavior.OnDetached when a dwell cycle ends.
    public static void ReleaseOccupant(BaseHouse house, PlayerBot bot)
    {
        if (house == null || !_states.TryGetValue(house, out var state))
        {
            return;
        }

        if (state.OccupantSerial == bot.Serial)
        {
            state.OccupantSerial = null;
            state.CooldownUntil = Core.Now + TimeSpan.FromMinutes(Utility.RandomMinMax(10, 20));

            // The Friends grant in BeginVisit was for the duration of this
            // stay only — pull it back out so the access list doesn't
            // accumulate every bot ever borrowed for this house.
            house.Friends.Remove(bot);
        }
    }

    // ---- Periodic rotation ----

    private static void MaintainOccupancy()
    {
        var authority = MerchantGuildAuthority.Instance;
        if (authority == null)
        {
            return;
        }

        var nonVendorHouses = new List<BaseHouse>();
        for (var i = 0; i < authority.Count; i++)
        {
            if (authority.ArchetypeAt(i) != OrganicMarketSpawner.AmbientResidenceArchetype)
            {
                continue;
            }

            var house = authority.HouseAt(i);
            if (house?.Deleted == false)
            {
                nonVendorHouses.Add(house);
            }
        }

        if (nonVendorHouses.Count == 0)
        {
            return;
        }

        var targetOccupied = Math.Max(1, (int)Math.Round(nonVendorHouses.Count * OccupancyTarget));
        var currentlyOccupied = 0;
        var eligibleHouses = new List<BaseHouse>();

        foreach (var house in nonVendorHouses)
        {
            var state = GetOrCreateState(house);
            if (state.OccupantSerial.HasValue)
            {
                currentlyOccupied++;
                continue;
            }

            if (Core.Now < state.CooldownUntil)
            {
                continue;
            }

            eligibleHouses.Add(house);
        }

        var toStart = targetOccupied - currentlyOccupied;

        while (toStart > 0 && eligibleHouses.Count > 0)
        {
            var idx = Utility.Random(eligibleHouses.Count);
            var house = eligibleHouses[idx];
            eligibleHouses.RemoveAt(idx);

            // Rotation prefers an existing bound resident if one is
            // available, but never auto-creates a new binding on its own —
            // that only happens through [sendhome's sign-only path. This
            // keeps most cycles a genuine rotation of different bots while
            // still honoring a GM's explicit choice of resident.
            var bot = TryGetBoundEligibleBot(house) ?? FindRandomEligibleBot();
            if (bot == null)
            {
                continue;
            }

            BeginVisit(house, bot, GetOrCreateState(house));
            toStart--;
        }
    }

    private static void BeginVisit(BaseHouse house, PlayerBot bot, HouseState state)
    {
        var dwell = TimeSpan.FromMinutes(Utility.RandomMinMax(15, 45));

        // [sendhome is a diagnostic GM override — it must win outright over
        // whatever the bot was doing (fighting, following a target, etc.)
        // rather than politely wait for that state to resolve on its own.
        // The Behavior setter itself already tears down the previous
        // behavior via OnDetached (see PlayerBot.Behavior), so clearing
        // combat state here is the only extra interrupt actually needed.
        bot.Combatant = null;
        bot.Warmode = false;

        // A borrowed bot can arrive carrying a Criminal flag (with its own
        // 2-minute expiry timer) or nonzero Kills from whatever it was
        // doing a moment ago under its previous behavior — that renders it
        // gray, which looks wrong for a "resident" standing in its own
        // house. Kills' setter already calls Delta(Noto) when murderer
        // status flips; the explicit call below covers the Criminal-only
        // case, where nothing else would otherwise refresh the nameplate.
        bot.Criminal = false;
        bot.Kills = 0;
        bot.Delta(MobileDelta.Noto);

        // Grant real resident access so doors/locks treat the bot as
        // belonging there rather than as a trespasser.
        if (!house.Friends.Contains(bot))
        {
            house.Friends.Add(bot);
        }

        var homeowner = new HomeownerBehavior();
        homeowner.Setup(house, dwell);
        bot.Behavior = homeowner;

        state.OccupantSerial = bot.Serial;
    }

    private static PlayerBot TryGetBoundEligibleBot(BaseHouse house)
    {
        if (!_states.TryGetValue(house, out var state) || state.BoundSerial is not { } serial)
        {
            return null;
        }

        return World.Mobiles.TryGetValue(serial, out var mobile) &&
               mobile is PlayerBot bot && !bot.Deleted && IsEligible(bot)
            ? bot
            : null;
    }

    private static bool IsEligible(PlayerBot bot) =>
        bot.Map != Map.Internal &&
        bot.Behavior is not HomeownerBehavior &&
        (bot.Behavior is IdleBehavior or TravelerBehavior or BankSitterBehavior);

    // Reservoir-samples one eligible bot in a single pass over
    // World.Mobiles — the same snapshot-and-filter idiom used throughout
    // CustomBots/ for infrequent bot queries (BotWhereCommand,
    // BotSessionManager, etc.), never called from a per-tick hot path
    // (only the ~90s maintenance timer or a GM command).
    private static PlayerBot FindRandomEligibleBot()
    {
        PlayerBot chosen = null;
        var seen = 0;

        foreach (var m in World.Mobiles.Values)
        {
            if (m is not PlayerBot bot || bot.Deleted || !IsEligible(bot))
            {
                continue;
            }

            seen++;
            if (Utility.Random(seen) == 0)
            {
                chosen = bot;
            }
        }

        return chosen;
    }
}
