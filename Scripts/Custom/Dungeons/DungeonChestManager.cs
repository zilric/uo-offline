// =========================================================================
// DungeonChestManager.cs — SP-049: discovers every already-placed
// LockableContainer sitting inside a DungeonRegion, tracks it, and keeps
// it stocked on an ongoing anti-camp-aware restock cycle. Nothing here
// spawns a chest or touches multi/static map tiles - every container this
// manages already exists as a real Item somewhere in the world; this only
// manages its Locked/TrapType/TrapPower state and contents.
//
// Discovery is a one-time (well: once at boot, once per [restockdungeonchests
// call) walk of World.Items.Values, not a per-tick scan - the same
// "infrequent snapshot over the live collection" idiom already established
// throughout Scripts/Custom/OrganicMarket/ (MerchantGuildAuthority,
// AmbientHouseManager) for exactly this kind of GM-command/maintenance-
// timer query, never a hot path.
//
// SP-049-Patch: tier is now derived from the container's real world
// coordinates against known classic Britannia dungeon footprints (Shame,
// Despise, Destard, Covetous, Deceit, Wrong, Hythloth, Fire, Ice), each
// sliced into its own sub-region tier band - replacing the original Z-depth
// heuristic entirely. Anything outside a mapped footprint (an unmapped
// cave, or off Felucca/Trammel entirely) falls back to tier 2. This
// intentionally doesn't depend on the core CustomBots DungeonRegistry/
// BotDestination waypoint data (a much heavier, differently-shaped dataset
// built for bot navigation, not container tiering) - this stays
// self-contained and works even if that data isn't loaded.
// =========================================================================

using System;
using System.Collections.Generic;
using Server.Commands;
using Server.Items;
using Server.Logging;
using Server.Regions;

namespace Server.Engines.Dungeons;

public static class DungeonChestManager
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(DungeonChestManager));

    // 1-in-10 chests are found unlocked/untrapped, per the ticket.
    private const double UnlockedChance = 0.10;

    // Roughly 1 in 3 restock cycles (including the very first fill) rolls
    // active loot; the other 2 in 3 leave the chest completely empty - an
    // abandoned, already-looted box. Lowered from 0.50 per SP-050.
    private const double ActiveFillChance = 0.33;

    // "No player or bot within 3 tiles" per the ticket - GetMobilesInRange
    // returns every Mobile regardless of type, so this covers both without
    // needing to distinguish them.
    private const int AntiCampRadius = 3;

    // How often the sweep looks for newly-emptied containers to start a
    // restock timer on. Cheap: it only ever walks the already-discovered
    // chest list (at most a few hundred entries), never the whole world.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);

    // If a restock attempt finds the area occupied, try again this soon
    // instead of waiting for the next full 20-40 minute cycle.
    private static readonly TimeSpan AntiCampRetryDelay = TimeSpan.FromMinutes(2);

    private sealed class ChestState
    {
        public LockableContainer Container;
        public int Tier;
        public bool RestockPending;
    }

    private static readonly List<ChestState> _chests = new();
    private static Timer _sweepTimer;

    public static void Configure()
    {
        CommandSystem.Register("restockdungeonchests", AccessLevel.GameMaster, OnRestockDungeonChests);
    }

    // Auto-discovered by AssemblyHandler.Invoke("Initialize") post-world-
    // load, the same convention every other Scripts/Custom Initialize()
    // already uses (VerboseConfig, MerchantGuildAuthority, ...).
    public static void Initialize()
    {
        Discover();

        foreach (var state in _chests)
        {
            RollAndFill(state.Container, state.Tier);
        }

        _sweepTimer ??= Timer.DelayCall(SweepInterval, SweepInterval, Sweep);

        logger.Information("DungeonChestManager: discovered and stocked {Count} dungeon container(s)", _chests.Count);
    }

    [Usage("restockdungeonchests")]
    [Description("Re-discovers every dungeon chest, clears spent/empty ones, re-rolls lock/trap state, and regenerates tier-scaled multi-item loot.")]
    private static void OnRestockDungeonChests(CommandEventArgs e)
    {
        Discover();

        var unlocked = 0;
        foreach (var state in _chests)
        {
            if (state.Container?.Deleted != false)
            {
                continue;
            }

            RollAndFill(state.Container, state.Tier);

            if (!state.Container.Locked)
            {
                unlocked++;
            }
        }

        var total = _chests.Count;
        var locked = total - unlocked;
        var message = $"Dungeon Chests: Restocked {total} containers ({unlocked} unlocked, {locked} locked/trapped)";

        e.Mobile?.SendMessage(0x59, message);
        logger.Information(message);
    }

    // One-time (per call) snapshot walk - see file header for why this is
    // the sanctioned exception to "no World.Items iteration."
    private static void Discover()
    {
        _chests.Clear();

        foreach (var item in World.Items.Values)
        {
            if (item is not LockableContainer container || container.Deleted)
            {
                continue;
            }

            var map = container.Map;
            if (map == null || map == Map.Internal)
            {
                continue;
            }

            var region = Region.Find(container.Location, map);
            if (!region.IsPartOf<DungeonRegion>())
            {
                continue;
            }

            _chests.Add(
                new ChestState
                {
                    Container = container,
                    Tier = GetDungeonTier(container)
                }
            );
        }
    }

    // Classic Britannia dungeon coordinate slices, each sliced into its own
    // tier band. Coordinates cover the well-known dungeon footprints only -
    // anything else (an unmapped cave, or off Felucca/Trammel) falls back
    // to tier 2.
    private static int GetDungeonTier(LockableContainer chest)
    {
        var p = chest.Location;
        var map = chest.Map;

        if (map != Map.Felucca && map != Map.Trammel)
        {
            return 2;
        }

        // SHAME (5376,0 to 5888,511)
        if (p.X >= 5376 && p.X <= 5888 && p.Y >= 0 && p.Y <= 511)
        {
            if (p.X <= 5632 && p.Y <= 255)
            {
                return 1; // L1: Mud Golems / Earth Elementals
            }

            if (p.X > 5632 && p.Y <= 255)
            {
                return 2; // L2: Lake / Mage Tower
            }

            if (p.X <= 5632 && p.Y > 255)
            {
                return 3; // L3: Air / Poison Elementals
            }

            return 4; // L4/L5: Blood Elementals / Crystals
        }

        // DESPISE (5376,512 to 5888,1024)
        if (p.X >= 5376 && p.X <= 5888 && p.Y >= 512 && p.Y <= 1024)
        {
            if (p.Y <= 680)
            {
                return 1; // L1: Despise entrance / Troglodytes / Lizardmen
            }

            if (p.Y <= 840)
            {
                return 2; // L2: Ettins / Earth Elementals
            }

            return 3; // L3: Ogre Lords / Acid Elementals
        }

        // DESTARD (5120,768 to 5375,1024)
        if (p.X >= 5120 && p.X <= 5375 && p.Y >= 768 && p.Y <= 1024)
        {
            if (p.Y <= 830)
            {
                return 2; // L1: Drakes / Swarm
            }

            if (p.Y <= 930)
            {
                return 3; // L2: Dragons / Wyverns
            }

            return 4; // L3: Ancient Wyrm Lair / Shadow Wyrms
        }

        // COVETOUS (5376,1536 to 5888,2048)
        if (p.X >= 5376 && p.X <= 5888 && p.Y >= 1536 && p.Y <= 2048)
        {
            if (p.Y >= 1850)
            {
                return 1; // L1: Harpies / Brigands
            }

            if (p.Y >= 1700)
            {
                return 2; // L2: Gazers / Skeletons
            }

            if (p.X <= 5632)
            {
                return 3; // L3: Liches / Water Elementals
            }

            return 4; // L4: Dread Horn / Lake Depths
        }

        // DECEIT (5120,512 to 5375,768)
        if (p.X >= 5120 && p.X <= 5375 && p.Y >= 512 && p.Y <= 768)
        {
            if (p.Y <= 580)
            {
                return 1; // L1: Skeletons / Zombies
            }

            if (p.Y <= 650)
            {
                return 2; // L2: Mummies / Ghouls
            }

            if (p.Y <= 720)
            {
                return 3; // L3: Liches / Bone Knights
            }

            return 4; // L4: Lich Lords / Poison Elementals
        }

        // WRONG (5632,512 to 5888,768)
        if (p.X >= 5632 && p.X <= 5888 && p.Y >= 512 && p.Y <= 768)
        {
            if (p.Y <= 630)
            {
                return 2; // L1: Jail cells / Lizardmen
            }

            return 3; // L2: Executioners / Ogre Cooks
        }

        // HYTHLOTH (5888,0 to 6144,512)
        if (p.X >= 5888 && p.X <= 6144 && p.Y >= 0 && p.Y <= 512)
        {
            if (p.Y <= 128)
            {
                return 2; // L1: Gargoyles / Hellhounds
            }

            if (p.Y <= 256)
            {
                return 3; // L2: Daemons / Stone Gargoyles
            }

            return 4; // L3/L4: Balrons / Imp Lairs
        }

        // FIRE DUNGEON (5632,1280 to 5888,1535)
        if (p.X >= 5632 && p.X <= 5888 && p.Y >= 1280 && p.Y <= 1535)
        {
            return p.Y <= 1400 ? 2 : 3;
        }

        // ICE DUNGEON (5120,128 to 5375,384)
        if (p.X >= 5120 && p.X <= 5375 && p.Y >= 128 && p.Y <= 384)
        {
            return p.Y <= 250 ? 2 : 3;
        }

        return 2; // Default baseline for unmapped cave regions
    }

    private static void Sweep()
    {
        foreach (var state in _chests)
        {
            var container = state.Container;
            if (container?.Deleted != false || state.RestockPending)
            {
                continue;
            }

            if (container.Items.Count == 0)
            {
                state.RestockPending = true;
                var delay = TimeSpan.FromMinutes(Utility.RandomMinMax(20, 40));
                Timer.DelayCall(delay, () => TryRestock(state));
            }
        }
    }

    private static void TryRestock(ChestState state)
    {
        var container = state.Container;
        if (container?.Deleted != false)
        {
            state.RestockPending = false;
            return;
        }

        if (IsOccupied(container))
        {
            // Anti-camp: leave RestockPending set and just check back
            // again shortly, rather than replenishing under a camper.
            Timer.DelayCall(AntiCampRetryDelay, () => TryRestock(state));
            return;
        }

        RollAndFill(container, state.Tier);
        state.RestockPending = false;
    }

    private static bool IsOccupied(LockableContainer container)
    {
        var map = container.Map;
        if (map == null || map == Map.Internal)
        {
            return false;
        }

        foreach (var _ in map.GetMobilesInRange(container.Location, AntiCampRadius))
        {
            return true;
        }

        return false;
    }

    private static void RollAndFill(LockableContainer container, int tier)
    {
        // Idempotent: safe whether the container is already empty (the
        // normal restock-timer path) or still has leftover contents (the
        // GM command's "clear spent/empty containers" force-restock path).
        foreach (var item in new List<Item>(container.Items))
        {
            item?.Delete();
        }

        if (Utility.RandomDouble() < ActiveFillChance)
        {
            RollLockState(container, tier);
            DungeonLootBridge.PopulateChestLoot(container, tier);
        }
        else
        {
            // Active-fill roll failed: leave it as an abandoned,
            // already-looted box - empty, unlocked, untrapped. It still
            // reads as Items.Count == 0 to Sweep() on its next pass, so it
            // gets its own 20-40 minute restock timer exactly like any
            // other newly-emptied chest - no separate tracking needed.
            container.Locked = false;
            container.TrapType = TrapType.None;
            container.TrapPower = 0;
            container.TrapLevel = 0;
        }
    }

    // Real TrapType values only go as granular as Dart/Poison/Explosion/
    // Magic (verified against Items/Containers/TrappableContainer.cs) -
    // the ticket's tier-flavored names ("Lesser Dart," "Deadly Poison,"
    // "Lethal Explosion") describe TrapPower scaling on the SAME
    // underlying types, not distinct enum members, so tier controls which
    // type pool is even eligible (no Explosion on a Tier 1 surface chest)
    // and how high Power/Level can roll within it.
    private static readonly TrapType[] LowTierTraps = { TrapType.DartTrap, TrapType.PoisonTrap };
    private static readonly TrapType[] HighTierTraps = { TrapType.PoisonTrap, TrapType.ExplosionTrap };

    private static void RollLockState(LockableContainer container, int tier)
    {
        if (Utility.RandomDouble() < UnlockedChance)
        {
            container.Locked = false;
            container.TrapType = TrapType.None;
            container.TrapPower = 0;
            container.TrapLevel = 0;
            return;
        }

        var (min, max, pool) = tier switch
        {
            1 => (30, 50, LowTierTraps),
            2 => (50, 70, HighTierTraps),
            3 => (70, 85, HighTierTraps),
            _ => (90, 100, HighTierTraps)
        };

        container.Locked = true;
        container.LockLevel = Utility.RandomMinMax(min, max);

        // TrapPower drives the damage the trap deals if it fires;
        // TrapLevel drives how hard it is to detect/remove. Scaled
        // together off the same roll so a nastier trap is consistently
        // also a harder one to defuse, not a mismatched pair.
        var power = Utility.RandomMinMax(min, max);
        container.TrapType = pool[Utility.Random(pool.Length)];
        container.TrapPower = power;
        container.TrapLevel = power;
    }
}
