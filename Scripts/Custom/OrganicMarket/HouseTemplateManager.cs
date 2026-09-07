// =========================================================================
// HouseTemplateManager.cs — SP-054/SP-055: curated decor templates a GM
// exports via [exporthouse (ExportHouseCommand.cs/ExportHouseGump.cs) and
// either a world seeder auto-stamps into a freshly placed house (TryStamp,
// via OrganicMarketSpawner.PlaceHouse) or a GM manually browses and stamps
// onto an existing house ([importhouse — ImportHouseCommand.cs/
// ImportHouseGump.cs). Both paths share the same PlaceTemplate placement
// logic and the same family-aware template lookup below - "unified" per
// the ticket's own wording, not two parallel implementations.
//
// TemplatesRoot (below) always reads/writes the LIVE, running server's own
// Core.BaseDirectory/Data/HouseTemplates - i.e. server-runtime/ModernUO/
// Distribution/Data/HouseTemplates in this repo's own deploy layout, never
// the repo directly. That deployed tree is its own separate git clone
// (server-runtime/ModernUO/ has its own .git/ pointed at modernuo/
// ModernUO.git), so nothing a GM exports there is ever committed in place.
// Data/HouseTemplates/ at the true repo root is the tracked source of
// truth instead - install-server.sh's install_house_templates mirrors it
// into the deployed tree on every install/update, the same one-way
// repo -> deployed sync playerbots/data/ already uses for its own
// sub-directories. Curating a GM-exported template into the shipped set
// means copying it from the deployed tree back into that repo-root
// directory by hand and committing it - there is no automatic write-back.
//
// SP-055: this file previously coexisted with an older, separate decor
// export/import system (Scripts/Custom/Housing/HouseTemplateRegistry.cs +
// HouseDecorGump.cs, keyed by "footprint category" under
// Data/HouseTemplates/<Category>/*.json, driven by the now-removed
// [exportdecor/[importdecor commands). That system has been deleted
// outright - [exporthouse/[importhouse and this file are now the only
// house-template pipeline. Housing.HouseDecorCommands survives, trimmed
// down to just CollectDecorItems/ClearDecor (the scanning/clearing
// primitives this file and AmbientHousePurchaseGump.cs both still call
// into - cross-namespace calls from Scripts/Custom/OrganicMarket/ into
// Scripts/Custom/Housing/ remain an established, working pattern here).
//
// Reuses Housing.HouseDecorCommands.CollectDecorItems for the actual
// "scan a house for its decor" traversal (LockDowns + BaseAddon
// Components + a spatial sweep for stray immovable-on-construction props
// like Anvil/Forge - see that file's own header for why all three sources
// are real) rather than re-deriving that logic.
//
// MarketArchetype note: the SP-054 ticket's own requirements listed a
// 7-name archetype set that doesn't match this codebase's real enum
// (MerchantGuildAuthority.cs) - only BlacksmithArmory/TinkerCarpenter
// actually existed among them. Uses the real 7 (BlacksmithArmory,
// MageApothecary, ScribeLibrary, RawResources, TailorFletcher,
// TinkerCarpenter, FisherCurioBaker) throughout, since stamping has to
// match against the same archetype values OrganicMarketSpawner/
// StockTemplateEngine actually place vendors under.
//
// HouseTypeName: house.GetType().Name (e.g. "SmallOldHouse"), matching
// the convention the now-removed Housing.DecorTemplate.HouseTypeName used
// - NOT OrganicMarketSpawner.HouseTypeName(house) (a friendly display
// string keyed on multi ID, which can contain spaces and collide across
// aliased styles sharing one class).
//
// SP-055 small-house cross-style compatibility: verified directly against
// Multis/Houses/Houses.cs rather than assumed from the ticket's own
// (partly wrong - it also named a "SmallStoneWorkshop" class that doesn't
// exist; the real style names for the same class are StoneWorkshop/
// MarbleWorkshop) naming:
//   SmallOldHouse.AreaArray  = [(-3,-3,7,7), (-1,4,3,1)]
//   SmallShop.AreaArray1/2   = [(-3,-3,7,7), (-1,4,4,1) or (-2,4,3,1)]
//   SmallTower.AreaArray     = [(-3,-3,8,7), (2,4,3,1)]
// SmallOldHouse and SmallShop share the exact same 7x7 main-room rectangle
// despite being different C# classes (SmallOldHouse alone already covers
// every "Wood & Plaster/Stone & Plaster/Fieldstone/Thatched Roof/Small
// Wood/Small Brick/Small Stone" wall-texture variant under one class, and
// SmallShop alone covers SmallShop/StoneWorkshop/MarbleWorkshop the same
// way - see MerchantGuildAuthority.cs's own MarketHouseStyle catalog - so
// house.GetType().Name already unifies each of those internally with zero
// extra code). FootprintFamilies below adds the one genuine cross-CLASS
// link this ticket asks for: SmallOldHouse <-> SmallShop. SmallTower's
// main room is 8 tiles wide, not 7 - a real, different footprint - and is
// deliberately left out of the family, unlike the ticket's own grouping
// example which conflated it with the others.
//
// Vendor-branch anchor safety: OrganicMarketSpawner.SpawnVendors already
// has a complete fallback for a null anchor (falls through to
// InteriorTileFinder.TryFindVendorSpots for every vendor, not just the
// primary - confirmed by direct inspection) - DynamicClutterGenerator.
// Furnish's own return value is therefore NOT a hard dependency, so
// stamping a template can cleanly REPLACE the procedural Furnish/
// FurnishResidential call entirely (an either/or swap) rather than
// needing to layer curated decor on top of - and risk visually colliding
// with - procedural counter/floor placement. See
// OrganicMarketSpawner.PlaceHouse for the actual call site.
//
// No persistent cache: a directory listing is cheap and only ever runs
// once per successfully placed house or GM [importhouse click (not a hot
// per-tick path).
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Server.Engines.Housing;
using Server.Multis;

namespace Server.Engines.OrganicMarket;

public sealed class HouseTemplateItemRecord
{
    public int Dx { get; set; }
    public int Dy { get; set; }
    public int Dz { get; set; }
    public int ItemId { get; set; }
    public int Hue { get; set; }
    public string Name { get; set; }
    public bool Movable { get; set; }
    public bool Locked { get; set; }
    public bool IsAddon { get; set; }
}

public sealed class HouseTemplateFile
{
    public string HouseTypeName { get; set; } = "";
    public List<HouseTemplateItemRecord> Items { get; set; } = new();
}

// Identifies one specific saved template unambiguously: which
// classification directory it lives under (Vendor+Archetype or Ambient),
// which house-type subfolder (its OWN exporting house type - may differ
// from the house it's about to be stamped onto, for a cross-family
// candidate), and its file name. ListAvailableTemplates hands these to
// ImportHouseGump; StampTemplate consumes one back.
public readonly record struct TemplateHandle(bool IsVendor, MarketArchetype? Archetype, string HouseTypeName, string Name);

public static class HouseTemplateManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private static string TemplatesRoot => Path.Combine(Core.BaseDirectory, "Data", "HouseTemplates");

    // Small-house footprint family - see file header for the verified
    // Area-rectangle math behind this grouping. Symmetric by construction
    // (every member maps to the same full member list, itself included),
    // so FamilyMembersFor never needs a separate "is X in Y's family"
    // direction check.
    private static readonly Dictionary<string, string[]> FootprintFamilies = new()
    {
        ["SmallOldHouse"] = new[] { "SmallOldHouse", "SmallShop" },
        ["SmallShop"] = new[] { "SmallOldHouse", "SmallShop" }
    };

    private static string[] FamilyMembersFor(string houseTypeName) =>
        FootprintFamilies.TryGetValue(houseTypeName, out var members) ? members : new[] { houseTypeName };

    // Data/HouseTemplates/Vendor/{Archetype}/{HouseTypeName}/ or
    // Data/HouseTemplates/Ambient/{HouseTypeName}/, created on demand -
    // every caller (export's save, a listing) goes through here, so none
    // of them has to separately remember to create it.
    public static string DirectoryFor(bool isVendor, MarketArchetype? archetype, string houseTypeName)
    {
        var dir = isVendor
            ? Path.Combine(TemplatesRoot, "Vendor", archetype?.ToString() ?? "Unknown", houseTypeName)
            : Path.Combine(TemplatesRoot, "Ambient", houseTypeName);

        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string TemplatePath(bool isVendor, MarketArchetype? archetype, string houseTypeName, string name) =>
        Path.Combine(DirectoryFor(isVendor, archetype, houseTypeName), $"{name}.json");

    private static List<string> ListTemplates(bool isVendor, MarketArchetype? archetype, string houseTypeName)
    {
        var dir = DirectoryFor(isVendor, archetype, houseTypeName);
        return Directory.GetFiles(dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .ToList();
    }

    private static HouseTemplateFile LoadTemplate(bool isVendor, MarketArchetype? archetype, string houseTypeName, string name)
    {
        var path = TemplatePath(isVendor, archetype, houseTypeName, name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<HouseTemplateFile>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            // A hand-edited or corrupted file - treat exactly like a
            // missing one rather than crashing the seeder pass/gump.
            return null;
        }
    }

    // Every candidate template for `houseTypeName`, aggregated across its
    // whole footprint family (itself alone, unless it has family-mates -
    // see FootprintFamilies).
    private static List<(string HouseTypeName, string Name)> ListTemplatesForFamily(
        bool isVendor, MarketArchetype? archetype, string houseTypeName
    )
    {
        var results = new List<(string, string)>();
        foreach (var member in FamilyMembersFor(houseTypeName))
        {
            foreach (var name in ListTemplates(isVendor, archetype, member))
            {
                results.Add((member, name));
            }
        }

        return results;
    }

    // Every template compatible with `house`, across BOTH classifications
    // (Ambient, and Vendor under every archetype) and its whole footprint
    // family - what ImportHouseGump renders as its picker list.
    public static List<TemplateHandle> ListAvailableTemplates(BaseHouse house)
    {
        var results = new List<TemplateHandle>();
        if (house?.Deleted != false)
        {
            return results;
        }

        var houseTypeName = house.GetType().Name;

        foreach (var (member, name) in ListTemplatesForFamily(false, null, houseTypeName))
        {
            results.Add(new TemplateHandle(false, null, member, name));
        }

        foreach (MarketArchetype archetype in Enum.GetValues(typeof(MarketArchetype)))
        {
            foreach (var (member, name) in ListTemplatesForFamily(true, archetype, houseTypeName))
            {
                results.Add(new TemplateHandle(true, archetype, member, name));
            }
        }

        return results;
    }

    // Called from [importhouse's own gump (ImportHouseGump's per-row
    // Delete button) to remove one unwanted saved template file.
    public static bool DeleteTemplate(TemplateHandle template)
    {
        var path = TemplatePath(template.IsVendor, template.Archetype, template.HouseTypeName, template.Name);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    // Called from [exporthouse's own gump (ExportHouseGump.OnResponse)
    // after the GM has classified the house and named (or left blank -
    // ExportHouseGump itself fills in a fallback name) the template.
    public static (int Count, string Path) ExportTemplate(
        BaseHouse house, bool isVendor, MarketArchetype? archetype, string templateName
    )
    {
        var houseTypeName = house.GetType().Name;
        var template = new HouseTemplateFile { HouseTypeName = houseTypeName };

        foreach (var (item, isAddon) in HouseDecorCommands.CollectDecorItems(house))
        {
            template.Items.Add(new HouseTemplateItemRecord
            {
                Dx = item.X - house.X,
                Dy = item.Y - house.Y,
                Dz = item.Z - house.Z,
                ItemId = item.ItemID,
                Hue = item.Hue,
                Name = item.Name,
                Movable = item.Movable,
                Locked = item.IsLockedDown,
                IsAddon = isAddon
            });
        }

        var name = SanitizeFileName(templateName);
        var path = TemplatePath(isVendor, archetype, houseTypeName, name);
        File.WriteAllText(path, JsonSerializer.Serialize(template, SerializerOptions));

        return (template.Items.Count, path);
    }

    // Called from OrganicMarketSpawner.PlaceHouse - archetype: null means
    // an ambient/filler house, matching PlaceHouse's own MarketArchetype?
    // convention. Picks a random compatible template (including any
    // footprint-family cross-style match) and stamps it; returns false
    // (do nothing further) whenever none exist, so the caller falls back
    // to the existing procedural DynamicClutterGenerator pass exactly as
    // it always has.
    //
    // Pool asymmetry (SP-056): a Vendor house only ever draws from its
    // own archetype's pool - a shop should look like the trade it sells.
    // An Ambient house has no archetype of its own to narrow by, so it
    // draws from the FULL compatible pool instead - every Vendor
    // archetype's templates as well as Ambient ones - since a curated
    // shop layout reads just as validly as "someone's residence" as a
    // dedicated ambient template does.
    public static bool TryStamp(BaseHouse house, MarketArchetype? archetype, Mobile authority)
    {
        if (house?.Deleted != false || house.Map == null || house.Map == Map.Internal || authority == null)
        {
            return false;
        }

        List<TemplateHandle> candidates;
        if (archetype is { } a)
        {
            // Vendor house: scoped to its own archetype's pool (+ family)
            // only - an archer's shop shouldn't get stamped with a
            // blacksmith's counter layout.
            candidates = new List<TemplateHandle>();
            foreach (var (member, name) in ListTemplatesForFamily(true, a, house.GetType().Name))
            {
                candidates.Add(new TemplateHandle(true, a, member, name));
            }
        }
        else
        {
            // Ambient/filler house: the WHOLE compatible pool, vendor
            // templates (any archetype) and ambient templates alike - an
            // ambient house has no archetype of its own to narrow by, and
            // stamping it with, say, a blacksmith's curated shop layout
            // reads as "someone set up shop here" just as validly as an
            // ambient-classified template does. Reuses
            // ListAvailableTemplates' own aggregation - the exact same
            // candidate set [importhouse's GM picker already offers for
            // this house, rather than a second, parallel aggregation.
            candidates = ListAvailableTemplates(house);
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        var pick = candidates[Utility.Random(candidates.Count)];
        var template = LoadTemplate(pick.IsVendor, pick.Archetype, pick.HouseTypeName, pick.Name);
        if (template?.Items is not { Count: > 0 })
        {
            return false;
        }

        PlaceTemplate(house, authority, template);
        return true;
    }

    // Called from [importhouse's own gump (ImportHouseGump.OnResponse)
    // once the GM has picked one specific template from
    // ListAvailableTemplates' own results - unlike TryStamp, this always
    // clears the house's existing decor first (the ticket's own "selecting
    // a template immediately clears existing decor... and stamps the
    // template" requirement - TryStamp never needs this, since it only
    // ever runs once, against a freshly placed and still-empty house).
    // house.Owner is the lockdown owner - matches the now-removed
    // HouseDecorCommands.ApplyTemplate's own precedent exactly, and works
    // whether the house still belongs to MerchantGuildAuthority or a real
    // player.
    public static bool StampTemplate(BaseHouse house, TemplateHandle template)
    {
        if (house?.Deleted != false || house.Map == null || house.Map == Map.Internal || house.Owner == null)
        {
            return false;
        }

        var file = LoadTemplate(template.IsVendor, template.Archetype, template.HouseTypeName, template.Name);
        if (file?.Items is not { Count: > 0 })
        {
            return false;
        }

        HouseDecorCommands.ClearDecor(house);
        PlaceTemplate(house, house.Owner, file);
        return true;
    }

    // Shared placement logic both TryStamp and StampTemplate reduce to -
    // same convention DynamicClutterGenerator's own helpers already use
    // (PlaceCounter/DressCounter/FurnishFloor/FurnishItems): lock each
    // item down under `lockdownOwner` (which also sets Movable = false on
    // success), delete rather than leave an untracked loose item behind
    // if the house's lockdown capacity is already exhausted.
    private static void PlaceTemplate(BaseHouse house, Mobile lockdownOwner, HouseTemplateFile template)
    {
        foreach (var record in template.Items)
        {
            var item = new Item(record.ItemId) { Hue = record.Hue };
            if (!string.IsNullOrEmpty(record.Name))
            {
                item.Name = record.Name;
            }

            item.MoveToWorld(new Point3D(house.X + record.Dx, house.Y + record.Dy, house.Z + record.Dz), house.Map);

            if (!house.LockDown(lockdownOwner, item, false))
            {
                item.Delete();
            }
        }
    }

    // GM-supplied template names become file names - strip anything that
    // isn't safe across the filesystems this server might run on.
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            buffer[i] = Array.IndexOf(invalid, c) >= 0 ? '_' : c;
        }

        return new string(buffer);
    }
}
