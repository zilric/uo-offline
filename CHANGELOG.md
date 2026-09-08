# Changelog

All notable custom-server changes land here, grouped by sprint ticket. Core ModernUO/upstream history is tracked separately via `git log` against the `upstream` remote.

## 2026-09-06

### SP-049 — Dynamic Dungeon Chest Loot & Restock Lifecycle

- Static container management across all 8 classic Britannia dungeons (Shame, Despise, Destard, Covetous, Deceit, Wrong, Hythloth, plus the Fire and Ice dungeons), using coordinate-based bounding boxes to resolve floor tiers (Tiers 1-4) per container.
- Rich, era-compliant pre-AOS loot generation: guaranteed tier-scaled gold, gems, spell scrolls, potions, and reagent stacks, plus classic magic weapons/armor (Ruin/Defense up to Vanq/Invulnerability).
- Low-percentage treasure map drops on Tier 3 and 4 chests.
- Restock calibrated to a 33% active-fill probability per cycle (the remaining containers are left as ambient, already-looted empty caches) alongside a rolling 10% unlocked/untrapped parity roll on whichever containers do fill.
- Empty-container restock sweeps on a randomized 20-40 minute timer, gated by a 3-tile anti-camping check before refilling.
- `[restockdungeonchests` command and a matching Command Panel button under a new "Spawners & World" tab.

### SP-050 — Transit Corridor & Moongate Vendor Hotspot Seeder

- `TransitVendorSeeder.cs`: fast-batched compact-shop seeding around the 8 classic public moongates (50-tile radius, 70% vendor / 30% ambient) and along road corridors (jittered off detected road tiles, 25% vendor / 75% ambient).
- House footprints restricted to compact styles only: the Small Old House trim variants (Wood & Plaster, Stone & Plaster, Fieldstone, Thatched Roof), Small Shop, and Small Tower.
- `[seedtransitvendors` command and a matching "Seed Transit & Gate Vendors (Compact Shops)" button on the `[vh` Organic Market admin panel.

### SP-051 — Resident Bot Open-Shop Sanctuary Hook

- Sanctuary protection preventing wild (untamed, unsummoned) creatures from ever engaging a resident/ambient PlayerBot in combat while it's standing inside a house region (open-front workshops and Small Shops included) — hooked in via `Mobile.AllowHarmfulHandler`, chained onto whatever core's own Notoriety handler already does rather than replacing it.
- Ambient self-defense fallback: a resident bot that still takes damage (AoE, wild-area effects, or a controlled pet — none of which the sanctuary blocks) immediately breaks its domestic idle routine and hands off to the combat-capable `AdventurerBehavior`.
- Vendor classes (`PlayerVendor`) and real-player combat/notoriety checks are untouched — the hook only ever applies to `PlayerBot` targets and wild `BaseCreature` attackers.

### SP-053 — Dynamic Vendor Container Auto-Grid Arranger

- `VendorGridArranger.cs`: eliminates the unpositioned drop-order clutter vendor sale containers were left in, replacing it with an adaptive grid — usable viewport bounds read from each container's own real `Bounds` data (Backpack, Bag, Basket, WoodenBox, Pouch each get their own correct rectangle) rather than a hand-picked coordinate table.
- Adaptive grid sizing (2x2 up to 5x4) driven by the count of distinct item groups, where duplicates sharing type + hue + unit price are grouped into one slot with a card-deck +2px stagger per additional item (capped at +8px) to visually read as a stack.
- Recursive traversal into sub-containers, with a bundle guardrail: a sub-container sold as a single priced unit (a grab bag) never has its own interior rearranged, since its contents aren't individually purchasable — only "display" organizer containers (individually priced children) are recursed into.
- Arranger pass integrated directly into vendor stocking (`OrganicMarketSpawner`), restocking (`MerchantGuildAuthority`), and the bot-shopper purchase path (`PlayerShopPatronageManager`) that this codebase owns end-to-end; a 60-second periodic watch sweep covers real-player purchases too, since core's `PlayerVendorBuyGump` has no extension point reachable without editing core.
- `[regridvendor` GM command for targeted testing, plus a Command Panel entry.
- **SP-054 follow-up (same day):** two layout bugs fixed in this same file before its first commit — distinct organizer sub-containers (e.g. separate swords/fencing/mace display chests) were incorrectly collapsing into one stacked slot since containers shared the same grouping key as ordinary goods; sub-containers now always get their own slot unless they're a genuine priced bundle. Also recalibrated the Backpack-specific viewport window (shifted up and left, `X=38,Y=62,W=105,H=75`) since the raw `Container.Bounds` rectangle read as biased toward the bottom-right drawstring flap against the real client art.

## 2026-09-07

### SP-054 — Curated House Template Export, Management & Seeder Auto-Stamping

- `HouseTemplateManager.cs`: JSON house-decor templates partitioned under `Data/HouseTemplates/Vendor/{Archetype}/{HouseTypeName}/` and `Data/HouseTemplates/Ambient/{HouseTypeName}/`, each item stored as an offset relative to the house's own origin.
- Small-house cross-style compatibility: `SmallOldHouse` and `SmallShop` share the exact same 7×7 main-room footprint despite being different classes (verified directly against `Multis/Houses/Houses.cs`), so a template exported from one is eligible to stamp onto the other. `SmallTower`'s own footprint is a genuinely different 8×7 and was deliberately kept out of the family — the ticket's own example grouping had conflated it with the others.
- `[exporthouse` command + gump: classify a house (Ambient vs Vendor + archetype), name a template, export. A blank name auto-generates `{HouseTypeName}_{UTC timestamp}` instead of blocking the export.
- `[importhouse` command + gump: browse every template compatible with a targeted house (including cross-style matches), stamp one on selection (clears existing decor first, no separate confirm step), with a per-row Delete button to prune unwanted templates.
- `OrganicMarketSpawner.PlaceHouse` tries a curated template first, falling back to the original procedural `DynamicClutterGenerator` pass only when none exists. Pool asymmetry: an Ambient house draws from the *entire* compatible pool (every Vendor archetype's templates plus Ambient ones), while a Vendor house stays scoped to just its own archetype.
- `AmbientHousePurchaseGump.cs`: removed the paid "Buy Furnished" option — purchasing a house now unconditionally strips all decor on ownership transfer, no prompt.
- Replaced the old footprint-category-keyed decor system outright: deleted `Housing/HouseTemplateRegistry.cs` and `Housing/HouseDecorGump.cs`, trimmed `Housing/HouseDecorCommands.cs` down to just the `CollectDecorItems`/`ClearDecor` scanning primitives the new system still calls into. `[exportdecor`/`[importdecor` are gone; `CommandPanelRegistry.cs` now exposes `[exporthouse`/`[importhouse` instead.
- **Storage note:** `Data/HouseTemplates/` at the repo root is the tracked source of truth. `server-runtime/ModernUO/` is its own separate git clone (own `.git`, own remote) — nothing exported live into the deployed tree can ever be committed from there directly. `install-server.sh`'s new `install_house_templates` mirrors the tracked directory into the deployed tree on every install/update, the same one-way sync `playerbots/data/` already uses for its own sub-directories. Curating a GM-exported template into the shipped set means copying it back into the repo-root directory by hand and committing it.

### Fix (Install) — Bazzite / Immutable OS Compatibility for `install-server.sh`

- Resolved installation failures on immutable/OSTree environments (e.g. Bazzite Linux) by detecting `rpm-ostree`/Bazzite/SteamOS hosts (`is_immutable_os`) and routing dependency acquisition to user-space instead of protected root directories: package-manager calls that would fail against a read-only `/usr` are skipped, and `Argon2.Bindings`' missing `linux-x64` native library is bundled straight into `Distribution/` via each distro's download-only package fetch (`dnf download` / `apt-get download` / `pacman -Sw`, never an arbitrary URL) rather than a system-wide install (`ensure_libargon2`, called from both `do_install` and `do_update`).
- Fixed a path-resolution bug in `scripts/start-server.sh` (deployed as `start.sh`) that could double-nest `server-runtime/server-runtime/...` depending on how the script was invoked, replacing the chained `dirname` calls with resolution based on the script's own real location.
- `start.sh` now exports `LD_LIBRARY_PATH` to include the bundled-libargon2 locations, and its first-launch setup-wizard fallback no longer backgrounds ModernUO against a non-TTY stdin when `script(1)` is unavailable (which reliably crashed with `HeadlessConsoleInputException`) — it now runs the wizard interactively in the foreground when a real terminal is attached, or exits with a clear error when neither is available.
- Verified end-to-end on a reference (non-atomic) Linux host: `is_immutable_os` and the libargon2 detection both correctly recognize a normal system as normal (no change in behavior on traditional package-managed distros), a full `install-server.sh update` — including `install_house_templates` and the rest of the install/update pipeline — completes with zero errors, and the server boots via the updated `start.sh` with zero exceptions. True immutable-OS behavior (read-only `/usr`, missing system libargon2, no `script(1)`/tty) could not be exercised on this reference host and remains unverified on an actual Bazzite/SteamOS machine.
