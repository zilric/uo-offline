// =========================================================================
// MoveVendorTarget.cs — the ground cursor "[Move Vendor]" throws from the
// directory GUMP. Relocates a PlayerVendor within its own house without
// touching its House link (that's driven by the House property, not
// Location, so it survives untouched) or its inventory.
// =========================================================================

using Server.Multis;
using Server.Targeting;

namespace Server.Engines.OrganicMarket;

public class MoveVendorTarget : Target
{
    private readonly BaseHouse _house;
    private readonly Mobile _vendor;
    private readonly int _page;

    public MoveVendorTarget(BaseHouse house, Mobile vendor, int page)
        : base(-1, true, TargetFlags.None)
    {
        _house = house;
        _vendor = vendor;
        _page = page;
    }

    protected override void OnTarget(Mobile from, object o)
    {
        if (_house?.Deleted != false || _vendor?.Deleted != false)
        {
            from.SendMessage("That house or vendor no longer exists.");
            OrganicMarketDirectoryGump.DisplayTo(from, _page);
            return;
        }

        if (o is not IPoint3D ip)
        {
            return;
        }

        var p = ip switch
        {
            Item item => item.GetWorldTop(),
            Mobile m  => m.Location,
            _         => new Point3D(ip)
        };

        var map = _house.Map;
        if (map == null || map == Map.Internal)
        {
            from.SendMessage("That house has no valid map.");
            OrganicMarketDirectoryGump.DisplayTo(from, _page);
            return;
        }

        // Genuinely inside THIS house's floor plan, not just somewhere on
        // the same map - and standing on ground the map itself will
        // actually let a mobile occupy (no wall, no other mobile there).
        if (!_house.IsInside(p, 16))
        {
            from.SendMessage("That spot is outside the house. Target a tile inside its walls.");
            OrganicMarketDirectoryGump.DisplayTo(from, _page);
            return;
        }

        if (!map.CanSpawnMobile(p.X, p.Y, p.Z))
        {
            from.SendMessage("The vendor can't stand there — try an open floor tile.");
            OrganicMarketDirectoryGump.DisplayTo(from, _page);
            return;
        }

        // Location only - House stays whatever it already was, so
        // house.PlayerVendors and the vendor's own inventory/_sellItems
        // are completely untouched by this.
        _vendor.MoveToWorld(p, map);

        var faceTarget = _house.Sign?.Location ?? _house.BanLocation;
        _vendor.Direction = _vendor.GetDirectionTo(faceTarget);

        from.SendMessage($"Moved the vendor to {p}.");
        OrganicMarketDirectoryGump.DisplayTo(from, _page);
    }

    protected override void OnTargetCancel(Mobile from, TargetCancelType cancelType)
    {
        if (cancelType == TargetCancelType.Canceled)
        {
            from.SendMessage("Move cancelled.");
        }

        OrganicMarketDirectoryGump.DisplayTo(from, _page);
    }
}
