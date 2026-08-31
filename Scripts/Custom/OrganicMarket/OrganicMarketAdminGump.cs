// =========================================================================
// OrganicMarketAdminGump.cs — the [vh / [vendorhouse main panel.
//
// Pick a house style + archetype, then "Place Test House" throws a
// ground target (see MarketHousePlacementTarget). From here a GM can also jump
// to the paged directory, force a global restock, or wipe every tracked
// market house.
// =========================================================================

using Server.Gumps;
using Server.Network;

namespace Server.Engines.OrganicMarket;

public class OrganicMarketAdminGump : DynamicGump
{
    public override bool Singleton => true;

    private const int StyleGroup = 0;
    private const int ArchetypeGroup = 1;

    private const int SwitchStyleBase = 0;      // 0..2
    private const int SwitchArchetypeBase = 10; // 10..13

    private const int ButtonPlace = 1;
    private const int ButtonDirectory = 2;
    private const int ButtonGlobalRestock = 3;
    private const int ButtonWipeAll = 4;

    private static readonly MarketHouseStyle[] Styles =
    {
        MarketHouseStyle.SmallShop,
        MarketHouseStyle.TwoStoryWoodPlaster,
        MarketHouseStyle.LargePatio
    };

    private static readonly MarketArchetype[] Archetypes =
    {
        MarketArchetype.Blacksmith,
        MarketArchetype.MageAlchemist,
        MarketArchetype.CurioRares,
        MarketArchetype.TailorFletcher
    };

    private OrganicMarketAdminGump() : base(50, 50)
    {
    }

    public static void DisplayTo(Mobile from)
    {
        if (from?.NetState == null)
        {
            return;
        }

        from.SendGump(new OrganicMarketAdminGump());
    }

    protected override void BuildLayout(ref DynamicGumpBuilder builder)
    {
        const int width = 380;
        var height = 400;

        builder.AddPage();
        builder.AddBackground(0, 0, width, height, 5054);
        builder.AddAlphaRegion(10, 10, width - 20, height - 20);

        builder.AddHtml(20, 20, width - 40, 20, "<center><basefont color=#FFD700>Organic Market Admin Tool</basefont></center>");

        var authority = MerchantGuildAuthority.Instance;
        var count = authority?.Count ?? 0;
        builder.AddLabel(20, 44, 0x480, $"Tracked market houses: {count}");

        builder.AddHtml(20, 68, width - 40, 20, "<basefont color=#7FFFD4>House Style</basefont>");
        builder.AddGroup(StyleGroup);
        for (var i = 0; i < Styles.Length; i++)
        {
            var y = 92 + i * 22;
            builder.AddRadio(24, y, 210, 211, i == 0, SwitchStyleBase + i);
            builder.AddLabel(50, y, 0x480, OrganicMarketSpawner.StyleName(Styles[i]));
        }

        var archY = 92 + Styles.Length * 22 + 16;
        builder.AddHtml(20, archY - 24, width - 40, 20, "<basefont color=#7FFFD4>Archetype Theme</basefont>");
        builder.AddGroup(ArchetypeGroup);
        for (var i = 0; i < Archetypes.Length; i++)
        {
            var y = archY + i * 22;
            builder.AddRadio(24, y, 210, 211, i == 0, SwitchArchetypeBase + i);
            builder.AddLabel(50, y, 0x480, OrganicMarketSpawner.ArchetypeName(Archetypes[i]));
        }

        var afterArch = archY + Archetypes.Length * 22 + 16;

        builder.AddButton(24, afterArch, 4005, 4007, ButtonPlace);
        builder.AddLabel(60, afterArch, 0x84, "Place Test House (ground target)");

        var directoryY = afterArch + 30;
        builder.AddButton(24, directoryY, 4005, 4007, ButtonDirectory);
        builder.AddLabel(60, directoryY, 0x480, "Open Market House Directory");

        var restockY = directoryY + 34;
        builder.AddHtml(20, restockY - 6, width - 40, 2, "<basefont color=#555555>________________________________</basefont>");
        builder.AddButton(24, restockY + 14, 4005, 4007, ButtonGlobalRestock);
        builder.AddLabel(60, restockY + 14, 0x44, "Force Global Restock (all vendors)");

        var wipeY = restockY + 44;
        builder.AddButton(24, wipeY, 4017, 4019, ButtonWipeAll);
        builder.AddLabel(60, wipeY, 0x25, "Wipe All Market Houses");
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
            case ButtonPlace:
            {
                var style = SelectedStyle(info);
                var archetype = SelectedArchetype(info);
                from.SendMessage("Target where the sign should go. Standard house placement rules apply.");
                from.Target = new MarketHousePlacementTarget(style, archetype);
                break;
            }

            case ButtonDirectory:
                OrganicMarketDirectoryGump.DisplayTo(from, 0);
                break;

            case ButtonGlobalRestock:
            {
                var authority = MerchantGuildAuthority.Instance;
                var restocked = authority?.RestockAll() ?? 0;
                from.SendMessage($"Force-restocked {restocked} vendor(s) across all market houses.");
                DisplayTo(from);
                break;
            }

            case ButtonWipeAll:
                OrganicMarketWipeConfirmGump.DisplayTo(from);
                break;
        }
    }

    private static MarketHouseStyle SelectedStyle(in RelayInfo info)
    {
        for (var i = 0; i < Styles.Length; i++)
        {
            if (info.IsSwitched(SwitchStyleBase + i))
            {
                return Styles[i];
            }
        }

        return Styles[0];
    }

    private static MarketArchetype SelectedArchetype(in RelayInfo info)
    {
        for (var i = 0; i < Archetypes.Length; i++)
        {
            if (info.IsSwitched(SwitchArchetypeBase + i))
            {
                return Archetypes[i];
            }
        }

        return Archetypes[0];
    }
}
