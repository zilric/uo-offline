// =========================================================================
// ExportHouseGump.cs — SP-054: the [exporthouse classification wizard.
// One gump rather than a chained multi-step flow (no gump anywhere in
// this codebase already uses AddPage(N) in-gump paging for a wizard -
// the established idiom for "select category, then sub-option" is either
// one gump showing every relevant control at once and reading back
// whichever was actually submitted, exactly like OrganicMarketAdminGump's
// own style+archetype radios, or chaining separate DynamicGump instances
// through OnResponse. This mirrors the former: both the Ambient/Vendor
// radio group and the archetype radio group render together: the
// archetype group is simply ignored on submit unless Vendor House is the
// selected type, matching the ticket's "Step 2 (if Vendor House)"
// wording without needing a second gump round-trip).
// =========================================================================

using System;
using Server.Gumps;
using Server.Multis;
using Server.Network;

namespace Server.Engines.OrganicMarket;

public class ExportHouseGump : DynamicGump
{
    public override bool Singleton => true;

    private const int TypeGroup = 0;
    private const int ArchetypeGroup = 1;

    private const int SwitchTypeAmbient = 0;
    private const int SwitchTypeVendor = 1;

    private const int SwitchArchetypeBase = 10; // 10..16, one per MarketArchetype value

    private const int EntryTemplateName = 0;

    private const int ButtonExport = 1;
    private const int ButtonCancel = 2;

    private static readonly MarketArchetype[] Archetypes =
        (MarketArchetype[])Enum.GetValues(typeof(MarketArchetype));

    private readonly BaseHouse _house;

    private ExportHouseGump(BaseHouse house) : base(100, 100)
    {
        _house = house;
    }

    public static void DisplayTo(Mobile from, BaseHouse house)
    {
        if (from?.NetState == null || house == null)
        {
            return;
        }

        from.SendGump(new ExportHouseGump(house));
    }

    protected override void BuildLayout(ref DynamicGumpBuilder builder)
    {
        const int width = 380;

        // Laid out top-down, sequentially - every Y below derives from the
        // one above it, so the window's own height (computed last, from
        // the final button row) always actually encloses everything.
        // Previously height was computed FIRST from a fixed guess (200 +
        // archetype block) and the buttons were pinned to height - 40,
        // which put them above the Template Name field/text entry instead
        // of below it, and left the text entry rendering past the
        // window's own bottom edge entirely.
        var archetypeStartY = 172;
        var archetypeBlockHeight = Archetypes.Length * 24;
        var nameLabelY = archetypeStartY + archetypeBlockHeight + 16;
        var nameEntryY = nameLabelY + 22;
        var buttonY = nameEntryY + 34;
        var height = buttonY + 50;

        builder.AddPage();
        builder.AddBackground(0, 0, width, height, 5054);
        builder.AddAlphaRegion(10, 10, width - 20, height - 20);

        builder.AddHtml(20, 20, width - 40, 20, "<center><basefont color=#FFD700>Export House Template</basefont></center>");
        builder.AddLabel(20, 44, 0x480, _house?.Deleted == false ? OrganicMarketSpawner.HouseTypeName(_house) : "(house)");

        builder.AddHtml(20, 68, width - 40, 20, "<basefont color=#7FFFD4>House Type</basefont>");
        builder.AddGroup(TypeGroup);
        builder.AddRadio(24, 92, 210, 211, true, SwitchTypeAmbient);
        builder.AddLabel(50, 92, 0x480, "Ambient House");
        builder.AddRadio(24, 116, 210, 211, false, SwitchTypeVendor);
        builder.AddLabel(50, 116, 0x480, "Vendor House");

        builder.AddHtml(20, 148, width - 40, 20, "<basefont color=#7FFFD4>Vendor Archetype (if Vendor House)</basefont>");
        builder.AddGroup(ArchetypeGroup);
        for (var i = 0; i < Archetypes.Length; i++)
        {
            var y = archetypeStartY + i * 24;
            builder.AddRadio(24, y, 210, 211, i == 0, SwitchArchetypeBase + i);
            builder.AddLabel(50, y, 0x480, OrganicMarketSpawner.ArchetypeName(Archetypes[i]));
        }

        builder.AddHtml(20, nameLabelY, width - 40, 20, "<basefont color=#7FFFD4>Template Name</basefont>");
        builder.AddTextEntry(24, nameEntryY, width - 60, 20, 0x480, EntryTemplateName, "");

        builder.AddButton(24, buttonY, 4005, 4007, ButtonExport);
        builder.AddLabel(60, buttonY, 0x59, "Export");

        builder.AddButton(width - 110, buttonY, 4017, 4019, ButtonCancel);
        builder.AddLabel(width - 74, buttonY, 0x480, "Cancel");
    }

    public override void OnResponse(NetState sender, in RelayInfo info)
    {
        var from = sender.Mobile;
        if (from == null || info.ButtonID != ButtonExport)
        {
            return;
        }

        if (_house?.Deleted != false)
        {
            from.SendMessage("That house is no longer available.");
            return;
        }

        var isVendor = info.IsSwitched(SwitchTypeVendor);
        MarketArchetype? archetype = null;

        if (isVendor)
        {
            for (var i = 0; i < Archetypes.Length; i++)
            {
                if (info.IsSwitched(SwitchArchetypeBase + i))
                {
                    archetype = Archetypes[i];
                    break;
                }
            }

            archetype ??= Archetypes[0];
        }

        // SP-055: a blank name never fails/aborts the export - it falls
        // back to an auto-generated, always-unique-enough name instead
        // (house type + a to-the-second UTC timestamp), so a GM in a
        // hurry can just hit Export without typing anything.
        var enteredName = info.GetTextEntry(EntryTemplateName)?.Trim();
        var name = string.IsNullOrWhiteSpace(enteredName)
            ? $"{_house.GetType().Name}_{DateTime.UtcNow:yyyyMMdd_HHmmss}"
            : enteredName;

        var (count, path) = HouseTemplateManager.ExportTemplate(_house, isVendor, archetype, name);
        from.SendMessage(0x59, $"[ExportHouse] Saved {count} item(s) to {path}.");
    }
}
