// =========================================================================
// VerboseCommands.cs — [verbose and [LogConfig open the Verbose Logging
// Manager GUMP. GameMaster+ only.
// =========================================================================

using Server.Commands;

namespace Server.Engines.OrganicMarket;

public static class VerboseCommands
{
    public static void Configure()
    {
        CommandSystem.Register("verbose", AccessLevel.GameMaster, OnCommand);
        CommandSystem.Register("LogConfig", AccessLevel.GameMaster, OnCommand);
    }

    [Usage("verbose")]
    [Description("Opens the Verbose Logging Manager: view and toggle chatty subsystem logging categories, backed by verbose.cfg.")]
    private static void OnCommand(CommandEventArgs e)
    {
        VerboseConfigGump.DisplayTo(e.Mobile);
    }
}
