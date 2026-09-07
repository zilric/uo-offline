// =========================================================================
// DungeonLootBridge.cs — SP-049-Patch: replaces the SP-049 LootPack-delegation
// approach with an explicit, tier-scaled multi-item loot composition — gold,
// gems, potions, scrolls, reagents, and a chance at classic pre-AOS magic
// weapons/armor — so a restocked chest reads as a curated haul instead of a
// single LootPack.Generate() roll.
//
// LootPack.Generate() (and the DungeonLootAnchor Mobile stand-in it needed)
// are gone: nothing here calls into LootPack anymore, so there's no longer
// a need for a non-null `from` Mobile.
//
// Two leak bugs were caught and fixed against real engine behavior before
// this was written, both stemming from the same fact — verified against
// Server/Items/Item.cs: `Item`'s constructor calls World.AddEntity(this)
// immediately, so *every* Loot.Random*() call (RandomReagent, RandomWeapon,
// RandomArmorOrShield, ...) silently registers a real, serialized Item the
// instant it's constructed, whether or not it ever gets dropped anywhere:
//   - the reagent routine would have called Loot.RandomReagent() only to
//     read its .GetType() and reconstruct a second instance via
//     Activator.CreateInstance, orphaning the first one in World.Items
//     forever (no Parent, no Location, never deleted). Fixed by calling
//     RandomReagent() exactly once and setting .Amount on that instance.
//   - the magic-item routine would have constructed BOTH a BaseWeapon *and*
//     a BaseArmor unconditionally, then only ever dropped whichever one
//     RandomBool() picked — orphaning the other every single time magic
//     loot rolls (25-100% of restocks depending on tier). Fixed by only
//     constructing the item on the branch actually taken.
// Left uncaught, both would have leaked a growing pile of invisible,
// forever-persisted ghost items into World.Items/the save file for as long
// as the server ran.
//
// Also corrected: the spec's scroll-circle formula (tier*2-1 .. tier*2)
// only ever indexes into circle 1 of Loot.RegularScrollTypes (a flat
// 64-entry array, 8 circles x 8 spells - verified directly against
// Loot.cs) for every tier from 1 to 4, so "mid-tier"/"high-tier" scrolls
// as described in the ticket's own closing example would never actually
// appear. Replaced with a formula that spans one quarter of the full
// 8-circle range per tier (tier 1 -> circles 1-2, ... tier 4 -> circles
// 7-8), so scroll power actually scales with dungeon tier.
//
// Loot.RandomGreaterPotion() doesn't exist as a built-in helper - verified
// against Loot.cs's own PotionTypes/RandomPotion(). GreaterPotionTypes
// below reproduces the exact same array-of-Type + Loot.Construct() idiom
// Loot.cs itself uses, built from the six classic Greater* potion classes
// (all present pre-AOS - confirmed each has a parameterless constructor).
// =========================================================================

using ModernUO.Serialization;
using Server.Items;

namespace Server.Engines.Dungeons;

// This shard already booted the original SP-049 build at least once, which
// constructed and persisted a DungeonLootAnchor Mobile into Saves/. Deleting
// this type outright (the first pass at this patch did exactly that) breaks
// world load on every save that still references it: the deserializer can't
// resolve the type name and, in headless mode, can't prompt to auto-delete
// it either - Server.Core.Setup crashes with a HeadlessConsoleInputException
// before the world ever loads. Verified directly by reproducing the crash
// against this shard's own Saves/ after removing the class.
//
// The type has to stay resolvable for as long as any save might still
// reference it. It now does nothing on a fresh world (Initialize() no
// longer constructs one - nothing calls LootPack.Generate anymore, so
// nothing needs this stand-in Mobile) and actively retires itself out of
// any save that still has one, via the same ReclaimInstance/Initialize
// lifecycle MerchantGuildAuthority already established elsewhere in this
// codebase.
[SerializationGenerator(0, false)]
public partial class DungeonLootAnchor : Mobile
{
    private static DungeonLootAnchor _instance;

    [AfterDeserialization]
    private void ReclaimInstance() => _instance = this;

    // Auto-discovered by AssemblyHandler.Invoke("Initialize") post-world-
    // load, same convention every other Scripts/Custom Initialize() uses.
    // Retires (rather than reconstructs) any anchor a prior world save
    // still has - see the class header for why this type can't simply be
    // deleted while such a save might exist.
    public static void Initialize()
    {
        if (_instance?.Deleted == false)
        {
            _instance.Delete();
        }

        _instance = null;
    }
}

public static class DungeonLootBridge
{
    // Loot.cs has no built-in "greater potion" pool - this is the same
    // array-of-Type + Loot.Construct() idiom Loot.cs uses for its own
    // PotionTypes/GemTypes/RegTypes, built from the six classic Greater*
    // potions (all predate AOS).
    private static readonly System.Type[] GreaterPotionTypes =
    {
        typeof(GreaterHealPotion), typeof(GreaterCurePotion), typeof(GreaterAgilityPotion),
        typeof(GreaterStrengthPotion), typeof(GreaterExplosionPotion), typeof(GreaterPoisonPotion)
    };

    // Full 8-circle spell range, split into one quarter per tier - see file
    // header for why the ticket's original tier*2-1..tier*2 formula never
    // escaped circle 1.
    private const int ScrollsPerTierBand = 16;

    // Caller (DungeonChestManager.RollAndFill) already clears the
    // container's prior contents before calling this - this only ever
    // adds, it never needs to purge.
    public static void PopulateChestLoot(Container chest, int tier)
    {
        if (chest?.Deleted != false)
        {
            return;
        }

        var goldAmount = tier switch
        {
            1 => Utility.RandomMinMax(80, 180),
            2 => Utility.RandomMinMax(250, 550),
            3 => Utility.RandomMinMax(700, 1400),
            4 => Utility.RandomMinMax(1800, 3500),
            _ => 150
        };
        chest.DropItem(new Gold(goldAmount));

        var gemCount = tier switch
        {
            1 => Utility.RandomMinMax(2, 5),
            2 => Utility.RandomMinMax(4, 9),
            3 => Utility.RandomMinMax(8, 16),
            4 => Utility.RandomMinMax(15, 30),
            _ => 3
        };
        for (var i = 0; i < gemCount; i++)
        {
            chest.DropItem(Loot.RandomGem());
        }

        var potionCount = Utility.RandomMinMax(1, tier);
        for (var i = 0; i < potionCount; i++)
        {
            chest.DropItem(tier <= 2 ? Loot.RandomPotion() : Loot.Construct(GreaterPotionTypes));
        }

        var scrollCount = Utility.RandomMinMax(1, tier);
        var scrollBase = (tier - 1) * ScrollsPerTierBand;
        for (var i = 0; i < scrollCount; i++)
        {
            chest.DropItem(Loot.RandomScroll(scrollBase, scrollBase + ScrollsPerTierBand - 1, SpellbookType.Regular));
        }

        if (tier >= 2)
        {
            var reagents = Loot.RandomReagent();
            reagents.Amount = Utility.RandomMinMax(5 * tier, 10 * tier);
            chest.DropItem(reagents);
        }

        var magicChance = tier switch
        {
            1 => 0.25,
            2 => 0.50,
            3 => 0.80,
            4 => 1.00,
            _ => 0.30
        };
        if (Utility.RandomDouble() < magicChance)
        {
            if (Utility.RandomBool())
            {
                var weapon = Loot.RandomWeapon();
                ApplyEraMagicWeapon(weapon, tier);
                chest.DropItem(weapon);
            }
            else
            {
                var armor = Loot.RandomArmorOrShield();
                ApplyEraMagicArmor(armor, tier);
                chest.DropItem(armor);
            }
        }

        if (tier >= 3 && Utility.RandomDouble() < (tier == 4 ? 0.35 : 0.15))
        {
            chest.DropItem(new TreasureMap(tier, chest.Map));
        }
    }

    // Real WeaponDamageLevel has no "Vanquishing" member - verified
    // against WeaponEnums.cs, the top tier is named Vanq.
    private static void ApplyEraMagicWeapon(BaseWeapon weapon, int tier)
    {
        weapon.Identified = false;
        weapon.DamageLevel = tier switch
        {
            1 => WeaponDamageLevel.Ruin,
            2 => Utility.RandomBool() ? WeaponDamageLevel.Might : WeaponDamageLevel.Force,
            3 => Utility.RandomBool() ? WeaponDamageLevel.Force : WeaponDamageLevel.Power,
            4 => Utility.RandomBool() ? WeaponDamageLevel.Power : WeaponDamageLevel.Vanq,
            _ => WeaponDamageLevel.Regular
        };

        weapon.AccuracyLevel = (WeaponAccuracyLevel)Utility.RandomMinMax(0, tier);
        weapon.DurabilityLevel = (WeaponDurabilityLevel)Utility.RandomMinMax(0, tier);
    }

    private static void ApplyEraMagicArmor(BaseArmor armor, int tier)
    {
        armor.Identified = false;
        armor.ProtectionLevel = tier switch
        {
            1 => ArmorProtectionLevel.Defense,
            2 => Utility.RandomBool() ? ArmorProtectionLevel.Guarding : ArmorProtectionLevel.Hardening,
            3 => Utility.RandomBool() ? ArmorProtectionLevel.Hardening : ArmorProtectionLevel.Fortification,
            4 => Utility.RandomBool() ? ArmorProtectionLevel.Fortification : ArmorProtectionLevel.Invulnerability,
            _ => ArmorProtectionLevel.Regular
        };

        armor.Durability = (ArmorDurabilityLevel)Utility.RandomMinMax(0, tier);
    }
}
