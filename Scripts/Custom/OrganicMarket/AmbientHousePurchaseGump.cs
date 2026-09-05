// =========================================================================
// AmbientHousePurchaseGump.cs — the "buy this house" confirmation a player
// sees after double-clicking an ambient filler house's sign
// (AmbientHouseSign.OnDoubleClick). Same yes/no confirmation shape as
// OrganicMarketWipeConfirmGump, but a confirmed purchase transfers real
// ownership instead of deleting anything.
//
// SP-043: now a three-button choice instead of a single Buy/Cancel pair —
// Purchase Vacant (base price, strips every locked-down decor item, same
// behavior this gump always had) or Purchase Furnished (base price + a
// flat per-item surcharge, keeps every locked-down item in place). Keeping
// them is sufficient on its own: BaseHouse lockdown capacity/ownership is
// tracked per-HOUSE, not per-account (see SetLockdown/CheckAosLockdowns,
// Multis/Houses/BaseHouse.cs) — nothing about an existing LockDowns entry
// references which Mobile locked it down, so the instant house.Owner
// below flips to the buyer, those items are simply the new owner's own
// fixtures. No per-item re-locking is needed or possible to do more
// correctly than that.
//
// SP-044: Vacant's own strip loop and GetFurnishingFee's own count both
// route through Housing.HouseDecorCommands.ClearDecor/CollectDecorItems
// now, not a bare house.LockDowns walk — a house's own addons (a real
// water trough, say) and stray non-movable props that were never
// lockdown-eligible in the first place (Anvil/Forge — see
// HouseDecorCommands' own header for why LockDowns was never the
// complete picture) need the exact same treatment here that
// [exportdecor/[importdecor already learned to give them, or Vacant would
// leave an addon standing and Furnished would undercharge for one.
// =========================================================================

using Server;
using Server.Engines.Housing;
using Server.Gumps;
using Server.Items;
using Server.Mobiles;
using Server.Multis;
using Server.Network;

namespace Server.Engines.OrganicMarket;

public class AmbientHousePurchaseGump : DynamicGump
{
    public override bool Singleton => true;

    private const int ButtonBuyVacant = 1;
    private const int ButtonCancel = 2;
    private const int ButtonBuyFurnished = 3;

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
        const int width = 420;
        const int height = 290;

        var basePrice = OrganicMarketSpawner.GetBaseDeedPrice(_style);
        var purchasePrice = OrganicMarketSpawner.GetPurchasePrice(_style);
        var furnishingFee = OrganicMarketSpawner.GetFurnishingFee(_house);
        var furnishedPrice = purchasePrice + furnishingFee;
        var itemCount = _house?.Deleted == false ? HouseDecorCommands.CollectDecorItems(_house).Count : 0;

        builder.AddPage();
        builder.AddBackground(0, 0, width, height, 5054);
        builder.AddAlphaRegion(10, 10, width - 20, height - 20);

        builder.AddHtml(20, 20, width - 40, 20, "<center><basefont color=#FFD700>This House is For Sale</basefont></center>");

        builder.AddHtml(
            20, 50, width - 40, 80,
            $"<basefont color=#FFFFFF>Style: {OrganicMarketSpawner.StyleName(_style)}<br>" +
            $"Base deed valuation: {basePrice:N0} gp<br>" +
            $"This house currently has {itemCount} decorative item(s) inside.<br><br>" +
            "Gold is withdrawn from your backpack first, then your bank.</basefont>"
        );

        builder.AddHtml(
            20, 130, width - 40, 40,
            $"<basefont color=#88FF88>Purchase Vacant: {purchasePrice:N0} gp</basefont><br>" +
            "<basefont color=#AAAAAA>Clears out all decor before you take ownership.</basefont>"
        );
        builder.AddButton(30, height - 90, 4017, 4019, ButtonBuyVacant);
        builder.AddLabel(66, height - 90, 0x59, "Buy Vacant");

        builder.AddHtml(
            20, 178, width - 40, 40,
            $"<basefont color=#88CCFF>Purchase Furnished: {furnishedPrice:N0} gp</basefont> " +
            $"<basefont color=#777777>(+{furnishingFee:N0} gp for {itemCount} item(s))</basefont><br>" +
            "<basefont color=#AAAAAA>Keeps every decor item, already locked down as yours.</basefont>"
        );
        builder.AddButton(30, height - 50, 4017, 4019, ButtonBuyFurnished);
        builder.AddLabel(66, height - 50, 0x59, "Buy Furnished");

        builder.AddButton(width - 110, height - 40, 4005, 4007, ButtonCancel);
        builder.AddLabel(width - 74, height - 40, 0x480, "Cancel");
    }

    public override void OnResponse(NetState sender, in RelayInfo info)
    {
        var from = sender.Mobile;
        if (from == null)
        {
            return;
        }

        switch (info.ButtonID)
        {
            case ButtonBuyVacant:
                TryPurchase(from, _house, _style, keepFurnishings: false);
                break;
            case ButtonBuyFurnished:
                TryPurchase(from, _house, _style, keepFurnishings: true);
                break;
        }
    }

    // Static and public so the actual purchase logic is reachable (and
    // testable) without a live client round-trip through the gump -
    // AmbientHouseSign.OnDoubleClick's own DisplayTo/OnResponse path is
    // just the normal player-facing entry point into the same method.
    //
    // keepFurnishings selects Purchase Furnished (base price + a flat
    // per-item surcharge, decor stays) over Purchase Vacant (base price
    // only, decor is stripped — the sole behavior this method had before
    // SP-043).
    public static bool TryPurchase(Mobile from, BaseHouse house, MarketHouseStyle style, bool keepFurnishings)
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
        if (keepFurnishings)
        {
            price += OrganicMarketSpawner.GetFurnishingFee(house);
        }

        // SP-043: checks (and, on success, spends from) the backpack
        // before the bank, rather than Banker.GetBalance/Withdraw's own
        // bank-only view - the ticket's explicit ask, and the more
        // forgiving order for a player who just walked up with a purse of
        // gold on hand.
        if (TotalAvailableGold(from) < price)
        {
            from.SendMessage($"You do not have enough gold on hand or in your bank to buy this house ({price:N0} gp required).");
            return false;
        }

        if (!TryPayGold(from, price))
        {
            from.SendMessage("You did not have enough gold to complete the purchase.");
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

        // SP-034/SP-044: strip every ambient decor item before handing the
        // house over, UNLESS the buyer paid the Furnished surcharge to
        // keep it - LockDowns, addons (a real water trough, say - Delete()
        // on the addon root cascades to its own Components), and any
        // stray non-movable prop that was never lockdown-eligible to
        // begin with (Anvil/Forge - see HouseDecorCommands' own header).
        // DynamicClutterGenerator/FurnishResidential locked ordinary
        // clutter down under `authority`, not the buyer, but a lockdown's
        // ownership is entirely implicit in which house it's IN - nothing
        // on the item itself remembers which Mobile locked it down (see
        // BaseHouse.SetLockdown) - so the house.Owner assignment above is
        // already all "converts decorative clutter to the buyer's own
        // locked-down items" requires when keeping them; there is nothing
        // more correct left to do per-item.
        if (!keepFurnishings)
        {
            HouseDecorCommands.ClearDecor(house);
        }

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

        from.SendMessage(
            $"You have purchased this {OrganicMarketSpawner.StyleName(style)} " +
            $"({(keepFurnishings ? "furnished" : "vacant")}) for {price:N0} gold."
        );
        return true;
    }

    // ---- Combined backpack + bank gold handling ----
    //
    // Banker.GetBalance/Withdraw (Mobiles/Townfolk/Banker.cs) only ever
    // look at account gold + the bank box - never the backpack. The
    // ticket explicitly wants both checked, so these two wrap that pair
    // with an initial backpack draw first.
    private static int TotalAvailableGold(Mobile from)
    {
        var inPack = (long)(from.Backpack?.GetAmount(typeof(Gold)) ?? 0);
        return (int)System.Math.Clamp(inPack + Banker.GetBalance(from), 0, int.MaxValue);
    }

    private static bool TryPayGold(Mobile from, int amount)
    {
        var inPack = from.Backpack?.GetAmount(typeof(Gold)) ?? 0;
        var fromPack = System.Math.Min(inPack, amount);

        if (fromPack > 0 && !from.Backpack.ConsumeTotal(typeof(Gold), fromPack))
        {
            return false;
        }

        var remaining = amount - fromPack;
        if (remaining <= 0)
        {
            return true;
        }

        if (Banker.Withdraw(from, remaining))
        {
            return true;
        }

        // Bank came up short after the backpack portion was already
        // spent — refund it so a failed purchase never partially charges.
        if (fromPack > 0)
        {
            from.Backpack.DropItem(new Gold(fromPack));
        }

        return false;
    }
}
