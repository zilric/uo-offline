// =========================================================================
// HouseDecorGump.cs — SP-043/SP-044: the blueprint picker [importdecor
// opens once a target house's footprint category is known. Lists every
// saved template in that category (name, item count including how many
// are addon-sourced, a small item-ID icon preview) with an Apply button
// and a Delete button per row; picking Apply hands off to
// HouseDecorCommands.ApplyTemplate for the actual clear-and-place work,
// the same "gump only picks, a static method does the work" split
// AmbientHousePurchaseGump/AmbientHousePurchaseGump.TryPurchase already
// uses. Delete removes the JSON file directly (HouseTemplateRegistry.
// DeleteTemplate) and refreshes the list in place.
// =========================================================================

using System;
using System.Collections.Generic;
using Server.Gumps;
using Server.Multis;
using Server.Network;

namespace Server.Engines.Housing;

public class HouseDecorGump : DynamicGump
{
    public override bool Singleton => true;

    private const int ApplyButtonBase = 10;
    private const int MaxPreviewIcons = 4;

    private readonly BaseHouse _house;
    private readonly HouseFootprintCategory _category;
    private readonly List<string> _templateNames;

    // Delete buttons start right after every Apply button, so both ranges
    // are always derived from the one list backing this gump instance —
    // never a magic gap that could someday collide with a growing list.
    private int DeleteButtonBase => ApplyButtonBase + _templateNames.Count;

    private HouseDecorGump(BaseHouse house, HouseFootprintCategory category, List<string> templateNames) : base(50, 50)
    {
        _house = house;
        _category = category;
        _templateNames = templateNames;
    }

    public static void DisplayTo(Mobile from, BaseHouse house, HouseFootprintCategory category, List<string> templateNames)
    {
        if (from?.NetState == null || house?.Deleted != false || templateNames is not { Count: > 0 })
        {
            return;
        }

        from.SendGump(new HouseDecorGump(house, category, templateNames));
    }

    protected override void BuildLayout(ref DynamicGumpBuilder builder)
    {
        const int width = 460;
        const int rowHeight = 60;

        var height = 110 + _templateNames.Count * rowHeight + 20;

        builder.AddPage();
        builder.AddBackground(0, 0, width, height, 5054);
        builder.AddAlphaRegion(10, 10, width - 20, height - 20);

        builder.AddHtml(20, 20, width - 40, 20, $"<center><basefont color=#FFD700>Decor Blueprints — {_category}</basefont></center>");
        builder.AddHtml(
            20, 44, width - 40, 20,
            "<basefont color=#AAAAAA>Apply clears this house's current decor and places the blueprint. Delete removes the file.</basefont>"
        );

        var y = 76;
        for (var i = 0; i < _templateNames.Count; i++)
        {
            var name = _templateNames[i];
            var template = HouseTemplateRegistry.LoadTemplate(_category, name);
            var itemCount = template?.Items.Count ?? 0;
            var addonCount = template?.Items.FindAll(r => r.IsAddon).Count ?? 0;

            builder.AddButton(24, y + 6, 4005, 4007, ApplyButtonBase + i);
            builder.AddHtml(
                60, y, width - 160, 20,
                $"<basefont color=#FFFFFF>{name}</basefont>  " +
                $"<basefont color=#888888>({itemCount} item(s){(addonCount > 0 ? $", {addonCount} addon" : "")})</basefont>"
            );

            builder.AddButton(width - 60, y + 2, 4020, 4022, DeleteButtonBase + i);
            builder.AddLabel(width - 42, y, 0x480, "Del");

            var iconX = 60;
            var previewCount = Math.Min(MaxPreviewIcons, template?.Items.Count ?? 0);
            for (var p = 0; p < previewCount; p++)
            {
                var record = template.Items[p];
                builder.AddItem(iconX, y + 20, record.ItemId, record.Hue);
                iconX += 34;
            }

            y += rowHeight;
        }
    }

    public override void OnResponse(NetState sender, in RelayInfo info)
    {
        var from = sender.Mobile;
        if (from == null)
        {
            return;
        }

        var applyIndex = info.ButtonID - ApplyButtonBase;
        if (applyIndex >= 0 && applyIndex < _templateNames.Count)
        {
            HouseDecorCommands.ApplyTemplate(from, _house, _category, _templateNames[applyIndex]);
            return;
        }

        var deleteIndex = info.ButtonID - DeleteButtonBase;
        if (deleteIndex < 0 || deleteIndex >= _templateNames.Count)
        {
            return;
        }

        var name = _templateNames[deleteIndex];
        HouseTemplateRegistry.DeleteTemplate(_category, name);
        from.SendMessage(0x22, $"[ImportDecor] Deleted blueprint '{name}'.");

        var remaining = HouseTemplateRegistry.ListTemplates(_category);
        if (remaining.Count > 0)
        {
            DisplayTo(from, _house, _category, remaining);
        }
        else
        {
            from.SendMessage("No blueprints remain in this category.");
        }
    }
}
