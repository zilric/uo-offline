// =========================================================================
// BotResurrectAid.cs — one bot putting another back on its feet.
//
// Before this, nothing living ever resurrected a bot: every ghost was
// found by an off-screen "wandering healer" after a timer. Now the two
// era-true ways a player got another player up both work, and both are
// gated on the same numbers the engine uses for a human:
//
//   MAGERY   Resurrection is 8th circle — Magery 80, 50 mana, and blood
//            moss / garlic / ginseng in the pack (or a Resurrection
//            scroll). A real cast: it can fizzle, and a hit landing
//            mid-mantra kills it silently, exactly like any other T2A
//            spell (see the disturb notes in AdventurerBehavior).
//   BANDAGES Healing 80 + Anatomy 80, one bandage spent, and the same
//            (healing - 68) / 50 roll Bandage.cs makes. This is why a
//            grandmaster warrior can raise a friend and a journeyman
//            cannot.
//
// The GHOST drives this — it spots someone who could help and floats
// over (GhostBehavior.CheckAid). Nothing reaches into another bot's
// behavior to march it across the room, which keeps the whole feature
// free of cross-behavior coupling.
//
// Who helps whom: anyone blue helps anyone blue. A murderer only gets
// help from another murderer, or from someone in its own party — a blue
// raising a red would flag the caster criminal, which is not a favor
// bots should do each other by accident.
// =========================================================================

using System;
using Server;
using Server.Items;
using Server.Mobiles;
using Server.Spells;

namespace Server.CustomBots
{
    public static class BotResurrectAid
    {
        public enum AidKind
        {
            None,
            Magery,   // Resurrection, 8th circle
            Bandage,  // Healing + Anatomy
        }

        // ---- Knobs (all lifted from the engine's own numbers) ----

        // Resurrection is 8th circle: 80 magery, 50 mana, three reagents.
        public const double MageryMin   = 80.0;
        public const int    ResManaCost = 50;

        public static readonly Type[] ResReagents =
            { typeof(Bloodmoss), typeof(Garlic), typeof(Ginseng) };

        // Bandage.cs: checkSkills = healing >= 80.0 && anatomy >= 80.0.
        public const double HealingMin = 80.0;
        public const double AnatomyMin = 80.0;

        // ResurrectionSpell.TargetRange is 1 — the caster stands on top of
        // the ghost. Bandages reach one tile further.
        public const int CastRange    = 1;
        public const int BandageRange = 2;

        // How far a ghost will look for someone who can help. Beyond this
        // it gets on with walking to a healer or a shrine.
        public const int SearchRange = 20;

        // How long a bandage takes to go on. The engine scales this with
        // dex; a flat five seconds reads the same from outside and keeps
        // the aider standing still for a beat.
        private static readonly TimeSpan BandageDelay = TimeSpan.FromSeconds(5.0);

        // -------------------------------------------------------------------
        // Capability — could this bot raise the dead RIGHT NOW? Magery is
        // checked first because a mage that also carries bandages should
        // say the words, not kneel with a bandage.
        // -------------------------------------------------------------------
        public static AidKind KindFor(PlayerBot m)
        {
            if (m is not { Deleted: false, Alive: true })
            {
                return AidKind.None;
            }

            var pack = m.Backpack;
            if (pack == null)
            {
                return AidKind.None;
            }

            if (m.Skills[SkillName.Magery].Base >= MageryMin &&
                (HasScroll(m) || (m.Mana >= ResManaCost && HasReagents(m))))
            {
                return AidKind.Magery;
            }

            if (m.Skills[SkillName.Healing].Base >= HealingMin &&
                m.Skills[SkillName.Anatomy].Base >= AnatomyMin &&
                pack.FindItemByType(typeof(Bandage)) != null)
            {
                return AidKind.Bandage;
            }

            return AidKind.None;
        }

        public static bool CanAid(PlayerBot m) => KindFor(m) != AidKind.None;

        private static bool HasScroll(PlayerBot m) =>
            m.Backpack?.FindItemByType(typeof(ResurrectionScroll)) != null;

        private static bool HasReagents(PlayerBot m)
        {
            var pack = m.Backpack;
            if (pack == null)
            {
                return false;
            }
            foreach (var t in ResReagents)
            {
                if (pack.GetAmount(t) < 1)
                {
                    return false;
                }
            }
            return true;
        }

        // -------------------------------------------------------------------
        // Would this one help that one? Notoriety first (a blue raising a
        // red flags the blue criminal), then the ordinary reasons somebody
        // walks past: mid-fight, dead themselves, halfway through a spell.
        // -------------------------------------------------------------------
        public static bool Willing(PlayerBot aider, PlayerBot ghost)
        {
            if (aider == null || ghost == null || aider == ghost)
            {
                return false;
            }
            if (aider.Deleted || !aider.Alive || ghost.Deleted || ghost.Alive)
            {
                return false;
            }
            if (aider.Map != ghost.Map || aider.Map == null || aider.Map == Map.Internal)
            {
                return false;
            }

            // Busy: swinging at something, already casting, or frozen
            // partway through someone else's favor.
            if (aider.Combatant != null || aider.Spell != null || aider.Frozen)
            {
                return false;
            }

            // Nobody raises the person who killed them, and nobody raises
            // someone they just killed. The first live run had a red strip
            // a body and then kneel down and bandage it back up.
            if (ghost.LastKiller == aider || aider.LastKiller == ghost)
            {
                return false;
            }

            // Help does not cross the notoriety line, in either direction.
            // A blue raising a red flags the blue criminal; a red raising
            // the blue it just murdered is nonsense. Party-mates are the
            // exception — a mixed party is a deliberate arrangement.
            if (RedTerritory.IsRed(ghost) != RedTerritory.IsRed(aider))
            {
                var party = BotPartyManager.PartyOf(ghost);
                if (party == null || BotPartyManager.PartyOf(aider) != party)
                {
                    return false;
                }
            }

            // Raising a criminal inside a guarded town flags the helper, and
            // the guards come for the helper next. The first thief caught at
            // the Britain bank took the healer who raised it down with it.
            if (ghost.Criminal && !RedTerritory.IsRed(aider))
            {
                var region = ghost.Region?.GetRegion<Server.Regions.GuardedRegion>();
                if (region != null && !region.IsDisabled())
                {
                    return false;
                }
            }

            // The engine refuses a res where the body cannot stand up.
            if (ghost.Map.CanFit(ghost.Location, 16, false, false) != true)
            {
                return false;
            }

            // Khaldun's veil blocks every form of resurrection.
            if (ghost.Region?.IsPartOf("Khaldun") == true)
            {
                return false;
            }

            return true;
        }

        // -------------------------------------------------------------------
        // The nearest living bot who could and would help. Null when the
        // ghost is on its own — which is most of the time, and should be.
        // -------------------------------------------------------------------
        public static PlayerBot FindAider(PlayerBot ghost, int range = SearchRange)
        {
            if (ghost?.Map == null || ghost.Map == Map.Internal)
            {
                return null;
            }

            PlayerBot best = null;
            int bestDist = int.MaxValue;

            foreach (var m in ghost.Map.GetMobilesInRange(ghost.Location, range))
            {
                if (m is not PlayerBot other)
                {
                    continue;
                }
                if (!Willing(other, ghost) || !CanAid(other))
                {
                    continue;
                }

                int dist = Dist(ghost.Location, other.Location);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = other;
                }
            }

            return best;
        }

        // -------------------------------------------------------------------
        // Make the attempt. Returns true when something was actually
        // started (a cast launched, a bandage applied) — the caller then
        // waits rather than asking again next tick. False means try
        // someone else, or walk.
        //
        // Neither route resurrects on its own: both end at
        // BotDeathManager.ResurrectBot, which owns the sparkle, the robe
        // and the corpse run.
        // -------------------------------------------------------------------
        public static bool TryAid(PlayerBot aider, PlayerBot ghost)
        {
            var kind = KindFor(aider);
            if (kind == AidKind.None || !Willing(aider, ghost))
            {
                return false;
            }

            int dist = Dist(aider.Location, ghost.Location);
            int reach = kind == AidKind.Magery ? CastRange : BandageRange;
            if (dist > reach)
            {
                return false; // the ghost has to float closer first
            }

            var face = aider.GetDirectionTo(ghost);
            if (aider.Direction != face)
            {
                aider.Direction = face;
            }

            var line = ChatLibrary.PickRandom("res_offer");
            if (!string.IsNullOrEmpty(line))
            {
                aider.Say(line);
            }

            return kind == AidKind.Magery
                ? CastResurrection(aider, ghost)
                : ApplyBandage(aider, ghost);
        }

        // ---- Magery route ----------------------------------------------

        private static bool CastResurrection(PlayerBot aider, PlayerBot ghost)
        {
            // Two circles easier off a scroll, same as every other bot
            // cast — a scroll in the pack gets read before the book burns
            // three reagents.
            var scroll = aider.Backpack?.FindItemByType(typeof(ResurrectionScroll))
                         as ResurrectionScroll;

            var spell = new BotResurrectionSpell(aider, ghost, scroll);
            try
            {
                if (!spell.Cast())
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            Console.WriteLine(
                $"[death] {aider.Name} casts Resurrection on {ghost.Name} " +
                $"at ({ghost.X},{ghost.Y})");
            return true;
        }

        // The genuine 8th-circle spell with two changes, both forced by the
        // fact that the target has no client:
        //   - OnCast resolves straight onto the ghost instead of raising a
        //     target cursor nobody can click.
        //   - Success calls ResurrectBot rather than sending a gump that
        //     would go nowhere. Everything real stays: mana, reagents, the
        //     skill roll, the fizzle, and the silent T2A disturb if the
        //     caster is hit mid-mantra.
        private sealed class BotResurrectionSpell : Server.Spells.Eighth.ResurrectionSpell
        {
            private readonly PlayerBot _patient;

            public BotResurrectionSpell(Mobile caster, PlayerBot patient, Item scroll)
                : base(caster, scroll) => _patient = patient;

            // The anti-macro cast-recovery gate exists to stop human spam;
            // other bot systems casting nearby kept re-stamping
            // NextSpellTime and starving honest attempts (same reason
            // MagicTravel's recall drops it).
            public override bool CheckNextSpellTime => false;

            public override void OnCast()
            {
                if (_patient == null || _patient.Deleted || _patient.Alive ||
                    _patient.Map != Caster.Map ||
                    !Caster.InRange(_patient, TargetRange) ||
                    _patient.Map?.CanFit(_patient.Location, 16, false, false) != true ||
                    _patient.Region?.IsPartOf("Khaldun") == true)
                {
                    FinishSequence();
                    return;
                }

                // CheckBSequence is what spends the mana and the reagents
                // and rolls the skill — a failure here is a real fizzle.
                if (CheckBSequence(_patient, true))
                {
                    SpellHelper.Turn(Caster, _patient);
                    BotDeathManager.ResurrectBot(
                        _patient, $"{Caster.Name} cast Resurrection");
                }

                FinishSequence();
            }
        }

        // ---- Bandage route ---------------------------------------------

        private static bool ApplyBandage(PlayerBot aider, PlayerBot ghost)
        {
            var bandage = aider.Backpack?.FindItemByType(typeof(Bandage));
            if (bandage == null)
            {
                return false;
            }

            bandage.Consume(1);
            aider.Animate(11, 5, 1, true, false, 0); // kneel over the body
            aider.PlaySound(0x57);

            Console.WriteLine(
                $"[death] {aider.Name} works a bandage on {ghost.Name} " +
                $"at ({ghost.X},{ghost.Y})");

            Timer.DelayCall(BandageDelay, () => FinishBandage(aider, ghost));
            return true;
        }

        private static void FinishBandage(PlayerBot aider, PlayerBot ghost)
        {
            if (aider == null || aider.Deleted || !aider.Alive ||
                ghost == null || ghost.Deleted || ghost.Alive)
            {
                return;
            }
            if (aider.Map != ghost.Map ||
                Dist(aider.Location, ghost.Location) > BandageRange)
            {
                return; // one of them wandered off mid-bandage
            }

            // Bandage.cs, verbatim: chance = (healing - 68) / 50, on top of
            // the 80/80 gate KindFor already checked.
            double healing = aider.Skills[SkillName.Healing].Value;
            double chance  = (healing - 68.0) / 50.0;

            if (chance > Utility.RandomDouble())
            {
                BotDeathManager.ResurrectBot(ghost, $"{aider.Name}'s bandages");
                return;
            }

            var line = ChatLibrary.PickRandom("res_fail");
            if (!string.IsNullOrEmpty(line))
            {
                aider.Say(line);
            }
            Console.WriteLine(
                $"[death] {aider.Name} failed to raise {ghost.Name} with bandages");
        }

        private static int Dist(Point3D a, Point3D b)
        {
            int dx = a.X - b.X; if (dx < 0) dx = -dx;
            int dy = a.Y - b.Y; if (dy < 0) dy = -dy;
            return dx > dy ? dx : dy;
        }
    }
}
