// =========================================================================
// ResidentBotSanctuary.cs — SP-051: stops wild (uncontrolled, unsummoned)
// creatures from ever engaging a resident PlayerBot in combat while that
// bot is standing inside a house.
//
// Hook chosen after tracing the real engagement path end-to-end (PlayerBot
// extends PlayerMobile, not BaseCreature - it has no AI/target-acquisition
// machinery of its own, and PlayerBot isn't declared partial, so it can't
// be reopened from Scripts/Custom to override CanBeHarmful/OnDamage
// directly):
//
//   BaseAI's per-tick target SCAN (AcquireNewFocusMob -> IsInvalidTarget)
//   does NOT consult CanBeHarmful at all for ordinary FightMode.Closest/
//   Weakest wild creatures - only the eventual `Mobile.Combatant = focus;`
//   assignment does, via the Combatant property setter's own
//   `!CanBeHarmful(value, false)` guard (Mobile.cs), which every AI
//   subclass (MeleeAI, ArcherAI, AnimalAI, PredatorAI, MageAI, ...) commits
//   a target through identically. A wild creature's FocusMob can still
//   flicker onto a sanctuaried bot on scan (cheap, invisible, harmless),
//   but it can never actually become Combatant, swing, or cast at it -
//   confirmed against BaseCreature's own existing CanBeHarmful override,
//   which already blocks monsters from engaging PlayerVendor/TownCrier
//   through this exact same chain.
//
//   Mobile.CanBeHarmful -> Region.AllowHarmful bubbles up the ATTACKER's
//   own region parent chain, only falling back to the static
//   Mobile.AllowHarmfulHandler delegate once it runs out of parents - but
//   that handler itself receives BOTH mobiles, so the sanctuary check below
//   reads the TARGET's region (bot.Region.IsPartOf<HouseRegion>()), not the
//   attacker's - exactly like the existing core precedent this chains onto
//   (Misc/Notoriety.cs's Mobile_AllowHarmful already checks
//   target.Region.IsPartOf<SafeZone>() the same way, so a ranged/casting
//   wild creature standing just outside the house doorway is still
//   correctly blocked from engaging a bot standing just inside it).
//
// AllowHarmfulHandler is a plain settable property, not a multicast event -
// overwriting it outright would silently discard Notoriety's own SafeZone/
// PvP enforcement (Misc/Notoriety.cs's NotorietyHandlers.Initialize sets
// it first). This captures whatever is already installed and chains to it,
// but does so from EventSink.ServerStarted rather than this file's own
// Initialize() - AssemblyHandler.Invoke("Initialize") runs every
// Scripts/Custom Initialize() and every core Initialize() (including
// Notoriety's) via one reflection scan with no guaranteed cross-type
// ordering, whereas Main.cs fires EventSink.ServerStarted only once that
// entire scan has already completed, guaranteeing Notoriety's handler is
// already in place to chain onto.
//
// Scope: only PlayerBot targets are ever affected (real players fleeing
// into a house, and every other Mobile type, fall straight through to
// whatever the previous handler already decided). Only wild creatures
// (!Controlled && !Summoned - the same idiom BaseCreature's own
// ShouldAcquireOnApproach already uses for "wild") are blocked; a player's
// tamed pet or a summoned creature can still engage a resident bot, which
// is deliberate - those are exactly the sources ResidentBotWakeUp (see
// HomeownerBehavior.cs's own Tick) exists to catch reactively instead.
// PlayerVendor is a distinct class from PlayerBot and is untouched here -
// BaseCreature.CanBeHarmful already blesses it separately in core.
// =========================================================================

using Server.Mobiles;
using Server.Regions;

namespace Server.CustomBots;

public static class ResidentBotSanctuary
{
    public static void Initialize()
    {
        EventSink.ServerStarted += OnServerStarted;
    }

    private static void OnServerStarted()
    {
        var previous = Mobile.AllowHarmfulHandler;
        Mobile.AllowHarmfulHandler = (from, target) =>
            AllowHarmful(from, target) && (previous?.Invoke(from, target) ?? true);
    }

    private static bool AllowHarmful(Mobile from, Mobile target)
    {
        if (target is not PlayerBot || from is not BaseCreature creature)
        {
            return true;
        }

        if (creature.Controlled || creature.Summoned)
        {
            return true;
        }

        return !target.Region.IsPartOf<HouseRegion>();
    }
}
