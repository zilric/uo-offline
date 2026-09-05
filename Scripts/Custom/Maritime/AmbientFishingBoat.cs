// =========================================================================
// AmbientFishingBoat.cs — SP-048: the two permanently-moored ambient
// fishing hull types (SmallBoat / SmallDragonBoat, for visual variety
// across the fleet), plus their shared lockdown logic.
//
// CRITICAL: never delete or null out TillerMan. Server.Items.TillerMan.
// OnAfterDelete() reads `_boat?.Delete();` — deleting the tiller cascades
// into deleting the boat itself, which was the actual root cause behind
// every "vanishing boat" symptom in the charter fleet (see
// FerrySystem/CharterCaptain.cs and PermanentCharterBoat.cs history).
// AmbientBoatLockdown below repurposes TillerMan in place (renamed,
// otherwise untouched) rather than removing it, exactly like
// PermanentCharterBoat now does.
// =========================================================================

using ModernUO.Serialization;
using Server.Engines.Maritime;

namespace Server.Multis;

// Shared setup for both ambient hull types — C# has no multiple
// inheritance, so this is a static helper each constructor calls rather
// than a common base class (SmallBoat and SmallDragonBoat both already
// derive directly from BaseBoat).
public static class AmbientBoatLockdown
{
    public static void Apply(BaseBoat boat)
    {
        boat.Movable = false;
        boat.Anchored = true;
        boat.Owner = MaritimeAuthority.Instance;

        if (boat.TillerMan != null)
        {
            boat.TillerMan.Name = "Ship's Mate";
        }

        boat.Refresh();
    }
}

[SerializationGenerator(0, false)]
public partial class AmbientFishingBoat : SmallBoat
{
    [Constructible]
    public AmbientFishingBoat() => AmbientBoatLockdown.Apply(this);

    public override bool HandlesOnSpeech => false;

    // Item.Decays is a computed getter (Movable && Visible && Spawner ==
    // null) — already false via Movable = false, but overriding it
    // directly is the unambiguous, literal form of "this never decays."
    public override bool Decays => false;
}

[SerializationGenerator(0, false)]
public partial class AmbientDragonFishingBoat : SmallDragonBoat
{
    [Constructible]
    public AmbientDragonFishingBoat() => AmbientBoatLockdown.Apply(this);

    public override bool HandlesOnSpeech => false;

    public override bool Decays => false;
}
