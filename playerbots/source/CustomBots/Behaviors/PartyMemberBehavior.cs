// =========================================================================
// PartyMemberBehavior.cs — a bot following its hunting-party leader
// (IDEAS 2.2).
//
// Subclasses AdventurerBehavior for the same reason DungeonCrawler does:
// ALL the combat, flee, and stuck-recovery machinery comes free. The only
// override is goal selection — instead of patrolling wilderness or
// dungeon rooms, the member's "patrol goal" is always a spot beside the
// party leader. The result is the pure MMO image: a line of players
// walking down a road together, fanning out when a fight starts (combat
// preempts patrol in the base class), and re-forming after.
//
// During the party's ENTERING phase a member on the surface aims at the
// dungeon entrance pad instead — it walks up and steps in after the
// leader (BotPartyManager ports in any straggler the pad doesn't catch).
//
// Membership itself lives in BotPartyManager, not here. A member whose
// party is gone (disband edge, server load — parties are transient)
// self-heals to a Traveler on its next tick.
// =========================================================================

using System;
using Server;
using Server.Mobiles;

namespace Server.CustomBots
{
    public class PartyMemberBehavior : AdventurerBehavior
    {
        public override string SerializableName => "PartyMember";

        // Close enough to the leader — stand instead of crowding him.
        private const int FollowNear = 3;

        public PartyMemberBehavior()
        {
            ChatCategories  = new[] { "traveling", "small_talk" };
            ChatChance      = 0.08;
            MinChatCooldown = TimeSpan.FromSeconds(40);
            MaxChatCooldown = TimeSpan.FromSeconds(120);
        }

        // How far a member will turn to help a partymate's fight.
        private const int AssistRange = 14;

        public override string GetStatusLine(PlayerBot bot)
        {
            var party = BotPartyManager.PartyOf(bot);
            if (bot.Combatant is Mobile foe && !foe.Deleted && foe.Alive)
            {
                return $"fighting {foe.Name} for {party?.Leader?.Name ?? "the party"}";
            }
            return party != null
                ? $"with {party.Leader?.Name}'s party"
                : base.GetStatusLine(bot);
        }

        public override void Tick(PlayerBot bot)
        {
            var party = BotPartyManager.PartyOf(bot);
            if (party == null)
            {
                // Orphaned (disband raced a tick, or a stale save carried
                // the name through a restart) — back to ordinary life.
                bot.Behavior = BehaviorRegistry.Create("Traveler");
                return;
            }

            // ASSIST. The reason to walk in a group is that the group
            // fights as one, and it did not: a member followed the leader
            // and fought only what came at the member. Helga led a party
            // of two to Covetous, met three reds on the road, ran at 2 of
            // 83 hit points and died, and her partymate stood there — the
            // threat scan only sees monsters, and the reds were on her,
            // not him. Whatever is on ANY partymate is on all of us.
            if (bot.Alive && bot.Combatant == null)
            {
                var foe = PartyFoe(bot, party);
                if (foe != null)
                {
                    bot.Combatant = foe;
                    if (CombatDebug)
                    {
                        Console.WriteLine(
                            $"[party] {bot.Name} assists against '{foe.Name}'");
                    }
                }
            }

            base.Tick(bot);
        }

        // A monster fighting any of us, or a red that any of us is
        // fighting. Nearest first.
        private static Mobile PartyFoe(PlayerBot bot, BotParty party)
        {
            Mobile best = null;
            int bestDist = int.MaxValue;
            foreach (var mate in party.Everyone())
            {
                if (mate == null || mate == bot || mate.Deleted || !mate.Alive ||
                    mate.Map != bot.Map)
                {
                    continue;
                }
                var foe = mate.Combatant as Mobile;
                if (foe == null || foe.Deleted || !foe.Alive || foe.Map != bot.Map ||
                    foe == bot || BotPartyManager.PartyOf(foe as PlayerBot) == party)
                {
                    continue;
                }
                if (foe is not BaseCreature && foe is not PlayerBot)
                {
                    continue;
                }
                if (bot.IsUnreachable(foe))
                {
                    continue;
                }
                int d = Math.Max(Math.Abs(foe.X - bot.X), Math.Abs(foe.Y - bot.Y));
                if (d <= AssistRange && d < bestDist)
                {
                    bestDist = d;
                    best = foe;
                }
            }
            return best;
        }

        // A monster fighting ANY member of my party is attacking a friend,
        // so the threat scan turns on it too.
        protected override bool IsPartyFriend(PlayerBot bot, Mobile m) =>
            m is PlayerBot other && BotPartyManager.PartyOf(other) is BotParty mine &&
            BotPartyManager.PartyOf(bot) == mine;

        protected override Point3D? SelectPatrolGoal(PlayerBot bot)
        {
            var party = BotPartyManager.PartyOf(bot);
            if (party == null)
            {
                return bot.Location; // Tick self-heals next pass
            }

            // Entering: the leader is inside — walk onto the entrance pad.
            if (party.State == BotPartyState.Entering &&
                !DungeonRegistry.IsInDungeon(bot))
            {
                return party.EntranceTile;
            }

            var leader = party.Leader;
            if (leader == null || leader.Deleted || leader.Map != bot.Map)
            {
                return bot.Location; // manager handles ports / disband
            }

            if (bot.InRange(leader.Location, FollowNear))
            {
                return bot.Location; // in formation — stand easy
            }

            // Aim beside the leader, not on top of him, so a 4-bot party
            // arrives as a loose knot instead of a single-tile pile.
            int ox = Utility.RandomMinMax(-2, 2);
            int oy = Utility.RandomMinMax(-2, 2);
            return new Point3D(leader.X + ox, leader.Y + oy, leader.Z);
        }
    }
}
