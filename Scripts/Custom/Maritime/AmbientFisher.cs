// =========================================================================
// AmbientFisher.cs — SP-048: the stationary fisher NPC standing on each
// ambient fishing boat's deck. A bare Mobile (no AI/BaseCreature — there
// is nothing here to path, fight, or tame; "cannot be controlled" is
// automatically true since taming only applies to BaseCreature).
//
// Invulnerability uses the same hand-rolled approach as
// FerrySystem/CharterCaptain.cs (huge Hits pool + CanBeDamaged/OnDamage/
// CanBeHarmful all blocking harm) rather than Blessed = true, for
// consistency — see that file's header comment for the full source-level
// explanation of what Blessed actually does (colors the health bar
// yellow; nothing to do with skin/body hue).
//
// The 15-30s ambient loop self-reschedules with a fresh random delay
// each cycle (rather than one fixed-interval repeating timer) so the
// interval itself varies, not just a phase offset.
// =========================================================================

using System;
using ModernUO.Serialization;
using Server;
using Server.Items;

namespace Server.Engines.Maritime;

[SerializationGenerator(0, false)]
public partial class AmbientFisher : Mobile
{
    private const int CastAnimAction = 12;
    private const int CastAnimFrames = 5;
    private const int SplashEffectId = 0x352D;
    private const int SplashSoundId = 0x027;
    private const int CatchChancePercent = 15;

    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(30);

    [SerializableField(0)]
    private Item _deckBarrel;

    [SerializableField(1)]
    private Point3D _waterTile;

    private Timer _loopTimer;

    public AmbientFisher()
    {
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

        Title = "the fisher";

        AddItem(new FloppyHat(Utility.RandomNeutralHue()) { Movable = false });
        AddItem(new ThighBoots(0x1) { Movable = false });
        AddItem(new FishingPole { Movable = false });

        var shirt = Utility.RandomBool() ? (Item)new Shirt() : new FancyShirt();
        shirt.Movable = false;
        AddItem(shirt);

        AddItem(new LongPants(0x1) { Movable = false });

        Utility.AssignRandomHair(this);

        StartLoop();
    }

    public void Setup(Item deckBarrel, Point3D waterTile, Direction faceWater)
    {
        DeckBarrel = deckBarrel;
        WaterTile = waterTile;
        Direction = faceWater;
    }

    public override int HitsMax => 65000;

    public override bool CanBeDamaged() => false;

    public override void OnDamage(int amount, Mobile from, bool willKill)
    {
        // Deliberately does not call base — no damage effects, ever.
    }

    public override bool CanBeHarmful(Mobile target, bool message, bool ignoreOurBlessedness) => false;

    private void StartLoop()
    {
        _loopTimer?.Stop();
        var delay = Utility.RandomMinMax((int)MinInterval.TotalSeconds, (int)MaxInterval.TotalSeconds);
        _loopTimer = Timer.DelayCall(TimeSpan.FromSeconds(delay), FishingCycle);
    }

    private void FishingCycle()
    {
        if (Deleted)
        {
            return;
        }

        if (_waterTile != Point3D.Zero && Map != null && Map != Map.Internal)
        {
            Direction = GetDirectionTo(_waterTile);
        }

        Animate(CastAnimAction, CastAnimFrames, 1, true, false, 0);

        if (Map != null && Map != Map.Internal)
        {
            var splashAt = _waterTile != Point3D.Zero ? _waterTile : Location;
            Effects.SendLocationEffect(splashAt, Map, SplashEffectId, 16, 4);
            Effects.PlaySound(splashAt, Map, SplashSoundId);
        }

        if (Utility.Random(100) < CatchChancePercent)
        {
            Item catchItem = Utility.RandomBool() ? new Fish() : new Boots();
            if (catchItem is Boots boot)
            {
                boot.Name = "a waterlogged old boot";
            }

            // Catches always go inside the barrel's own container
            // inventory — never dropped loose on the deck/dock/water.
            // If the barrel reference is ever missing or deleted, the
            // item is deleted rather than left to land wherever this
            // mobile happens to be standing.
            if (_deckBarrel is Container barrel && !barrel.Deleted)
            {
                barrel.DropItem(catchItem);
            }
            else
            {
                catchItem.Delete();
            }
        }

        StartLoop();
    }

    public override void OnDelete()
    {
        _loopTimer?.Stop();
        _loopTimer = null;
        base.OnDelete();
    }

    [AfterDeserialization]
    private void RestartLoop()
    {
        StartLoop();
    }
}
