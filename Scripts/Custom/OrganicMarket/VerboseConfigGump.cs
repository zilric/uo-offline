// =========================================================================
// VerboseConfigGump.cs — [verbose / [LogConfig. Lists every VerboseConfig
// category with its live ENABLED/DISABLED state and a per-row toggle
// button, plus [Reload from Disk] and [Save] actions. GameMaster+ only
// (see VerboseCommands.cs).
// =========================================================================

using Server.Gumps;
using Server.Network;

namespace Server.Engines.OrganicMarket;

public class VerboseConfigGump : DynamicGump
{
    public override bool Singleton => true;

    private const int ToggleButtonBase = 10; // 10..(10 + Categories.Length - 1)
    private const int ButtonReload = 1;
    private const int ButtonSave = 2;

    private VerboseConfigGump() : base(50, 50)
    {
    }

    public static void DisplayTo(Mobile from)
    {
        if (from?.NetState == null)
        {
            return;
        }

        from.SendGump(new VerboseConfigGump());
    }

    protected override void BuildLayout(ref DynamicGumpBuilder builder)
    {
        const int width = 420;
        const int rowHeight = 34;
        var categories = VerboseConfig.Categories;
        var height = 120 + categories.Length * rowHeight + 60;

        builder.AddPage();
        builder.AddBackground(0, 0, width, height, 5054);
        builder.AddAlphaRegion(10, 10, width - 20, height - 20);

        builder.AddHtml(20, 20, width - 40, 20, "<center><basefont color=#FFD700>Verbose Logging Manager</basefont></center>");
        builder.AddHtml(20, 44, width - 40, 40, "<basefont color=#AAAAAA>Toggle chatty subsystem logging. Changes save to verbose.cfg immediately.</basefont>");

        var y = 92;
        for (var i = 0; i < categories.Length; i++)
        {
            var (name, comment, def) = categories[i];
            var enabled = VerboseConfig.Get(name);

            builder.AddButton(24, y, 4005, 4007, ToggleButtonBase + i);

            var status = enabled
                ? "<basefont color=#33CC33>[ENABLED]</basefont>"
                : "<basefont color=#888888>[DISABLED]</basefont>";
            builder.AddHtml(60, y - 2, width - 100, 20, $"<basefont color=#FFFFFF>{name}</basefont>  {status}");
            builder.AddHtml(60, y + 16, width - 100, 18, $"<basefont color=#777777><i>{comment}</i></basefont>");

            y += rowHeight + 8;
        }

        y += 12;
        builder.AddHtml(20, y - 10, width - 40, 2, "<basefont color=#555555>________________________________</basefont>");

        builder.AddButton(24, y + 16, 4005, 4007, ButtonReload);
        builder.AddLabel(60, y + 16, 0x59, "Reload from Disk");

        builder.AddButton(24, y + 50, 4005, 4007, ButtonSave);
        builder.AddLabel(60, y + 50, 0x44, "Save");
    }

    public override void OnResponse(NetState sender, in RelayInfo info)
    {
        var from = sender.Mobile;
        if (from == null)
        {
            return;
        }

        var categories = VerboseConfig.Categories;

        if (info.ButtonID >= ToggleButtonBase && info.ButtonID < ToggleButtonBase + categories.Length)
        {
            var index = info.ButtonID - ToggleButtonBase;
            var (name, _, _) = categories[index];
            var newValue = !VerboseConfig.Get(name);

            VerboseConfig.Set(name, newValue);
            VerboseConfig.Save();

            from.SendMessage(newValue ? 0x40 : 0x21, $"Verbose logging: {name} is now {(newValue ? "ENABLED" : "DISABLED")} (saved to verbose.cfg).");
            DisplayTo(from);
            return;
        }

        switch (info.ButtonID)
        {
            case ButtonReload:
                VerboseConfig.Reload();
                from.SendMessage(0x59, "Verbose logging: reloaded verbose.cfg from disk.");
                DisplayTo(from);
                break;

            case ButtonSave:
                VerboseConfig.Save();
                from.SendMessage(0x59, "Verbose logging: current settings saved to verbose.cfg.");
                DisplayTo(from);
                break;
        }
    }
}
