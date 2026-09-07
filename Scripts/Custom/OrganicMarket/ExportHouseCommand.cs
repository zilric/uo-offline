// =========================================================================
// ExportHouseCommand.cs — SP-054: [exporthouse, targets a house sign (or
// anything inside a house) and opens ExportHouseGump to classify it
// (Ambient vs Vendor + archetype) and name a curated decor template for
// world-seeder stamping (HouseTemplateManager.cs). Target-resolution
// mirrors Housing.HouseDecorCommands.ExportDecorTarget's own
// ResolveHouse switch exactly.
// =========================================================================

using Server.Commands;
using Server.Items;
using Server.Multis;
using Server.Targeting;

namespace Server.Engines.OrganicMarket;

public static class ExportHouseCommand
{
    public static void Configure()
    {
        CommandSystem.Register("exporthouse", AccessLevel.GameMaster, OnCommand);
    }

    [Usage("exporthouse")]
    [Description("Targets a house sign (or anything inside a house) to classify and export its decor as a curated seeder template.")]
    private static void OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;
        if (from == null)
        {
            return;
        }

        from.SendMessage("Target a house sign (or something inside a house) to export its decor as a seeder template.");
        from.Target = new ExportHouseTarget();
    }

    private class ExportHouseTarget : Target
    {
        public ExportHouseTarget() : base(-1, false, TargetFlags.None)
        {
        }

        protected override void OnTarget(Mobile from, object o)
        {
            var house = ResolveHouse(o);
            if (house?.Deleted != false)
            {
                from.SendMessage("That is not a house sign, or not something inside a house.");
                return;
            }

            ExportHouseGump.DisplayTo(from, house);
        }

        protected override void OnTargetCancel(Mobile from, TargetCancelType cancelType)
        {
            if (cancelType == TargetCancelType.Canceled)
            {
                from.SendMessage("Cancelled.");
            }
        }

        private static BaseHouse ResolveHouse(object targeted) => targeted switch
        {
            HouseSign sign => sign.Owner,
            BaseHouse house => house,
            Item item => BaseHouse.FindHouseAt(item),
            Mobile mobile => BaseHouse.FindHouseAt(mobile),
            _ => null
        };
    }
}
