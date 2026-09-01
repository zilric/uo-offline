// =========================================================================
// AmbientHousePurchaseGump.cs — the "buy this house" confirmation a player
// sees after double-clicking an ambient filler house's sign
// (AmbientHouseSign.OnDoubleClick). Same yes/no confirmation shape as
// OrganicMarketWipeConfirmGump, but a confirmed purchase transfers real
// ownership instead of deleting anything.
// =========================================================================

using Server;
using Server.Gumps;
using Server.Mobiles;
using Server.Multis;
using Server.Network;

namespace Server.Engines.OrganicMarket;

public class AmbientHousePurchaseGump : DynamicGump
{
    public override bool Singleton => true;

    private const int ButtonBuy = 1;
    private const int ButtonCancel = 2;

    private readonly BaseHouse _house;
    private readonly MarketHouseStyle _style;

    private AmbientHousePurchaseGump(BaseHouse house, MarketHouseStyle style) : base(150, 150)
    {
        _house = house;
        _style = style;
    }

    public static void DisplayTo(Mobile from, BaseHouse house, MarketHouseStyle style)
    {
        if (from?.NetState == null || house == null)
        {
            return;
        }

        from.SendGump(new AmbientHousePurchaseGump(house, style));
    }

    protected override void BuildLayout(ref DynamicGumpBuilder builder)
    {
        const int width = 380;
        const int height = 230;

        var basePrice = OrganicMarketSpawner.GetBaseDeedPrice(_style);
        var purchasePrice = OrganicMarketSpawner.GetPurchasePrice(_style);

        builder.AddPage();
        builder.AddBackground(0, 0, width, height, 5054);
        builder.AddAlphaRegion(10, 10, width - 20, height - 20);

        builder.AddHtml(20, 20, width - 40, 20, "<center><basefont color=#FFD700>This House is For Sale</basefont></center>");

        builder.AddHtml(
            20, 50, width - 40, 100,
            $"<basefont color=#FFFFFF>Style: {OrganicMarketSpawner.StyleName(_style)}<br>" +
            $"Base deed valuation: {basePrice:N0} gp<br>" +
            $"Purchase price (+10%): {purchasePrice:N0} gp<br><br>" +
            "Gold is withdrawn directly from your bank account.</basefont>"
        );

        builder.AddButton(30, height - 40, 4017, 4019, ButtonBuy);
        builder.AddLabel(66, height - 40, 0x59, "Buy House");

        builder.AddButton(200, height - 40, 4005, 4007, ButtonCancel);
        builder.AddLabel(236, height - 40, 0x480, "Cancel");
    }

    public override void OnResponse(NetState sender, in RelayInfo info)
    {
        var from = sender.Mobile;
        if (from == null || info.ButtonID != ButtonBuy)
        {
            return;
        }

        TryPurchase(from, _house, _style);
    }

    // Static and public so the actual purchase logic is reachable (and
    // testable) without a live client round-trip through the gump -
    // AmbientHouseSign.OnDoubleClick's own DisplayTo/OnResponse path is
    // just the normal player-facing entry point into the same method.
    public static bool TryPurchase(Mobile from, BaseHouse house, MarketHouseStyle style)
    {
        var authority = MerchantGuildAuthority.Instance;

        // Re-validated fresh here, not trusted from whenever the gump was
        // opened - an admin wipe, or another player buying this exact
        // house, could have happened in the meantime.
        if (authority == null || house?.Deleted != false || house.Owner != authority || !authority.IsRegistered(house))
        {
            from.SendMessage("That house is no longer available.");
            return false;
        }

        if (from.AccessLevel < AccessLevel.GameMaster && BaseHouse.HasAccountHouse(from))
        {
            // You already own a house, you may not place another! (matches
            // HouseDeed.OnPlacement's own rule - a bought ambient house is
            // real property, not a photo op, so the same one-house-per-
            // account limit applies.)
            from.SendLocalizedMessage(501271);
            return false;
        }

        var price = OrganicMarketSpawner.GetPurchasePrice(style);
        if (Banker.GetBalance(from) < price)
        {
            from.SendMessage($"You do not have enough gold in your bank to buy this house ({price:N0} gp required).");
            return false;
        }

        if (!Banker.Withdraw(from, price))
        {
            from.SendMessage("Your bank did not have enough gold to complete the purchase.");
            return false;
        }

        // Same sequence BaseHouse's own player-to-player trade completion
        // uses (BaseHouse.cs, OnSecureTrade) - remove the old "owner"'s
        // keys before handing off, clear the access lists a fresh owner
        // shouldn't inherit, then mint new keys and re-key every door to
        // match. RestrictDecay was only ever there so an unsold market
        // house never condemned while waiting for a buyer - a real,
        // player-owned house should decay/refresh normally like any other.
        house.RemoveKeys(authority);
        house.Owner = from;
        house.Bans.Clear();
        house.Friends.Clear();
        house.CoOwners.Clear();
        house.ChangeLocks(from);
        house.LastTraded = Core.Now;
        house.RestrictDecay = false;

        // Crucial: pull this slot out of the registry so [Wipe All Market
        // Houses] can never touch it again.
        authority.Deregister(house);

        // Internalize, NEVER Delete, the old AmbientHouseSign - it's still
        // a HouseSign underneath, and HouseSign.OnAfterDelete cascades into
        // deleting its own Owner (see OrganicMarketSpawner.PlaceHouse's
        // matching comment on the same pattern). Deleting it here would
        // delete the house the player just paid for.
        var oldSign = house.Sign;
        var signLoc = oldSign?.Location ?? new Point3D(house.X, house.Y - 1, house.Z);
        var signMap = house.Map;
        oldSign?.Internalize();

        var newSign = new HouseSign(house) { Name = $"{from.Name}'s House" };
        newSign.MoveToWorld(signLoc, signMap);
        house.Sign = newSign;

        from.SendMessage($"You have purchased this {OrganicMarketSpawner.StyleName(style)} for {price:N0} gold.");
        return true;
    }
}
