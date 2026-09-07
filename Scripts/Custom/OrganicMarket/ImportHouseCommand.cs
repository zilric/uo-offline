// =========================================================================
// ImportHouseCommand.cs — SP-055: [importhouse, the manual/testing
// counterpart to [exporthouse. Targets a house sign (or anything inside a
// house - resolution mirrors ExportHouseCommand's own ResolveHouse
// switch), lists every template HouseTemplateManager.ListAvailableTemplates
// finds compatible with that house (including any footprint-family
// cross-style match - e.g. a SmallShop-exported template offered for a
// SmallOldHouse), and opens ImportHouseGump to pick one.
// =========================================================================

using Server.Commands;
using Server.Items;
using Server.Multis;
using Server.Targeting;

namespace Server.Engines.OrganicMarket;

public static class ImportHouseCommand
{
    public static void Configure()
    {
        CommandSystem.Register("importhouse", AccessLevel.GameMaster, OnCommand);
    }

    [Usage("importhouse")]
    [Description("Targets a house sign (or anything inside a house) to browse and stamp a curated decor template onto it.")]
    private static void OnCommand(CommandEventArgs e)
    {
        var from = e.Mobile;
        if (from == null)
        {
            return;
        }

        from.SendMessage("Target a house sign (or something inside a house) to import a decor template into.");
        from.Target = new ImportHouseTarget();
    }

    private class ImportHouseTarget : Target
    {
        public ImportHouseTarget() : base(-1, false, TargetFlags.None)
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

            var templates = HouseTemplateManager.ListAvailableTemplates(house);
            if (templates.Count == 0)
            {
                from.SendMessage(0x22, $"[ImportHouse] No compatible templates exist yet for {house.GetType().Name}.");
                return;
            }

            ImportHouseGump.DisplayTo(from, house, templates);
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
