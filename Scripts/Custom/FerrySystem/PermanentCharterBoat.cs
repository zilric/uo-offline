// =========================================================================
// PermanentCharterBoat.cs — SP-044: fixes the actual root cause of every
// "vanishing boat" / "captain standing in the sea" symptom reported since
// SP-039.
//
// Server.Items.TillerMan.OnAfterDelete() reads:
//     public override void OnAfterDelete() => _boat?.Delete();
// Every earlier boat class in this system (AmbientFishingSkiff, then
// PermanentCharterBoat) called `TillerMan?.Delete(); TillerMan = null;`
// in its constructor to get rid of the vanilla crew mobile — which
// instantly cascaded into deleting the BOAT ITSELF, before
// FerryFleetSeeder even got to call MoveToWorld on it. The boat was gone
// before it was ever placed; only the separately-placed CharterCaptain
// (a plain Mobile, unaware the boat under it no longer existed) actually
// showed up in the world, standing wherever BoatLocation happened to be —
// open water at most stops, which is exactly the "captain submerged at
// Dagger Isle" symptom.
//
// Fix: never delete or null TillerMan. Let BaseBoat construct it
// normally, then repurpose it in place — rename it, keep it locked out of
// player commands the same way the whole boat already is
// (HandlesOnSpeech => false on the boat covers TillerMan too; it has no
// OnSpeech of its own to worry about), and let it stand there as
// permanent scenery. It is an Item, not a Mobile — it has no combat/HP
// concept to make "invulnerable", and it is already exempt from decay by
// its own constructor (Movable = false, matching Item.Decays' definition
// below).
//
// Other lockdown, unchanged from earlier sprints:
//   - Anchored = true — BaseBoat's own movement commands all refuse to
//     run while anchored.
//   - HandlesOnSpeech = false — the boat-command speech parser (name/
//     anchor/forward/etc.) never runs for this subclass at all.
//   - No keys are ever created (CreateKeys is never called), so there is
//     nothing to hijack, drydock, or leave behind.
//   - Both planks are unlocked (Plank.Locked = false) so any player can
//     double-click one from the dock/shore to open it and step aboard —
//     no key needed.
//   - Owner is assigned to FerrySystemAuthority.Instance (this system's
//     own persistent singleton) rather than left null, and Decays is
//     force-overridden to false — Item.Decays is a computed getter
//     (Movable && Visible && Spawner == null), not a settable field, so
//     Movable = false already makes it false structurally; overriding it
//     directly is the literal, unambiguous form of "this never decays."
// =========================================================================

using ModernUO.Serialization;
using Server.Engines.FerrySystem;

namespace Server.Multis;

[SerializationGenerator(0, false)]
public partial class PermanentCharterBoat : SmallBoat
{
    [Constructible]
    public PermanentCharterBoat()
    {
        Movable = false;
        Anchored = true;
        Owner = FerrySystemAuthority.Instance;

        if (TillerMan != null)
        {
            TillerMan.Name = "Charter Purser";
        }

        if (PPlank != null)
        {
            PPlank.Locked = false;
        }

        if (SPlank != null)
        {
            SPlank.Locked = false;
        }

        Refresh();
    }

    public override bool HandlesOnSpeech => false;

    public override bool Decays => false;

    public void KeepFresh() => Refresh();
}
