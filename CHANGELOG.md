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
