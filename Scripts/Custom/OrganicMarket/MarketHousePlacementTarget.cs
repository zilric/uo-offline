// =========================================================================
// MarketHousePlacementTarget.cs — the ground cursor "Place Test House"
// throws. This is the SAME multi-tile ghost-outline mechanic (MultiTarget)
// and the SAME HousePlacement.Check validation real house deeds use — see
// HouseDeed.OnPlacement / HousePlacementTarget for the stock flow this
// mirrors.
//
// The one deliberate difference: HousePlacement.Check and the region
// AllowHousing() check both auto-pass for AccessLevel.GameMaster+, which
// would make this admin tool bypass every rule it's supposed to enforce
// (guard zones, roads, terrain, overlapping structures). So the checks
// below run against MerchantGuildAuthority (a plain, non-staff Mobile)
// instead of the invoking GM, temporarily relocated to the candidate spot
// for exactly the duration of the check, then restored. Feedback messages
// still go to the real GM.
// =========================================================================

using Server.Multis;
using Server.Regions;
using Server.Targeting;

namespace Server.Engines.OrganicMarket;

public class MarketHousePlacementTarget : MultiTarget
{
    private readonly MarketHouseStyle _style;
    private readonly MarketArchetype _archetype;

    public MarketHousePlacementTarget(MarketHouseStyle style, MarketArchetype archetype)
        : base(OrganicMarketSpawner.MultiId(style), OrganicMarketSpawner.PlacementOffset(style))
    {
        _style = style;
        _archetype = archetype;
    }

    protected override void OnTarget(Mobile from, object o)
    {
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

        var map = from.Map;
        if (map == null || map == Map.Internal)
        {
            from.SendMessage("You can't place a test house there.");
            return;
        }

        var authority = MerchantGuildAuthority.Instance;
        if (authority == null)
        {
            from.SendMessage("Merchant Guild Authority is not initialized.");
            return;
        }

        var reg = Region.Find(p, map);

        // Real region rule, checked against a non-staff Mobile so GM
        // access doesn't silently pass through a guard zone / other
        // house's region / dungeon, etc.
        if (!reg.AllowHousing(authority, p))
        {
            SendRegionDenied(from, reg);
            return;
        }

        var offset = OrganicMarketSpawner.PlacementOffset(_style);
        var center = new Point3D(p.X - offset.X, p.Y - offset.Y, p.Z - offset.Z);

        var res = OrganicMarketSpawner.CheckPlacement(map, center, _style, out var toMove);
        if (res != HousePlacementResult.Valid)
        {
            SendPlacementFailure(from, res);
            return;
        }

        var index = OrganicMarketSpawner.PlaceTestHouse(map, center, _style, _archetype, toMove);
        if (index < 0)
        {
            from.SendMessage("Could not place the test house.");
        }
        else
        {
            from.SendMessage(
                $"Placed a {OrganicMarketSpawner.StyleName(_style)} ({OrganicMarketSpawner.ArchetypeName(_archetype)}) at {center}."
            );
        }

        OrganicMarketAdminGump.DisplayTo(from);
    }

    protected override void OnTargetCancel(Mobile from, TargetCancelType cancelType)
    {
        if (cancelType == TargetCancelType.Canceled)
        {
            from.SendMessage("Placement cancelled.");
        }

        OrganicMarketAdminGump.DisplayTo(from);
    }

    // Mirrors HousePlacementTarget's region-denied branches exactly, so a
    // GM sees the same messages a player would.
    private static void SendRegionDenied(Mobile from, Region reg)
    {
        if (reg.IsPartOf<TempNoHousingRegion>())
        {
            // Lord British has decreed a 'no build' period, thus you cannot build this house at this time.
            from.SendLocalizedMessage(501270);
        }
        else if (reg.IsPartOf<TreasureRegion>() || reg.IsPartOf<HouseRegion>())
        {
            // The house could not be created here. Either something is blocking the house, or the house would not be on valid terrain.
            from.SendLocalizedMessage(1043287);
        }
        else if (reg.IsPartOf<HouseRaffleRegion>())
        {
            from.SendLocalizedMessage(1150493); // You must have a deed for this plot of land in order to build here.
        }
        else
        {
            from.SendLocalizedMessage(501265); // Housing can not be created in this area.
        }
    }

    // Mirrors HouseDeed.OnPlacement's failure switch exactly.
    private static void SendPlacementFailure(Mobile from, HousePlacementResult res)
    {
        switch (res)
        {
            case HousePlacementResult.BadItem:
            case HousePlacementResult.BadLand:
            case HousePlacementResult.BadStatic:
            case HousePlacementResult.BadRegionHidden:
                // The house could not be created here. Either something is blocking the house, or the house would not be on valid terrain.
                from.SendLocalizedMessage(1043287);
                break;

            case HousePlacementResult.NoSurface:
                from.SendMessage("The house could not be created here. Part of the foundation would not be on any surface.");
                break;

            case HousePlacementResult.BadRegion:
                from.SendLocalizedMessage(501265); // Housing cannot be created in this area.
                break;

            case HousePlacementResult.BadRegionTemp:
                // Lord British has decreed a 'no build' period, thus you cannot build this house at this time.
                from.SendLocalizedMessage(501270);
                break;

            case HousePlacementResult.BadRegionRaffle:
                from.SendLocalizedMessage(1150493); // You must have a deed for this plot of land in order to build here.
                break;

            default:
                from.SendMessage("The house could not be created here.");
                break;
        }
    }
}
