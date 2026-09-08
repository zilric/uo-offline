// =========================================================================
// ThiefBehavior.cs — a bot that picks pockets.
//
// The Thief class has had the skills (Stealing, Snooping, Hiding, Stealth)
// and the dark leathers since day one, and never once used them. This is
// the brain. It works a beat the way a 1999 thief did: stand around the
// bank looking like anybody else, pick a mark, walk up, peek in the pack,
// take the best light thing in it, and walk off. Marks are anything
// player-shaped: other bots and you.
//
// Every roll is the engine's own. The bot calls the real Stealing skill
// and targets the real item, so the weight cap, the skill check, the
// "caught" roll, the karma loss, the criminal flag, the perma-gray to the
// victim and the two-minute return-on-death of stolen goods all come from
// Stealing.cs unchanged. What this file adds is the judgement around
// that: who to rob, where it is safe to try, and what to do when it goes
// wrong.
//
// When it goes wrong the shard already knows what to do. A caught thief
// flags criminal, PlayerBot.CriminalAction tells BotGrayWatch, and the
// fighters nearby draw. If a human town NPC is within eight tiles the
// engine has that NPC yell for the guards on the spot, which is instant
// death. A bot victim in a guarded town yells for the guards itself a
// second or two later, the way a player would type it. So a thief that
// gets caught at the bank counter dies at the bank counter, exactly like
// 1999, and the thief knows it: most of them only rob marks standing away
// from the NPCs, and the bold ones gamble.
//
// The getaway is running, then hiding. Hiding fails while anyone hunting
// the bot is close and can see it, so the thief runs first and hides when
// it has a gap. Once hidden it stays put until the flag lapses.
//
// Loot goes to the bank after every lift. Stolen goods return to the
// victim if the thief dies inside two minutes, so the thief walks to the
// counter and puts each take in the box before trying again. The haul is
// remembered per bot, because the walk to the bank swaps brains and the
// thief that arrives is a fresh one.
// =========================================================================

using System;
using System.Collections.Generic;
using Server;
using Server.Items;
using Server.Mobiles;
using Server.Regions;
using Server.SkillHandlers;
using MoveDelays = Server.Movement.Movement;

namespace Server.CustomBots
{
    public class ThiefBehavior : PlayerBotBehavior
    {
        public override string SerializableName => "Thief";

        // ---- Knobs ----

        // How far off the beat centre the thief looks for marks.
        private const int LookRange = 12;

        // How far the thief will drift from the beat before walking back.
        private const int BeatRadius = 14;

        // Give up on a mark that takes longer than this to reach.
        private static readonly TimeSpan ApproachLimit = TimeSpan.FromSeconds(25);

        // A mark that was tried is left alone this long.
        private static readonly TimeSpan MarkCooldown = TimeSpan.FromMinutes(4);

        // Nothing worth less than this gets lifted. Nobody risked a guard
        // whack for a torch.
        private const int WorthFloor = 15;

        // Bank the take after every successful lift.
        private const int FenceAfterLifts = 1;

        // The engine's rule: item plus contents must weigh no more than this.
        private static int MaxWeight => Stealing.MaxWeightToSteal;

        // The engine has an NPC yell for the guards when a criminal is within
        // this many tiles of it. A mark standing that close to the banker is
        // a hot mark.
        private const int NpcHeatRange = 8;

        // Cap on the running part of the getaway before the thief starts
        // trying to hide regardless.
        private const int GetawayRunSteps = 45;

        // Try to hide this often while getting away.
        private static readonly TimeSpan HideRetry = TimeSpan.FromSeconds(4);

        // Odds a bot victim bothers to yell and call the guards.
        private const double VictimCallsChance = 0.75;

        // ---- Public counters, for the test rig and the status page ----

        public int Attempts { get; private set; }
        public int Lifts    { get; private set; }
        public int Caught   { get; private set; }
        public int GoldTaken { get; private set; }

        public static int TotalAttempts { get; private set; }
        public static int TotalLifts    { get; private set; }
        public static int TotalCaught   { get; private set; }

        // ---- State ----

        private enum Mode { Prowl, Approach, Case, Getaway, Hidden, Fence }

        private Mode _mode = Mode.Prowl;

        public Point3D Home { get; private set; }
        public Map     HomeMap { get; private set; }

        private Mobile _mark;
        private DateTime _markUntil;
        private DateTime _caseUntil;
        private DateTime _nextLook;
        private readonly Dictionary<Mobile, DateTime> _tried = new();

        private PathFollower _follower;
        private Timer _stepTimer;
        private bool _running;

        // The blade goes in the pack while working. Both hands must be free
        // to steal.
        private Item _pocketed;

        // Getaway bookkeeping.
        private Point3D _scene;
        private int _runSteps;
        private DateTime _nextHideTry;
        private DateTime _getawayStarted;

        // What this thief has lifted and not yet banked. Kept per bot, not
        // per brain: the trip to the bank goes through a Traveler and the
        // thief that arrives is a new ThiefBehavior.
        private sealed class Haul
        {
            public int Gold;
            public int Lifts;
            public readonly List<Item> Items = new();
            // Lifted stacks other than gold (reagents, gems, bandages):
            // the type and how many, since they merge into the thief's own.
            public readonly List<(Type type, int amount)> Stacks = new();
        }

        private static readonly Dictionary<PlayerBot, Haul> _hauls = new();

        private static Haul HaulOf(PlayerBot bot)
        {
            if (!_hauls.TryGetValue(bot, out var haul))
            {
                if (_hauls.Count > 200)
                {
                    PruneHauls();
                }
                haul = new Haul();
                _hauls[bot] = haul;
            }
            return haul;
        }

        private static void PruneHauls()
        {
            var gone = new List<PlayerBot>();
            foreach (var (b, _) in _hauls)
            {
                if (b.Deleted)
                {
                    gone.Add(b);
                }
            }
            foreach (var b in gone)
            {
                _hauls.Remove(b);
            }
        }

        // The Traveler asks this on arrival at a bank: a thief carrying a
        // take always goes to work there, so the take gets banked.
        public static bool HasHaul(PlayerBot bot) =>
            _hauls.TryGetValue(bot, out var h) && (h.Lifts > 0 || h.Gold > 0 || h.Items.Count > 0);

        // Stable per bot. Nerve decides whether this thief works marks that
        // are standing next to an NPC (a guard whack if caught) or keeps to
        // the edges of the crowd.
        private double _nerve = 1.0;

        public ThiefBehavior()
        {
            ChatCategories  = new[] { "thief_talk", "small_talk" };
            ChatChance      = 0.10;
            MinChatCooldown = TimeSpan.FromSeconds(40);
            MaxChatCooldown = TimeSpan.FromSeconds(120);
        }

        public override string GetStatusLine(PlayerBot bot) => _mode switch
        {
            Mode.Prowl    => "working the crowd",
            Mode.Approach => _mark != null ? $"sizing up {_mark.Name}" : "working the crowd",
            Mode.Case     => _mark != null ? $"next to {_mark.Name}" : "working the crowd",
            Mode.Getaway  => "getting away",
            Mode.Hidden   => "hiding with the flag on",
            Mode.Fence    => "banking the take",
            _             => null,
        };

        public override Point3D? NavGoal(PlayerBot bot) => _mode switch
        {
            Mode.Approach when _mark != null => _mark.Location,
            Mode.Fence => Home,
            _ => null,
        };

        public override void OnAttached(PlayerBot bot)
        {
            base.OnAttached(bot);

            Home    = bot.Location;
            HomeMap = bot.Map;
            _mode   = Mode.Prowl;
            _nextLook = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(4, 12));

            // Stealing from a player needs the guild card. Thieves get it at
            // creation; bots saved before that get it here.
            if (bot.Class == BotClass.Thief && bot.NpcGuild != NpcGuild.ThievesGuild)
            {
                bot.NpcGuild = NpcGuild.ThievesGuild;
                bot.NpcGuildJoinTime = Core.Now;
            }

            var seed = bot.Serial.Value % 100;
            _nerve = 0.60 + seed / 100.0;
            if (bot.Personality.HasTrait(PersonalityTrait.Brave))    _nerve += 0.30;
            if (bot.Personality.HasTrait(PersonalityTrait.Cautious)) _nerve -= 0.30;
            _nerve = Math.Clamp(_nerve, 0.50, 1.70);

            PocketBlade(bot);

            // A thief that loaded from a save with the flag still on picks
            // up where it left off: getting away.
            if (bot.Criminal)
            {
                StartGetaway(bot, null);
                return;
            }

            // Arrived at the bank with a take in the pack: the counter
            // comes before the next mark.
            if (HasHaul(bot))
            {
                _nextLook = Core.Now;
            }
        }

        public override void OnDetached(PlayerBot bot)
        {
            StopStepTimer();
            _follower = null;
            _mark = null;
            RearmBlade(bot);
            base.OnDetached(bot);
        }

        // -------------------------------------------------------------------
        // Tick — every 2 seconds.
        // -------------------------------------------------------------------
        public override void Tick(PlayerBot bot)
        {
            if (bot.Map == null || bot.Map == Map.Internal || !bot.Alive)
            {
                return;
            }

            // Only thieves do this. A save can hand the name to anybody.
            if (bot.Class != BotClass.Thief)
            {
                bot.Behavior = RedTerritory.TravelBrain(bot);
                return;
            }

            // Somebody is on the bot. Thieves do not fight, they leave.
            if (_mode is not (Mode.Getaway or Mode.Hidden) && UnderAttack(bot, out var attacker))
            {
                Console.WriteLine(
                    $"[thief] {bot.Name} is being attacked by {attacker.Name} at " +
                    $"{BotEventJournal.PlaceName(bot.Location, bot.Map)}, running");
                StartGetaway(bot, attacker);
                return;
            }

            // The flag came on some other way (a swing in self defence, a
            // failed snoop noticed in a way the engine flags). Same answer.
            if (bot.Criminal && _mode is not (Mode.Getaway or Mode.Hidden))
            {
                StartGetaway(bot, null);
                return;
            }

            switch (_mode)
            {
                case Mode.Prowl:    TickProwl(bot);    break;
                case Mode.Approach: TickApproach(bot); break;
                case Mode.Case:     TickCase(bot);     break;
                case Mode.Getaway:  TickGetaway(bot);  break;
                case Mode.Hidden:   TickHidden(bot);   break;
                case Mode.Fence:    TickFence(bot);    break;
            }
        }

        // -------------------------------------------------------------------
        // Prowl — stand in the crowd, look for a mark now and then.
        // -------------------------------------------------------------------
        private void TickProwl(PlayerBot bot)
        {
            // The visit ends between marks, never mid-lift and never with
            // the flag on (a Traveler walking through town gray dies).
            if (CheckVisitExpired(bot))
            {
                return;
            }

            if (FenceDue(bot))
            {
                BeginFence(bot);
                return;
            }

            TrySpeak(bot);

            // Drifted off the beat (shoved, or a getaway ended out on the
            // street). Walk back before looking again.
            if (bot.Map == HomeMap && !bot.InRange(Home, BeatRadius))
            {
                WalkTo(bot, Home, run: false);
                return;
            }
            StopStepTimer();

            if (Core.Now < _nextLook)
            {
                // Look around like anybody waiting at a bank.
                if (Utility.RandomDouble() < 0.12)
                {
                    bot.Direction = (Direction)Utility.Random(8);
                }
                return;
            }

            // The skill's own cooldown. Ten seconds after a lift, thirty
            // after an aborted one.
            if (Core.TickCount - bot.NextSkillTime < 0)
            {
                return;
            }

            _nextLook = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(6, 18));

            var mark = PickMark(bot);
            if (mark == null)
            {
                return;
            }

            _mark      = mark;
            _markUntil = Core.Now + ApproachLimit;
            _mode      = Mode.Approach;
            _follower  = null;

            if (Verbose)
            {
                Console.WriteLine(
                    $"[thief] {bot.Name} eyeing {mark.Name} " +
                    $"{bot.GetDistanceToSqrt(mark.Location):0} tiles off");
            }
        }

        // Extra console lines about marks picked and dropped. On while the
        // behaviour is new; the lift/caught/getaway lines are always on.
        public static bool Verbose = true;

        // -------------------------------------------------------------------
        // Who to rob. Player-shaped, awake, holding something, not on our
        // side, and standing somewhere this thief has the nerve to work.
        // -------------------------------------------------------------------
        private Mobile PickMark(PlayerBot bot)
        {
            Mobile best = null;
            double bestScore = double.MinValue;
            var region = bot.Region?.GetRegion<GuardedRegion>();
            if (region != null && region.IsDisabled())
            {
                region = null;
            }

            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, LookRange))
            {
                if (!IsMark(bot, m))
                {
                    continue;
                }

                // How many town NPCs would yell if the thief flagged next to
                // this mark. Any at all means a guard in one second.
                int heat = region == null ? 0 : CountTownNpcs(m, region);
                if (heat > 0 && !TakesHotMarks(bot))
                {
                    continue;
                }

                double score = -bot.GetDistanceToSqrt(m.Location);

                // A real player is the audience. Bots rob them first.
                if (m is not PlayerBot)
                {
                    score += 6;
                }

                // Standing still reads as afk, the easiest mark there is.
                if (m is PlayerBot { Behavior: BankSitterBehavior or IdleBehavior })
                {
                    score += 2;
                }

                score += Utility.RandomDouble() * 3;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = m;
                }
            }

            return best;
        }

        private bool IsMark(PlayerBot bot, Mobile m)
        {
            if (m is not PlayerMobile pm || pm == bot || pm.Deleted || !pm.Alive ||
                pm.AccessLevel > AccessLevel.Player || pm.Blessed || pm.Hidden)
            {
                return false;
            }

            // A real player that is not connected is on the way out.
            if (pm is not PlayerBot && pm.NetState == null && !BotThiefTest.IsRig(pm))
            {
                return false;
            }

            if (pm.Young || pm.Murderer || pm.Combatant != null)
            {
                return false;
            }

            // Other thieves are guild. Guildmates and partymates are ours.
            if (pm is PlayerBot other)
            {
                if (other.Class == BotClass.Thief || other.Behavior is GhostBehavior)
                {
                    return false;
                }
                if (bot.BotGuildIndex >= 0 && bot.BotGuildIndex == other.BotGuildIndex)
                {
                    return false;
                }
                var party = BotPartyManager.PartyOf(bot);
                if (party != null && party == BotPartyManager.PartyOf(other))
                {
                    return false;
                }
            }

            var botParty = Engines.PartySystem.Party.Get(bot);
            if (botParty != null && botParty == Engines.PartySystem.Party.Get(pm))
            {
                return false;
            }

            var pack = pm.Backpack;
            if (pack == null || pack.Items.Count == 0)
            {
                return false;
            }

            if (_tried.TryGetValue(pm, out var until) && Core.Now < until)
            {
                return false;
            }

            return bot.CanSee(pm) && bot.InLOS(pm) && bot.CanBeHarmful(pm, false);
        }

        // The bold ones work the counter. Low skill never does: a novice is
        // caught two tries in three and would die every few minutes.
        // The test rig sets this so a run at the bank always tries.
        public bool Fearless { get; set; }

        private bool TakesHotMarks(PlayerBot bot)
        {
            if (Fearless)
            {
                return true;
            }
            if (bot.Skills.Stealing.Value < 70.0)
            {
                return false;
            }
            return Utility.RandomDouble() < _nerve - 0.90;
        }

        // Same test the engine runs when it decides whether an NPC yells.
        private static int CountTownNpcs(Mobile around, GuardedRegion region)
        {
            int n = 0;
            foreach (var v in around.Map.GetMobilesInRange(around.Location, NpcHeatRange))
            {
                if (v.Player || v == around || !v.Alive || region.IsGuardCandidate(v))
                {
                    continue;
                }
                bool human = (v as BaseCreature)?.IsHumanInTown()
                             ?? (v.Body.IsHuman && v.Region.IsPartOf(region));
                if (human)
                {
                    n++;
                }
            }
            return n;
        }

        // -------------------------------------------------------------------
        // Approach — walk up next to the mark.
        // -------------------------------------------------------------------
        private void TickApproach(PlayerBot bot)
        {
            var gone = WhyMarkGone(bot);
            if (gone != null)
            {
                DropMark(bot, TimeSpan.FromSeconds(45), gone);
                return;
            }

            if (Core.Now >= _markUntil)
            {
                DropMark(bot, MarkCooldown, "could not get next to it in time");
                return;
            }

            if (bot.InRange(_mark.Location, 1))
            {
                StopStepTimer();
                _follower = null;
                bot.Direction = bot.GetDirectionTo(_mark);
                // Stand there a moment. Walking up and lifting in the same
                // breath is how a player spots you.
                _caseUntil = Core.Now + TimeSpan.FromSeconds(1 + Utility.RandomDouble() * 3);
                _mode = Mode.Case;
                return;
            }

            // Walk when close, so it reads as someone drifting over. Run
            // when the mark is across the plaza.
            WalkTo(bot, _mark, run: bot.GetDistanceToSqrt(_mark.Location) > 6);
        }

        private bool MarkStillGood(PlayerBot bot) => WhyMarkGone(bot) == null;

        // Null while the mark is still worth following, else the reason.
        private string WhyMarkGone(PlayerBot bot)
        {
            if (_mark == null || _mark.Deleted) return "gone";
            if (!_mark.Alive) return "it died";
            if (_mark.Map != bot.Map) return "it left the map";
            if (_mark.Hidden) return "it hid";
            if (!bot.CanSee(_mark)) return "cannot see it";
            if (!bot.InRange(_mark.Location, LookRange + 8)) return "it got away";
            if (!(_mark.Backpack?.Items.Count > 0)) return "its pack is empty";
            return null;
        }

        private void DropMark(PlayerBot bot, TimeSpan leaveAlone, string why = null)
        {
            if (_mark != null)
            {
                _tried[_mark] = Core.Now + leaveAlone;
                if (Verbose && why != null)
                {
                    Console.WriteLine($"[thief] {bot.Name} gave up on {_mark.Name}: {why}");
                }
            }
            _mark = null;
            _follower = null;
            StopStepTimer();
            _mode = Mode.Prowl;
            _nextLook = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(3, 8));
            TidyTried();
        }

        private void TidyTried()
        {
            if (_tried.Count < 40)
            {
                return;
            }
            var gone = new List<Mobile>();
            foreach (var (m, until) in _tried)
            {
                if (Core.Now >= until || m.Deleted)
                {
                    gone.Add(m);
                }
            }
            foreach (var m in gone)
            {
                _tried.Remove(m);
            }
        }

        // -------------------------------------------------------------------
        // Case — next to the mark. Peek, pick, lift.
        // -------------------------------------------------------------------
        private void TickCase(PlayerBot bot)
        {
            if (!MarkStillGood(bot) || !bot.InRange(_mark.Location, 1))
            {
                // It walked off. Follow a little, then give up.
                if (MarkStillGood(bot) && Core.Now < _markUntil)
                {
                    _mode = Mode.Approach;
                    return;
                }
                DropMark(bot, TimeSpan.FromSeconds(45), "it walked off");
                return;
            }

            if (Core.Now < _caseUntil)
            {
                return;
            }

            if (Core.TickCount - bot.NextSkillTime < 0)
            {
                return;
            }

            var mark = _mark;
            var pack = mark.Backpack;

            // The peek. The engine rolls Snooping, docks karma, and tells
            // any real player in eight tiles when a low-skill peek is
            // noticed. Whether the thief gets a look is its own roll on
            // the same skill.
            bool gotALook = true;
            if (pack != null)
            {
                try { pack.OnSnoop(bot); } catch { }
                double snoop = bot.Skills.Snooping.Value;
                gotALook = snoop >= 100.0 || Utility.RandomDouble() * 100.0 < snoop;
            }

            object target;
            Item wanted = null;
            if (gotALook)
            {
                wanted = PickItem(bot, mark);
                if (wanted == null)
                {
                    // Nothing in there worth the risk.
                    DropMark(bot, MarkCooldown, "nothing worth taking");
                    return;
                }
                target = wanted;
            }
            else if (Utility.RandomDouble() < 0.5)
            {
                // No look. Half the time take a blind grab, which the
                // engine resolves as a random item from the pack.
                target = mark;
            }
            else
            {
                DropMark(bot, TimeSpan.FromSeconds(60), "could not get a look in the pack");
                return;
            }

            Lift(bot, mark, target, wanted);
        }

        // Best value under the weight cap. The engine refuses equipped,
        // newbied, blessed and (in this build) container targets, so those
        // are skipped here rather than wasted on a thirty-second cooldown.
        private static Item PickItem(PlayerBot bot, Mobile mark)
        {
            var pack = mark.Backpack;
            if (pack == null)
            {
                return null;
            }

            Item best = null;
            int bestValue = WorthFloor - 1;

            foreach (var item in pack.Items)
            {
                if (item == null || item.Deleted || !item.Movable ||
                    item.LootType == LootType.Newbied || item.CheckBlessed(mark) ||
                    item is Container || item.InSecureTrade)
                {
                    continue;
                }

                var weight = item.Weight + item.TotalWeight;
                // A stack heavier than the cap is still worth a partial
                // grab: the engine takes what the skill allows from it.
                if (weight > MaxWeight && !(item.Stackable && item.Weight <= MaxWeight))
                {
                    continue;
                }

                int value = BotAppraisal.Value(item);
                if (item is Gold)
                {
                    value = item.Amount;
                }
                if (value <= bestValue)
                {
                    continue;
                }
                bestValue = value;
                best = item;
            }

            return best;
        }

        // -------------------------------------------------------------------
        // Lift — the real skill, the real target, the real outcome.
        // -------------------------------------------------------------------
        private void Lift(PlayerBot bot, Mobile mark, object target, Item wanted)
        {
            PocketBlade(bot);
            bool wasCriminal = bot.Criminal;
            var packBefore = SnapshotPack(bot);
            var victimBefore = SnapshotVictim(mark);

            Attempts++;
            TotalAttempts++;

            bot.Target = null;
            try
            {
                Stealing.OnUse(bot);
                if (bot.Target == null)
                {
                    // Hands were not free or the spot forbids it.
                    DropMark(bot, MarkCooldown, "no steal cursor (hands full or safe zone)");
                    return;
                }
                bot.Target.Invoke(bot, target);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[thief] {bot.Name} steal threw: {ex.Message}");
                DropMark(bot, MarkCooldown);
                return;
            }

            var place = BotEventJournal.PlaceName(bot.Location, bot.Map);
            bool caught = !wasCriminal && bot.Criminal;

            // What left the victim's pack is the truth. A stolen stack of
            // gold or reagents merges into the thief's own stack on the
            // way in, so the thief's pack shows nothing new.
            var (success, what, gold, value, stackType, stackAmount) = WhatLeft(victimBefore, mark);
            if (!success)
            {
                what = wanted != null ? DescribeAmount(wanted, 1) : "something";
            }

            if (success)
            {
                Lifts++;
                TotalLifts++;
                GoldTaken += gold;

                var haul = HaulOf(bot);
                haul.Lifts++;
                haul.Gold += gold;
                if (stackType != null && stackAmount > 0)
                {
                    haul.Stacks.Add((stackType, stackAmount));
                }

                // Loose items go in the box as they are. Coins are counted
                // and the same amount is moved at the counter.
                var got = FindNewItem(bot, packBefore);
                if (got != null && !got.Stackable)
                {
                    haul.Items.Add(got);
                }
            }

            if (caught)
            {
                Caught++;
                TotalCaught++;
            }

            Console.WriteLine(
                $"[thief] {bot.Name} {(success ? "lifted" : "fumbled")} {what} " +
                $"{(success ? "from" : "on")} {mark.Name} at {place} " +
                $"({(caught ? "CAUGHT" : "clean")})");

            // Gossip. The victim's story: who did it if they saw, "somebody"
            // if they did not. A miss nobody noticed is not a story.
            if (success || caught)
            {
                BotEventJournal.Record("theft", mark.Name, caught ? bot.Name : "somebody",
                    mark.Location, mark.Map);
            }

            _tried[mark] = Core.Now + MarkCooldown;

            if (caught)
            {
                // Nothing to say. Run.
                VictimReacts(bot, mark);
                StartGetaway(bot, mark);
                return;
            }

            // Clean. Wander off a step or two and look innocent.
            _mark = null;
            _mode = Mode.Prowl;
            _nextLook = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(8, 25));
            StepAwayFrom(bot, bot.GetDirectionTo(mark));
        }

        private readonly struct PackRow
        {
            public readonly Item Item;
            public readonly int Amount;
            public PackRow(Item item, int amount) { Item = item; Amount = amount; }
        }

        private static List<PackRow> SnapshotVictim(Mobile mark)
        {
            var rows = new List<PackRow>();
            var pack = mark.Backpack;
            if (pack == null)
            {
                return rows;
            }
            foreach (var item in pack.Items)
            {
                rows.Add(new PackRow(item, item.Amount));
            }
            return rows;
        }

        // Compare the victim's pack with the snapshot. Whole items gone,
        // or a stack that shrank, is what was taken.
        private static (bool success, string what, int gold, int value, Type stackType, int stackAmount) WhatLeft(
            List<PackRow> before, Mobile mark)
        {
            var pack = mark.Backpack;
            foreach (var row in before)
            {
                var item = row.Item;

                if (item.Stackable)
                {
                    // A split stack can leave either half behind, so count
                    // the type as a whole rather than trusting one object.
                    var type = item.GetType();
                    int was = 0;
                    foreach (var r in before)
                    {
                        if (r.Item.GetType() == type)
                        {
                            was += r.Amount;
                        }
                    }
                    int now = 0;
                    if (pack != null)
                    {
                        foreach (var it in pack.Items)
                        {
                            if (it.GetType() == type)
                            {
                                now += it.Amount;
                            }
                        }
                    }
                    if (now < was)
                    {
                        int taken = was - now;
                        int g = item is Gold ? taken : 0;
                        int v = item is Gold ? 0 : BotAppraisal.Value(item) * taken / Math.Max(1, row.Amount);
                        return (true, DescribeAmount(item, taken), g, v,
                                item is Gold ? null : type, item is Gold ? 0 : taken);
                    }
                    continue;
                }

                if (item.Deleted || item.RootParent != mark)
                {
                    return (true, DescribeAmount(item, 1), 0, BotAppraisal.Value(item), null, 0);
                }
            }
            return (false, null, 0, 0, null, 0);
        }

        private static string DescribeAmount(Item item, int amount)
        {
            if (item is Gold)
            {
                return $"{amount} gold";
            }
            if (item.Stackable)
            {
                // The appraiser's name carries the stack size, which is not
                // the number taken. Use the tile name.
                var plain = item.Name ?? item.ItemData.Name ?? item.GetType().Name.ToLowerInvariant();
                return amount > 1 ? $"{amount} {plain}" : plain;
            }
            var name = BotAppraisal.NameFor(item);
            if (string.IsNullOrEmpty(name))
            {
                name = item.Name ?? item.GetType().Name.ToLowerInvariant();
            }
            return name;
        }

        private static HashSet<Item> SnapshotPack(PlayerBot bot)
        {
            var set = new HashSet<Item>();
            var pack = bot.Backpack;
            if (pack == null)
            {
                return set;
            }
            foreach (var item in pack.Items)
            {
                set.Add(item);
            }
            return set;
        }

        // The stolen item lands in the thief's pack, or merges into a
        // stack that was already there (gold, reagents). Either counts.
        private static Item FindNewItem(PlayerBot bot, HashSet<Item> before)
        {
            var pack = bot.Backpack;
            if (pack == null)
            {
                return null;
            }
            foreach (var item in pack.Items)
            {
                if (!before.Contains(item))
                {
                    return item;
                }
            }
            return null;
        }

        private static string Describe(Item item) => DescribeAmount(item, item.Amount);

        // -------------------------------------------------------------------
        // The victim. A real player saw the engine's own message and can
        // type "guards" themselves. A bot yells and calls the watch, a
        // second or two later, if the thief is still where the guards can
        // reach it.
        // -------------------------------------------------------------------
        private static void VictimReacts(PlayerBot thief, Mobile mark)
        {
            if (mark is not PlayerBot victim || victim.Deleted || !victim.Alive)
            {
                return;
            }

            if (Utility.RandomDouble() > VictimCallsChance)
            {
                return;
            }

            var delay = TimeSpan.FromSeconds(1.5 + Utility.RandomDouble() * 2.5);
            Timer.DelayCall(delay, () => VictimCallsGuards(victim, thief));
        }

        private static void VictimCallsGuards(PlayerBot victim, PlayerBot thief)
        {
            if (victim.Deleted || !victim.Alive || thief.Deleted || !thief.Alive ||
                thief.Map != victim.Map || !victim.InRange(thief.Location, 14))
            {
                return;
            }

            bool someoneListening = false;
            foreach (var m in victim.Map.GetMobilesInRange(victim.Location, 22))
            {
                if (m is PlayerMobile && m is not PlayerBot)
                {
                    someoneListening = true;
                    break;
                }
            }

            var region = thief.Region?.GetRegion<GuardedRegion>();
            bool guardsHere = region != null && !region.IsDisabled() &&
                              region.IsGuardCandidate(thief) &&
                              victim.Region?.GetRegion<GuardedRegion>() == region;

            if (someoneListening)
            {
                var line = ChatLibrary.PickRandom(guardsHere ? "guards_call" : "thief_victim");
                if (!string.IsNullOrEmpty(line))
                {
                    victim.Say(line);
                }
            }

            if (guardsHere)
            {
                region.CallGuards(thief.Location);
                Console.WriteLine(
                    $"[thief] {victim.Name} called the guards on {thief.Name} at " +
                    $"{BotEventJournal.PlaceName(victim.Location, victim.Map)}");
            }
        }

        // -------------------------------------------------------------------
        // Getaway — run, then hide, then wait out the flag.
        // -------------------------------------------------------------------
        private void StartGetaway(PlayerBot bot, Mobile from)
        {
            _mark = null;
            _follower = null;
            StopStepTimer();
            _scene = from?.Location ?? bot.Location;
            _runSteps = 0;
            _getawayStarted = Core.Now;
            _nextHideTry = Core.Now + TimeSpan.FromSeconds(3);
            _mode = Mode.Getaway;
            bot.Combatant = null;
            EnsureStepTimer(bot, running: true);
        }

        private void TickGetaway(PlayerBot bot)
        {
            if (bot.Hidden)
            {
                StopStepTimer();
                _mode = Mode.Hidden;
                Console.WriteLine(
                    $"[thief] {bot.Name} hid at {BotEventJournal.PlaceName(bot.Location, bot.Map)}" +
                    $"{(bot.Criminal ? " with the flag on" : "")}");
                return;
            }

            var threat = NearestHunter(bot, 10);

            // Clear of the flag and of anyone chasing: the episode is over.
            if (!bot.Criminal && threat == null &&
                Core.Now - _getawayStarted > TimeSpan.FromSeconds(6))
            {
                EndGetaway(bot, "got clear");
                return;
            }

            // A gap. Try to vanish. The engine refuses while a hunter is
            // close and can see the bot, and reveals it for trying.
            if (Core.Now >= _nextHideTry && (threat == null || !bot.InRange(threat.Location, 8)))
            {
                _nextHideTry = Core.Now + HideRetry;
                StopStepTimer();
                try { Hiding.OnUse(bot); } catch { }
                if (bot.Hidden)
                {
                    return;
                }
            }

            if (_runSteps < GetawayRunSteps || threat != null)
            {
                EnsureStepTimer(bot, running: true);
            }
            else
            {
                StopStepTimer();
            }

            // Ran a long way and still gray with nobody hiding-close. Sit
            // tight and keep trying; the flag is two minutes at most.
            if (Core.Now - _getawayStarted > TimeSpan.FromMinutes(3))
            {
                EndGetaway(bot, "gave up hiding");
            }
        }

        private void TickHidden(PlayerBot bot)
        {
            if (!bot.Hidden)
            {
                // Found, or the hide fell apart. Back to running.
                _mode = Mode.Getaway;
                _runSteps = 0;
                _getawayStarted = Core.Now;
                return;
            }

            if (bot.Criminal)
            {
                return;
            }

            // Flag lapsed. Stand up.
            bot.RevealingAction();
            EndGetaway(bot, "flag lapsed");
        }

        private void EndGetaway(PlayerBot bot, string how)
        {
            StopStepTimer();
            Console.WriteLine(
                $"[thief] {bot.Name} {how} at {BotEventJournal.PlaceName(bot.Location, bot.Map)}");

            // Half the time the thief has had enough of this town.
            if (Utility.RandomDouble() < 0.5 || (bot.Map == HomeMap && !bot.InRange(Home, 40)))
            {
                bot.Behavior = RedTerritory.TravelBrain(bot);
                return;
            }

            _mode = Mode.Prowl;
            _nextLook = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(20, 60));
        }

        // Anyone in range who is fighting this bot.
        private static Mobile NearestHunter(PlayerBot bot, int range)
        {
            Mobile best = null;
            double bestDist = double.MaxValue;
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, range))
            {
                if (m == bot || !m.Alive || m.Deleted)
                {
                    continue;
                }
                if (m.Combatant != bot && !(m is BaseGuard))
                {
                    continue;
                }
                var d = bot.GetDistanceToSqrt(m.Location);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = m;
                }
            }
            return best;
        }

        private static bool UnderAttack(PlayerBot bot, out Mobile attacker)
        {
            attacker = bot.Combatant as Mobile;
            if (attacker != null && attacker.Alive && !attacker.Deleted)
            {
                return true;
            }
            attacker = null;

            var list = bot.Aggressors;
            for (int i = 0; i < list.Count; i++)
            {
                var info = list[i];
                if (info.Attacker == null || info.Attacker.Deleted || !info.Attacker.Alive)
                {
                    continue;
                }
                if (Core.Now - info.LastCombatTime > TimeSpan.FromSeconds(8))
                {
                    continue;
                }
                if (!bot.InRange(info.Attacker.Location, 12))
                {
                    continue;
                }
                attacker = info.Attacker;
                return true;
            }
            return false;
        }

        // -------------------------------------------------------------------
        // Fence — walk to the counter and put the take in the box.
        // -------------------------------------------------------------------
        private static bool FenceDue(PlayerBot bot) =>
            _hauls.TryGetValue(bot, out var h) && h.Lifts >= FenceAfterLifts;

        private void BeginFence(PlayerBot bot)
        {
            if (FindBanker(bot) != null)
            {
                DoFence(bot);
                return;
            }

            // Not at a bank. Walk to one; the beat ends there.
            var bank = NearestBank(bot);
            if (bank == null)
            {
                DoFence(bot); // no bank known: just clear the tally
                return;
            }

            var take = HaulOf(bot);
            Console.WriteLine(
                $"[thief] {bot.Name} heading to {bank.Name} to bank the take " +
                $"({take.Gold} gold, {take.Items.Count} item(s))");
            bot.Behavior = new TravelerBehavior { DestinationName = bank.Name };
        }

        private void TickFence(PlayerBot bot)
        {
            // Only reached when the fence is at this bank but out of reach.
            DoFence(bot);
        }

        private void DoFence(PlayerBot bot)
        {
            var banker = FindBanker(bot);
            var haul = HaulOf(bot);
            int items = 0;
            int gold = 0;

            if (banker != null)
            {
                var box = bot.BankBox;
                var pack = bot.Backpack;
                if (box != null)
                {
                    for (int i = haul.Items.Count - 1; i >= 0; i--)
                    {
                        var item = haul.Items[i];
                        if (item == null || item.Deleted || item.RootParent != bot)
                        {
                            continue;
                        }
                        if (box.TryDropItem(bot, item, false))
                        {
                            items++;
                        }
                    }

                    // The lifted coins, and only those. The purse stays.
                    int have = pack?.GetAmount(typeof(Gold)) ?? 0;
                    int move = Math.Min(haul.Gold, have);
                    if (move > 0 && pack.ConsumeTotal(typeof(Gold), move))
                    {
                        box.DropItem(new Gold(move));
                        gold = move;
                    }

                    // Lifted reagents, gems and the like: the same count
                    // that was taken comes back out of the thief's stack.
                    foreach (var (type, amount) in haul.Stacks)
                    {
                        int own = pack?.GetAmount(type) ?? 0;
                        int n = Math.Min(amount, own);
                        if (n <= 0 || !pack.ConsumeTotal(type, n))
                        {
                            continue;
                        }
                        Item part = null;
                        try { part = type.CreateInstance<Item>(); } catch { }
                        if (part == null)
                        {
                            continue;
                        }
                        part.Amount = n;
                        box.DropItem(part);
                        items++;
                    }
                }

                if (items > 0 || gold > 0)
                {
                    Console.WriteLine(
                        $"[thief] {bot.Name} banked {gold} gold and {items} item(s) at " +
                        $"{BotEventJournal.PlaceName(bot.Location, bot.Map)}");
                    TryEventLine(bot, 0.30, "thief_brag");
                }
            }

            _hauls.Remove(bot);
            _mode = Mode.Prowl;
            _nextLook = Core.Now + TimeSpan.FromSeconds(Utility.RandomMinMax(10, 30));
        }

        private static Banker FindBanker(PlayerBot bot)
        {
            foreach (var m in bot.Map.GetMobilesInRange(bot.Location, 12))
            {
                if (m is Banker b && b.Alive && !b.Deleted)
                {
                    return b;
                }
            }
            return null;
        }

        private static BotDestination NearestBank(PlayerBot bot)
        {
            BotDestination best = null;
            double bestDist = double.MaxValue;
            foreach (var d in DestinationCatalog.All)
            {
                if (d.Type != DestinationType.Bank)
                {
                    continue;
                }
                var dist = bot.GetDistanceToSqrt(d.Location);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = d;
                }
            }
            return best;
        }

        // -------------------------------------------------------------------
        // Hands. The dagger lives in the pack on the beat and goes back on
        // when the beat ends.
        // -------------------------------------------------------------------
        private void PocketBlade(PlayerBot bot)
        {
            var one = bot.FindItemOnLayer(Layer.OneHanded);
            var two = bot.FindItemOnLayer(Layer.TwoHanded);
            var blade = one ?? two;
            if (blade == null)
            {
                return;
            }
            if (bot.Backpack != null && bot.Backpack.TryDropItem(bot, blade, false))
            {
                _pocketed ??= blade;
            }
            if (one != null && two != null)
            {
                bot.Backpack?.TryDropItem(bot, two, false);
            }
        }

        private void RearmBlade(PlayerBot bot)
        {
            var blade = _pocketed;
            _pocketed = null;
            if (blade == null || blade.Deleted || blade.RootParent != bot || !bot.Alive)
            {
                return;
            }
            if (bot.FindItemOnLayer(Layer.OneHanded) == null &&
                bot.FindItemOnLayer(Layer.TwoHanded) == null)
            {
                bot.EquipItem(blade);
            }
        }

        // -------------------------------------------------------------------
        // Feet.
        // -------------------------------------------------------------------
        // A mobile goal is followed as it moves. Home is boxed once so the
        // follower is not rebuilt every tick.
        private IPoint3D _homeGoal;

        private void WalkTo(PlayerBot bot, IPoint3D goal, bool run)
        {
            if (goal is Point3D p)
            {
                if (_homeGoal == null || !new Point3D(_homeGoal).Equals(p))
                {
                    _homeGoal = p;
                }
                goal = _homeGoal;
            }
            if (_follower == null || !ReferenceEquals(_follower.Goal, goal))
            {
                _follower = new PathFollower(bot, goal);
            }
            EnsureStepTimer(bot, run);
        }

        private void EnsureStepTimer(PlayerBot bot, bool running)
        {
            if (_stepTimer != null && _running == running)
            {
                return;
            }
            StopStepTimer();
            _running = running;
            int delayMs = running ? MoveDelays.RunFootDelay : MoveDelays.WalkFootDelay;
            var interval = TimeSpan.FromMilliseconds(delayMs);
            _stepTimer = Timer.DelayCall(interval, interval, () => StepOnce(bot));
        }

        private void StopStepTimer()
        {
            if (_stepTimer != null)
            {
                _stepTimer.Stop();
                _stepTimer = null;
            }
        }

        private void StepOnce(PlayerBot bot)
        {
            if (bot.Deleted || !bot.Alive || bot.Map == null || bot.Behavior != this)
            {
                StopStepTimer();
                return;
            }

            switch (_mode)
            {
                case Mode.Getaway:
                    if (bot.Hidden)
                    {
                        StopStepTimer();
                        return;
                    }
                    var hunter = NearestHunter(bot, 12);
                    var toward = hunter != null
                        ? bot.GetDirectionTo(hunter)
                        : bot.GetDirectionTo(_scene);
                    if (hunter == null && bot.GetDistanceToSqrt(_scene) < 1)
                    {
                        toward = (Direction)Utility.Random(8);
                    }
                    if (StepAwayFrom(bot, toward))
                    {
                        _runSteps++;
                    }
                    else
                    {
                        // Pinned. Any way out at all.
                        for (int i = 0; i < 8; i++)
                        {
                            if (bot.Move((Direction)i))
                            {
                                _runSteps++;
                                break;
                            }
                        }
                    }
                    break;

                case Mode.Approach:
                case Mode.Prowl:
                    if (_follower == null)
                    {
                        StopStepTimer();
                        return;
                    }
                    if (_follower.Follow(_running, 1))
                    {
                        StopStepTimer();
                    }
                    break;

                default:
                    StopStepTimer();
                    break;
            }
        }

        // One tile directly away from whatever `toward` points at, flowing
        // round blockers. False means every way back is blocked.
        private static bool StepAwayFrom(PlayerBot bot, Direction toward)
        {
            var away = (Direction)(((int)toward + 4) & 0x7);
            if (bot.Move(away))
            {
                return true;
            }
            var l = (Direction)(((int)away + 7) & 0x7);
            var r = (Direction)(((int)away + 1) & 0x7);
            return bot.Move(l) || bot.Move(r);
        }
    }
}
