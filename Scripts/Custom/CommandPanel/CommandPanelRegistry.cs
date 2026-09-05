// =========================================================================
// CommandPanelRegistry.cs — SP-046: a categorized registry of GM/admin
// commands, backing the unified [panel GUMP (CommandPanelGump.cs).
//
// Populated from an exhaustive grep sweep of every `CommandSystem.Register`
// call across Scripts/Custom/ and the PlayerBots CustomBots/ tree. Several
// commands the ticket assumed exist do not, under those exact names:
//   - "seedmarket"/"wipemarket" aren't commands at all — seeding and
//     wiping Organic Market houses are button actions inside the gump
//     [vh opens (OrganicMarketAdminGump), not standalone chat commands.
//   - "botcount" doesn't exist — the closest real info commands are
//     BotWhere (list by region) and BotGoals (list behaviors/goals).
//   - "destinationinfo" doesn't exist — the real commands are `destaudit`
//     and `exportdestinations`.
// Rather than register commands that don't exist (which would make the
// panel's Execute button silently fail with "that is not a command"),
// this registry lists the real, verified command names.
//
// Any future script can add its own entry with a single line:
//   CommandPanelRegistry.Register(PanelCategory.Diagnostics, "mycmd",
//       "What it does", AccessLevel.GameMaster, requiresArgs: false);
// =========================================================================

using System;
using System.Collections.Generic;
using Server;

namespace Server.Engines.CommandPanel;

public enum PanelCategory
{
    Maritime,
    OrganicMarket,
    PlayerBots,
    WaypointsHpa,
    Diagnostics
}

public record PanelCommand(string Command, string Description, AccessLevel AccessLevel, bool RequiresTargetOrArgs);

public static class CommandPanelRegistry
{
    // Declared in the fixed order the panel's tabs are drawn in.
    public static readonly PanelCategory[] Categories =
    {
        PanelCategory.Maritime,
        PanelCategory.OrganicMarket,
        PanelCategory.PlayerBots,
        PanelCategory.WaypointsHpa,
        PanelCategory.Diagnostics
    };

    public static string DisplayName(PanelCategory category) =>
        category switch
        {
            PanelCategory.Maritime     => "Maritime / Ferry",
            PanelCategory.OrganicMarket => "Organic Market",
            PanelCategory.PlayerBots   => "PlayerBots",
            PanelCategory.WaypointsHpa => "Waypoints & HPA",
            PanelCategory.Diagnostics  => "Diagnostics & GM Tools",
            _                          => category.ToString()
        };

    private static readonly Dictionary<PanelCategory, List<PanelCommand>> _entries = new();

    public static void Register(PanelCategory category, string command, string description, AccessLevel accessLevel, bool requiresArgs)
    {
        if (!_entries.TryGetValue(category, out var list))
        {
            _entries[category] = list = new List<PanelCommand>();
        }

        list.Add(new PanelCommand(command, description, accessLevel, requiresArgs));
    }

    public static IReadOnlyList<PanelCommand> GetCommands(PanelCategory category) =>
        _entries.TryGetValue(category, out var list) ? list : Array.Empty<PanelCommand>();

    public static void Configure()
    {
        // --- Maritime / Ferry --- (Scripts/Custom/FerrySystem/FerryFleetSeeder.cs)
        Register(PanelCategory.Maritime, "seedferries", "Moor all 13 charter boats and captains", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.Maritime, "wipeferries", "Remove every charter boat and captain", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.Maritime, "seedfishers", "Scatter ~60 ambient fishing boats fleet-wide", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.Maritime, "wipefishers", "Remove every ambient fishing boat and fisher", AccessLevel.GameMaster, requiresArgs: false);

        // --- Organic Market --- (Scripts/Custom/OrganicMarket/)
        Register(PanelCategory.OrganicMarket, "vh", "Open the market admin tool (seed/wipe/restock)", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.OrganicMarket, "spawnbuyer", "Spawn a guaranteed test shopper (targets a vendor)", AccessLevel.GameMaster, requiresArgs: true);
        Register(PanelCategory.OrganicMarket, "verbose", "Open the verbose logging manager", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.OrganicMarket, "sendhome", "Send a resident home (target bot then sign, or sign only)", AccessLevel.GameMaster, requiresArgs: true);
        Register(PanelCategory.OrganicMarket, "leavehome", "Check out a homeowner bot (target bot or its house sign)", AccessLevel.GameMaster, requiresArgs: true);
        Register(PanelCategory.OrganicMarket, "exportdecor", "Export a house's decor as a reusable JSON blueprint", AccessLevel.GameMaster, requiresArgs: true);
        Register(PanelCategory.OrganicMarket, "importdecor", "Apply a saved decor blueprint to a compatible house", AccessLevel.GameMaster, requiresArgs: true);

        // --- PlayerBots --- (CustomBots/)
        Register(PanelCategory.PlayerBots, "SpawnBot", "Spawn one bot of a class/tier here (needs args)", AccessLevel.GameMaster, requiresArgs: true);
        Register(PanelCategory.PlayerBots, "SpawnTestBot", "Spawn a single idle test bot here", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.PlayerBots, "BotInfo", "Inspect a bot's class/stats (targets one)", AccessLevel.GameMaster, requiresArgs: true);
        Register(PanelCategory.PlayerBots, "BotWhere", "List bots by region with coordinates", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.PlayerBots, "BotGoto", "Teleport to a named or targeted bot", AccessLevel.GameMaster, requiresArgs: true);
        Register(PanelCategory.PlayerBots, "BotGoals", "List every bot's current behavior/goal", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.PlayerBots, "BotGuilds", "List bot guilds with live member counts", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.PlayerBots, "ClearBots", "Delete bots in range (or 'all')", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.PlayerBots, "SetBotPopulation", "Set the world bot population target (needs args)", AccessLevel.Administrator, requiresArgs: true);
        Register(PanelCategory.PlayerBots, "GenerateBots", "Regenerate world PlayerBot spawners", AccessLevel.Administrator, requiresArgs: false);

        // --- Waypoints & HPA --- (CustomBots/Behaviors/, CustomBots/Nav/)
        Register(PanelCategory.WaypointsHpa, "MarkWay", "Add a waypoint here (needs a name)", AccessLevel.GameMaster, requiresArgs: true);
        Register(PanelCategory.WaypointsHpa, "RecordWaypoints", "Auto-capture waypoints as you walk (start/stop)", AccessLevel.GameMaster, requiresArgs: true);
        Register(PanelCategory.WaypointsHpa, "AuditNav", "Audit waypoints/destinations for stale links", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.WaypointsHpa, "auditedges", "Flood-check waypoint edges for blockage", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.WaypointsHpa, "buildhpa", "Force-rebuild the HPA* graph", AccessLevel.Administrator, requiresArgs: false);
        Register(PanelCategory.WaypointsHpa, "hpainfo", "Report HPA* graph size", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.WaypointsHpa, "testroute", "Test an abstract HPA* route (needs a name)", AccessLevel.GameMaster, requiresArgs: true);
        Register(PanelCategory.WaypointsHpa, "hpacomponents", "Report HPA graph connected components", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.WaypointsHpa, "ReloadWaypoints", "Reload waypoints.json from disk", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.WaypointsHpa, "ReloadDestinations", "Reload destinations.json from disk", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.WaypointsHpa, "destaudit", "Audit destinations for missing nearby vendors", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.WaypointsHpa, "exportdestinations", "Export live vendor NPCs to JSON for review", AccessLevel.GameMaster, requiresArgs: false);

        // --- Diagnostics & GM Tools --- (catch-all, CustomBots/)
        Register(PanelCategory.Diagnostics, "GmPanel", "Open the legacy GM admin panel", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.Diagnostics, "LiveMap", "Toggle the live world snapshot for the map editor", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.Diagnostics, "CombatDebug", "Toggle verbose bot combat/spell logging", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.Diagnostics, "BotDuel", "Force a bank-sitter duel nearby", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.Diagnostics, "BotTrade", "Force one bot trade scene", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.Diagnostics, "BotDanger", "List places with recent murder heat", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.Diagnostics, "BotSessions", "Show or toggle bot session-layer status", AccessLevel.GameMaster, requiresArgs: false);
        Register(PanelCategory.Diagnostics, "BotFactions", "Show faction war status / force a fight", AccessLevel.GameMaster, requiresArgs: false);
    }
}
