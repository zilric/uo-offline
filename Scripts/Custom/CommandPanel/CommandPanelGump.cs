// =========================================================================
// CommandPanelGump.cs — SP-046 through SP-048: the unified [panel /
// [plugins / [offline GM command panel. Tabbed by PanelCategory
// (CommandPanelRegistry.cs), each tab listing that category's commands
// with a one-click Execute.
//
// SP-048 retention requirement forced an architecture change. SP-047's
// design put every category on its own native gump page (AddPage(1..N))
// inside ONE gump instance, switched client-side via GumpButtonType.Page
// — which works fine for browsing, but a UO gump has no field for "open
// on page 3": every SendGump always starts the client back on page 1.
// Re-sending that same multi-page gump after Execute (to keep the panel
// open) would therefore always snap back to category 0, regardless of
// which tab the GM had actually been viewing — retention is simply not
// expressible in that design.
//
// The fix: track the active category SERVER-SIDE instead, one int field
// on the gump instance (constructor parameter, matching the ticket's own
// `new CommandPanelGump(from, activePage)` sketch), and build only THAT
// category's rows into a single page 0 per send — the same pattern
// Server.Gumps.AdminGump (a real, core ModernUO admin panel) already
// uses for its own tabs: clicking a tab is a Reply button that
// reconstructs and resends the whole gump with a new active category,
// not a client-side page flip. This also makes the earlier page-bleeding
// bug structurally impossible — there is only ever one page per gump
// instance now, so there is nothing for a stray category to bleed onto.
//
// Execute behavior unchanged: CommandSystem.Handle(from, command,
// MessageType.Command) either way. A command that sets its own
// Mobile.Target works exactly as if typed — Handle() runs the handler,
// which puts the GM into targeting mode normally. A command needing
// typed arguments runs with none supplied, so its own usage-guard fires
// and messages the correct syntax (there's no server-side way to push
// text into a client's local chat input buffer). Commands flagged
// RequiresTargetOrArgs get a "(*)" marker in the panel. After either
// outcome, the panel re-sends itself on the SAME active category —
// closing only happens via right-click (default gump behavior, never
// disabled here) or the explicit Close button, both of which send
// button ID 0 and are simply never re-opened by OnResponse.
// =========================================================================

using Server;
using Server.Commands;
using Server.Gumps;
using Server.Network;

namespace Server.Engines.CommandPanel;

public class CommandPanelGump : DynamicGump
{
    public override bool Singleton => true;

    private const int ButtonClose = 0;
    private const int TabButtonBase = 100;
    private const int ExecuteButtonBase = 1000;

    private const int GumpWidth = 720;
    private const int GumpHeight = 480;
    private const int TabTop = 46;
    private const int ListTop = 90;
    private const int RowHeight = 28;

    private readonly int _activeCategory;

    private CommandPanelGump(int activeCategory) : base(50, 50)
    {
        var categories = CommandPanelRegistry.Categories;
        _activeCategory = activeCategory >= 0 && activeCategory < categories.Length ? activeCategory : 0;
    }

    public static void Configure()
    {
        CommandSystem.Register("panel", AccessLevel.GameMaster, OnOpen);
        CommandSystem.Register("plugins", AccessLevel.GameMaster, OnOpen);
        CommandSystem.Register("offline", AccessLevel.GameMaster, OnOpen);
    }

    [Usage("panel")]
    [Description("Opens the unified server command panel — every registered custom subsystem command, tabbed by category, one-click execute.")]
    private static void OnOpen(CommandEventArgs e) => DisplayTo(e.Mobile, 0);

    public static void DisplayTo(Mobile from, int activeCategory)
    {
        if (from?.NetState == null)
        {
            return;
        }

        from.SendGump(new CommandPanelGump(activeCategory));
    }

    protected override void BuildLayout(ref DynamicGumpBuilder builder)
    {
        var categories = CommandPanelRegistry.Categories;

        builder.AddPage();
        builder.AddBackground(0, 0, GumpWidth, GumpHeight, 0x13BE);
        builder.AddAlphaRegion(10, 10, GumpWidth - 20, GumpHeight - 20);

        builder.AddHtml(
            20, 12, GumpWidth - 60, 22,
            "<basefont color=#F4E4BC><center>Server Command Panel</center></basefont>"
        );
        builder.AddButton(GumpWidth - 34, 12, 4017, 4019, ButtonClose);

        var tabWidth = (GumpWidth - 40) / categories.Length;
        for (var i = 0; i < categories.Length; i++)
        {
            var tabX = 20 + i * tabWidth;
            var isActive = i == _activeCategory;
            var color = isActive ? "#FFD700" : "#8899AA";

            // Reply, not Page — switching tabs has to round-trip through
            // OnResponse so the NEXT gump instance is built with the new
            // active category baked in (see header comment).
            builder.AddButton(tabX, TabTop, isActive ? 4006 : 4005, 4007, TabButtonBase + i);
            builder.AddHtml(
                tabX + 22, TabTop + 2, tabWidth - 24, 22,
                $"<basefont color={color}>{CommandPanelRegistry.DisplayName(categories[i])}</basefont>"
            );
        }

        var commands = CommandPanelRegistry.GetCommands(categories[_activeCategory]);
        if (commands.Count == 0)
        {
            builder.AddLabel(20, ListTop, 0x480, "No commands registered in this category.");
            return;
        }

        var y = ListTop;
        for (var row = 0; row < commands.Count; row++)
        {
            var cmd = commands[row];
            var flag = cmd.RequiresTargetOrArgs ? " (*)" : "";

            builder.AddLabel(30, y, 0x47E, $"[{cmd.Command}{flag}");
            builder.AddLabel(200, y, 0x384, cmd.Description);
            builder.AddButton(630, y, 0xFA5, 0xFA7, ExecuteButtonBase + row);

            y += RowHeight;
        }
    }

    public override void OnResponse(NetState sender, in RelayInfo info)
    {
        var from = sender.Mobile;
        if (from == null || info.ButtonID == ButtonClose)
        {
            return;
        }

        if (info.ButtonID >= TabButtonBase && info.ButtonID < ExecuteButtonBase)
        {
            DisplayTo(from, info.ButtonID - TabButtonBase);
            return;
        }

        if (info.ButtonID < ExecuteButtonBase)
        {
            return;
        }

        var rowIndex = info.ButtonID - ExecuteButtonBase;
        var commands = CommandPanelRegistry.GetCommands(CommandPanelRegistry.Categories[_activeCategory]);
        if (rowIndex < 0 || rowIndex >= commands.Count)
        {
            return;
        }

        var cmd = commands[rowIndex];

        if (from.AccessLevel < cmd.AccessLevel)
        {
            from.SendMessage("You do not have access to that command.");
        }
        else
        {
            CommandSystem.Handle(from, cmd.Command, MessageType.Command);
        }

        // Retention: re-send the panel on the same tab so a GM can fire
        // several commands in a row without reopening [panel each time.
        DisplayTo(from, _activeCategory);
    }
}
