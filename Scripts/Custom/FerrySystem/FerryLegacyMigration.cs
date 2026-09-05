// =========================================================================
// FerryLegacyMigration.cs — SP-043 one-time migration safety net.
//
// SP-043 deletes DockmasterNPC, AmbientFishingSkiff and AmbientFisherBot
// outright per the ticket. But if [seedferries was ever run under
// SP-039/SP-040/SP-041/SP-042 (which earlier verification checklists in
// this project's history explicitly asked the tester to confirm), the
// current world save's Mobiles.bin/Items.bin already contain live,
// serialized instances of those exact types by full name.
//
// On a HEADLESS server, ModernUO's deserializer cannot silently drop an
// entity whose type no longer exists: GenericEntityPersistence.
// GetConstructorFor prompts "Delete all of those types? (y/n)" via
// Console.Write + ConsoleInputHandler.ReadLine(), and ReadLine() throws
// HeadlessConsoleInputException the instant it's called with no TTY
// attached (Core.Headless == true) — uncaught, this crashes world load
// and the server never boots again.
//
// The fix is the standard one for "a type with live save data was
// deleted": keep a byte-layout-identical stub under the exact same full
// name (namespace + class), so the type still resolves and the exact
// same fields still deserialize correctly, then have the stub delete
// itself immediately once the world has finished loading. No gameplay
// logic lives here — these three classes exist only to be found once,
// deleted, and never spawned again ([Constructible] is deliberately
// omitted so nothing can [add one).
//
// Safe to delete this whole file once you've confirmed (e.g. via a world
// save made after running [seedferries on the SP-043 build) that no
// instances of these three types remain in Saves/.
// =========================================================================

using ModernUO.Serialization;
using Server;

namespace Server.Multis
{
    // Was Server.Multis.AmbientFishingSkiff : SmallBoat, no custom fields.
    [SerializationGenerator(0, false)]
    public partial class AmbientFishingSkiff : SmallBoat
    {
        [AfterDeserialization(false)]
        private void SelfDelete() => Delete();
    }
}

namespace Server.Engines.FerrySystem
{
    // Was Server.Engines.FerrySystem.DockmasterNPC : Mobile, one field.
    [SerializationGenerator(0, false)]
    public partial class DockmasterNPC : Mobile
    {
        [SerializableField(0)]
        private string _stopName;

        [AfterDeserialization(false)]
        private void SelfDelete() => Delete();
    }

    // Was Server.Engines.FerrySystem.AmbientFisherBot : Mobile, two fields.
    [SerializationGenerator(0, false)]
    public partial class AmbientFisherBot : Mobile
    {
        [SerializableField(0)]
        private Item _deckBarrel;

        [SerializableField(1)]
        private Point3D _waterTile;

        [AfterDeserialization(false)]
        private void SelfDelete() => Delete();
    }
}
