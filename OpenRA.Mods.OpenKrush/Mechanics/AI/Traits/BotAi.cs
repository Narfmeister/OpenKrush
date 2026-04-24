#region Copyright & License Information

/*
 * Copyright 2007-2022 The OpenKrush Developers (see AUTHORS)
 * This file is part of OpenKrush, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */

#endregion

namespace OpenRA.Mods.OpenKrush.Mechanics.AI.Traits;

using Common;
using Common.Activities;
using Common.Traits;
using Construction.Orders;
using Construction.Traits;
using JetBrains.Annotations;
using Misc.Traits;
using Oil.Traits;
using OpenRA;
using OpenRA.Mods.Common.Traits;
using OpenRA.Support;
using OpenRA.Traits;
using Production.Traits;
using Repairbays.Traits;
using Researching;
using Researching.Orders;
using Researching.Traits;

/// <summary>How the bot picks which affordable combat unit to queue (cost diversity vs pure random vs legacy cheapest pool).</summary>
public enum BotCombatUnitPickMode
{
	/// <summary>Equal chance to pick a unit from a low, mid, or high cost third of the queue roster (when budget allows that band).</summary>
	Tertile = 0,
	/// <summary>Random choice weighted to prefer unit costs near the mean cost of the full buildable roster for this queue.</summary>
	MeanWeighted = 1,
	/// <summary>Uniform random among all affordable units.</summary>
	Uniform = 2,
	/// <summary>Random among the <see cref="BotAiInfo.RandomPickCheapestOf" /> cheapest affordable units (legacy, tends to spam low cost).</summary>
	CheapestK = 3,
}

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public class BotAiInfo : ConditionalTraitInfo
{
	// For performance we delay some ai tasks => OpenKrush runs with 25 ticks per second (at normal speed).
	public int ThinkDelay = 25;

	[Desc("Minimum ticks between attempts to queue a combat unit (infantry, vehicle, …; 25 ticks ≈ 1s at normal speed).")]
	public int InfantryProductionCooldownTicks = 50;

	[Desc("Maximum total combat units (infantry, vehicles, etc.) queued across all production structures.")]
	public int MaxInfantryQueued = 12;

	[Desc("If set, try these actor names in order for any queue that can build them; otherwise use UnitCostPick selection.")]
	public string[] PreferredInfantry = [];

	[Desc("When UnitCostPick is CheapestK: pick randomly among the K cheapest affordable units (K clamped to available count, min 1).")]
	public int RandomPickCheapestOf = 5;

	[Desc("How to pick among affordable combat unit types. Tertile = balance low/mid/high cost; MeanWeighted = prefer near roster average; Uniform; CheapestK = only pool of cheapest (legacy).")]
	public BotCombatUnitPickMode UnitCostPick = BotCombatUnitPickMode.Tertile;

	[Desc("Unit production types to use; default infantry + vehicle. Must match queue Type: (e.g. infantry, vehicle).")]
	public string[] CombatProductionQueueTypes = ["infantry", "vehicle"];

	[Desc("During early-mid: max field combat + combat-queued units (stops 50+ low-tier stockpiles before research/upgrades). 0 = disabled (no cap). If > 0 and CombatForceSoftCapEndWorldTick is 0, the early cap applies for the whole match.")]
	public int CombatForceSoftCapEarlyGame = 22;

	[Desc("Before this world tick, CombatForceSoftCapEarlyGame applies; after, CombatForceSoftCapLateGame (0 = no late cap). If CombatForceSoftCapEarlyGame is 0, ignored. If early cap > 0 and this is 0, the early cap never lifts.")]
	public int CombatForceSoftCapEndWorldTick = 8000;

	[Desc("After CombatForceSoftCapEndWorldTick: max field+queued combat; 0 = unlimited. Example: 70 for a lategame ceiling.")]
	public int CombatForceSoftCapLateGame = 0;

	[Desc("While the early combat cap is active, clamp MaxInfantryQueued to at most this (so queues do not sit full of basics). 0 = use normal effective max only.")]
	public int EarlyPhaseMaxCombatQueued = 6;

	[Desc("Relative pick weight: lab starts research on combat production (infantry/vehicle/air tiers). See ResearchTrackMix* siblings.")]
	public int ResearchTrackMixWeightCombat = 100;

	[Desc("Relative pick weight: building/tower/wall construction track (e.g. mobile base).")]
	public int ResearchTrackMixWeightBuildings = 78;

	[Desc("Relative pick weight: power-station pump tiers.")]
	public int ResearchTrackMixWeightPower = 72;

	[Desc("Relative pick weight: research / alchemy building self-upgrade track.")]
	public int ResearchTrackMixWeightLabSelf = 55;

	[Desc("Relative pick weight: cash-trickler (oil rig) track.")]
	public int ResearchTrackMixWeightCash = 42;

	[Desc("Relative pick weight: radar, repair, and other IProvidesResearchables tracks.")]
	public int ResearchTrackMixWeightOther = 35;

	[Desc("When picking which track to advance, multiply each track weight by a random factor in [100−N, 100+N] / 100 so choices stay mixed; 0 = no jitter.")]
	public int ResearchTrackMixJitterPercent = 18;

	[Desc("0–1. How much effective personality + mood nudges research track weights (combat when aggressive, power/cash/buildings when economy-focused). 0 = yaml weights and jitter only.")]
	public float ResearchTrackDesireBlend = 0.6f;

	[Desc("Do not queue a unit if player cash/oil would fall below this after paying (frees oil for building).")]
	public int MinimumOilReserveForProduction = 0;

	[Desc("Minimum extra ticks after game start before the first attack can launch (inclusive, sync-safe random).")]
	public int FirstAttackDelayTicksMin = 0;

	[Desc("Maximum extra ticks after game start before the first attack (inclusive). If min > max, values are swapped.")]
	public int FirstAttackDelayTicksMax = 1000;

	[Desc("Minimum idle combat units in a single attack wave (randomized each wave, sync-safe).")]
	public int AttackWaveSquadSizeMin = 2;

	[Desc("Maximum idle combat units in a single attack wave.")]
	public int AttackWaveSquadSizeMax = 7;

	[Desc("When this many or more idle combat units are waiting, scale the next wave up toward a fraction of the stack (so production does not pile units that only leave 2–7 at a time). 0 = only use Min/Max.")]
	public int AttackWaveIdleEscalationThreshold = 14;

	[Desc("When idle count is at or above IdleEscalationThreshold, cap the wave at this many units (still ≤ idle count).")]
	public int AttackWaveIdleEscalationSendMax = 20;

	[Desc("If >0, units within this many cells of the bot home (and combat-eligible) count toward attack waves even when not IsIdle—on full-vision / no shroud, AutoTarget often keeps units 'busy' at long range, which caused tiny squads to trickle. 0 = only IsIdle (legacy).")]
	public int AttackWaveGatherMaxDistanceFromHomeCells = 24;

	[Desc("If >0, delay a wave until the pool has at least this many units, only when field combat count is already that high; if most of the army is off-map the pool can stay small—pair with GatherMax distance. 0 = off.")]
	public int AttackWaveMinPoolUnitsToLaunch = 0;

	[Desc("Ticks before the next attack wave when this wave was at minimum size (larger squads get longer pauses, up to the max below).")]
	public int AttackWaveCooldownTicksIfSmallSquad = 400;

	[Desc("Ticks before the next attack wave when this wave was at maximum size.")]
	public int AttackWaveCooldownTicksIfLargeSquad = 1100;

	[Desc("Random +/- ticks mixed into the next cooldown so intervals stay irregular (replays desync if omitted).")]
	public int AttackWaveCooldownJitterTicks = 100;

	[Desc("If true, the next post-wave wait is a random value between CooldownRandom ticks (plus jitter), unrelated to wave size. If false, a larger sent squad yields a longer regroup (lerp from CooldownIfSmallSquad to CooldownIfLargeSquad) plus jitter.")]
	public bool DecoupleSquadSizeFromNextCooldown = false;

	[Desc("When DecoupleSquadSizeFromNextCooldown is true: lower bound for a random post-wave tick delay (inclusive).")]
	public int AttackWaveCooldownRandomTicksMin = 450;

	[Desc("When DecoupleSquadSizeFromNextCooldown is true: upper bound for a random post-wave tick delay (inclusive).")]
	public int AttackWaveCooldownRandomTicksMax = 1000;

	[Desc("When true, pick a non-bot enemy as the attack target when one exists.")]
	public bool PreferNonBotTargets = false;

	[Desc("When true, attack waves and tactical \"raid vs home\" decisions prefer win-state opponents (not NonCombatant, e.g. creep/neutral players) and only use neutral-faction targets if no other enemies exist. Stops the bot from rotating attacks toward map neutrals while human/bot enemies are in play.")]
	public bool DeprioritizeNonCombatantEnemies = true;

	[Desc("If true, periodically react to home threats: send idle field units to defend (reinforce), and if the raid is not winning, Move-order away teams back. Uses Tactical* radii and ratios.")]
	public bool TacticalDefenceEnabled = true;

	[Desc("Min ticks between full tactical threat scans (in addition to damage-triggered urgency).")]
	public int TacticalDefenceIntervalTicks = 100;

	[Desc("Radius in cells around the bot home: hostiles in this ring count as \"pressure on base\" for retreat decisions.")]
	public int TacticalHomeThreatRadiusCells = 20;

	[Desc("Radius in cells around a chosen human/bot enemy home: our combat in this ring counts as \"the push\" (active raid).")]
	public int TacticalPushRadiusCells = 12;

	[Desc("Tactical logic runs only if at least this many hostile field combat units are near the bot's home (see radius).")]
	public int TacticalMinHostileNearHome = 2;

	[Desc("If our combat count in the push ring >= this ratio times the hostile count at home, the bot treats the offensive as strong enough to keep: no Move-recall, only possible reinforce. ~1.1–1.4 typical.")]
	public float TacticalStickRaidOutnumberRatio = 1.15f;

	[Desc("Max units per tick to issue a grouped Move back toward the bot home (non-idle, far from home, when not sticking to the raid).")]
	public int TacticalRecallMaxUnits = 6;

	[Desc("Only consider units for recall if their horizontal distance from home is at least this many cells.")]
	public int TacticalRecallMinDistanceFromHomeCells = 14;

	[Desc("Max idle field units to send AttackMove to hostiles at home in one go (reinforcements).")]
	public int TacticalReinforceMaxSquad = 7;

	[Desc("Only reinforce with idle units that are at least this many cells from home (so we do not re-stomp defenders already in the base).")]
	public int TacticalReinforceMinDistanceFromHomeCells = 6;

	[Desc("Per complete oil node (drill rig + power station) in a claimed sector, how many total tankers to aim for (the power station’s free tanker counts). Min/max inclusive; a random value in range is chosen at game start (sync-safe). Set both to the same value for a fixed count (e.g. 2 or 3).")]
	public int MinTankersPerCompleteOilNode = 2;

	[Desc("See MinTankersPerCompleteOilNode. If > min, a random per-node target is rolled once at game start; if equal, that count is used.")]
	public int MaxTankersPerCompleteOilNode = 3;

	[Desc("If true, each bot instance rolls its personality axes once at game start (sync-safe). If false, the fixed values below are used for all instances.")]
	public bool PersonalityUseRandom = true;

	[Desc("When PersonalityUseRandom is false: fixed aggression 0 = passive/boom, 1 = rusher. Otherwise ignored.")]
	public float PersonalityAggression = 0.5f;

	[Desc("When PersonalityUseRandom is false: 0 = spend on military, 1 = hoard/expand. Otherwise ignored.")]
	public float PersonalityEconomy = 0.5f;

	[Desc("When PersonalityUseRandom is false: 0 = no tower focus, 1 = defences before commiting to attacks. Otherwise ignored.")]
	public float PersonalityDefense = 0.5f;

	[Desc("When rolling aggression randomly: minimum value (0-1 scale).")]
	public float PersonalityRandomAggressionMin = 0.1f;

	[Desc("When rolling aggression randomly: maximum value (0-1 scale). Inclusive of values between min and max.")]
	public float PersonalityRandomAggressionMax = 0.95f;

	[Desc("Random economy weight minimum.")]
	public float PersonalityRandomEconomyMin = 0.1f;

	[Desc("Random economy weight maximum.")]
	public float PersonalityRandomEconomyMax = 0.95f;

	[Desc("Random defence weight minimum.")]
	public float PersonalityRandomDefenseMin = 0f;

	[Desc("Random defence weight maximum.")]
	public float PersonalityRandomDefenseMax = 0.9f;

	[Desc("With high defence, the bot can require up to this many tower-class buildings in its primary base sector before launching attack waves. Scaled by personality defence: required = round(defence * this).")]
	public int DefenseTowersMaxBeforeAllowAttack = 3;

	[Desc("Max tower-queue buildings that must exist before attack waves may launch (applies on top of the defence formula). Stops very defensive personalities from never attacking. 0 = no cap (full DefenceTowersMaxBeforeAllowAttack × defence).")]
	public int MaxTowersRequiredBeforeAttackWaves = 2;

	[Desc("Caps the extra first-attack delay from economy + defence personality (adds to FirstAttackDelayTicks*). 0 = no cap.")]
	public int FirstAttackPersonalityExtraDelayCapTicks = 1100;

	[Desc("Upper bound for the post–attack wave cooldown scale (low aggression and high economy increase it). 0 = no cap. Default keeps conservative AIs from extremely long lulls between waves.")]
	public float PostAttackCooldownScaleMax = 1.04f;

	[Desc("If true, the bot estimates how much oil is left in each of its own drill wells (Current/Maximum per rig) and leans on economy/expansion when the thinnest well is low. Infinite wells act full, so they do not add stress.")]
	public bool WellSupplyAwareness = true;

	[Desc("The bot's thinnest well (lowest remaining fraction) above this value adds no well-supply stress. Must be > Critical for a ramp. ~0.32 = 32% in the worst well is still 'comfortable'.")]
	public float WellSupplyComfortMinFraction = 0.32f;

	[Desc("The thinnest well at or below this remaining fraction = full (1) well-supply stress. Must be < ComfortMin. ~0.08 = 8% in the worst well is critical.")]
	public float WellSupplyCriticalMaxFraction = 0.08f;

	[Desc("At full well-supply stress, add this much to the effective economy axis (0-1) so the AI prioritises income and expansion.")]
	public float WellSupplyTensionToEconomyAxis = 0.28f;

	[Desc("At full well-supply stress, subtract this from effective aggression (0-1) so the AI favours fixing supply over spending on attacks.")]
	public float WellSupplyTensionTrimAggression = 0.1f;

	[Desc("If AdaptiveMood, add this x well-supply stress to economyUrgency each think (0-1) so the adaptive blend also reacts. 0 = do not nudge urge from wells (axis blend still applies).")]
	public float WellSupplyUrgencyNudge = 0.04f;

	[Desc("If true, the bot nudges its effective aggression, economy, and defence from live oil/cash: poor income raises economy focus, flush reserves restore aggression and tech attempts.")]
	public bool AdaptiveMood = true;

	[Desc("Extra aggression (0-1) at game start, tapering to 0 by EarlyGameAggressionPhaseTicks. 0 = disabled.")]
	public float EarlyGameAggressionOffset = 0.08f;

	[Desc("Length of early game for aggression nudge, in world ticks. 0 = no early-game offset. Trailing edge is linear: full bonus at t=0, 0 at this tick. ~25 ticks/s at normal speed — 2000 ≈ 80s.")]
	public int EarlyGameAggressionPhaseTicks = 2000;

	[Desc("Build this many power stations in the primary sector in the opening: first is placed on a drill node, second in-base for the extra free tanker; then the newest may be sold (see below). 0 = disable opening power logic.")]
	public int OpeningPowerStationsWanted = 2;

	[Desc("After at least this many power stations are complete in the primary sector, the bot will sell the newest (highest actor id) so sell-grants stack with a single node; 0 = never auto-sell for opening. Only used during OpeningPowerRushTactic (see).")]
	public int OpeningPowerSellWhenAtLeast = 2;

	[Desc("World-tick end of the early-game \"double power then sell one\" micro (≈25 ticks/s). After this, the bot no longer in-base duplicates power or auto-sells excess here—only normal one-power-per-(drill) node behaviour. 0 = use 8000.")]
	public int OpeningPowerRushTacticEndWorldTick = 0;

	[Desc("Do not queue the research (Researches) building until this many world ticks have passed (~25 ticks/s). Lets economy and power come online first.")]
	public int EarlyGameResearchBuildingDelayTicks = 3500;

	[Desc("Do not queue new research (TryStartResearch) until this many world ticks have passed, unless AdaptiveMood would already hard-block (see also MinimumCashToStartResearch).")]
	public int EarlyGameTryStartResearchDelayTicks = 2500;

	[Desc("From this world tick onward (~25 ticks/s: 10000 ≈ 6.7 min at normal speed), prioritize claimed sectors with oil that has a derrick but no drill, and try to queue a drill on those nodes before the generic base-building pass. 0 = disabled.")]
	public int MidGameDrillExpansionPushStartWorldTick = 10000;

	[Desc("If true and economyUrgency is at/above ExpansionDrillStrappedUrgencyThreshold, the drill-expansion window starts this many world ticks earlier (incentivize income when strapped).")]
	public bool ExpansionDrillPushEarlierWhenStrapped = true;

	[Desc("Urgency threshold (0-1) for ExpansionDrillPushEarlierWhenStrapped.")]
	public float ExpansionDrillStrappedUrgencyThreshold = 0.38f;

	[Desc("When ExpansionDrillPushEarlierWhenStrapped applies, subtract this many world ticks from MidGameDrillExpansionPushStartWorldTick (e.g. 2000 ≈ 80 s earlier at 25 ticks/s).")]
	public int ExpansionDrillPushStrappedTicksEarly = 2000;

	[Desc("Require at least this much cash/oil before starting a research from a lab, on top of other gates.")]
	public int MinimumCashToStartResearch = 4000;

	[Desc("If AdaptiveMood: when economyUrgency is above this, usually skip a research start (1 in 2, sync-safe) so tech does not choke spending.")]
	public float ResearchEconomyUrgencySoftCap = 0.38f;

	[Desc("Cap on personality+adaptive oil reserve for **combat unit** spending only. Economy can still use full effective reserve for buildings. Stops high economy from hoarding 3k+ in bank and blocking units. 0 = no cap (use full effective).")]
	public int MilitaryOilReserveCap = 2200;

	[Desc("If our field combat unit count (attack-capable) is below this, unit production uses only MinimumOilReserveForProduction (hoarding off) to rebuild. 0 = never.")]
	public int RebuildArmyIfFieldCombatBelow = 6;

	[Desc("Do not queue a research (Researches trait) **building** until we have at least this many field combat units, so a destroyed lab is not auto-replaced before the army. 0 = off (favours tech earlier when combined with CombatForceSoftCap*). Use a small value if a faction should delay its lab.")]
	public int MinFieldCombatToQueueResearchStructure = 0;

	public override object Create(ActorInitializer init)
	{
		return new BotAi(init.World, this);
	}
}

public class BotAi : IBotTick, IBotRespondToAttack
{
	private class OilPatchData
	{
		public readonly Actor OilPatch;
		public Actor? Derrick;
		public Actor? Drillrig;
		public Actor? PowerStation;
		public readonly List<Actor> Tankers = new();

		public OilPatchData(Actor oilPatch)
		{
			this.OilPatch = oilPatch;
		}
	}

	private class Sector
	{
		public readonly WPos Origin;
		public readonly List<OilPatchData> OilPatches = new();
		public bool Claimed;
		public Actor? MobileBase;

		public Sector(WPos origin)
		{
			this.Origin = origin;
		}
	}

	private readonly World world;
	private readonly BotAiInfo info;

	private Sector[] sectors = Array.Empty<Sector>();
	private bool initialized;
	private int thinkDelay;
	private int nextUnitProdTick;
	private int nextAttackTick;
	/// <summary>Last production queue type we successfully queued; used to alternate infantry/vehicle so both get built.</summary>
	private string? lastCombatQueueType;
	private int attackTargetCursor;
	/// <summary>Next time <see cref="TryTacticalDefence"/> is allowed to run a full non-urgent scan.</summary>
	private int nextTacticalDefenceWorldTick;
	/// <summary>True when a friendly was damaged: run tactical on the next think without waiting the interval.</summary>
	private bool tacticalUrgent;
	/// <summary>Randomized once at init from <see cref="BotAiInfo.MinTankersPerCompleteOilNode"/> / <see cref="BotAiInfo.MaxTankersPerCompleteOilNode"/>.</summary>
	private int tankersPerCompleteOilNodeTarget;
	/// <summary>True after the opening double-power + optional sell is done, or the rush window has ended—prevents a build/sell loop.</summary>
	private bool openingPowerRushTacticComplete;

	/// <summary>0 = passive / save, 1 = early pressure and high unit output.</summary>
	private float personalityAggression;

	/// <summary>0 = military spend, 1 = oil buffer, expand, and slower combat cadence.</summary>
	private float personalityEconomy;

	/// <summary>0 = minimal static defence, 1 = build towers and delay attacks until some are up.</summary>
	private float personalityDefense;

	private int effectiveInfantryProductionCooldownTicks;
	private int effectiveMaxInfantryQueued;
	private int effectiveMinimumOilReserve;
	private int effectiveAttackSquadSizeMin;
	private int effectiveAttackSquadSizeMax;
	/// <summary>Added to the first-attack window from FirstAttackDelayTicks*.</summary>
	private int effectiveFirstAttackExtraTicks;

	/// <summary>Multiplies post-attack regroup time once jitter is applied.</summary>
	private float effectivePostAttackCooldownScale;

	/// <summary>Attack waves are skipped until the primary sector has at least this many tower-queue buildings (0 = no gate).</summary>
	private int defenseTowersRequiredBeforeAttack;

	/// <summary>Extra oiltankers per full node (small bonus for economy-heavy bots).</summary>
	private int economyExtraTankerPerNode;

	/// <summary>0–1, rises when oil stays below a baseline (focus expand/tanker/reseve), decays when flush.</summary>
	private float economyUrgency;

	/// <summary>0–1, rises with sustained high reserves, falls when poor.</summary>
	private float prosperity;

	/// <summary>0–1, rises when it is a good time to invest in research; falls when the AI is strapped.</summary>
	private float techOpportunity;

	/// <summary>0–1, from lowest remaining well fraction across our <see cref="Drillrig" />s; 0 = comfortable, 1 = critical.</summary>
	private float wellSupplyTension;

	/// <summary>Min reserve from base personality (used for pressure thresholds, not the moving effective value).</summary>
	private int personalityRefMinReserve;

	/// <summary>Defence weight after blending (e.g. tower build throttle, attack gate).</summary>
	private float runtimeDefenseAxis;
	/// <summary>0–1 fight pressure after adaptive mood; same <c>agg</c> as in <see cref="RefreshPersonalityToEffective" /> / <see cref="ApplyAxisToEffectiveValues" />.</summary>
	private float runtimeAggressionAxis;
	/// <summary>0–1 expand/hoard after adaptive mood; same <c>eco</c> as in those methods.</summary>
	private float runtimeEconomyAxis;

	/// <summary>From <see cref="BotAiInfo.CombatProductionQueueTypes"/>; avoids rehashing every production tick.</summary>
	private HashSet<string> combatQueueTypeSet = null!;

	/// <summary>Prerequisite building groups by sector, valid when <see cref="prereqBuildingsBySectorTick"/> equals <see cref="World.WorldTick"/>.</summary>
	private int prereqBuildingsBySectorTick = -1;
	private Dictionary<Sector, IGrouping<Sector, Actor>>? prereqBuildingsBySector;

	/// <summary>Primary sector for <see cref="GetPrimaryBaseSector"/>, same tick gating as <see cref="prereqBuildingsBySectorTick"/>.</summary>
	private int primaryBaseSectorCacheTick = -1;
	private Sector? primaryBaseSector;

	public BotAi(World world, BotAiInfo info)
	{
		this.world = world;
		this.info = info;
	}

	void IBotTick.BotTick(IBot bot)
	{
		if (!this.initialized)
			this.Initialize();

		var t = this.world.WorldTick;
		if (t >= this.nextUnitProdTick)
			this.TryQueueCombatUnits(bot, t);

		if (t >= this.nextAttackTick)
			this.TryAttackSquad(bot, t);

		this.thinkDelay = ++this.thinkDelay % this.info.ThinkDelay;

		if (this.thinkDelay != 0)
			return;

		if (this.info.WellSupplyAwareness)
			this.UpdateWellSupplyTension(bot.Player);
		else
			this.wellSupplyTension = 0f;

		if (this.info.AdaptiveMood)
			this.UpdateEconomicMood(bot.Player);
		// Recompute effective axis every think so early-game aggression decays; mood blends only if AdaptiveMood.
		this.RefreshPersonalityToEffective();

		this.UpdateSectorsClaims(bot);
		this.HandleMobileBases(bot);
		this.HandleMobileDerricks(bot);
		this.AssignOilActors(bot);
		this.ConstructBuildings(bot);
		this.TryOpeningSellExcessPowerStation(bot);
		this.FinishOpeningPowerRushTacticIfExpired();
		this.TryConstructDefensiveTowers(bot);
		this.TryTacticalDefence(bot);
		this.TryStartResearch(bot);
		this.TryQueueExtraTankers(bot);
		this.SellBuildings(bot);
	}

	private void Initialize()
	{
		this.sectors = this.world.Actors.Where(actor => actor.Info.Name == "mpspawn").Select(actor => new Sector(actor.CenterPosition)).ToArray();

		foreach (var oilPatch in this.world.ActorsHavingTrait<OilPatch>())
		{
			var distanceToNearestSector = this.sectors.Min(sector => (sector.Origin - oilPatch.CenterPosition).Length);

			foreach (var sector in this.sectors.Where(sector => (sector.Origin - oilPatch.CenterPosition).Length == distanceToNearestSector))
				sector.OilPatches.Add(new(oilPatch));
		}

		{
			var r = this.world.LocalRandom;
			this.RollAndApplyPersonality();

			var lo = Math.Min(this.info.FirstAttackDelayTicksMin, this.info.FirstAttackDelayTicksMax);
			var hi = Math.Max(this.info.FirstAttackDelayTicksMin, this.info.FirstAttackDelayTicksMax);
			this.nextAttackTick = lo >= hi ? lo : r.Next(lo, hi + 1);
			this.nextAttackTick = Math.Max(0, this.nextAttackTick + this.effectiveFirstAttackExtraTicks);

			var tLo = Math.Min(this.info.MinTankersPerCompleteOilNode, this.info.MaxTankersPerCompleteOilNode);
			var tHi = Math.Max(this.info.MinTankersPerCompleteOilNode, this.info.MaxTankersPerCompleteOilNode);
			this.tankersPerCompleteOilNodeTarget = tLo >= tHi ? tLo : r.Next(tLo, tHi + 1);
			this.tankersPerCompleteOilNodeTarget = Math.Min(tHi, this.tankersPerCompleteOilNodeTarget + this.economyExtraTankerPerNode);
		}

		if (this.info.OpeningPowerStationsWanted < 2)
			this.openingPowerRushTacticComplete = true;

		this.combatQueueTypeSet = new(
			this.info.CombatProductionQueueTypes, StringComparer.Ordinal
		);

		this.initialized = true;
	}

	private void RollAndApplyPersonality()
	{
		if (this.info.PersonalityUseRandom)
		{
			var r = this.world.LocalRandom;
			this.personalityAggression = RollRangeF(r, this.info.PersonalityRandomAggressionMin, this.info.PersonalityRandomAggressionMax);
			this.personalityEconomy = RollRangeF(r, this.info.PersonalityRandomEconomyMin, this.info.PersonalityRandomEconomyMax);
			this.personalityDefense = RollRangeF(r, this.info.PersonalityRandomDefenseMin, this.info.PersonalityRandomDefenseMax);
		}
		else
		{
			this.personalityAggression = BotAi.ClampF(this.info.PersonalityAggression, 0, 1);
			this.personalityEconomy = BotAi.ClampF(this.info.PersonalityEconomy, 0, 1);
			this.personalityDefense = BotAi.ClampF(this.info.PersonalityDefense, 0, 1);
		}

		// Slight early-game pressure (decays), without changing personalityRefMinReserve (keeps pressure thresholds stable).
		var agg0 = BotAi.ClampF(this.personalityAggression + this.GetEarlyGameAggressionBonus(), 0, 1);
		this.ApplyAxisToEffectiveValues(agg0, this.personalityEconomy, this.personalityDefense, setInitOnly: true);

		var ecoB = this.personalityEconomy;
		var aggB = this.personalityAggression;
		this.personalityRefMinReserve = Math.Max(
			500,
			(int)
			(
				this.info.MinimumOilReserveForProduction
					+ 3200f * ecoB * (0.25f + (1 - aggB) * 0.75f)
			)
		);

		this.economyUrgency = 0;
		this.prosperity = 0;
		this.techOpportunity = 0.25f;
	}

	/// <param name="setInitOnly">If true, also sets <see cref="effectiveFirstAttackExtraTicks"/> and <see cref="economyExtraTankerPerNode"/> (game start only).</param>
	private void ApplyAxisToEffectiveValues(float agg, float eco, float def, bool setInitOnly)
	{
		agg = BotAi.ClampF(agg, 0, 1);
		eco = BotAi.ClampF(eco, 0, 1);
		def = BotAi.ClampF(def, 0, 1);
		this.runtimeAggressionAxis = agg;
		this.runtimeEconomyAxis = eco;
		this.runtimeDefenseAxis = def;

		// Tighter = faster re-queue, higher cap (relative to yaml base).
		this.effectiveInfantryProductionCooldownTicks = Math.Max(10, (int)(this.info.InfantryProductionCooldownTicks * (0.72 + (1 - agg) * 0.38) * (0.88 + eco * 0.22)));

		this.effectiveMaxInfantryQueued = Math.Max(2, (int)(this.info.MaxInfantryQueued * (0.62 + agg * 0.48) * (0.9 + (1 - eco) * 0.18)));

		// Hoarding economy + low aggression: keep more oil in reserve; rushers pay less attention. (Capped vs old 3.2k so turtly bots do not block unit spend forever.)
		this.effectiveMinimumOilReserve = (int)
			(this.info.MinimumOilReserveForProduction
				+ 2500f * eco * (0.28f + (1 - agg) * 0.62f));

		this.effectiveAttackSquadSizeMin = Math.Max(1, (int)(this.info.AttackWaveSquadSizeMin * (0.45f + 0.75f * agg)));
		this.effectiveAttackSquadSizeMax = Math.Max(
			this.effectiveAttackSquadSizeMin,
			(int)(this.info.AttackWaveSquadSizeMax * (0.45f + 0.6f * agg))
		);

		if (setInitOnly)
		{
			// Delay first offensive until production/economy can kick in, unless aggressive. (Milder than legacy so conservative personalities still show pressure.)
			var extra = (int)
				(1200f * eco * (1.15f - 0.88f * agg) + 360f * (1 - agg) + 400f * def);
			if (this.info.FirstAttackPersonalityExtraDelayCapTicks > 0)
				extra = Math.Min(extra, this.info.FirstAttackPersonalityExtraDelayCapTicks);
			this.effectiveFirstAttackExtraTicks = extra;
			// Economy-focused: sometimes +1 tanker per full node (still clamped to yaml max); roll once at start.
			var r0 = this.world.LocalRandom;
			this.economyExtraTankerPerNode = eco > 0.72f && r0.Next(3) == 0 ? 1 : 0;
		}

		// Slightly shorter regroup for aggressive, longer for turtly economy.
		this.effectivePostAttackCooldownScale = 0.64f + (1 - agg) * 0.5f * (0.9f + eco * 0.12f);
		if (this.info.PostAttackCooldownScaleMax > 0f
			&& this.effectivePostAttackCooldownScale > this.info.PostAttackCooldownScaleMax)
		{
			this.effectivePostAttackCooldownScale = this.info.PostAttackCooldownScaleMax;
		}

		var cap = Math.Max(0, this.info.DefenseTowersMaxBeforeAllowAttack);
		if (cap == 0)
			this.defenseTowersRequiredBeforeAttack = 0;
		else
		{
			// When strapped, require fewer (or no) static defences before attacking; avoid double punishment.
			var t = this.economyUrgency;
			var dGate = def * (1f - 0.72f * t);
			var n = (int)MathF.Floor(dGate * cap + 0.0001f);
			if (this.info.MaxTowersRequiredBeforeAttackWaves > 0)
				n = Math.Min(n, this.info.MaxTowersRequiredBeforeAttackWaves);
			this.defenseTowersRequiredBeforeAttack = n;
		}
	}

	private void UpdateEconomicMood(Player player)
	{
		var pr = player.PlayerActor.Trait<PlayerResources>().GetCashAndResources();
		var refB = this.personalityRefMinReserve;
		// Tight: push economy urgency up so we deprioritise spendy behaviour.
		if (pr < refB * 0.5f)
			this.economyUrgency = Math.Min(1, this.economyUrgency + 0.055f);
		else if (pr < refB * 0.8f)
			this.economyUrgency = Math.Min(1, this.economyUrgency + 0.022f);
		else if (pr > refB * 2.1f + 2400f)
			this.economyUrgency = Math.Max(0, this.economyUrgency - 0.032f);
		else
			this.economyUrgency = Math.Max(0, this.economyUrgency - 0.014f);

		if (pr > refB * 2.5f + 3600f)
			this.prosperity = Math.Min(1, this.prosperity + 0.045f);
		else if (pr < refB * 0.9f)
			this.prosperity = Math.Max(0, this.prosperity - 0.055f);
		else
			this.prosperity = Math.Max(0, this.prosperity - 0.012f);

		if (this.prosperity > 0.45f && this.economyUrgency < 0.28f)
			this.techOpportunity = Math.Min(1, this.techOpportunity + 0.04f);
		else if (this.economyUrgency > 0.52f)
			this.techOpportunity = Math.Max(0, this.techOpportunity - 0.09f);
		else
			this.techOpportunity = Math.Max(0, this.techOpportunity - 0.02f);

		if (this.info.AdaptiveMood
			&& this.info.WellSupplyAwareness
			&& this.info.WellSupplyUrgencyNudge > 0f
			&& this.wellSupplyTension > 0.001f)
		{
			this.economyUrgency = Math.Min(1, this.economyUrgency + this.info.WellSupplyUrgencyNudge * this.wellSupplyTension);
		}
	}

	/// <summary>Lowest remaining oil fraction across our drill rigs (each well is independent; worst well drives stress). Infinite wells stay at full fraction and do not trigger.</summary>
	private void UpdateWellSupplyTension(Player player)
	{
		this.wellSupplyTension = 0f;
		if (!this.info.WellSupplyAwareness)
			return;

		var worst = 1f;
		var any = false;
		foreach (var pair in this.world.ActorsWithTrait<Drillrig>())
		{
			if (pair.Actor.Owner != player || !pair.Actor.IsInWorld || pair.Actor.IsDead)
				continue;
			if (pair.Trait.IsTraitDisabled)
				continue;
			any = true;
			var max = Math.Max(1, pair.Trait.Maximum);
			var cur = Math.Min(Math.Max(0, pair.Trait.Current), max);
			var f = cur / (float)max;
			if (f < worst)
				worst = f;
		}

		if (!any)
			return;

		var loF = Math.Min(this.info.WellSupplyComfortMinFraction, this.info.WellSupplyCriticalMaxFraction);
		var hiF = Math.Max(this.info.WellSupplyComfortMinFraction, this.info.WellSupplyCriticalMaxFraction);
		if (hiF <= loF)
			hiF = loF + 0.0001f;
		if (worst >= hiF)
			this.wellSupplyTension = 0f;
		else if (worst <= loF)
			this.wellSupplyTension = 1f;
		else
			this.wellSupplyTension = 1f - (worst - loF) / (hiF - loF);
	}

	/// <summary>Blend static personality (and mood if enabled), apply early-game aggression, refresh derived timings.</summary>
	private void RefreshPersonalityToEffective()
	{
		float aggD, ecoD, defD;
		if (this.info.AdaptiveMood)
		{
			var u = this.economyUrgency;
			var s = this.prosperity;

			// Strapped: calmer, more "eco" behaviour; when flush, reward aggression a bit, trim passive eco bias.
			aggD = this.personalityAggression * (1f - 0.52f * u) + 0.32f * s * (1f - u);
			ecoD = this.personalityEconomy + 0.48f * u - 0.12f * s;
			defD = this.personalityDefense * (1f - 0.4f * u);
		}
		else
		{
			aggD = this.personalityAggression;
			ecoD = this.personalityEconomy;
			defD = this.personalityDefense;
		}

		// Slight early offset towards aggression, tapering off (applies with or without adaptive mood).
		aggD += this.GetEarlyGameAggressionBonus();
		aggD = BotAi.ClampF(aggD, 0, 1);
		ecoD = BotAi.ClampF(ecoD, 0, 1);
		defD = BotAi.ClampF(defD, 0, 1);

		if (this.info.WellSupplyAwareness)
		{
			var w = this.wellSupplyTension;
			if (w > 0.001f)
			{
				ecoD = BotAi.ClampF(ecoD + w * this.info.WellSupplyTensionToEconomyAxis, 0, 1);
				if (this.info.WellSupplyTensionTrimAggression > 0f)
					aggD = BotAi.ClampF(aggD - w * this.info.WellSupplyTensionTrimAggression, 0, 1);
			}
		}

		this.ApplyAxisToEffectiveValues(aggD, ecoD, defD, setInitOnly: false);
	}

	/// <summary>Linearly falls from 1 at t=0 to 0 at <see cref="BotAiInfo.EarlyGameAggressionPhaseTicks"/>; scaled by <see cref="BotAiInfo.EarlyGameAggressionOffset"/>.</summary>
	private float GetEarlyGameAggressionBonus()
	{
		var end = this.info.EarlyGameAggressionPhaseTicks;
		if (end <= 0)
			return 0f;

		var off = this.info.EarlyGameAggressionOffset;
		if (off <= 0f)
			return 0f;

		var t = this.world.WorldTick;
		if (t >= end)
			return 0f;

		var phase = 1f - t / (float)end;

		return off * phase;
	}

	private static float RollRangeF(MersenneTwister r, float min, float max)
	{
		if (max < min)
			(min, max) = (max, min);

		if (min == max)
			return min;

		// 1000 steps between bounds (sync-only RNG).
		return min + (max - min) * r.Next(0, 1001) / 1000f;
	}

	private static float ClampF(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

	/// <summary>Same grouping as the original <c>GroupBy(…).ToDictionary</c>, computed at most once per <see cref="World.WorldTick"/> per bot.</summary>
	private Dictionary<Sector, IGrouping<Sector, Actor>> GetPrerequisiteBuildingsBySector(Player player)
	{
		if (this.prereqBuildingsBySectorTick == this.world.WorldTick && this.prereqBuildingsBySector != null)
			return this.prereqBuildingsBySector;

		this.prereqBuildingsBySector = this.world.ActorsHavingTrait<ProvidesPrerequisite>()
			.Where(actor => actor.Owner == player)
			.GroupBy(actor => this.sectors.MinBy(sector => (sector.Origin - actor.CenterPosition).Length))
			.ToDictionary(e => e.Key, e => e);
		this.prereqBuildingsBySectorTick = this.world.WorldTick;
		return this.prereqBuildingsBySector;
	}

	private void UpdateSectorsClaims(IBot bot)
	{
		var buildingsInSectors = this.GetPrerequisiteBuildingsBySector(bot.Player);

		foreach (var sector in this.sectors)
		{
			sector.Claimed = buildingsInSectors.ContainsKey(sector);

			foreach (var oilPatch in sector.OilPatches.ToArray())
			{
				if (oilPatch.OilPatch is { IsDead: true })
				{
					sector.OilPatches.Remove(oilPatch);

					continue;
				}

				if (!sector.Claimed || oilPatch.Drillrig is { IsDead: true })
					oilPatch.Drillrig = null;

				if (!sector.Claimed || oilPatch.PowerStation is { IsDead: true })
					oilPatch.PowerStation = null;

				oilPatch.Tankers.RemoveAll(tanker => !sector.Claimed || tanker.IsDead);
			}
		}
	}

	private void SellBuildings(IBot bot)
	{
		var sellables = this.world.ActorsWithTrait<DeconstructSellable>()
			.Where(e => e.Actor.Owner == bot.Player && !e.Trait.IsTraitDisabled)
			.Select(e => e.Actor);

		foreach (var sellable in sellables)
		{
			if (!this.sectors.MinBy(sector => (sector.Origin - sellable.CenterPosition).Length).Claimed)
				bot.QueueOrder(new(SellOrderGenerator.Id, sellable, false));

			if (sellable.TraitOrDefault<Drillrig>() != null && !this.sectors.Any(s => s.OilPatches.Any(oilPatch => sellable == oilPatch.Drillrig)))
				bot.QueueOrder(new(SellOrderGenerator.Id, sellable, false));
		}
	}

	private void HandleMobileBases(IBot bot)
	{
		var mobileBases = this.world.ActorsHavingTrait<BaseBuilding>().Where(actor => actor.TraitsImplementing<Mobile>().Any() && actor.Owner == bot.Player);

		foreach (var sector in this.sectors)
		{
			if (sector.MobileBase is { IsDead: true })
				sector.MobileBase = null;
		}

		foreach (var mobileBase in mobileBases)
		{
			if (this.sectors.Any(sector => mobileBase == sector.MobileBase))
				continue;

			var sector = this.sectors.Where(s => s is { Claimed: false, MobileBase: null }).MinByOrDefault(s => (s.Origin - mobileBase.CenterPosition).Length);

			if (sector == null)
				break;

			sector.MobileBase = mobileBase;
		}

		foreach (var sector in this.sectors)
		{
			if (sector.MobileBase is not { IsIdle: true })
				continue;

			sector.MobileBase.QueueActivity(
				sector.Origin == sector.MobileBase.CenterPosition
					? sector.MobileBase.Trait<Transforms>().GetTransformActivity()
					: new Move(sector.MobileBase, this.world.Map.CellContaining(sector.Origin))
			);
		}
	}

	private void HandleMobileDerricks(IBot bot)
	{
		var derricks = this.world.ActorsHavingTrait<DeploysOnActor>().Where(actor => actor.Owner == bot.Player);
		var primary = this.GetPrimaryBaseSector(bot.Player);

		foreach (var oilPatch in this.sectors.SelectMany(sector => sector.OilPatches.Where(op => !sector.Claimed || op.Derrick is { IsDead: true })))
			oilPatch.Derrick = null;

		foreach (var derrick in derricks)
		{
			if (this.sectors.Any(sector => sector.OilPatches.Any(oilPatch => derrick == oilPatch.Derrick)))
				continue;

			var oilPatch = this.sectors
				.Where(sector => sector.Claimed)
				.OrderBy(sector => primary != null && sector == primary ? 1 : 0)
				.ThenBy(sector => (sector.Origin - derrick.CenterPosition).Length)
				.Select(
					sector => sector.OilPatches
						.Where(o => o.Derrick == null && o.Drillrig == null)
						.MinByOrDefault(o => (o.OilPatch.CenterPosition - derrick.CenterPosition).Length)
				)
				.FirstOrDefault(o => o != null);

			if (oilPatch == null)
				continue;

			oilPatch.Derrick = derrick;
		}

		foreach (var oilPatch in this.sectors.SelectMany(sector => sector.OilPatches))
		{
			if (oilPatch.Derrick is { IsIdle: true } && oilPatch.OilPatch.CenterPosition != oilPatch.Derrick.CenterPosition)
				oilPatch.Derrick.QueueActivity(new Move(oilPatch.Derrick, this.world.Map.CellContaining(oilPatch.OilPatch.CenterPosition)));
		}
	}

	private void AssignOilActors(IBot bot)
	{
		var drillrigs = this.world.ActorsHavingTrait<Drillrig>().Where(actor => actor.Owner == bot.Player).ToArray();
		var powerStations = this.world.ActorsHavingTrait<PowerStation>().Where(actor => actor.Owner == bot.Player).ToList();

		foreach (var sector in this.sectors.Where(sector => sector.Claimed))
		{
			foreach (var oilPatch in sector.OilPatches)
			{
				oilPatch.Drillrig ??= drillrigs.FirstOrDefault(drillrig => (drillrig.CenterPosition - oilPatch.OilPatch.CenterPosition).Length < WDist.FromCells(1).Length);

				if (oilPatch.PowerStation != null)
					powerStations.Remove(oilPatch.PowerStation);
			}
		}
		
		foreach (var powerStation in powerStations)
		{
			var oilPatch = this.sectors.Where(sector => sector.Claimed)
				.MinByOrDefault(sector => (sector.Origin - powerStation.CenterPosition).Length)
				?.OilPatches.Where(oilPatch => oilPatch is { Drillrig: { }, PowerStation: null })
				.MinByOrDefault(oilPatch => (oilPatch.OilPatch.CenterPosition - powerStation.CenterPosition).Length);

			if (oilPatch != null)
				oilPatch.PowerStation = powerStation;
		}

		// One pass over tankers, then attach by drill (was O(tankers × oil patches) per frame).
		var byDrill = new Dictionary<Actor, List<Actor>>();
		foreach (var t in this.world.ActorsWithTrait<Tanker>())
		{
			if (t.Actor.Owner != bot.Player || !t.Actor.IsInWorld || t.Actor.IsDead)
				continue;
			if (t.Trait.PreferedDrillrig is not { } drill)
				continue;
			if (!byDrill.TryGetValue(drill, out var list))
			{
				list = new();
				byDrill[drill] = list;
			}

			list.Add(t.Actor);
		}

		foreach (var sector in this.sectors.Where(s => s.Claimed))
		{
			foreach (var oilPatch in sector.OilPatches)
			{
				oilPatch.Tankers.Clear();

				if (oilPatch.Drillrig == null)
					continue;
				if (byDrill.TryGetValue(oilPatch.Drillrig, out var forDrill))
					oilPatch.Tankers.AddRange(forDrill);
			}
		}
	}

	private int GetDrillExpansionPushEffectiveStartWorldTick()
	{
		if (this.info.MidGameDrillExpansionPushStartWorldTick <= 0)
			return int.MaxValue;

		var t = this.info.MidGameDrillExpansionPushStartWorldTick;
		if (this.info.ExpansionDrillPushEarlierWhenStrapped
			&& this.economyUrgency >= this.info.ExpansionDrillStrappedUrgencyThreshold)
		{
			t = Math.Max(0, t - this.info.ExpansionDrillPushStrappedTicksEarly);
		}

		return t;
	}

	/// <summary>Queue a <see cref="Drillrig" /> on oil that already has a derrick but no rig (income expansion).</summary>
	private bool TryBuildDrillOnDerrickReadiedOil(
		IBot bot,
		SelfConstructingProductionQueue productionQueue,
		ActorInfo[] buildables,
		Sector sector,
		WPos homeWPos
	)
	{
		if (productionQueue.IsConstructing())
			return false;

		var needDrill = sector.OilPatches
			.Where(o => o is { Derrick: { } } && o.Drillrig == null)
			.OrderBy(o => (o.OilPatch.CenterPosition - homeWPos).LengthSquared)
			.ToList();

		if (needDrill.Count == 0)
			return false;

		var drillB = buildables
			.FirstOrDefault(
				b => b.HasTraitInfo<DrillrigInfo>() && b.TraitInfoOrDefault<BuildingInfo>() != null
					&& this.BelowOrEqualBuildLimit(bot.Player, b)
			);

		if (drillB == null)
			return false;

		foreach (var op in needDrill)
		{
			if (this.Build(bot, sector, drillB, productionQueue, op.OilPatch.CenterPosition))
				return true;
		}

		return false;
	}

	private void ConstructBuildings(IBot bot)
	{
		var productionQueue = this.world.ActorsWithTrait<SelfConstructingProductionQueue>()
			.FirstOrDefault(e => e.Actor.Owner == bot.Player && e.Trait.Info.Type == "building")
			.Trait;

		if (productionQueue == null)
			return;

		var buildables = productionQueue.BuildableItems().ToArray();

		var primary = this.GetPrimaryBaseSector(bot.Player);
		var homeWPos = this.world.Map.CenterOfCell(bot.Player.HomeLocation);
		var worldTick = this.world.WorldTick;
		var effectiveDrillPushStart = this.GetDrillExpansionPushEffectiveStartWorldTick();
		var expansionDrillPushActive = this.info.MidGameDrillExpansionPushStartWorldTick > 0
			&& worldTick >= effectiveDrillPushStart;

		var claimed = this.sectors.Where(sector => sector.Claimed);
		var sectorOrder = expansionDrillPushActive
			? claimed
				.OrderBy(sector => sector.OilPatches.Any(
					o => o is { Derrick: { } } && o.Drillrig == null
				)
					? 0
					: 1
				)
				.ThenBy(sector => primary != null && sector == primary ? 1 : 0)
				.ThenBy(sector => (sector.Origin - homeWPos).LengthSquared)
			: claimed
				.OrderBy(sector => primary != null && sector == primary ? 1 : 0)
				.ThenBy(sector => (sector.Origin - homeWPos).LengthSquared);

		var buildingsInSectors = this.GetPrerequisiteBuildingsBySector(bot.Player);

		foreach (var sector in sectorOrder)
		{
			if (!buildingsInSectors.TryGetValue(sector, out var buildingsInSector))
				continue;

			// Second opening power: duplicate PowerStation in primary (first is placed on a drill+oil; same actor type again in-base for another free tanker).
			if (this.info.OpeningPowerStationsWanted > 1
				&& primary != null
				&& sector == primary
				&& this.TryBuildSecondOpeningPowerStation(bot, productionQueue, buildables, sector))
			{
				break;
			}

			if (expansionDrillPushActive
				&& this.TryBuildDrillOnDerrickReadiedOil(bot, productionQueue, buildables, sector, homeWPos))
			{
				break;
			}

			var build = buildables.Where(buildable => buildable.HasTraitInfo<BaseBuildingInfo>())
				.FirstOrDefault(buildable => buildingsInSector.All(building => building.Info.Name != buildable.Name));

			if (this.Build(bot, sector, build, productionQueue))
				continue;

			if (productionQueue.IsConstructing())
				continue;

			var oilPatch = sector.OilPatches.Where(oilPatch => oilPatch is { Drillrig: { }, PowerStation: null })
				.MinByOrDefault(oilPatch => (oilPatch.OilPatch.CenterPosition - sector.Origin).Length);

			if (oilPatch != null)
			{
				build = buildables.Where(buildable => buildable.HasTraitInfo<PowerStationInfo>())
					.FirstOrDefault(buildable => buildingsInSector.All(building => building.Info.Name != buildable.Name));

				if (this.Build(bot, sector, build, productionQueue, oilPatch.OilPatch.CenterPosition))
					break;
			}

			if (worldTick >= this.info.EarlyGameResearchBuildingDelayTicks
				&& (this.info.MinFieldCombatToQueueResearchStructure <= 0
					|| this.CountOurFieldCombatUnits(bot.Player) >= this.info.MinFieldCombatToQueueResearchStructure))
			{
				build = buildables.Where(buildable => buildable.HasTraitInfo<ResearchesInfo>())
					.FirstOrDefault(buildable => buildingsInSector.All(building => building.Info.Name != buildable.Name));

				if (this.Build(bot, sector, build, productionQueue))
					break;
			}

			build = buildables.Where(buildable => buildable.HasTraitInfo<AdvancedProductionInfo>())
				.FirstOrDefault(buildable => buildingsInSector.All(building => building.Info.Name != buildable.Name));

			if (this.Build(bot, sector, build, productionQueue))
				break;

			build = buildables.Where(buildable => buildable.HasTraitInfo<CashTricklerInfo>())
				.FirstOrDefault(buildable => buildingsInSector.All(building => building.Info.Name != buildable.Name));

			if (this.Build(bot, sector, build, productionQueue))
				break;

			build = buildables.Where(buildable => buildable.HasTraitInfo<AdvancedAirstrikePowerInfo>())
				.FirstOrDefault(buildable => buildingsInSector.All(building => building.Info.Name != buildable.Name));

			if (this.Build(bot, sector, build, productionQueue))
				break;

			build = buildables.Where(buildable => buildable.HasTraitInfo<AdvancedProductionInfo>())
				.FirstOrDefault(
					buildable =>
					{
						var existing = buildingsInSector.Where(building => building.Info.Name == buildable.Name).ToArray();

						if (existing.Length != 1)
							return false;

						var researchable = existing.First().TraitOrDefault<Researchable>();

						return researchable == null || researchable.Level == researchable.MaxLevel;
					}
				);

			if (this.Build(bot, sector, build, productionQueue))
				break;

			build = buildables.Where(buildable => buildable.HasTraitInfo<RepairsVehiclesInfo>())
				.FirstOrDefault(buildable => buildingsInSector.All(building => building.Info.Name != buildable.Name));

			if (this.Build(bot, sector, build, productionQueue))
				break;
		}
	}

	/// <summary>Build a second <see cref="PowerStation" /> in the primary sector (first is the node-linked one) for another <see cref="FreeActor" /> oil tanker.</summary>
	private bool TryBuildSecondOpeningPowerStation(
		IBot bot, SelfConstructingProductionQueue productionQueue, ActorInfo[] buildables, Sector sector
	)
	{
		if (this.openingPowerRushTacticComplete)
			return false;
		if (this.world.WorldTick >= this.GetOpeningPowerRushTacticEndWorldTick())
			return false;
		if (productionQueue.IsConstructing())
			return false;
		if (this.info.OpeningPowerStationsWanted < 2)
			return false;
		if (this.CountPowerStationsInSector(sector, bot.Player) != 1)
			return false;
		var b = buildables.FirstOrDefault(bi => bi.HasTraitInfo<PowerStationInfo>());
		if (b == null)
			return false;
		return this.Build(bot, sector, b, productionQueue, null);
	}

	private int CountPowerStationsInSector(Sector sector, Player player) =>
		this.world.ActorsWithTrait<PowerStation>().Count(
			e =>
			{
				if (e.Actor.Owner != player || !e.Actor.IsInWorld || e.Actor.IsDead)
					return false;
				return this.sectors.MinBy(s => (s.Origin - e.Actor.CenterPosition).Length) == sector;
			}
		);

	/// <summary>When the opening brought more power stations than drill heads in the primary, sell the newest for refund (extra tanker already spawned).</summary>
	private void TryOpeningSellExcessPowerStation(IBot bot)
	{
		if (this.openingPowerRushTacticComplete)
			return;
		if (this.world.WorldTick >= this.GetOpeningPowerRushTacticEndWorldTick())
			return;
		if (this.info.OpeningPowerSellWhenAtLeast < 2)
			return;
		var primary = this.GetPrimaryBaseSector(bot.Player);
		if (primary == null)
			return;
		var pws = this.world.ActorsWithTrait<PowerStation>()
			.Where(e => e.Actor.Owner == bot.Player && e.Actor.IsInWorld && !e.Actor.IsDead)
			.Where(
				e => this.sectors.MinBy(s => (s.Origin - e.Actor.CenterPosition).Length) == primary
			)
			.Select(e => e.Actor)
			.OrderBy(a => a.ActorID)
			.ToList();
		if (pws.Count < this.info.OpeningPowerSellWhenAtLeast)
			return;
		var drillSlots = primary.OilPatches.Count(o => o.Drillrig != null);
		if (pws.Count <= drillSlots)
			return;
		var toSell = pws[^1];
		if (toSell.TraitOrDefault<Health>() is { } h && h.HP < h.MaxHP)
			return;
		if (toSell.TraitOrDefault<DeconstructSellable>() is not { } sellable || sellable.IsTraitDisabled)
			return;
		bot.QueueOrder(new(SellOrderGenerator.Id, toSell, false));
		this.openingPowerRushTacticComplete = true;
	}

	/// <summary>Default 8000 when <see cref="BotAiInfo.OpeningPowerRushTacticEndWorldTick"/> is 0.</summary>
	private int GetOpeningPowerRushTacticEndWorldTick()
	{
		var e = this.info.OpeningPowerRushTacticEndWorldTick;
		return e > 0 ? e : 8000;
	}

	/// <summary>Ends the one-shot opening power micro after the time window, so mid-game never replays it.</summary>
	private void FinishOpeningPowerRushTacticIfExpired()
	{
		if (this.openingPowerRushTacticComplete)
			return;
		if (this.world.WorldTick >= this.GetOpeningPowerRushTacticEndWorldTick())
			this.openingPowerRushTacticComplete = true;
	}

	/// <summary>Places tower-queue buildings; <see cref="runtimeDefenseAxis"/> includes adaptive mood (drops when the AI is strapped).</summary>
	private void TryConstructDefensiveTowers(IBot bot)
	{
		if (this.runtimeDefenseAxis < 0.07f)
			return;

		var r = this.world.LocalRandom;
		// Throttle: defencive AIs do not build a tower every think tick.
		if (r.Next(100) > (int)(15 + this.runtimeDefenseAxis * 70f))
			return;

		var q = this.world.ActorsWithTrait<SelfConstructingProductionQueue>()
			.FirstOrDefault(e => e.Actor.Owner == bot.Player && e.Trait.Info.Type == "tower");
		if (q.Actor is null)
			return;

		var towerQ = q.Trait;

		if (towerQ.IsConstructing())
			return;

		var buildables = towerQ.BuildableItems().Where(BotAi.IsBuildableFromTowerQueue).ToArray();
		if (buildables.Length == 0)
			return;

		var primary = this.GetPrimaryBaseSector(bot.Player);
		var homeWPos = this.world.Map.CenterOfCell(bot.Player.HomeLocation);
		var sectorOrder = this.sectors
			.Where(sector => sector.Claimed)
			.OrderBy(sector => primary != null && sector == primary ? 0 : 1)
			.ThenBy(sector => (sector.Origin - homeWPos).LengthSquared);

		foreach (var sector in sectorOrder)
		{
			foreach (var b in buildables.Shuffle(r))
			{
				if (!this.BelowOrEqualBuildLimit(bot.Player, b))
					continue;

				if (this.Build(bot, sector, b, towerQ))
					return;
			}
		}
	}

	private static bool IsBuildableFromTowerQueue(ActorInfo info)
	{
		var t = info.TraitInfoOrDefault<TechLevelBuildableInfo>();
		if (t == null)
			return false;

		return t.Queue.Contains("tower");
	}

	/// <summary>Tower-queue defences in any <see cref="Sector.Claimed"/> sector (primary may have no build space while an outpost has towers).</summary>
	private int CountTowerBuildingsInClaimedSectors(Player player)
	{
		var n = 0;
		foreach (var a in this.world.Actors)
		{
			if (a.Owner != player || !a.IsInWorld || a.IsDead)
				continue;
			if (!IsBuildableFromTowerQueue(a.Info))
				continue;
			var sector = this.sectors.MinBy(s => (s.Origin - a.CenterPosition).Length);
			if (sector.Claimed)
				n++;
		}
		return n;
	}

	private bool BelowOrEqualBuildLimit(Player player, ActorInfo info)
	{
		var b = info.TraitInfoOrDefault<BuildableInfo>();
		if (b is not { BuildLimit: > 0 and var limit })
			return true;

		var n = this.world.Actors.Count(
			x => x.Owner == player
				&& !x.IsDead
				&& x.IsInWorld
				&& x.Info.Name == info.Name
		);

		return n < limit;
	}

	private bool Build(IBot bot, Sector sector, ActorInfo? buildable, ProductionQueue queue, WPos? target = null)
	{
		if (buildable == null)
			return false;

		var buildingInfo = buildable.TraitInfoOrDefault<BuildingInfo>();

		if (buildingInfo == null)
			return true;

		// Only queue construction if the player can pay (gathered oil / cash); the engine would reject otherwise.
		var pr = bot.Player.PlayerActor.Trait<PlayerResources>();
		var buildCost = buildable.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;
		if (pr.GetCashAndResources() < buildCost)
			return false;

		var center = this.world.Map.CellContaining(sector.Origin);
		var buildTarget = target == null ? center : this.world.Map.CellContaining(target.Value);
		var minRange = 0;
		var maxRange = this.sectors.Where(other => other != sector).Min(other => (other.Origin - sector.Origin).Length) / 1024 / 2;

		var cells = this.world.Map.FindTilesInAnnulus(center, minRange, maxRange);

		cells = center != buildTarget ? cells.OrderBy(c => (c - buildTarget).LengthSquared) : cells.Shuffle(this.world.LocalRandom);

		foreach (var cell in cells)
		{
			if (!this.world.CanPlaceBuilding(cell, buildable, buildingInfo, null))
				continue;

			if (!buildingInfo.IsCloseEnoughToBase(this.world, bot.Player, buildable, cell))
				continue;

			bot.QueueOrder(
				new("PlaceBuilding", bot.Player.PlayerActor, Target.FromCell(this.world, cell), false)
				{
					TargetString = buildable.Name, ExtraData = queue.Actor.ActorID, SuppressVisualFeedback = true
				}
			);

			break;
		}

		return true;
	}

	/// <summary>Sector with the most friendly prerequisite buildings; used to prefer "natural" expansions first.</summary>
	private Sector? GetPrimaryBaseSector(Player player)
	{
		if (this.primaryBaseSectorCacheTick == this.world.WorldTick)
			return this.primaryBaseSector;

		var bestCount = 0;
		Sector? best = null;

		foreach (var sector in this.sectors)
		{
			var n = 0;

			foreach (var a in this.world.ActorsHavingTrait<ProvidesPrerequisite>())
			{
				if (a.Owner != player)
					continue;

				if (this.sectors.MinBy(s => (s.Origin - a.CenterPosition).Length) == sector)
					n++;
			}

			if (n > bestCount)
			{
				bestCount = n;
				best = sector;
			}
		}

		this.primaryBaseSector = bestCount > 0 ? best : null;
		this.primaryBaseSectorCacheTick = this.world.WorldTick;
		return this.primaryBaseSector;
	}

	private void TryStartResearch(IBot bot)
	{
		var pr0 = bot.Player.PlayerActor.Trait<PlayerResources>();
		if (this.info.EarlyGameTryStartResearchDelayTicks > 0
			&& this.world.WorldTick < this.info.EarlyGameTryStartResearchDelayTicks)
		{
			return;
		}
		if (this.info.MinimumCashToStartResearch > 0
			&& pr0.GetCashAndResources() < this.info.MinimumCashToStartResearch)
		{
			return;
		}
		// When the AI is strapped, research is triaged more often; when prosperous, techOpportunity is high and we start research reliably.
		if (this.info.AdaptiveMood
			&& this.techOpportunity < 0.2f
			&& this.economyUrgency > 0.5f
			&& this.world.LocalRandom.Next(4) != 0)
		{
			return;
		}
		// Soften lab micro when income is under pressure, so the queue does not starve other spending.
		if (this.info.AdaptiveMood
			&& this.economyUrgency > this.info.ResearchEconomyUrgencySoftCap
			&& this.world.LocalRandom.Next(2) == 0)
		{
			return;
		}

		var researchLabs = this.world.ActorsWithTrait<Researches>()
			.Where(e => e.Actor.Owner == bot.Player && !e.Trait.IsTraitDisabled)
			.OrderBy(e => e.Actor.ActorID)
			.ToArray();

		foreach (var lab in researchLabs)
		{
			if (lab.Trait.GetState() != ResarchState.Available)
				continue;

			var target = this.PickResearchTarget(lab.Actor, bot);

			if (target == null)
				continue;

			bot.QueueOrder(new(ResearchOrderTargeter.Id, lab.Actor, Target.FromActor(target), false));

			return;
		}
	}

	/// <summary>
	/// Chooses a <see cref="Researchable" /> for the <see cref="Researches" /> lab to work on. OpenKrush model: the
	/// lab does not “tech” in an abstract tree — each target building has a <see cref="Researchable" /> plus one or
	/// more <see cref="IProvidesResearchables" /> (e.g. <see cref="ResearchableProduction" />, <see cref="AdvancedProduction" />,
	/// <see cref="PowerStation" />) that publish tech steps; unit tiers come from <see cref="TechLevelBuildableInfo" />
	/// on each trainable. Picks a <see cref="ResearchTargetKindRank" /> track using weighted randomness (see
	/// <see cref="BotAiInfo.ResearchTrackMixWeightCombat" />, <see cref="BotAiInfo.ResearchTrackDesireBlend" />, jitter)
	/// so combat, economy, and base tech interleave; desire nudges weights when the bot is fight- vs eco-oriented.
	/// </summary>
	private Actor? PickResearchTarget(Actor researchActor, IBot bot)
	{
		var candidates = this.world.ActorsWithTrait<Researchable>()
			.Select(e => e.Actor)
			.Where(a => a.Owner == bot.Player && a.IsInWorld && !a.IsDead)
			.Where(
				a =>
				{
					var r = a.TraitOrDefault<Researchable>();

					return r is { IsTraitDisabled: false } && ResearchUtils.GetAction(researchActor, a) == ResearchAction.Start;
				}
			)
			.ToList();

		if (candidates.Count == 0)
			return null;

		if (candidates.Count == 1)
			return candidates[0];

		var r = this.world.LocalRandom;
		var byTrack = candidates
			.GroupBy(ResearchTargetKindRank)
			.ToDictionary(g => g.Key, g => g.OrderByDescending(RemainingResearchSteps).ThenBy(a => a.ActorID).ToList());

		var tracks = byTrack.Keys.OrderBy(t => t).ToArray();
		var weights = new long[tracks.Length];
		for (var i = 0; i < tracks.Length; i++)
		{
			var w = this.ApplyResearchDesireToTrackWeight(tracks[i], this.GetResearchTrackMixWeight(tracks[i]));
			var j = this.info.ResearchTrackMixJitterPercent;
			if (j > 0)
				w = Math.Max(1L, w * (100L + r.Next(-j, j + 1)) / 100L);
			weights[i] = w;
		}

		var idx = BotAi.PickWeightedIndex(weights, r);
		if (idx < 0 || idx >= tracks.Length)
			idx = 0;

		return byTrack[tracks[idx]][0];
	}

	/// <summary>Base weight from rules; must match <see cref="ResearchTargetKindRank" /> bands.</summary>
	private long GetResearchTrackMixWeight(int track) =>
		track switch
		{
			0 => this.info.ResearchTrackMixWeightCombat,
			1 => this.info.ResearchTrackMixWeightBuildings,
			2 => this.info.ResearchTrackMixWeightPower,
			3 => this.info.ResearchTrackMixWeightLabSelf,
			4 => this.info.ResearchTrackMixWeightCash,
			_ => this.info.ResearchTrackMixWeightOther,
		};

	/// <summary>Scales base track weight by blended personality + mood: high <see cref="runtimeAggressionAxis" /> favours combat; high <see cref="runtimeEconomyAxis" /> favours power, cash, and base build; <see cref="runtimeDefenseAxis" /> nudges building track; <see cref="techOpportunity" /> nudges lab self-upgrade.</summary>
	private long ApplyResearchDesireToTrackWeight(int track, long baseWeight)
	{
		var k = BotAi.ClampF(this.info.ResearchTrackDesireBlend, 0f, 1f);
		if (k <= 0f)
			return Math.Max(1L, baseWeight);

		var f = this.runtimeAggressionAxis;
		var e = this.runtimeEconomyAxis;
		var def = this.runtimeDefenseAxis;
		var tech = this.techOpportunity;

		// Per-track multipliers ~0.35–1.5 when k=1; blend toward 1 when k<1.
		var m = track switch
		{
			0 => 0.62f + 0.76f * f,
			1 => 0.68f + 0.42f * e + 0.28f * def,
			2 => 0.66f + 0.68f * e,
			3 => 0.74f + 0.48f * tech,
			4 => 0.69f + 0.58f * e,
			_ => 0.82f + 0.18f * (f + e),
		};

		// While the early combat soft cap is active, favour combat *production* tier research (track 0) so the bot
		// unlocks better units for the same unit-count budget instead of over-weighting other tracks.
		if (track == 0
			&& this.InCombatForceEarlyPhase()
			&& this.info.CombatForceSoftCapEarlyGame > 0)
		{
			var tip = 1f + 0.4f * tech * (1f - 0.55f * this.economyUrgency);
			m *= MathF.Min(1.48f, tip);
		}

		m = BotAi.ClampF(m, 0.35f, 1.55f);
		var scaled = 1f + k * (m - 1f);
		return Math.Max(1L, (long)(baseWeight * scaled + 0.5f));
	}

	/// <summary>Picks an index with probability proportional to weights; if all zero, uniform.</summary>
	private static int PickWeightedIndex(long[] weights, MersenneTwister r)
	{
		if (weights.Length == 0)
			return -1;
		if (weights.Length == 1)
			return 0;

		long sum = 0;
		for (var i = 0; i < weights.Length; i++)
			sum += Math.Max(0, weights[i]);

		if (sum <= 0)
			return r.Next(weights.Length);

		if (sum > int.MaxValue)
			return r.Next(weights.Length);

		var pick = (long)r.Next(0, (int)sum);
		long acc = 0;
		for (var i = 0; i < weights.Length; i++)
		{
			acc += Math.Max(0, weights[i]);
			if (pick < acc)
				return i;
		}

		return weights.Length - 1;
	}

	private static int RemainingResearchSteps(Actor a)
	{
		var r = a.TraitOrDefault<Researchable>();

		return r == null ? 0 : Math.Max(0, r.MaxLevel - r.Level);
	}

	/// <summary>Track id for grouping and <see cref="GetResearchTrackMixWeight" />; not a strict global order anymore — <see cref="PickResearchTarget" /> picks among tracks at random with weights.</summary>
	private static int ResearchTargetKindRank(Actor a)
	{
		// Barracks, machine shop, etc.: AdvancedProduction is ResearchableProduction that drives TechLevel for infantry/vehicles.
		if (a.Info.HasTraitInfo<AdvancedProductionInfo>())
		{
			var ap = a.Info.TraitInfoOrDefault<AdvancedProductionInfo>();
			if (ap is { Produces: not null }
				&& ap.Produces.Any(
					p =>
						string.Equals(p, "infantry", StringComparison.OrdinalIgnoreCase)
						|| string.Equals(p, "vehicle", StringComparison.OrdinalIgnoreCase)
						|| string.Equals(p, "aircraft", StringComparison.OrdinalIgnoreCase)
				))
				return 0;
		}

		// Research / alchemy hall: self-upgrade; after combat production (was incorrectly buried behind power).
		if (a.Info.HasTraitInfo<ResearchesInfo>())
			return 3;

		// Mobile base: building/tower/wall ResearchableProduction only (no AdvancedProduction on this actor).
		if (a.Info.HasTraitInfo<ResearchableProductionInfo>() && !a.Info.HasTraitInfo<AdvancedProductionInfo>())
			return 1;

		// Power station: pump tier (IProvidesResearchables), not a substitute for training tiers.
		if (a.Info.HasTraitInfo<PowerStationInfo>())
			return 2;

		if (a.Info.HasTraitInfo<CashTricklerInfo>())
			return 4;

		return 5;
	}

	private void TryQueueExtraTankers(IBot bot)
	{
		var nodes = this.sectors
			.Where(s => s.Claimed)
			.SelectMany(s => s.OilPatches)
			.Count(op => op is { Drillrig: { }, PowerStation: { } });

		if (nodes == 0)
			return;

		var target = nodes * this.tankersPerCompleteOilNodeTarget;
		var have = this.world.ActorsWithTrait<Tanker>().Count(t => t.Actor.Owner == bot.Player && t.Actor.IsInWorld && !t.Actor.IsDead);
		var queued = this.CountQueuedProductionWithTraitInfo<TankerInfo>(bot.Player);

		if (have + queued >= target)
			return;

		var pr = bot.Player.PlayerActor.Trait<PlayerResources>();
		var r = this.world.LocalRandom;
		var factories = this.world.ActorsWithTrait<AdvancedProductionQueue>()
			.Where(e => e.Actor.Owner == bot.Player && e.Trait.Info.Type == "vehicle" && e.Trait.Enabled)
			.ToArray();

		if (factories.Length == 0)
			return;

		foreach (var f in factories.Shuffle(r))
		{
			var buildable = f.Trait.BuildableItems().FirstOrDefault(b => b.HasTraitInfo<TankerInfo>());

			if (buildable == null)
				continue;

			var cost = buildable.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;
			var oil = pr.GetCashAndResources();

			if (oil < cost)
				continue;

			if (oil - cost < this.effectiveMinimumOilReserve)
				continue;

			bot.QueueOrder(Order.StartProduction(f.Actor, buildable.Name, 1));

			return;
		}
	}

	private int CountQueuedProductionWithTraitInfo<TTraitInfo>(Player player) where TTraitInfo : TraitInfo
	{
		var rules = this.world.Map.Rules;
		var n = 0;

		foreach (var pq in this.world.ActorsWithTrait<AdvancedProductionQueue>())
		{
			if (pq.Actor.Owner != player || !pq.Trait.Enabled)
				continue;

			foreach (var item in pq.Trait.AllQueued())
			{
				if (rules.Actors.TryGetValue(item.Item, out var ai) && ai.HasTraitInfo<TTraitInfo>())
					n++;
			}
		}

		return n;
	}

	/// <summary>Queued slots in combat queues that build <see cref="IsProducibleCombatUnit"/> actors (excludes stray non-combat entries).</summary>
	private int CountCombatProducibleQueued(Player player)
	{
		var rules = this.world.Map.Rules;
		var n = 0;

		foreach (var pq in this.world.ActorsWithTrait<AdvancedProductionQueue>())
		{
			if (pq.Actor.Owner != player || !pq.Trait.Enabled || !this.combatQueueTypeSet.Contains(pq.Trait.Info.Type))
				continue;

			foreach (var item in pq.Trait.AllQueued())
			{
				if (rules.Actors.TryGetValue(item.Item, out var ai) && IsProducibleCombatUnit(ai))
					n++;
			}
		}

		return n;
	}

	/// <summary>Staged cap: early small army, then optional late cap or unlimited.</summary>
	private int GetCombatForceSoftCap()
	{
		var early = this.info.CombatForceSoftCapEarlyGame;
		if (early <= 0)
			return int.MaxValue;

		var end = this.info.CombatForceSoftCapEndWorldTick;
		if (end > 0 && this.world.WorldTick >= end)
		{
			var late = this.info.CombatForceSoftCapLateGame;
			return late > 0 ? late : int.MaxValue;
		}

		return early;
	}

	/// <summary>True while the early combat cap rules apply (tight max units + optional queue clamp).</summary>
	private bool InCombatForceEarlyPhase()
	{
		if (this.info.CombatForceSoftCapEarlyGame <= 0)
			return false;
		var end = this.info.CombatForceSoftCapEndWorldTick;
		return end <= 0 || this.world.WorldTick < end;
	}

	private void TryQueueCombatUnits(IBot bot, int t)
	{
		this.nextUnitProdTick = t + this.effectiveInfantryProductionCooldownTicks;

		var r = this.world.LocalRandom;
		var pr = bot.Player.PlayerActor.Trait<PlayerResources>();
		var factories = this.world.ActorsWithTrait<AdvancedProductionQueue>()
			.Where(e => e.Actor.Owner == bot.Player && this.combatQueueTypeSet.Contains(e.Trait.Info.Type) && e.Trait.Enabled)
			.ToArray();

		if (factories.Length == 0)
			return;

		var field = this.CountOurFieldCombatUnits(bot.Player);
		var qCombat = this.CountCombatProducibleQueued(bot.Player);
		var cap = this.GetCombatForceSoftCap();
		if (cap < int.MaxValue && field + qCombat >= cap)
			return;

		var maxQueued = this.effectiveMaxInfantryQueued;
		if (this.info.EarlyPhaseMaxCombatQueued > 0 && this.InCombatForceEarlyPhase())
			maxQueued = Math.Min(maxQueued, this.info.EarlyPhaseMaxCombatQueued);

		if (qCombat >= maxQueued)
			return;

		var militaryReserve = this.GetMilitaryOilReserveForSpending(bot.Player, field);
		foreach (var f in this.OrderCombatFactories(factories, r))
		{
			var unit = this.PickCombatUnitToBuild(f.Trait, r, pr, militaryReserve);
			if (unit == null)
				continue;

			var valued = unit.TraitInfoOrDefault<ValuedInfo>();
			if (valued == null)
				continue;

			// Only spend what the player has (gathered oil / cash), after military reserve (not full economy hoard).
			var oil = pr.GetCashAndResources();
			if (oil < valued.Cost)
				continue;

			if (oil - valued.Cost < militaryReserve)
				continue;

			bot.QueueOrder(Order.StartProduction(f.Actor, unit.Name, 1));
			this.lastCombatQueueType = f.Trait.Info.Type;
			return;
		}
	}

	/// <summary>
	/// Puts the opposite of the last-queued type first when both <c>infantry</c> and <c>vehicle</c> exist,
	/// so the bot does not only spam cheap infantry from a random-shuffled list.
	/// </summary>
	private List<TraitPair<AdvancedProductionQueue>> OrderCombatFactories(
		TraitPair<AdvancedProductionQueue>[] factories, MersenneTwister r
	)
	{
		if (factories.Length == 0)
			return new();

		// Gen1: mostly infantry + vehicle. Keep any other queue type (e.g. future aircraft) in a third group.
		var byType = factories.GroupBy(f => f.Trait.Info.Type)
			.ToDictionary(g => g.Key, g => g.ToList());

		if (byType.Count == 1)
			return factories.Shuffle(r).ToList();

		if (byType.TryGetValue("infantry", out var inf) && byType.TryGetValue("vehicle", out var veh))
		{
			byType.Remove("infantry");
			byType.Remove("vehicle");
			inf = inf.Shuffle(r).ToList();
			veh = veh.Shuffle(r).ToList();
			var other = byType.SelectMany(e => e.Value).ToList();
			other = other.Count > 0 ? other.Shuffle(r).ToList() : other;

			bool vehFirst;
			if (this.lastCombatQueueType == "infantry")
				vehFirst = true;
			else if (this.lastCombatQueueType == "vehicle")
				vehFirst = false;
			else
				vehFirst = r.Next(2) == 0;

			var res = new List<TraitPair<AdvancedProductionQueue>>(inf.Count + veh.Count + other.Count);
			if (vehFirst)
			{
				res.AddRange(veh);
				res.AddRange(inf);
			}
			else
			{
				res.AddRange(inf);
				res.AddRange(veh);
			}

			if (other.Count > 0)
				res.AddRange(other);

			return res;
		}

		return factories.Shuffle(r).ToList();
	}

	/// <summary>Budget reserve for combat spending: softer than full <see cref="effectiveMinimumOilReserve"/> when army is low or cap is set.</summary>
	/// <param name="knownFieldCombat">If you already called <see cref="CountOurFieldCombatUnits"/>, pass it to avoid a second world scan.</param>
	private int GetMilitaryOilReserveForSpending(Player player, int? knownFieldCombat = null)
	{
		var f = knownFieldCombat ?? this.CountOurFieldCombatUnits(player);
		if (this.info.RebuildArmyIfFieldCombatBelow > 0
			&& f < this.info.RebuildArmyIfFieldCombatBelow)
		{
			return this.info.MinimumOilReserveForProduction;
		}
		var r = this.effectiveMinimumOilReserve;
		if (this.info.MilitaryOilReserveCap > 0)
			r = Math.Min(r, this.info.MilitaryOilReserveCap);
		return r;
	}

	private int CountOurFieldCombatUnits(Player player) =>
		this.world.Actors.Count(
			a => a.Owner == player
				&& a.IsInWorld
				&& !a.IsDead
				&& IsCombatSquadUnit(a)
		);

	/// <summary>Uses BuildableItems (prereqs, tech) then picks variety within oil budget and optional reserve.</summary>
	private ActorInfo? PickCombatUnitToBuild(
		AdvancedProductionQueue queue, MersenneTwister r, PlayerResources pr, int militaryOilReserve
	)
	{
		var buildables = queue.BuildableItems().Where(IsProducibleCombatUnit).ToList();
		if (buildables.Count == 0)
			return null;

		var available = pr.GetCashAndResources() - militaryOilReserve;

		foreach (var name in this.info.PreferredInfantry)
		{
			if (name.Length == 0)
				continue;

			if (!this.world.Map.Rules.Actors.TryGetValue(name, out var preferred))
				continue;

			if (!buildables.Contains(preferred))
				continue;

			var cost = preferred.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;
			if (cost > available)
				continue;

			return preferred;
		}

		var affordable = buildables
			.Where(
				a =>
				{
					var c = a.TraitInfoOrDefault<ValuedInfo>()?.Cost;
					if (c == null)
						return false;

					return c.Value <= available;
				}
			)
			.ToList();

		if (affordable.Count == 0)
			return null;

		return this.info.UnitCostPick switch
		{
			BotCombatUnitPickMode.CheapestK => PickCheapestKFromAffordable(affordable, r, this.info.RandomPickCheapestOf),
			BotCombatUnitPickMode.Uniform => affordable[r.Next(affordable.Count)],
			BotCombatUnitPickMode.MeanWeighted => PickMeanWeighted(affordable, buildables, r),
			_ => PickTertile(affordable, buildables, r),
		};
	}

	/// <summary>Legacy: random among the k cheapest (by Valued) affordable units; k is at least 1.</summary>
	private static ActorInfo? PickCheapestKFromAffordable(IReadOnlyList<ActorInfo> affordable, MersenneTwister r, int k)
	{
		if (affordable.Count == 0)
			return null;

		var kClamped = Math.Max(1, k);
		var pool = affordable
			.OrderBy(a => a.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? int.MaxValue)
			.Take(kClamped)
			.ToList();
		if (pool.Count == 0)
			return null;

		return pool[r.Next(pool.Count)];
	}

	/// <summary>Split the full roster (ordered by cost) into three bands; try a random band first, then others.</summary>
	private static ActorInfo? PickTertile(IReadOnlyList<ActorInfo> affordable, IReadOnlyList<ActorInfo> fullRoster, MersenneTwister r)
	{
		if (affordable.Count == 1)
			return affordable[0];

		var ordered = fullRoster
			.Select(a => (A: a, C: a.TraitInfoOrDefault<ValuedInfo>()?.Cost))
			.Where(t => t.C != null)
			.Select(t => (t.A, C: t.C!.Value))
			.OrderBy(t => t.C)
			.ToArray();
		if (ordered.Length < 2)
		{
			// Roster with missing Valued: fall back
			return affordable[r.Next(affordable.Count)];
		}

		if (ordered.Length < 3)
		{
			// Not enough types for three distinct bands: uniform among affordable
			return affordable[r.Next(affordable.Count)];
		}

		// index bands: [0, i1), [i1, i2), [i2, n) — split roster into three price tiers by type count
		var n = ordered.Length;
		var i1 = n / 3;
		var i2 = (2 * n) / 3;

		static int BandForIndex(int i, int a, int b) => i < a ? 0 : (i < b ? 1 : 2);

		var nameToBand = new Dictionary<string, int>();
		for (var i = 0; i < n; i++)
		{
			if (!nameToBand.ContainsKey(ordered[i].A.Name))
				nameToBand[ordered[i].A.Name] = BandForIndex(i, i1, i2);
		}

		var affordableByBand = new[] { new List<ActorInfo>(), new List<ActorInfo>(), new List<ActorInfo>() };
		foreach (var a in affordable)
		{
			if (!nameToBand.TryGetValue(a.Name, out var b))
				continue;
			affordableByBand[b].Add(a);
		}

		var nonEmptyBands = new List<int>(3);
		for (var b = 0; b < 3; b++)
		{
			if (affordableByBand[b].Count > 0)
				nonEmptyBands.Add(b);
		}

		if (nonEmptyBands.Count == 0)
			return affordable[r.Next(affordable.Count)];

		var chosen = nonEmptyBands[r.Next(nonEmptyBands.Count)];
		return affordableByBand[chosen][r.Next(affordableByBand[chosen].Count)];
	}

	/// <summary>Weight each affordable type by 1 / (1 + k * normalized squared distance from roster mean cost).</summary>
	private static ActorInfo? PickMeanWeighted(
		IReadOnlyList<ActorInfo> affordable, IReadOnlyList<ActorInfo> fullRoster, MersenneTwister r
	)
	{
		var costs = fullRoster
			.Select(a => a.TraitInfoOrDefault<ValuedInfo>()?.Cost)
			.Where(c => c != null)
			.Select(c => c!.Value)
			.ToArray();
		if (costs.Length == 0)
		{
			// no costs on roster: uniform
			return affordable[r.Next(affordable.Count)];
		}

		var mean = costs.Average();
		var min = costs.Min();
		var max = costs.Max();
		var spread = Math.Max(1, max - min);
		// w ∝ 1 / (1 + 8 * ((c-mu)/span)^2)  — peak at mean, not always cheapest
		var w = new long[affordable.Count];
		for (var i = 0; i < affordable.Count; i++)
		{
			var c = (double) (affordable[i].TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0);
			var d = (c - mean) / spread;
			var wFloat = 1_000_000.0 / (1.0 + 8.0 * d * d);
			if (wFloat < 1.0)
				wFloat = 1.0;
			w[i] = (long)wFloat;
		}

		return PickWeighted(affordable, w, r);
	}

	/// <summary>Pick by weights; delegates to <see cref="PickWeightedIndex"/> (same fallbacks for extreme sums).</summary>
	private static ActorInfo? PickWeighted(IReadOnlyList<ActorInfo> items, long[] weights, MersenneTwister r)
	{
		if (items.Count == 0)
			return null;
		if (items.Count != weights.Length)
			return null;

		var i = PickWeightedIndex(weights, r);
		if (i < 0 || i >= items.Count)
			return items[r.Next(items.Count)];
		return items[i];
	}

	/// <summary>True for actor types the bot can train as combat and the same set it sends on attack waves (workers/tankers/base vehicles excluded).</summary>
	private static bool IsProducibleCombatUnit(ActorInfo a) => IsCombatRosterUnitInfo(a);

	/// <summary>Shared rule set for <see cref="IsProducibleCombatUnit"/> and <see cref="IsCombatSquadUnit"/> (Actor vs <see cref="ActorInfo"/>).</summary>
	private static bool IsCombatRosterUnitInfo(ActorInfo a)
	{
		if (a.Name.Length == 0 || a.Name[0] == '^')
			return false;
		if (a.HasTraitInfo<BuildingInfo>())
			return false;
		if (!a.HasTraitInfo<MobileInfo>())
			return false;
		if (a.HasTraitInfo<BaseBuildingInfo>() && a.HasTraitInfo<MobileInfo>())
			return false;
		if (a.HasTraitInfo<DeploysOnActorInfo>())
			return false;
		if (a.TraitInfoOrDefault<TankerInfo>() != null)
			return false;
		if (!a.HasTraitInfo<AttackBaseInfo>())
			return false;
		if (!HasAttackMoveForOrders(a) || !a.HasTraitInfo<AutoTargetInfo>())
			return false;

		return true;
	}

	void IBotRespondToAttack.RespondToAttack(IBot bot, Actor self, AttackInfo e)
	{
		if (!this.info.TacticalDefenceEnabled)
			return;
		if (e.Damage.Value <= 0)
			return;
		if (e.Attacker is not { } atk || !atk.IsInWorld || atk.IsDead)
			return;
		if (self is null)
			return;
		if (!self.IsInWorld || self.IsDead)
			return;
		if (self.Owner != bot.Player)
			return;
		if (bot.Player.RelationshipWith(atk.Owner) != PlayerRelationship.Enemy)
			return;

		this.tacticalUrgent = true;
	}

	private void TryTacticalDefence(IBot bot)
	{
		if (!this.info.TacticalDefenceEnabled)
			return;
		if (bot.Player.WinState != WinState.Undefined)
			return;

		var t = this.world.WorldTick;
		if (!this.tacticalUrgent && t < this.nextTacticalDefenceWorldTick)
			return;

		this.tacticalUrgent = false;
		this.nextTacticalDefenceWorldTick = t + Math.Max(1, this.info.TacticalDefenceIntervalTicks);

		var p = bot.Player;
		if (!this.world.Map.Contains(p.HomeLocation))
			return;

		var r = this.world.LocalRandom;
		var homeC = p.HomeLocation;
		var homeW = this.world.Map.CenterOfCell(homeC);
		var homeR = WDist.FromCells(
			Math.Max(1, this.info.TacticalHomeThreatRadiusCells)
		);
		var hostilesNearHome = this.world
			.FindActorsInCircle(homeW, homeR)
			.Where(a => a.IsInWorld && !a.IsDead && a.OccupiesSpace != null && this.IsHostileFieldCombatTo(a, p))
			.ToList();
		if (hostilesNearHome.Count < this.info.TacticalMinHostileNearHome)
			return;

		var enemyPlayers = this.GetTacticalEnemyPlayers(p);
		if (enemyPlayers.Count == 0)
			return;

		var targetEnemy = this.PickTacticalTargetEnemy(enemyPlayers, r);
		if (!this.world.Map.Contains(targetEnemy.HomeLocation))
			return;

		var theirHomeW = this.world.Map.CenterOfCell(targetEnemy.HomeLocation);
		var pushR = WDist.FromCells(
			Math.Max(1, this.info.TacticalPushRadiusCells)
		);
		var nPush = this.world
			.FindActorsInCircle(theirHomeW, pushR)
			.Count(a => a.IsInWorld && !a.IsDead && a.Owner == p && IsCombatSquadUnit(a));
		var nHome = hostilesNearHome.Count;
		var ratio = MathF.Max(0.01f, this.info.TacticalStickRaidOutnumberRatio);
		var stickWithRaid = nHome > 0 && nPush >= nHome * (double)ratio;

		// Always try to bring idle stragglers in when we are under pressure (reinforcements).
		this.TryTacticalReinforce(bot, p, homeW, hostilesNearHome, r);

		if (stickWithRaid)
			return;

		// The raid is not outnumbering the home threat: pull non-idle far units back to defend.
		this.TryTacticalRecall(bot, p, homeC, homeW, r);
	}

	private void TryTacticalReinforce(
		IBot bot, Player p, WPos homeW, IReadOnlyList<Actor> hostiles, MersenneTwister r
	)
	{
		if (hostiles.Count == 0)
			return;

		var minDist = WDist.FromCells(Math.Max(1, this.info.TacticalReinforceMinDistanceFromHomeCells)).Length;
		var maxN = Math.Max(1, this.info.TacticalReinforceMaxSquad);
		var shroud = p.Shroud;
		// Prefer visible hostiles; fall back to any in list so AttackMove is still well-defined in fog edge cases
		var vision = hostiles
			.Where(a => a.IsInWorld && a.OccupiesSpace != null)
			.Where(a => shroud == null || shroud.IsVisible(a.CenterPosition))
			.ToList();
		if (vision.Count == 0)
			vision = hostiles.Where(a => a.IsInWorld && a.OccupiesSpace != null).ToList();
		if (vision.Count == 0)
			return;

		var targetActor = vision[r.Next(vision.Count)];

		var field = this.world
			.Actors.Where(
				a => a.IsInWorld
					&& !a.IsDead
					&& a.Owner == p
					&& a.IsIdle
					&& a.OccupiesSpace != null
					&& IsCombatSquadUnit(a)
					&& (a.CenterPosition - homeW).HorizontalLength > minDist
			)
			.OrderBy(_ => r.Next(1000))
			.Take(maxN)
			.ToArray();

		if (field.Length == 0)
			return;

		bot.QueueOrder(
			new Order("AttackMove", null, Target.FromActor(targetActor), false, null, field)
		);
	}

	private void TryTacticalRecall(IBot bot, Player p, CPos homeC, WPos homeW, MersenneTwister r)
	{
		var needDist = WDist.FromCells(Math.Max(1, this.info.TacticalRecallMinDistanceFromHomeCells))
			.Length;
		var maxN = Math.Max(1, this.info.TacticalRecallMaxUnits);
		var away = this.world
			.Actors.Where(
				a => a.IsInWorld
					&& !a.IsDead
					&& a.Owner == p
					&& !a.IsIdle
					&& a.OccupiesSpace != null
					&& IsCombatSquadUnit(a)
					&& (a.CenterPosition - homeW).HorizontalLength > needDist
			)
			.OrderByDescending(a => (a.CenterPosition - homeW).HorizontalLengthSquared)
			.Take(maxN)
			.ToArray();

		if (away.Length == 0)
			return;

		bot.QueueOrder(
			new Order("Move", null, Target.FromCell(this.world, homeC), false, null, away)
		);
	}
	/// <summary>Enemies for attack waves and tactical raid push scoring; may omit NonCombatant when other enemies exist.</summary>
	private List<Player> GetTacticalEnemyPlayers(Player p) => this.GetEnemyPlayersForOffensiveAI(p);

	/// <summary>Relationship enemies, optionally with neutral/creep (NonCombatant) players de-prioritised so the AI focuses on win-state opponents.</summary>
	private List<Player> GetEnemyPlayersForOffensiveAI(Player self)
	{
		var all = this.world.Players
			.Where(
				p => p != self
					&& !p.Spectating
					&& p.WinState == WinState.Undefined
					&& self.RelationshipWith(p) == PlayerRelationship.Enemy
			)
			.ToList();

		if (!this.info.DeprioritizeNonCombatantEnemies)
			return all;

		var withoutNeutrals = all.Where(p => !p.NonCombatant).ToList();
		return withoutNeutrals.Count > 0 ? withoutNeutrals : all;
	}

	/// <summary>Enemy to score the “offensive” ring around. Requires <paramref name="enemies" /> non-empty.</summary>
	private Player PickTacticalTargetEnemy(IReadOnlyList<Player> enemies, MersenneTwister r)
	{
		if (this.info.PreferNonBotTargets)
		{
			var h = enemies.Where(p => !p.IsBot).ToList();
			if (h.Count > 0)
				return h[r.Next(h.Count)];
		}
		return enemies[r.Next(enemies.Count)];
	}

	private bool IsHostileFieldCombatTo(Actor a, Player us)
	{
		if (a.IsDead)
			return false;
		if (a.Owner == us)
			return false;
		if (us.RelationshipWith(a.Owner) != PlayerRelationship.Enemy)
			return false;
		if (a.Info.HasTraitInfo<BuildingInfo>())
			return false;
		return IsCombatSquadUnit(a);
	}

	/// <summary>
	/// True if the unit may be ordered in a grouped attack wave. Uses <see cref="Actor.IsIdle" /> or, when
	/// <see cref="BotAiInfo.AttackWaveGatherMaxDistanceFromHomeCells" /> is set, units near the player home—on
	/// no-shroud maps AutoTarget often clears idle for units that are still at base, which produced tiny squads.
	/// </summary>
	private bool IsUnitAvailableForAttackWave(Actor a, IBot bot)
	{
		if (a.IsIdle)
			return true;
		var cells = this.info.AttackWaveGatherMaxDistanceFromHomeCells;
		if (cells <= 0)
			return false;
		if (!this.world.Map.Contains(bot.Player.HomeLocation))
			return false;
		var homeW = this.world.Map.CenterOfCell(bot.Player.HomeLocation);
		var maxL = WDist.FromCells(cells).Length;
		return (a.CenterPosition - homeW).HorizontalLength <= maxL;
	}

	private void TryAttackSquad(IBot bot, int t)
	{
		if (bot.Player.WinState != WinState.Undefined)
			return;

		var r = this.world.LocalRandom;

		if (this.defenseTowersRequiredBeforeAttack > 0)
		{
			var primary = this.GetPrimaryBaseSector(bot.Player);
			// Any claimed sector: towers may only fit in an expansion, not the primary.
			if (primary != null
				&& this.CountTowerBuildingsInClaimedSectors(bot.Player) < this.defenseTowersRequiredBeforeAttack)
			{
				this.nextAttackTick = t + r.Next(80, 250);

				return;
			}
		}

		var sMin = Math.Min(this.effectiveAttackSquadSizeMin, this.effectiveAttackSquadSizeMax);
		var sMax = Math.Max(this.effectiveAttackSquadSizeMin, this.effectiveAttackSquadSizeMax);

		// When DeprioritizeNonCombatantEnemies, attack rotation favours real opponents; neutrals (Creeps) are only
		// used if there is no one else, so the bot is not "fed" to neutral farmers while other enemies exist.
		var enemies = this.GetEnemyPlayersForOffensiveAI(bot.Player);

		if (enemies.Count == 0)
		{
			this.nextAttackTick = t + this.ComputeNextAttackWaveDelay(0, sMin, sMax, r, noEnemies: true);
			return;
		}

		if (this.info.PreferNonBotTargets)
		{
			var nonBots = enemies.Where(p => !p.IsBot).ToList();
			if (nonBots.Count > 0)
				enemies = nonBots;
		}

		var attackPool = this.world.Actors
			.Where(
				a => a.Owner == bot.Player
					&& a.IsInWorld
					&& !a.IsDead
					&& IsCombatSquadUnit(a)
					&& this.IsUnitAvailableForAttackWave(a, bot)
			)
			.OrderBy(_ => r.Next(1000))
			.ToList();

		if (attackPool.Count == 0)
		{
			// No units available: try again after a short, randomized pause.
			this.nextAttackTick = t + r.Next(120, 320);
			return;
		}

		var minPool = this.info.AttackWaveMinPoolUnitsToLaunch;
		if (minPool > 0)
		{
			var fieldCombat = this.CountOurFieldCombatUnits(bot.Player);
			if (fieldCombat >= minPool && attackPool.Count < minPool)
			{
				// More chips could join the gather pool (e.g. walking in); avoid one-at-a-time waves when configured.
				this.nextAttackTick = t + r.Next(45, 120);
				return;
			}
		}

		var baseWave = sMin >= sMax ? sMin : r.Next(sMin, sMax + 1);
		var desiredSquad = baseWave;
		var thr = this.info.AttackWaveIdleEscalationThreshold;
		if (thr > 0 && attackPool.Count >= thr)
		{
			var sendMax = Math.Max(1, this.info.AttackWaveIdleEscalationSendMax);
			// Send a larger slice when many units are banked (default: up to half the stack, capped).
			var escalated = Math.Max(baseWave, Math.Min(sendMax, (attackPool.Count + 1) / 2));
			desiredSquad = Math.Min(attackPool.Count, escalated);
		}
		else if (attackPool.Count > sMax)
			desiredSquad = Math.Min(attackPool.Count, Math.Max(baseWave, sMax));

		var squad = attackPool.Take(desiredSquad).ToArray();

		var targetPlayer = enemies[this.attackTargetCursor++ % enemies.Count];
		var target = this.PickAttackTarget(bot.Player, targetPlayer, squad, r);
		if (target == Target.Invalid)
		{
			// Retry soon: long post-wave delays are for successful orders, not "no valid target yet."
			this.nextAttackTick = t + r.Next(40, 140);
			return;
		}

		bot.QueueOrder(new Order("AttackMove", null, target, false, groupedActors: squad));
		// Bigger sent waves (when coupled) or separate random (when decoupled) + jitter: feels less clockwork.
		var waveDelay = this.ComputeNextAttackWaveDelay(squad.Length, sMin, sMax, r, noEnemies: false);
		// Tight economy: a slightly longer regroup so spend can catch up to mood (military cadence is already lower via effective axes).
		if (this.info.AdaptiveMood && this.economyUrgency > 0.45f)
			waveDelay += Math.Min(80, (int)(28f * this.economyUrgency * (0.45f + 0.55f * (1f - this.prosperity))));

		this.nextAttackTick = t + waveDelay;
	}

	// Cooldown after a wave: optional scaling by sent squad + jitter, or a separate random band when decoupled.
	private int ComputeNextAttackWaveDelay(int squadSize, int smin, int smax, MersenneTwister r, bool noEnemies)
	{
		if (noEnemies)
		{
			var a = Math.Min(this.info.AttackWaveCooldownRandomTicksMin, this.info.AttackWaveCooldownRandomTicksMax);
			var b = Math.Max(this.info.AttackWaveCooldownRandomTicksMin, this.info.AttackWaveCooldownRandomTicksMax);
			if (a == b)
				return this.ScaledJitteredDelay(a, r);

			return this.ScaledJitteredDelay(r.Next(a, b + 1), r);
		}

		if (this.info.DecoupleSquadSizeFromNextCooldown)
		{
			var a = Math.Min(this.info.AttackWaveCooldownRandomTicksMin, this.info.AttackWaveCooldownRandomTicksMax);
			var b = Math.Max(this.info.AttackWaveCooldownRandomTicksMin, this.info.AttackWaveCooldownRandomTicksMax);
			var baseDelay = r.Next(a, b + 1);
			return this.ScaledJitteredDelay(baseDelay, r);
		}

		var iLo = this.info.AttackWaveCooldownTicksIfSmallSquad;
		var iHi = this.info.AttackWaveCooldownTicksIfLargeSquad;
		if (iLo > iHi)
			(iLo, iHi) = (iHi, iLo);

		if (smax == smin)
			return this.ScaledJitteredDelay((iLo + iHi) / 2, r);

		var u = (float)(squadSize - smin) / (smax - smin);
		var scaled = (int)(iLo + u * (iHi - iLo) + 0.5f);
		return this.ScaledJitteredDelay(scaled, r);
	}

	private int JitteredDelay(int baseDelay, MersenneTwister r)
	{
		var j = this.info.AttackWaveCooldownJitterTicks;
		if (j > 0)
			baseDelay += r.Next(-j, j + 1);

		return Math.Max(1, baseDelay);
	}

	private int ScaledJitteredDelay(int baseDelay, MersenneTwister r) =>
		Math.Max(1, (int)(this.JitteredDelay(baseDelay, r) * this.effectivePostAttackCooldownScale + 0.5f));

	private static bool IsCombatSquadUnit(Actor a) => IsCombatRosterUnitInfo(a.Info);

	/// <summary>AttackMoveInfo in the engine assembly is not visible to the mod; we match the trait by runtime type name.</summary>
	private static bool HasAttackMoveForOrders(ActorInfo info) =>
		info.TraitsInConstructOrder().Any(t => t.GetType().Name == "AttackMoveInfo");

	private Target PickAttackTarget(Player selfPlayer, Player enemy, Actor[] squad, MersenneTwister r)
	{
		var refPoint = squad.FirstOrDefault(a => a.OccupiesSpace != null)?.CenterPosition
			?? this.world.Map.CenterOfCell(selfPlayer.HomeLocation);
		var shroud = selfPlayer.Shroud;
		var map = this.world.Map;

		static List<Actor> GatherEnemyInShroud(
			World w, Shroud? sh, Player e, System.Func<Shroud, WPos, bool> cellOk
		)
		{
			// Some actors (no IOccupySpace) cannot report CenterPosition; skip them to avoid NRE on Actor.CenterPosition.
			return w.Actors
				.Where(
					a => a.OccupiesSpace != null
						&& a.Owner == e
						&& a.IsInWorld
						&& !a.IsDead
						&& (sh == null || cellOk(sh, a.CenterPosition))
				)
				.ToList();
		}

		Target PickFrom(List<Actor> list, WPos from)
		{
			if (list.Count == 0)
				return Target.Invalid;

			var building = list
				.Where(a => a.Info.HasTraitInfo<BuildingInfo>() || a.Info.TraitInfos<ProvidesPrerequisiteInfo>().Any())
				.ClosestToIgnoringPath(from);
			if (building != null)
				return Target.FromActor(building);

			return Target.FromActor(list.ClosestToIgnoringPath(from));
		}

		// 1) Current vision: attack known structures or units.
		var visible = GatherEnemyInShroud(this.world, shroud, enemy, (s, wpos) => s.IsVisible(wpos));
		var t = PickFrom(visible, refPoint);
		if (t != Target.Invalid)
			return t;

		// 2) Stale intel: explored but not in sight (e.g. scouted base), still a valid push direction.
		if (shroud != null)
		{
			var explored = GatherEnemyInShroud(this.world, shroud, enemy, (s, wpos) => s.IsExplored(wpos));
			t = PickFrom(explored, refPoint);
			if (t != Target.Invalid)
				return t;
		}

		// 3) No direct vision: move toward the enemy start location
		if (map.Contains(enemy.HomeLocation))
			return Target.FromCell(this.world, enemy.HomeLocation);

		// 4) No home: map center of playable bounds (some modes leave Home unset).
		{
			var b = map.Bounds;
			var mid = new MPos((b.Left + b.Right) / 2, (b.Top + b.Bottom) / 2);
			if (map.Contains(mid))
				return Target.FromCell(this.world, mid.ToCPos(map));
		}

		// 5) Last resort: any walkable cell (still an AttackMove, not stuck Invalid).
		return Target.FromCell(this.world, map.ChooseRandomCell(r));
	}
}
