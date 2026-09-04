// =========================================================================
// SpawnBuyerCommand.cs — SP-036 task 5: [spawnbuyer, the GM diagnostic
// for PlayerShopPatronageManager. Targets a PlayerVendor (or anywhere
// inside a player-owned house), instantly spawns a shopper using one of
// the three arrival vectors, and flags it ForcedPurchase so the fair-
// price check is bypassed - a guaranteed purchase for testing gold
// transfer into PlayerVendor.HoldGold without waiting on the real
// density-scaled scheduler.
// =========================================================================

using Server.Commands;
using Server.Mobiles;
using Server.Multis;
using Server.Targeting;

namespace Server.Engines.OrganicMarket;

public static class SpawnBuyerCommand
{
    public static void Configure()
    {
        CommandSystem.Register("spawnbuyer", AccessLevel.GameMaster, OnCommand);
    }

    [Usage("spawnbuyer")]
    [Description("Targets a player vendor (or a spot inside a player-owned house) and spawns a test shopper that guarantees a purchase.")]
    private static void OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;
        if (from == null)
        {
            return;
        }

        from.SendMessage("Target a player vendor, or anywhere inside their shop, to spawn a test shopper.");
        from.Target = new SpawnBuyerTarget();
    }

    private class SpawnBuyerTarget : Target
    {
        public SpawnBuyerTarget() : base(-1, false, TargetFlags.None)
        {
        }

        protected override void OnTarget(Mobile from, object o)
        {
            BaseHouse house;
            PlayerVendor vendor = null;

            switch (o)
            {
                case PlayerVendor pv:
                    vendor = pv;
                    house = pv.House;
                    break;

                case BaseHouse bh:
                    house = bh;
                    break;

                case Mobile m:
                    house = BaseHouse.FindHouseAt(m);
                    break;

                case Item it:
                    house = BaseHouse.FindHouseAt(it);
                    break;

                default:
                    from.SendMessage("That's not a valid target - pick a player vendor or a spot inside their shop.");
                    return;
            }

            if (house?.Deleted != false)
            {
                from.SendMessage("No player-owned house found there.");
                return;
            }

            vendor ??= PlayerShopPatronageManager.PickActiveVendor(house);
            if (vendor == null)
            {
                from.SendMessage("That house has no active vendor to shop from.");
                return;
            }

            if (PlayerShopPatronageManager.SpawnShopper(house, forcedPurchase: true, vendor))
            {
                from.SendMessage($"Spawned a test shopper for {vendor.Name}'s shop - purchase guaranteed.");
            }
            else
            {
                from.SendMessage("Couldn't find a valid arrival spot for that shop (no front door, or no open ground nearby).");
            }
        }

        protected override void OnTargetCancel(Mobile from, TargetCancelType cancelType)
        {
            if (cancelType == TargetCancelType.Canceled)
            {
                from.SendMessage("Cancelled.");
            }
        }
    }
}
