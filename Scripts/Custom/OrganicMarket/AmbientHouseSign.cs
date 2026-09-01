// =========================================================================
// AmbientHouseSign.cs — the house sign every ambient (non-vendor) filler
// house gets, in place of the stock HouseSign BaseHouse's own constructor
// creates. Swapped in right after placement (see
// OrganicMarketSpawner.PlaceHouse's ambient branch) so the sign already
// standing there knows which MarketHouseStyle it's pricing.
//
// A double-click from a non-staff player, while the house is still owned
// by MerchantGuildAuthority and still tracked in its registry, opens
// AmbientHousePurchaseGump instead of the normal HouseGump/decay-label
// flow HouseSign.OnDoubleClick runs. Staff, and anyone once the house has
// actually been bought (AmbientHousePurchaseGump.Purchase replaces this
// sign with a plain HouseSign on success), fall straight through to that
// normal behavior - there's no permanent "for sale" state to fight once a
// real owner exists.
// =========================================================================

using ModernUO.Serialization;
using Server;
using Server.Multis;

namespace Server.Engines.OrganicMarket;

[SerializationGenerator(0, false)]
public partial class AmbientHouseSign : HouseSign
{
    [SerializableField(0)]
    private MarketHouseStyle _style;

    public AmbientHouseSign(BaseHouse owner, MarketHouseStyle style) : base(owner)
    {
        _style = style;
        Name = $"{OrganicMarketSpawner.StyleName(style)} (For Sale)";
    }

    public override void OnDoubleClick(Mobile from)
    {
        if (IsStillForSale(from))
        {
            AmbientHousePurchaseGump.DisplayTo(from, Owner, _style);
            return;
        }

        base.OnDoubleClick(from);
    }

    // "For sale" means all three: a real player (not staff - GMs get the
    // normal sign so their own house-admin tools still work), the house
    // still belongs to the internal authority Mobile (nobody's bought it
    // yet), and it's still a live registry entry (not deleted out from
    // under this sign by an admin wipe that somehow left the sign behind).
    private bool IsStillForSale(Mobile from) =>
        from.AccessLevel < AccessLevel.GameMaster &&
        Owner?.Deleted == false &&
        Owner.Owner == MerchantGuildAuthority.Instance &&
        MerchantGuildAuthority.Instance?.IsRegistered(Owner) == true;
}
