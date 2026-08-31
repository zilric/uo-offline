// =========================================================================
// VendorHouseCommands.cs — [vh and [vendorhouse open the Organic Market
// admin GUMP. GameMaster+ only.
// =========================================================================

using Server.Commands;

namespace Server.Engines.OrganicMarket;

public static class VendorHouseCommands
{
    public static void Configure()
    {
        CommandSystem.Register("vh", AccessLevel.GameMaster, OnCommand);
        CommandSystem.Register("vendorhouse", AccessLevel.GameMaster, OnCommand);
    }

    [Usage("vh")]
    [Description("Opens the Organic Market admin tool: place themed test houses, browse the directory, restock or wipe vendor houses.")]
    private static void OnCommand(CommandEventArgs e)
    {
        OrganicMarketAdminGump.DisplayTo(e.Mobile);
    }
}
