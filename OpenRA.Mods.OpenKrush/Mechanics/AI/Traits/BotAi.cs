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
using Researching.Traits;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public class BotAiInfo : ConditionalTraitInfo
{
	// For performance we delay some ai tasks => OpenKrush runs with 25 ticks per second (at normal speed).
	public int ThinkDelay = 25;

	[Desc("Minimum ticks between attempts to queue a combat unit (infantry, vehicle, …; 25 ticks ≈ 1s at normal speed).")]
	public int InfantryProductionCooldownTicks = 50;

	[Desc("Maximum total combat units (infantry, vehicles, etc.) queued across all production structures.")]
	public int MaxInfantryQueued = 12;

	[Desc("If set, try these actor names in order for any queue that can build them; otherwise pick randomly among affordable combat units.")]
	public string[] PreferredInfantry = [];

	[Desc("When PreferredInfantry is empty, prefer a random pick over the N cheapest buildable combat units to encourage mixed armies (0 = use full random among affordable).")]
	public int RandomPickCheapestOf = 5;

	[Desc("Unit production types to use; default infantry + vehicle. Must match queue Type: (e.g. infantry, vehicle).")]
	public string[] CombatProductionQueueTypes = ["infantry", "vehicle"];

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
	public bool PreferNonBotTargets = true;

	public override object Create(ActorInitializer init)
	{
		return new BotAi(init.World, this);
	}
}

public class BotAi : IBotTick
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

		this.UpdateSectorsClaims(bot);
		this.HandleMobileBases(bot);
		this.HandleMobileDerricks(bot);
		this.AssignOilActors(bot);
		this.ConstructBuildings(bot);
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
			var lo = Math.Min(this.info.FirstAttackDelayTicksMin, this.info.FirstAttackDelayTicksMax);
			var hi = Math.Max(this.info.FirstAttackDelayTicksMin, this.info.FirstAttackDelayTicksMax);
			this.nextAttackTick = lo >= hi ? lo : r.Next(lo, hi + 1);
		}

		this.initialized = true;
	}

	private void UpdateSectorsClaims(IBot bot)
	{
		var buildingsInSectors = this.world.ActorsHavingTrait<ProvidesPrerequisite>()
			.Where(actor => actor.Owner == bot.Player)
			.GroupBy(actor => this.sectors.MinBy(sector => (sector.Origin - actor.CenterPosition).Length))
			.ToDictionary(e => e.Key, e => e);

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
		var sectorOrder = this.sectors
			.Where(sector => sector.Claimed)
			.OrderBy(sector => primary != null && sector == primary ? 1 : 0)
			.ThenBy(sector => (sector.Origin - homeWPos).LengthSquared);

		var buildingsInSectors = this.world.ActorsHavingTrait<ProvidesPrerequisite>()
			.Where(actor => actor.Owner == bot.Player)
			.GroupBy(actor => this.sectors.MinBy(sector => (sector.Origin - actor.CenterPosition).Length))
			.ToDictionary(e => e.Key, e => e);

		foreach (var sector in sectorOrder)
		{
			if (!buildingsInSectors.TryGetValue(sector, out var buildingsInSector))
				continue;

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

			build = buildables.Where(buildable => buildable.HasTraitInfo<AdvancedProductionInfo>())
				.FirstOrDefault(buildable => buildingsInSector.All(building => building.Info.Name != buildable.Name));

			if (this.Build(bot, sector, build, productionQueue))
				break;

			build = buildables.Where(buildable => buildable.HasTraitInfo<ResearchesInfo>())
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

		return bestCount > 0 ? best : null;
	}

	private void TryQueueCombatUnits(IBot bot, int t)
	{
		this.nextUnitProdTick = t + this.info.InfantryProductionCooldownTicks;

		var r = this.world.LocalRandom;
		var pr = bot.Player.PlayerActor.Trait<PlayerResources>();
		var typeSet = this.info.CombatProductionQueueTypes.ToHashSet();
		var factories = this.world.ActorsWithTrait<AdvancedProductionQueue>()
			.Where(e => e.Actor.Owner == bot.Player && typeSet.Contains(e.Trait.Info.Type) && e.Trait.Enabled)
			.ToArray();

		if (factories.Length == 0)
			return;

		var queued = factories.Sum(f => f.Trait.AllQueued().Count());
		if (queued >= this.info.MaxInfantryQueued)
			return;

		foreach (var f in this.OrderCombatFactories(factories, r))
		{
			var unit = this.PickCombatUnitToBuild(f.Trait, r, pr);
			if (unit == null)
				continue;

			var valued = unit.TraitInfoOrDefault<ValuedInfo>();
			if (valued == null)
				continue;

			// Only spend what the player has (gathered oil / cash), after reserve.
			var oil = pr.GetCashAndResources();
			if (oil < valued.Cost)
				continue;

			if (oil - valued.Cost < this.info.MinimumOilReserveForProduction)
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

	/// <summary>Uses BuildableItems (prereqs, tech) then picks variety within oil budget and optional reserve.</summary>
	private ActorInfo? PickCombatUnitToBuild(AdvancedProductionQueue queue, MersenneTwister r, PlayerResources pr)
	{
		var buildables = queue.BuildableItems().Where(IsProducibleCombatUnit).ToList();
		if (buildables.Count == 0)
			return null;

		var available = pr.GetCashAndResources() - this.info.MinimumOilReserveForProduction;

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

		if (this.info.RandomPickCheapestOf <= 0)
			return affordable[r.Next(affordable.Count)];

		var k = this.info.RandomPickCheapestOf;
		var pool = affordable
			.OrderBy(a => a.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? int.MaxValue)
			.Take(k)
			.ToList();
		if (pool.Count == 0)
			return null;

		return pool[r.Next(pool.Count)];
	}

	/// <summary>Matches the produced unit types we send on AttackMove, so we do not build workers/tankers.</summary>
	private static bool IsProducibleCombatUnit(ActorInfo a)
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

	private void TryAttackSquad(IBot bot, int t)
	{
		if (bot.Player.WinState != WinState.Undefined)
			return;

		var r = this.world.LocalRandom;
		var sMin = Math.Min(this.info.AttackWaveSquadSizeMin, this.info.AttackWaveSquadSizeMax);
		var sMax = Math.Max(this.info.AttackWaveSquadSizeMin, this.info.AttackWaveSquadSizeMax);
		var desiredSquad = r.Next(sMin, sMax + 1);

		// Do not require !p.NonCombatant: map "Creeps" and similar are NonCombatant (no win/lose) but are still
		// declared Enemies: ... and must be engaged or the bot ignores hostile neutrals and loses.
		var enemies = this.world.Players
			.Where(
				p => p != bot.Player
					&& !p.Spectating
					&& p.WinState == WinState.Undefined
					&& bot.Player.RelationshipWith(p) == PlayerRelationship.Enemy
			)
			.ToList();

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

		var targetPlayer = enemies[this.attackTargetCursor++ % enemies.Count];
		var squad = this.world.Actors
			.Where(
				a => a.Owner == bot.Player
					&& a.IsInWorld
					&& !a.IsDead
					&& a.IsIdle
					&& IsCombatSquadUnit(a)
			)
			.Take(desiredSquad)
			.ToArray();

		if (squad.Length == 0)
		{
			// No idle raiders: try again after a short, randomized pause.
			this.nextAttackTick = t + r.Next(120, 320);
			return;
		}

		var target = this.PickAttackTarget(bot.Player, targetPlayer, squad);
		if (target == Target.Invalid)
		{
			this.nextAttackTick = t + this.ComputeNextAttackWaveDelay(squad.Length, sMin, sMax, r, noEnemies: false);
			return;
		}

		bot.QueueOrder(new Order("AttackMove", null, target, false, groupedActors: squad));
		// Bigger sent waves (when coupled) or separate random (when decoupled) + jitter: feels less clockwork.
		this.nextAttackTick = t + this.ComputeNextAttackWaveDelay(squad.Length, sMin, sMax, r, noEnemies: false);
	}

	// Cooldown after a wave: optional scaling by sent squad + jitter, or a separate random band when decoupled.
	private int ComputeNextAttackWaveDelay(int squadSize, int smin, int smax, MersenneTwister r, bool noEnemies)
	{
		if (noEnemies)
		{
			var a = Math.Min(this.info.AttackWaveCooldownRandomTicksMin, this.info.AttackWaveCooldownRandomTicksMax);
			var b = Math.Max(this.info.AttackWaveCooldownRandomTicksMin, this.info.AttackWaveCooldownRandomTicksMax);
			if (a == b)
				return this.JitteredDelay(a, r);

			return this.JitteredDelay(r.Next(a, b + 1), r);
		}

		if (this.info.DecoupleSquadSizeFromNextCooldown)
		{
			var a = Math.Min(this.info.AttackWaveCooldownRandomTicksMin, this.info.AttackWaveCooldownRandomTicksMax);
			var b = Math.Max(this.info.AttackWaveCooldownRandomTicksMin, this.info.AttackWaveCooldownRandomTicksMax);
			var baseDelay = r.Next(a, b + 1);
			return this.JitteredDelay(baseDelay, r);
		}

		var iLo = this.info.AttackWaveCooldownTicksIfSmallSquad;
		var iHi = this.info.AttackWaveCooldownTicksIfLargeSquad;
		if (iLo > iHi)
			(iLo, iHi) = (iHi, iLo);

		if (smax == smin)
			return this.JitteredDelay((iLo + iHi) / 2, r);

		var u = (float)(squadSize - smin) / (smax - smin);
		var scaled = (int)(iLo + u * (iHi - iLo) + 0.5f);
		return this.JitteredDelay(scaled, r);
	}

	private int JitteredDelay(int baseDelay, MersenneTwister r)
	{
		var j = this.info.AttackWaveCooldownJitterTicks;
		if (j > 0)
			baseDelay += r.Next(-j, j + 1);

		return Math.Max(1, baseDelay);
	}

	private static bool IsCombatSquadUnit(Actor a)
	{
		if (a.Info.Name[0] == '^')
			return false;
		if (a.Info.HasTraitInfo<BuildingInfo>())
			return false;
		if (!a.Info.HasTraitInfo<MobileInfo>())
			return false;
		if (a.Info.HasTraitInfo<BaseBuildingInfo>() && a.Info.HasTraitInfo<MobileInfo>())
			return false;
		if (a.Info.HasTraitInfo<DeploysOnActorInfo>())
			return false;
		if (a.Info.TraitInfoOrDefault<TankerInfo>() != null)
			return false;
		if (!a.Info.HasTraitInfo<AttackBaseInfo>())
			return false;
		if (!HasAttackMoveForOrders(a.Info) || !a.Info.HasTraitInfo<AutoTargetInfo>())
			return false;

		return true;
	}

	/// <summary>AttackMoveInfo in the engine assembly is not visible to the mod; we match the trait by runtime type name.</summary>
	private static bool HasAttackMoveForOrders(ActorInfo info) =>
		info.TraitsInConstructOrder().Any(t => t.GetType().Name == "AttackMoveInfo");

	private Target PickAttackTarget(Player selfPlayer, Player enemy, Actor[] squad)
	{
		var refPoint = squad.FirstOrDefault(a => a.OccupiesSpace != null)?.CenterPosition
			?? this.world.Map.CenterOfCell(selfPlayer.HomeLocation);
		var shroud = selfPlayer.Shroud;

		// Some actors (no IOccupySpace) cannot report CenterPosition; skip them to avoid NRE on Actor.CenterPosition.
		var enemyActors = this.world.Actors
			.Where(
				a => a.OccupiesSpace != null
					&& a.Owner == enemy
					&& a.IsInWorld
					&& !a.IsDead
					&& shroud.IsVisible(a.CenterPosition)
			)
			.ToList();

		if (enemyActors.Count > 0)
		{
			var building = enemyActors
				.Where(a => a.Info.HasTraitInfo<BuildingInfo>() || a.Info.TraitInfos<ProvidesPrerequisiteInfo>().Any())
				.ClosestToIgnoringPath(refPoint);
			if (building != null)
				return Target.FromActor(building);

			return Target.FromActor(enemyActors.ClosestToIgnoringPath(refPoint));
		}

		// No direct vision: move toward the enemy start location
		if (this.world.Map.Contains(enemy.HomeLocation))
			return Target.FromCell(this.world, enemy.HomeLocation);

		return Target.Invalid;
	}
}
