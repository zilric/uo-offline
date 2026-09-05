// =========================================================================
// FerrySystemAuthority.cs — persistent singleton tracking every entity
// FerryFleetSeeder has spawned (one PermanentCharterBoat + one
// CharterCaptain per FerryRouteRegistry stop), so [wipeferries] can clean
// up correctly even after a server restart (a plain static List<> in the
// seeder would reset to empty on reboot even though the actual boats/
// captains persisted via normal world serialization).
//
// Deliberately untyped (List<Item>/List<Mobile>, no "outpost"/"dockmaster"
// specific fields) — SP-043 removed the old camp/dockmaster/scenic-voyage
// concepts entirely, and this class never needed to know about them in
// the first place, so there was nothing type-specific to strip here.
//
// Modeled directly on Scripts/Custom/OrganicMarket/MerchantGuildAuthority.cs
// — a hidden, internal-map Mobile singleton with serializable tracking
// lists, reclaimed on load via [AfterDeserialization] and created fresh
// on a brand-new world via the auto-discovered static Initialize().
// =========================================================================

using System.Collections.Generic;
using ModernUO.Serialization;
using Server;

namespace Server.Engines.FerrySystem;

[SerializationGenerator(0, false)]
public partial class FerrySystemAuthority : Mobile
{
    private static FerrySystemAuthority _instance;

    public static FerrySystemAuthority Instance => _instance;

    [SerializableField(0)]
    private List<Item> _items;

    [SerializableField(1)]
    private List<Mobile> _mobiles;

    [SerializableField(2)]
    private bool _isSeeded;

    public FerrySystemAuthority()
    {
        Name = "Ferry System Authority";
        Body = 0x190;
        Hidden = true;
        Blessed = true;
        CantWalk = true;
        Frozen = true;

        _items = new List<Item>();
        _mobiles = new List<Mobile>();
        _isSeeded = false;

        MoveToWorld(Point3D.Zero, Map.Internal);
        _instance = this;
    }

    [AfterDeserialization]
    private void ReclaimInstance()
    {
        _instance = this;
    }

    public static void Initialize()
    {
        if (_instance?.Deleted != false)
        {
            _ = new FerrySystemAuthority();
        }
    }

    public void Track(Item item)
    {
        if (item != null)
        {
            _items.Add(item);
        }
    }

    public void Track(Mobile mobile)
    {
        if (mobile != null)
        {
            _mobiles.Add(mobile);
        }
    }

    public IEnumerable<Item> TrackedItems => _items;

    public IEnumerable<Mobile> TrackedMobiles => _mobiles;

    public void WipeAll()
    {
        for (var i = _mobiles.Count - 1; i >= 0; i--)
        {
            _mobiles[i]?.Delete();
        }

        for (var i = _items.Count - 1; i >= 0; i--)
        {
            _items[i]?.Delete();
        }

        _mobiles.Clear();
        _items.Clear();
        IsSeeded = false;
    }
}
