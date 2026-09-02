// =========================================================================
// VerboseConfig.cs — SP-032: centralized, config-backed logging category
// manager. Persists to verbose.cfg in the server's runtime root
// (Core.BaseDirectory) as simple `Category=true/false` lines, so an
// operator can toggle chatty subsystems on disk OR in-game via
// [verbose/VerboseConfigGump without touching source or restarting.
//
// Not every category has a live consumer yet: MarketSeeder/VendorStock/
// WorldHousing gate real logging in WorldHouseSeeder.cs/
// StockTemplateEngine.cs/MerchantGuildAuthority.cs (all under
// Scripts/Custom/OrganicMarket/), while Crafting/Pathfinding are reserved
// for the PlayerBots subsystem, which lives outside Scripts/Custom/ and
// is deliberately left untouched this sprint (see the workspace rule in
// SP-032's own ticket). Their flags are still fully live/toggleable/
// persisted - just unread until something in that subsystem checks them.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using Server.Logging;

namespace Server.Engines.OrganicMarket;

public static class VerboseConfig
{
    private const string FileName = "verbose.cfg";

    private static readonly ILogger logger = LogFactory.GetLogger(typeof(VerboseConfig));

    // The one place every category's name, on-disk comment, and default
    // (spam-heavy = false, critical = true) is defined - Save/Reload and
    // VerboseConfigGump's row list all walk this instead of duplicating
    // the category set.
    public static readonly (string Name, string Comment, bool Default)[] Categories =
    {
        (
            "MarketSeeder",
            "Crossroads/world-inhabitation node scanning, house evaluation, and placement passes (WorldHouseSeeder).",
            false
        ),
        (
            "VendorStock",
            "Restock cycles, vendor slot population, and item pricing (StockTemplateEngine, MerchantGuildAuthority).",
            false
        ),
        (
            "Crafting",
            "Crafting bot transactions and item creation loops (PlayerBots).",
            false
        ),
        (
            "Pathfinding",
            "Bot movement loops and navigation debug (PlayerBots).",
            false
        ),
        (
            "WorldHousing",
            "Sign purchase events and teardown sweeps (MerchantGuildAuthority, AmbientHousePurchaseGump).",
            true
        )
    };

    private static readonly Dictionary<string, bool> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static bool MarketSeeder => Get("MarketSeeder");
    public static bool VendorStock => Get("VendorStock");
    public static bool Crafting => Get("Crafting");
    public static bool Pathfinding => Get("Pathfinding");
    public static bool WorldHousing => Get("WorldHousing");

    // Generic lookup - falls back to that category's own documented
    // default if somehow asked about a name not in _flags (shouldn't
    // happen post-Initialize, but never throws on an unknown category).
    public static bool Get(string category)
    {
        if (_flags.TryGetValue(category, out var value))
        {
            return value;
        }

        foreach (var (name, _, def) in Categories)
        {
            if (string.Equals(name, category, StringComparison.OrdinalIgnoreCase))
            {
                return def;
            }
        }

        return false;
    }

    public static void Set(string category, bool value) => _flags[category] = value;

    private static string FilePath => Path.Combine(Core.BaseDirectory, FileName);

    // Auto-discovered by AssemblyHandler.Invoke("Initialize") post-world-load
    // (same hook MerchantGuildAuthority.Initialize uses). Generates a fresh,
    // commented default file on a brand-new server; an existing file is
    // read as-is via Reload.
    public static void Initialize()
    {
        if (!File.Exists(FilePath))
        {
            foreach (var (name, _, def) in Categories)
            {
                _flags[name] = def;
            }

            Save();
            logger.Information("VerboseConfig: no {File} found - wrote defaults", FileName);
            return;
        }

        Reload();
    }

    // Writes every category's CURRENT in-memory flag (falling back to its
    // own default for any category Reload/Initialize never populated) to
    // disk as a clean, commented file - called on every toggle from
    // VerboseConfigGump so disk state never lags behind what a GM sees.
    public static void Save()
    {
        using var writer = new StreamWriter(FilePath, false);

        writer.WriteLine("# verbose.cfg - OrganicMarket logging category toggles (SP-032).");
        writer.WriteLine("# Generated/maintained by VerboseConfig - edit by hand, or in-game via [verbose.");
        writer.WriteLine("# One `Category=true` or `Category=false` pair per line. Lines starting with # are comments.");
        writer.WriteLine();

        foreach (var (name, comment, def) in Categories)
        {
            writer.WriteLine($"# {comment}");
            writer.WriteLine($"{name}={(_flags.GetValueOrDefault(name, def) ? "true" : "false")}");
            writer.WriteLine();
        }
    }

    // Re-reads verbose.cfg from disk and replaces every in-memory flag -
    // any category missing from the file (a hand-edited file, or one from
    // before a category was added) falls back to that category's own
    // documented default rather than silently staying false.
    public static void Reload()
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        var parsed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in File.ReadAllLines(FilePath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            parsed[key] = value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        foreach (var (name, _, def) in Categories)
        {
            _flags[name] = parsed.GetValueOrDefault(name, def);
        }

        logger.Information("VerboseConfig: reloaded {File}", FileName);
    }
}
