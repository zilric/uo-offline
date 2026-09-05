// =========================================================================
// HouseTemplateRegistry.cs — SP-043: groups every classic house style this
// server places into "footprint categories" — houses that share the exact
// same underlying BaseHouse subclass (and therefore the exact same
// MultiComponentList) can safely exchange decor blueprints, since a
// relative (dx, dy, dz) offset recorded against one is guaranteed to land
// on the same real tile against any other house built from the same
// class. Different multi IDs passed to the SAME class (e.g. SmallOldHouse's
// several wall textures) still share one footprint and one category;
// genuinely different classes never do, even when their names suggest a
// similar size (SandStonePatio is its own shape, not interchangeable with
// LargePatioHouse).
//
// Categorization is grounded directly in OrganicMarketSpawner.BuildHouse's
// own style-to-class switch — the actual, verified source of truth for
// which MarketHouseStyle values construct which ModernUO class — rather
// than guessed from style names alone.
// =========================================================================

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Server.Multis;

namespace Server.Engines.Housing;

// One decorative item's placement, relative to the house's own (X, Y, Z) —
// portable across any house built from the same footprint category.
public sealed class DecorItemRecord
{
    public int Dx { get; set; }
    public int Dy { get; set; }
    public int Dz { get; set; }
    public int ItemId { get; set; }
    public int Hue { get; set; }
    public string Name { get; set; }
    public bool Movable { get; set; }

    // True if this record came from a BaseAddon's own Components list
    // (a forge, anvil, water trough, ...) rather than house.LockDowns or
    // a bare stray prop. Informational only — reconstruction always
    // builds a plain generic Item regardless (see HouseDecorCommands'
    // own header comment for why complex addon-deed state is never
    // reconstructed), but it's useful for a GM eyeballing the JSON, and
    // for HouseDecorGump's own item-count breakdown.
    public bool IsAddon { get; set; }
}

// A saved [exportdecor blueprint: the exporting house's own class name
// (informational only — HouseFootprintCategory is what actually gates
// compatibility) plus every decor item it captured.
public sealed class DecorTemplate
{
    public string HouseTypeName { get; set; } = "";
    public List<DecorItemRecord> Items { get; set; } = new();
}

public enum HouseFootprintCategory
{
    // SmallOldHouse: WoodAndPlasterHouse, StoneAndPlasterHouse,
    // SmallPlasterHouse, SmallStoneHouse, SmallWoodHouse, SmallBrickHouse,
    // FieldStoneHouse — same class, seven different wall-texture multi IDs.
    SmallOldStyle,

    // SmallShop: SmallShop itself, plus StoneWorkshop/MarbleWorkshop (the
    // ticket's own "Small Forge/Smithy, Small Marble" examples).
    SmallWorkshop,

    // TwoStoryHouse: TwoStoryWoodPlaster, TwoStoryStoneAndPlaster.
    TwoStory,

    // SmallTower — its own class, no aliases.
    SmallTower,

    // LogCabin: LogCabin, TwoStoryLogCabin (a documented naming alias for
    // the same art/class — see MerchantGuildAuthority's own enum comment).
    LogCabin,

    // TwoStoryVilla — its own class, no aliases.
    Villa,

    // SandStonePatio: SandStonePatio, SandstoneHouseWithPatio (also a
    // documented alias pair).
    SandstonePatio,

    // LargePatioHouse — its own class, no aliases.
    LargePatio,

    // LargeMarbleHouse — its own class (MarbleHouseWithPatio style).
    MarbleHouseWithPatio,

    // Tower — its own class (LargeTower style).
    LargeTower,

    // Keep — its own class.
    Keep,

    // Castle — its own class.
    Castle,

    // GuildHouse — its own class (ThreeRoomBrickHouse style).
    ThreeRoomBrickHouse,

    // A house type this registry doesn't recognize (a future style added
    // to BuildHouse without a matching entry here, or a real player house
    // this system never placed) — decor tools refuse to operate on these
    // rather than guessing at compatibility.
    Unknown
}

public static class HouseTemplateRegistry
{
    private static readonly Dictionary<System.Type, HouseFootprintCategory> _categoryByType = new()
    {
        [typeof(SmallOldHouse)] = HouseFootprintCategory.SmallOldStyle,
        [typeof(SmallShop)] = HouseFootprintCategory.SmallWorkshop,
        [typeof(TwoStoryHouse)] = HouseFootprintCategory.TwoStory,
        [typeof(SmallTower)] = HouseFootprintCategory.SmallTower,
        [typeof(LogCabin)] = HouseFootprintCategory.LogCabin,
        [typeof(TwoStoryVilla)] = HouseFootprintCategory.Villa,
        [typeof(SandStonePatio)] = HouseFootprintCategory.SandstonePatio,
        [typeof(LargePatioHouse)] = HouseFootprintCategory.LargePatio,
        [typeof(LargeMarbleHouse)] = HouseFootprintCategory.MarbleHouseWithPatio,
        [typeof(Tower)] = HouseFootprintCategory.LargeTower,
        [typeof(Keep)] = HouseFootprintCategory.Keep,
        [typeof(Castle)] = HouseFootprintCategory.Castle,
        [typeof(GuildHouse)] = HouseFootprintCategory.ThreeRoomBrickHouse
    };

    public static HouseFootprintCategory CategoryOf(BaseHouse house) =>
        house != null && _categoryByType.TryGetValue(house.GetType(), out var category)
            ? category
            : HouseFootprintCategory.Unknown;

    private static string TemplatesRoot => Path.Combine(Core.BaseDirectory, "Data", "HouseTemplates");

    // Ensures the category's own directory exists before handing back its
    // path — every caller (export's save, import's list) goes through
    // here, so neither has to separately remember to create it.
    public static string DirectoryFor(HouseFootprintCategory category)
    {
        var dir = Path.Combine(TemplatesRoot, category.ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Every saved template's file name (no extension) in this category,
    // alphabetically — the exact list HouseDecorGump renders as rows.
    public static List<string> ListTemplates(HouseFootprintCategory category)
    {
        var dir = DirectoryFor(category);
        return Directory.GetFiles(dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(name => name, System.StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string TemplatePath(HouseFootprintCategory category, string name) =>
        Path.Combine(DirectoryFor(category), $"{name}.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static void SaveTemplate(HouseFootprintCategory category, string name, DecorTemplate template) =>
        File.WriteAllText(TemplatePath(category, name), JsonSerializer.Serialize(template, SerializerOptions));

    public static DecorTemplate LoadTemplate(HouseFootprintCategory category, string name)
    {
        var path = TemplatePath(category, name);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DecorTemplate>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            // A hand-edited or corrupted file — treat exactly like a
            // missing one rather than crashing the importer/gump.
            return null;
        }
    }

    // Backing "Add option to delete blueprint" — HouseDecorGump's own
    // per-row Delete button. Returns false (not an error, just nothing to
    // do) if the file was already gone.
    public static bool DeleteTemplate(HouseFootprintCategory category, string name)
    {
        var path = TemplatePath(category, name);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}
