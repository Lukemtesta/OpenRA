#region Copyright & License Information
/*
 * Copyright 2007-2012 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Traits;
using OpenRA.FileFormats;
using OpenRA.Mods.RA.Activities;
using OpenRA.Mods.RA.Move;
using OpenRA.Mods.RA.Buildings;

namespace OpenRA.Mods.RA.Missions
{
	class MonsterTankMadnessScriptInfo : ITraitInfo, Requires<SpawnMapActorsInfo>
	{
		public readonly string[] FirstStartUnits = null;
		public readonly string[] SecondStartUnits = null;
		public readonly string[] ThirdStartUnits = null;
		public readonly string[] FirstBaseUnits = null;
		public readonly string[] CivilianEvacuees = null;

		public object Create(ActorInitializer init) { return new MonsterTankMadnessScript(this); }
	}

	class MonsterTankMadnessScript : IHasObjectives, IWorldLoaded, ITick
	{
		MonsterTankMadnessScriptInfo info;

		public MonsterTankMadnessScript(MonsterTankMadnessScriptInfo info)
		{
			this.info = info;
		}

		public event Action<bool> OnObjectivesUpdated = notify => { };

		public IEnumerable<Objective> Objectives { get { return objectives.Values; } }

		Dictionary<int, Objective> objectives = new Dictionary<int, Objective>
		{
			{ BriefingID, new Objective(ObjectiveType.Primary, Briefing, ObjectiveStatus.InProgress) }
		};

		const int BriefingID = 0;
		const string Briefing = "Dr. Demitri, creator of a Soviet Super Tank, wants to defect."
							+ " We planned to extract him while the Soviets were testing their new weapon, but something has gone wrong."
							+ " The Super Tanks are out of control, and Demitri is missing -- likely hiding in the village to the far south."
							+ " Find our outpost and start repairs on it, then find and evacuate Demitri."
							+ " As for the tanks, we can reprogram them. Send a spy into the Soviet radar dome in the NE, turning the tanks on their creators.";

		World world;

		Player neutral;
		Player greece;
		Player ussr;
		Player turkey;

		Actor startEntryPoint;
		Actor startMovePoint;
		Actor startBridgeEndPoint;
		Actor alliedBaseTopLeft;
		Actor alliedBaseBottomRight;
		Actor alliedBaseProc;
		Actor alliedBaseEntryPoint;
		Actor alliedBaseMovePoint;

		Actor demitriChurch;
		Actor demitriChurchSpawnPoint;
		Actor demitriTriggerAreaCenter;
		Actor demitri;
		Actor demitriLZ;
		Actor demitriLZFlare;
		Actor demitriChinook;

		Actor provingGroundsCameraPoint;

		Actor[] superTanks;

		Actor hospital;
		Actor hospitalCivilianSpawnPoint;
		Actor hospitalSuperTankPoint;

		bool demitriExtracted;
		bool hospitalEvacuated;

		bool superTanksAttackingGreece;

		int baseTransferredTick = -1;

		void MissionAccomplished(string text)
		{
			MissionUtils.CoopMissionAccomplished(world, text, greece);
		}

		void MissionFailed(string text)
		{
			MissionUtils.CoopMissionFailed(world, text, greece);
		}

		public void Tick(Actor self)
		{
			if (greece.WinState != WinState.Undefined) return;

			if (world.FrameNumber == 1)
				SpawnAndMoveBridgeUnits(info.FirstStartUnits);

			else if (world.FrameNumber == 25 * 3)
				SpawnAndMoveBridgeUnits(info.SecondStartUnits);

			else if (world.FrameNumber == 25 * 8)
				SpawnAndMoveBridgeUnits(info.ThirdStartUnits);

			MissionUtils.CapOre(ussr);

			if (!hospitalEvacuated && !hospital.IsDead() && MissionUtils.AreaSecuredWithUnits(world, greece, hospital.CenterLocation, 5))
			{
				EvacuateCivilians();
				hospitalEvacuated = true;
			}

			if (baseTransferredTick == -1)
			{
				var actorsInBase = world.FindUnits(alliedBaseTopLeft.CenterLocation, alliedBaseBottomRight.CenterLocation).Where(a => !a.IsDead() && a.IsInWorld);
				if (actorsInBase.Any(a => a.Owner == greece))
				{
					foreach (var actor in actorsInBase)
						TransferActorToAllies(actor);
					baseTransferredTick = world.FrameNumber;
				}
			}
			else
			{
				if (world.FrameNumber == baseTransferredTick + 25 * 120)
					foreach (var tank in superTanks.Where(t => !t.IsDead() && t.IsInWorld))
						tank.QueueActivity(false, new Move.Move(hospitalSuperTankPoint.Location, 2));

				else if (world.FrameNumber == baseTransferredTick + 25 * 200)
					foreach (var tank in superTanks.Where(t => !t.IsDead() && t.IsInWorld))
						tank.QueueActivity(false, new Move.Move(alliedBaseBottomRight.Location, 2));

				else if (world.FrameNumber == baseTransferredTick + 25 * 260)
					foreach (var tank in superTanks.Where(t => !t.IsDead() && t.IsInWorld))
						tank.QueueActivity(false, new Move.Move(demitriTriggerAreaCenter.Location, 2));

				else if (world.FrameNumber == baseTransferredTick + 25 * 500)
					foreach (var tank in superTanks.Where(t => !t.IsDead() && t.IsInWorld))
					{
						tank.QueueActivity(false, new AttackMove.AttackMoveActivity(tank, new Move.Move(demitriLZ.Location, 2)));
						superTanksAttackingGreece = true;
					}

				if (superTanksAttackingGreece)
					foreach (var tank in superTanks.Where(t => !t.IsDead() && t.IsInWorld && t.IsIdle))
						AttackNearestAlliedActor(tank);
			}
			if (!demitriExtracted)
			{
				if (demitri == null)
				{
					if (demitriChurch.IsDead())
					{
						objectives[BriefingID].Status = ObjectiveStatus.Failed;
						OnObjectivesUpdated(true);
						MissionFailed("Dr. Demitri was killed.");
					}

					else if (MissionUtils.AreaSecuredWithUnits(world, greece, demitriTriggerAreaCenter.CenterLocation, 3))
					{
						demitri = world.CreateActor("demitri", greece, demitriChurchSpawnPoint.Location, null);
						demitri.QueueActivity(new Move.Move(demitriTriggerAreaCenter.Location, 0));
						demitriLZFlare = world.CreateActor("flare", greece, demitriLZ.Location, null);
						Sound.Play("flaren1.aud");
						var chinookEntry = new CPos(demitriLZ.Location.X, 0);
						demitriChinook = MissionUtils.ExtractUnitWithChinook(world, greece, demitri, chinookEntry, demitriLZ.Location, chinookEntry);
					}
				}
				else if (demitri.IsDead())
				{
					objectives[BriefingID].Status = ObjectiveStatus.Failed;
					OnObjectivesUpdated(true);
					MissionFailed("Dr. Demitri was killed.");
				}
				else if (demitriChinook != null && !demitriChinook.IsDead() && !world.Map.IsInMap(demitriChinook.Location) && demitriChinook.Trait<Cargo>().Passengers.Contains(demitri))
				{
					demitriLZFlare.Destroy();
					SpawnAndMoveAlliedBaseUnits(info.FirstBaseUnits);
					demitriExtracted = true;
				}
			}
			if (!world.Actors.Any(a => a.Owner == greece && a.IsInWorld && !a.IsDead()
				&& ((a.HasTrait<Building>() && !a.HasTrait<Wall>()) || a.HasTrait<BaseBuilding>())))
			{
				objectives[BriefingID].Status = ObjectiveStatus.Failed;
				OnObjectivesUpdated(true);
				MissionFailed("The remaining Allied forces in the area have been wiped out.");
			}
		}

		void AttackNearestAlliedActor(Actor self)
		{
			var enemies = world.Actors.Where(u => u.AppearsHostileTo(self) && u.Owner == greece
					&& ((u.HasTrait<Building>() && !u.HasTrait<Wall>()) || u.HasTrait<Mobile>()) && u.IsInWorld && !u.IsDead());

			var enemy = enemies.OrderBy(u => (self.CenterLocation - u.CenterLocation).LengthSquared).FirstOrDefault();
			if (enemy != null)
				self.QueueActivity(new AttackMove.AttackMoveActivity(self, new Attack(Target.FromActor(enemy), 3)));
		}

		void TransferActorToAllies(Actor actor)
		{
			// hack hack hack
			actor.ChangeOwner(greece);
			if (actor.Info.Name == "pbox")
			{
				actor.AddTrait(new TransformedAction(s => s.Trait<Cargo>().Load(s, world.CreateActor(false, "e1", greece, null, null))));
				actor.QueueActivity(new Transform(actor, "hbox.e1") { SkipMakeAnims = true });
			}
			else if (actor.Info.Name == "proc.nofreeactor")
				actor.QueueActivity(new Transform(actor, "proc") { SkipMakeAnims = true });
			var building = actor.TraitOrDefault<Building>();
			if (building != null)
				building.OnCapture(actor, actor, neutral, greece);
		}

		void EvacuateCivilians()
		{
			foreach (var unit in info.CivilianEvacuees)
			{
				var actor = world.CreateActor(unit, neutral, hospitalCivilianSpawnPoint.Location, null);
				actor.Trait<Mobile>().Nudge(actor, actor, true);
				actor.QueueActivity(new Move.Move(alliedBaseEntryPoint.Location, 0));
				actor.QueueActivity(new RemoveSelf());
			}
		}

		void SpawnAndMoveBridgeUnits(string[] units)
		{
			Sound.Play("reinfor1.aud");
			foreach (var unit in units)
				world.CreateActor(unit, greece, startEntryPoint.Location, Util.GetFacing(startBridgeEndPoint.CenterLocation - startEntryPoint.CenterLocation, 0))
				.QueueActivity(new Move.Move(startMovePoint.Location, 0));
		}

		void SpawnAndMoveAlliedBaseUnits(string[] units)
		{
			Sound.Play("reinfor1.aud");
			foreach (var unit in units)
				world.CreateActor(unit, greece, alliedBaseEntryPoint.Location, Util.GetFacing(alliedBaseMovePoint.CenterLocation - alliedBaseEntryPoint.CenterLocation, 0))
				.QueueActivity(new Move.Move(alliedBaseMovePoint.Location, 0));
		}

		public void WorldLoaded(World w)
		{
			world = w;

			neutral = w.Players.Single(p => p.InternalName == "Neutral");
			greece = w.Players.Single(p => p.InternalName == "Greece");
			ussr = w.Players.Single(p => p.InternalName == "USSR");
			turkey = w.Players.Single(p => p.InternalName == "Turkey");

			greece.PlayerActor.Trait<PlayerResources>().Cash = 0;
			ussr.PlayerActor.Trait<PlayerResources>().Cash = 2000;

			var actors = w.WorldActor.Trait<SpawnMapActors>().Actors;
			startEntryPoint = actors["StartEntryPoint"];
			startMovePoint = actors["StartMovePoint"];
			startBridgeEndPoint = actors["StartBridgeEndPoint"];
			alliedBaseTopLeft = actors["AlliedBaseTopLeft"];
			alliedBaseBottomRight = actors["AlliedBaseBottomRight"];
			alliedBaseProc = actors["AlliedBaseProc"];
			alliedBaseEntryPoint = actors["AlliedBaseEntryPoint"];
			alliedBaseMovePoint = actors["AlliedBaseMovePoint"];

			demitriChurch = actors["DemitriChurch"];
			demitriChurchSpawnPoint = actors["DemitriChurchSpawnPoint"];
			demitriTriggerAreaCenter = actors["DemitriTriggerAreaCenter"];
			demitriLZ = actors["DemitriLZ"];

			hospital = actors["Hospital"];
			hospitalCivilianSpawnPoint = actors["HospitalCivilianSpawnPoint"];
			hospitalSuperTankPoint = actors["HospitalSuperTankPoint"];

			superTanks = actors.Values.Where(a => a.Info.Name == "5tnk" && a.Owner == turkey).ToArray();

			provingGroundsCameraPoint = actors["ProvingGroundsCameraPoint"];
			world.CreateActor("camera", greece, provingGroundsCameraPoint.Location, null);

			Game.MoveViewport(startEntryPoint.Location.ToFloat2());
			MissionUtils.PlayMissionMusic();
		}
	}
}