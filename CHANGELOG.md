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
