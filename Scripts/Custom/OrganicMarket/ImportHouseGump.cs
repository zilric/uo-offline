// =========================================================================
// ImportHouseGump.cs — SP-055: the [importhouse picker. Lists every
// template HouseTemplateManager.ListAvailableTemplates found compatible
// with the targeted house (category, source archetype if Vendor, and the
// exporting house type - relevant when a row is a footprint-family
// cross-style match rather than an exact type match). Picking one calls
// HouseTemplateManager.StampTemplate directly, which clears the house's
// existing decor first and stamps the template in its place - no
// separate "Apply" confirmation step, matching the ticket's own
// "selecting a template immediately... stamps" wording.
//
// Each row also carries a far-right Delete button (mirrors the now-
// removed Housing.HouseDecorGump's own Apply/Delete row shape) so a GM
// can prune an unwanted saved template file without needing a separate
// command - deletes the JSON via HouseTemplateManager.DeleteTemplate,
// then redisplays the gump with the fresh, shorter list.
// =========================================================================

using System.Collections.Generic;
using Server.Gumps;
using Server.Multis;
using Server.Network;

namespace Server.Engines.OrganicMarket;

public class ImportHouseGump : DynamicGump
{
    public override bool Singleton => true;

    private const int ApplyButtonBase = 10;

    private readonly BaseHouse _house;
    private readonly List<TemplateHandle> _templates;

    // Delete buttons start right after every Apply button, so both
    // ranges are always derived from the one list backing this gump
    // instance - never a magic gap that could someday collide with a
    // growing list (same idiom the now-removed Housing.HouseDecorGump
    // used for its own Apply/Delete button ranges).
    private int DeleteButtonBase => ApplyButtonBase + _templates.Count;

    private ImportHouseGump(BaseHouse house, List<TemplateHandle> templates) : base(50, 50)
    {
        _house = house;
        _templates = templates;
    }

    public static void DisplayTo(Mobile from, BaseHouse house, List<TemplateHandle> templates)
    {
        if (from?.NetState == null || house?.Deleted != false || templates is not { Count: > 0 })
        {
            return;
        }

        from.SendGump(new ImportHouseGump(house, templates));
    }

    protected override void BuildLayout(ref DynamicGumpBuilder builder)
    {
        const int width = 480;
        const int rowHeight = 26;

        var height = 100 + _templates.Count * rowHeight + 20;

        builder.AddPage();
        builder.AddBackground(0, 0, width, height, 5054);
        builder.AddAlphaRegion(10, 10, width - 20, height - 20);

        builder.AddHtml(20, 20, width - 40, 20, $"<center><basefont color=#FFD700>Import House Template — {_house.GetType().Name}</basefont></center>");
        builder.AddHtml(
            20, 44, width - 40, 32,
            "<basefont color=#AAAAAA>Selecting a template clears this house's current decor and stamps the template in its place.</basefont>"
        );

        var y = 80;
        for (var i = 0; i < _templates.Count; i++)
        {
            var t = _templates[i];
            var category = t.IsVendor ? OrganicMarketSpawner.ArchetypeName(t.Archetype!.Value) : "Ambient";
            var crossStyle = t.HouseTypeName == _house.GetType().Name ? "" : $" <basefont color=#FFAA55>[from {t.HouseTypeName}]</basefont>";

            builder.AddButton(24, y + 2, 4005, 4007, ApplyButtonBase + i);
            builder.AddHtml(
                60, y, width - 170, 20,
                $"<basefont color=#FFFFFF>{t.Name}</basefont> " +
                $"<basefont color=#888888>({category})</basefont>{crossStyle}"
            );

            builder.AddButton(width - 60, y + 2, 4020, 4022, DeleteButtonBase + i);
            builder.AddLabel(width - 42, y, 0x480, "Del");

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
        if (applyIndex >= 0 && applyIndex < _templates.Count)
        {
            if (_house?.Deleted != false)
            {
                from.SendMessage("That house is no longer available.");
                return;
            }

            var handle = _templates[applyIndex];
            if (HouseTemplateManager.StampTemplate(_house, handle))
            {
                from.SendMessage(0x59, $"[ImportHouse] Stamped '{handle.Name}' onto this house.");
            }
            else
            {
                from.SendMessage(0x22, "[ImportHouse] Could not stamp that template (missing file, or house unavailable).");
            }

            return;
        }

        var deleteIndex = info.ButtonID - DeleteButtonBase;
        if (deleteIndex < 0 || deleteIndex >= _templates.Count)
        {
            return;
        }

        var toDelete = _templates[deleteIndex];
        if (HouseTemplateManager.DeleteTemplate(toDelete))
        {
            from.SendMessage(0x22, $"[ImportHouse] Deleted template '{toDelete.Name}'.");
        }
        else
        {
            from.SendMessage(0x22, $"[ImportHouse] Template '{toDelete.Name}' was already gone.");
        }

        if (_house?.Deleted != false)
        {
            return;
        }

        var remaining = HouseTemplateManager.ListAvailableTemplates(_house);
        if (remaining.Count > 0)
        {
            DisplayTo(from, _house, remaining);
        }
        else
        {
            from.SendMessage("No templates remain for this house.");
        }
    }
}
