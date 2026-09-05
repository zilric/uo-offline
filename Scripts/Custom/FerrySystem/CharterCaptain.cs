// =========================================================================
// CharterCaptain.cs — SP-043: the on-deck NPC that replaces DockmasterNPC.
// One stands aboard each PermanentCharterBoat. Modeled on Server.Mobiles.
// TownCrier (a bare Mobile, no AI — there's nothing here to path or fight
// with): invulnerable, stationary, opens FerryCharterGump on double-click
// or on a charter/ferry/travel/destination speech trigger.
// =========================================================================

using System;
using ModernUO.Serialization;
using Server;
using Server.Items;

namespace Server.Engines.FerrySystem;

[SerializationGenerator(0, false)]
public partial class CharterCaptain : Mobile
{
    [SerializableField(0)]
    private string _stopName;

    public CharterCaptain(string stopName)
    {
        _stopName = stopName;

        Blessed = true;
        CantWalk = true;

        if (!Core.AOS)
        {
            NameHue = 0x35;
        }

        InitStats(60, 60, 25);

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

        AddItem(new Boots(Utility.RandomNeutralHue()));
        AddItem(new LongPants(Utility.RandomNeutralHue()));

        var shirt = Utility.RandomBool() ? (Item)new Shirt() : new FancyShirt();
        shirt.Hue = Utility.RandomNeutralHue();
        AddItem(shirt);

        AddItem(new SkullCap(Utility.RandomNeutralHue()));

        var coat = Utility.RandomBool() ? (Item)new HalfApron() : new Cloak();
        coat.Hue = Utility.RandomNeutralHue();
        AddItem(coat);

        Utility.AssignRandomHair(this);
    }

    public override bool CanBeDamaged() => false;

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
