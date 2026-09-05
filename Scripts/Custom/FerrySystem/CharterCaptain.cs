// =========================================================================
// CharterCaptain.cs — SP-043: the on-deck NPC that replaces DockmasterNPC.
// One stands aboard each PermanentCharterBoat. Modeled on Server.Mobiles.
// TownCrier (a bare Mobile, no AI — there's nothing here to path or fight
// with): invulnerable, stationary, opens FerryCharterGump on double-click
// or on a charter/ferry/travel/destination speech trigger.
//
// SP-046 diagnosed the yellow render as a missing `Hue = Race.Human.
// RandomSkinHue();` line and fixed it. SP-047 added a full round of
// defensive hardening (SolidHueOverride, layer-strip, hardcoded garment
// hues) on the chance something else was contributing. SP-048's ticket
// reports the same symptom again, hypothesizing an engine-level "yellow
// invulnerability hue" tied to Blessed — so this pass actually reads the
// source rather than guessing further:
//
//   Server.Mobile's Blessed setter (Projects/Server/Mobiles/Mobile.cs)
//   is exactly:
//       set { if (m_Blessed != value) { m_Blessed = value;
//               Delta(MobileDelta.HealthbarYellow); } }
//
//   That is the ENTIRE effect Blessed has beyond gating CanBeDamaged().
//   MobileDelta.HealthbarYellow tells clients to render this mobile's
//   HEALTH BAR (the status/target-window bar, not the body) in yellow —
//   the standard UO convention for "this NPC cannot be harmed." It does
//   not touch Hue, SolidHueOverride, or any equipped item. There is no
//   engine-level "yellow skin" effect anywhere in Server.Mobile tied to
//   Blessed — grepped and read the whole property.
//
//   So: if what's being seen really is a solid-yellow BODY, Blessed was
//   never the cause and never could have been (the skin/hue fixes from
//   SP-046/047 are what actually matter for that). If what's being seen
//   is the health/status bar rendering yellow above the captain's head,
//   that IS Blessed, working exactly as designed — a deliberate,
//   standard "invulnerable" indicator, not a bug. Per the ticket, this
//   pass replaces Blessed with an equivalent hand-rolled invulnerability
//   (huge Hits pool + CanBeDamaged/OnDamage/CanBeHarmful all blocking
//   harm) specifically so the yellow health bar stops appearing too,
//   removing that variable entirely regardless of which explanation is
//   the real one.
// =========================================================================

using System;
using ModernUO.Serialization;
using Server;
using Server.Items;

namespace Server.Engines.FerrySystem;

[SerializationGenerator(0, false)]
public partial class CharterCaptain : Mobile
{
    // Off-white/undyed shirt.
    private const int ShirtHue = 0;

    // Pure black — pants and boots.
    private const int TrousersHue = 0x1;

    // Deep sea navy — tricorne hat.
    private const int HatHue = 0x485;

    [SerializableField(0)]
    private string _stopName;

    public CharterCaptain(string stopName)
    {
        _stopName = stopName;

        // Belt-and-suspenders: strip every layer before equipping
        // anything. A freshly-constructed Mobile has nothing on any
        // layer yet, so this is a no-op today — it just guarantees this
        // constructor can never leave a stray default item behind.
        for (var i = 0; i <= 30; i++)
        {
            FindItemOnLayer((Layer)i)?.Delete();
        }

        SolidHueOverride = -1;

        CantWalk = true;

        if (!Core.AOS)
        {
            NameHue = 0x35;
        }

        InitStats(60, 60, 25);
        Hits = HitsMax;

        Hue = Race.Human.RandomSkinHue();

        if (Female = Utility.RandomBool())
        {
            Body = 0x191;
            Name = NameList.RandomName("female");
        }
        else
        {
            Body = 0x190;
            Name = NameList.RandomName("male");
        }

        Title = "the Charter Captain";

        AddItem(new TricorneHat(HatHue) { Movable = false });
        AddItem(new FancyShirt { Hue = ShirtHue, Movable = false });
        AddItem(new LongPants(TrousersHue) { Movable = false });
        AddItem(new ThighBoots(TrousersHue) { Movable = false });

        Utility.AssignRandomHair(this);
    }

    // Hand-rolled invulnerability replacing Blessed=true (see header
    // comment) — HitsMax is a computed get-only property on Mobile
    // (=> 50 + Str/2), so overriding it is the only way to raise it;
    // Hits = HitsMax in the constructor then clamps against this override,
    // not the base formula. CanBeDamaged() is what actually stops damage
    // from landing; CanBeHarmful/OnDamage below are extra
    // belt-and-suspenders per the ticket, not the real enforcement point.
    public override int HitsMax => 65000;

    public override bool CanBeDamaged() => false;

    public override void OnDamage(int amount, Mobile from, bool willKill)
    {
        // Deliberately does not call base — no damage effects, ever.
    }

    public override bool CanBeHarmful(Mobile target, bool message, bool ignoreOurBlessedness) => false;

    public override void OnDoubleClick(Mobile from)
    {
        if (from.InRange(Location, 4))
        {
            FerryCharterGump.DisplayTo(from, _stopName);
        }
        else
        {
            base.OnDoubleClick(from);
        }
    }

    public override bool HandlesOnSpeech(Mobile from) => from.InRange(Location, 8);

    public override void OnSpeech(SpeechEventArgs e)
    {
        if (e.Handled || !e.Mobile.InRange(Location, 8))
        {
            return;
        }

        var speech = e.Speech;
        if (speech.IndexOf("charter", StringComparison.OrdinalIgnoreCase) >= 0 ||
            speech.IndexOf("ferry", StringComparison.OrdinalIgnoreCase) >= 0 ||
            speech.IndexOf("travel", StringComparison.OrdinalIgnoreCase) >= 0 ||
            speech.IndexOf("destination", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            e.Handled = true;
            Direction = GetDirectionTo(e.Mobile);
            FerryCharterGump.DisplayTo(e.Mobile, _stopName);
        }
    }
}
