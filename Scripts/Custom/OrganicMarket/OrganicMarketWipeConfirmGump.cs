// =========================================================================
// OrganicMarketWipeConfirmGump.cs — confirmation safeguard for
// "Wipe All Market Houses". A destructive, world-wide action gets a
// dedicated yes/no prompt rather than firing straight off the main gump.
// =========================================================================

using Server.Gumps;
using Server.Network;

namespace Server.Engines.OrganicMarket;

public class OrganicMarketWipeConfirmGump : DynamicGump
{
    public override bool Singleton => true;

    private const int ButtonConfirm = 1;
    private const int ButtonCancel = 2;

    private OrganicMarketWipeConfirmGump() : base(150, 150)
    {
    }

    public static void DisplayTo(Mobile from)
    {
        if (from?.NetState == null)
        {
            return;
        }

        from.SendGump(new OrganicMarketWipeConfirmGump());
    }

    protected override void BuildLayout(ref DynamicGumpBuilder builder)
    {
        const int width = 320;
        const int height = 170;

        var authority = MerchantGuildAuthority.Instance;
        var count = authority?.Count ?? 0;

        builder.AddPage();
        builder.AddBackground(0, 0, width, height, 5054);
        builder.AddAlphaRegion(10, 10, width - 20, height - 20);

        builder.AddHtml(20, 20, width - 40, 20, "<center><basefont color=#FF4040>Wipe All Market Houses?</basefont></center>");
        builder.AddHtml(
            20, 48, width - 40, 60,
            $"<basefont color=#FFFFFF>This permanently deletes all {count} tracked market house(s), their locked-down fixtures, and their vendors. This cannot be undone.</basefont>"
        );

        builder.AddButton(30, height - 40, 4017, 4019, ButtonConfirm);
        builder.AddLabel(66, height - 40, 0x25, "Yes, wipe everything");

        builder.AddButton(30, height - 65, 4005, 4007, ButtonCancel);
        builder.AddLabel(66, height - 65, 0x480, "Cancel");
    }

    public override void OnResponse(NetState sender, in RelayInfo info)
    {
        var from = sender.Mobile;
        if (from == null)
        {
            return;
        }

        if (info.ButtonID == ButtonConfirm)
        {
            var authority = MerchantGuildAuthority.Instance;
            var removed = authority?.WipeAll() ?? 0;
            from.SendMessage($"Wiped {removed} market house(s).");
        }

        OrganicMarketAdminGump.DisplayTo(from);
    }
}
