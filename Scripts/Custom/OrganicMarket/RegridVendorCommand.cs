// =========================================================================
// RegridVendorCommand.cs — SP-053: [regridvendor, the GM diagnostic for
// VendorGridArranger. Targets a PlayerVendor directly, or any item/
// container inside its inventory (resolved via Item.RootParent, so a GM
// can click a specific display box just as easily as the vendor itself).
// Modeled on SpawnBuyerCommand.cs's own target-with-fallback-resolution
// shape.
// =========================================================================

using Server.Commands;
using Server.Mobiles;
using Server.Targeting;

namespace Server.Engines.OrganicMarket;

public static class RegridVendorCommand
{
    public static void Configure()
    {
        CommandSystem.Register("regridvendor", AccessLevel.GameMaster, OnCommand);
    }

    [Usage("regridvendor")]
    [Description("Targets a player vendor (or any item/container in their inventory) and re-arranges its sale containers into a grid.")]
    private static void OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;
        if (from == null)
        {
            return;
        }

        from.SendMessage("Target a player vendor, or a container in their inventory, to re-arrange its sale grid.");
        from.Target = new RegridVendorTarget();
    }

    private class RegridVendorTarget : Target
    {
        public RegridVendorTarget() : base(-1, false, TargetFlags.None)
        {
        }

        protected override void OnTarget(Mobile from, object o)
        {
            var vendor = o switch
            {
                PlayerVendor pv => pv,
                Item it when it.RootParent is PlayerVendor pv2 => pv2,
                _ => null
            };

            if (vendor?.Deleted != false)
            {
                from.SendMessage("That's not a valid target - pick a player vendor or something in their inventory.");
                return;
            }

            var (containerCount, itemCount) = VendorGridArranger.Arrange(vendor);
            from.SendMessage($"Vendor Grid: Arranged {containerCount} containers ({itemCount} total items).");
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
