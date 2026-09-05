// =========================================================================
// MaritimeAuthority.cs — SP-048: persistent singleton tracking every
// entity AmbientFisherSeeder has spawned, so [wipefishers can clean up
// correctly even after a server restart. Deliberately separate from
// Server.Engines.FerrySystem.FerrySystemAuthority — the ambient fisher
// fleet is a different subsystem from the charter network, and keeping
// their tracked-entity lists apart means [wipefishers can never touch a
// charter boat/captain and vice versa.
//
// Modeled directly on FerrySystemAuthority.cs / OrganicMarket's
// MerchantGuildAuthority.cs — a hidden, internal-map Mobile singleton
// with serializable tracking lists, reclaimed on load via
// [AfterDeserialization] and created fresh on a brand-new world via the
// auto-discovered static Initialize().
// =========================================================================

using System.Collections.Generic;
using ModernUO.Serialization;
using Server;

namespace Server.Engines.Maritime;

[SerializationGenerator(0, false)]
public partial class MaritimeAuthority : Mobile
{
    private static MaritimeAuthority _instance;

    public static MaritimeAuthority Instance => _instance;

    [SerializableField(0)]
    private List<Item> _items;

    [SerializableField(1)]
    private List<Mobile> _mobiles;

    [SerializableField(2)]
    private bool _isSeeded;

    public MaritimeAuthority()
    {
        Name = "Maritime Authority";
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
            _ = new MaritimeAuthority();
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
