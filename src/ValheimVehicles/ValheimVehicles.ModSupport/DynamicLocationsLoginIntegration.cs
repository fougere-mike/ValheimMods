using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx;
using ValheimVehicles.Components;
using DynamicLocations.API;
using DynamicLocations.Config;
using DynamicLocations.Constants;
using DynamicLocations.Controllers;
using DynamicLocations.Interfaces;
using DynamicLocations.Structs;
using JetBrains.Annotations;
using Jotunn;
using UnityEngine;
using UnityEngine.UI;
using ValheimVehicles.BepInExConfig;
using ValheimVehicles.Controllers;
using ValheimVehicles.RPC;
using ValheimVehicles.Shared.Constants;
using ZdoWatcher;
using Zolantris.Shared.Debug;
using Logger = Jotunn.Logger;

namespace ValheimVehicles.ModSupport;

[UsedImplicitly]
public class DynamicLocationsLoginIntegration : DynamicLoginIntegration
{
  /// <inheritdoc />
  public DynamicLocationsLoginIntegration(IntegrationConfig config) :
    base(config)
  {
  }

  protected override IEnumerator OnLoginMoveToZDO(ZDO zdo, Vector3? offset,
    PlayerSpawnController playerSpawnController)
  {
    var localTimer = Stopwatch.StartNew();
    var onboardData =
      VehicleOnboardController.GetOnboardCharacterData(Player.m_localPlayer);

    // character is already onboard a vehicle. We assume it's the same one...
    if (onboardData != null)
    {
      if (onboardData.OnboardController)
        yield break;
    }

    yield return playerSpawnController.MovePlayerToZdo(zdo, offset, true, true);

    var vehicle = GetVehicleFromZdo(zdo);
    while (vehicle == null &&
           localTimer.ElapsedMilliseconds < 5000)
    {
      yield return new WaitForFixedUpdate();
      vehicle = GetVehicleFromZdo(zdo);
    }

    if (vehicle == null)
    {
      if (Player.m_localPlayer.IsDebugFlying())
      {
        Player.m_localPlayer.ToggleDebugFly();
      }

      yield break;
    }

    yield return new WaitUntil(() => vehicle.Instance != null && vehicle.Instance.PiecesController != null && (
                                       vehicle.Instance.PiecesController.isInitialPieceActivationComplete ||
                                       vehicle.Instance.PiecesController.IsActivationComplete) ||
                                     localTimer.ElapsedMilliseconds > 2000);

    // Restore the player to the EXACT boat-relative spot where they logged off (acceptance #1).
    // The stored offset is relative to the vehicle pieces transform, so TransformPoint reconstructs
    // the world position even if the boat has moved or rotated since logout.
    var vpc = vehicle.PiecesController;
    var player = Player.m_localPlayer;
    if (vpc != null && player != null && offset.HasValue)
    {
      var worldPos = vpc.transform.TransformPoint(offset.Value);
      player.transform.position = worldPos;
      player.transform.SetParent(vpc.transform);
      if (player.m_body != null)
      {
        player.m_body.velocity = Vector3.zero;
        player.m_body.angularVelocity = Vector3.zero;
      }

      // Keep the server/zdo in sync so the player isn't streamed back to the pre-teleport position.
      playerSpawnController.SyncPlayerPosition(worldPos);
    }
    else if (ModSupportConfig.DynamicLocationLoginMovesPlayerToBed.Value)
    {
      // Fallback only (no stored standing offset): snap to a bed on the ship.
      MovePlayerToBedOnShip(vehicle);
    }

    if (player != null)
    {
      if (player.IsDebugFlying()) player.ToggleDebugFly();
      if (player.m_body != null) player.m_body.isKinematic = false;
    }

    var isActivationComplete = vehicle.Instance.PiecesController != null && vehicle.Instance.PiecesController.IsActivationComplete;

    Logger.LogDebug(
      $"Waiting completed, IsActivationComplete {isActivationComplete} timer: {localTimer.ElapsedMilliseconds}");
  }

  private static void PlayerMoveToTransformSafe(Transform bedTransform)
  {
    if (Player.m_localPlayer == null) return;
    var offset = bedTransform.position + Vector3.up * 1.5f;

    if (PlayerSpawnController.Instance != null)
    {
      PlayerSpawnController.Instance.DynamicTeleport(offset,
        Player.m_localPlayer.transform.rotation);
    }
  }

  private bool MovePlayerToBedOnShip(VehicleManager vehicle)
  {
    if (vehicle.PiecesController == null || Player.m_localPlayer == null) return false;
    var bedPieces = vehicle.PiecesController.GetBedPieces();
    if (bedPieces.Count < 1) return false;
    if (bedPieces.Count == 1)
    {
      PlayerMoveToTransformSafe(bedPieces[0].transform);
      return true;
    }

    var bedPlacedByPlayer = bedPieces.Find((p) => p.IsMine());
    var selectedPiece = bedPlacedByPlayer != null
      ? bedPlacedByPlayer
      : bedPieces.First();
    if (selectedPiece == null) return false;

    // Use the LIVE bed transform. Beds already sync their ZDO position to world position
    // (UpdateBedPieces), so adding the stored MBPositionHash offset on top double-counted and
    // placed the player off the bed.
    PlayerMoveToTransformSafe(selectedPiece.transform);
    return true;
  }

  // Internal Methods

  private VehicleManager? GetVehicleFromZdo(ZDO zdo)
  {
    var vehicleShipNetView = ZNetScene.instance.FindInstance(zdo);
    if (!vehicleShipNetView) return null;
    var vehicleShip = vehicleShipNetView.GetComponent<VehicleManager>();
    return vehicleShip;
  }

  /// <summary>
  /// Death-respawn finalizer for a BED zdo on a vehicle (assigned to
  /// <see cref="PlayerSpawnController.OnSpawnMoveToVehicle" /> in ValheimRaftPlugin), used as the
  /// Strategy B fallback when the boat could not be streamed in before the spawn. Coarse-places the
  /// player via MovePlayerToZdo, then hands off to <see cref="HoldAndLoadThenOnboard" /> to fully
  /// stream the boat in and onboard the player before releasing control.
  /// </summary>
  public static IEnumerator OnSpawnMoveToVehicleZdo(ZDO zdo, Vector3? offset,
    PlayerSpawnController playerSpawnController)
  {
    // Coarse placement + freeze. MovePlayerToZdo stream-chases a moving/far boat until its instance
    // loads, leaving the player roughly on the bed.
    yield return playerSpawnController.MovePlayerToZdo(zdo, offset, true, true);

    // Then hold the player frozen + onboarded and fully stream the boat in before releasing them.
    yield return HoldAndLoadThenOnboard(zdo, playerSpawnController);
  }

  private static void UnfreezeSpawnedPlayer(Player? player)
  {
    if (player == null) return;
    if (player.IsDebugFlying()) player.ToggleDebugFly();
    if (player.m_body != null) player.m_body.isKinematic = false;
  }

  /// <summary>
  /// Strategy B onboard step (assigned to <see cref="PlayerSpawnController.OnSpawnOnboardToVehicle" />).
  /// Vanilla FindSpawnPoint already spawned the player ON the live bed; hold them there and fully
  /// stream the boat in before releasing control.
  /// </summary>
  public static IEnumerator OnSpawnOnboardToVehicleZdo(ZDO bedZdo,
    PlayerSpawnController playerSpawnController)
  {
    yield return HoldAndLoadThenOnboard(bedZdo, playerSpawnController);
  }

  /// <summary>
  /// Shared respawn finalizer: hold the player FROZEN on the live bed and ONBOARDED to the boat while
  /// actively driving it to fully stream + activate (force-send pieces, re-centre the streaming
  /// reference on the boat's live position, re-run piece activation) — exactly like standing on a boat
  /// right after a relog. The freeze is released only once a REAL deck collider exists under the bed
  /// for a few consecutive frames (<see cref="HasSolidFloorUnderBed" />), never the lying
  /// <c>vpc.IsActivationComplete</c> (which "completes" with whatever ~15% had streamed in). On
  /// timeout it releases anyway, so the player is never trapped frozen on the death screen.
  ///
  /// Onboarding immediately is load-bearing: <see cref="VehiclePiecesController.NonOwnerSync" /> drives
  /// the streaming reference from the onboarded local player, so once onboard the boat keeps loading
  /// around the player even as it sails — the same condition that makes a relog load the whole boat.
  /// </summary>
  private static IEnumerator HoldAndLoadThenOnboard(ZDO bedZdo,
    PlayerSpawnController playerSpawnController)
  {
    var player = Player.m_localPlayer;
    if (player == null) yield break;

    // Freeze immediately so a moving boat / partially-loaded deck cannot drag the player off or drop
    // them into the water before the boat finishes loading. Released on every exit path below.
    if (player.m_body != null) player.m_body.isKinematic = true;

    var timer = Stopwatch.StartNew();
    const long maxWaitMs = 25000; // relog-like full-load budget for big / far boats
    VehiclePiecesController? vpc = null;
    Bed? bed = null;
    var solidFrames = 0;
    var logged = false;

    while (timer.ElapsedMilliseconds < maxWaitMs)
    {
      var nv = ZNetScene.instance != null
        ? ZNetScene.instance.FindInstance(bedZdo)
        : null;
      if (nv != null)
      {
        if (bed == null) bed = nv.GetComponent<Bed>();
        vpc = VehiclePiecesController.GetVehiclePiecesController(nv.gameObject);
      }

      // Keep driving the boat to stream + activate (idempotent; throttled internally).
      GetVehicleLiveStreamPositionForBed(bedZdo);

      if (vpc != null)
      {
        // Onboard so the engine carries the player AND keeps the boat loaded (NonOwnerSync centres the
        // streaming reference on the onboarded player). A bare SetParent is undone next frame by
        // Character_Patch.UpdateGroundContact, so register onboard.
        if (vpc.OnboardController != null)
          vpc.OnboardController.TryAddPlayerIfMissing(player);
        else
          player.transform.SetParent(vpc.transform);

        // Re-run activation for any pieces that have instantiated but not yet activated.
        vpc.StartActivatePendingPieces();

        // Hold the player exactly on the live bed each frame while we wait.
        var holdPos = (bed != null ? bed.GetSpawnPoint() : vpc.transform.position) +
                      Vector3.up * DynamicLocationsConfig.RespawnHeightOffset.Value;
        player.transform.position = holdPos;
        if (player.m_body != null)
        {
          player.m_body.velocity = Vector3.zero;
          player.m_body.angularVelocity = Vector3.zero;
        }

        if (nv != null && HasSolidFloorUnderBed(nv, vpc))
        {
          if (++solidFrames >= 10) break; // deck under the bed is solid + stable — safe to release
        }
        else
        {
          solidFrames = 0;
        }
      }

      if (!logged && timer.ElapsedMilliseconds > 3000)
      {
        logged = true;
        Logger.LogInfo(
          $"[Respawn] still streaming boat for bed {bedZdo?.m_uid}; vpc={vpc != null} pieces={CountRegisteredPieces(vpc)} solidFrames={solidFrames}");
      }

      yield return new WaitForFixedUpdate();
    }

    // Final placement on the live bed + onboard, then release the freeze.
    if (vpc != null)
    {
      var nv = ZNetScene.instance != null
        ? ZNetScene.instance.FindInstance(bedZdo)
        : null;
      if (bed == null && nv != null) bed = nv.GetComponent<Bed>();
      var worldPos = (bed != null ? bed.GetSpawnPoint() : vpc.transform.position) +
                     Vector3.up * DynamicLocationsConfig.RespawnHeightOffset.Value;
      player.transform.position = worldPos;

      if (vpc.OnboardController != null)
        vpc.OnboardController.TryAddPlayerIfMissing(player);
      else
        player.transform.SetParent(vpc.transform);

      playerSpawnController.SyncPlayerPosition(worldPos);

      Logger.LogInfo(
        $"[Respawn] released player onto boat (pieces={CountRegisteredPieces(vpc)}, solidFloor={(nv != null && HasSolidFloorUnderBed(nv, vpc))}, waited {timer.ElapsedMilliseconds}ms).");
    }
    else
    {
      Logger.LogWarning(
        $"[Respawn] boat for bed {bedZdo?.m_uid} never streamed in within {timer.ElapsedMilliseconds}ms; releasing player at coarse position.");
    }

    UnfreezeSpawnedPlayer(player);
    if (player.m_body != null)
    {
      player.m_body.velocity = Vector3.zero;
      player.m_body.angularVelocity = Vector3.zero;
    }
  }

  // Per-vehicle throttle state for the stream driver.
  private static int _streamVehicleId;
  private static int _streamTick;

  /// <summary>
  /// Drives the bed's parent VEHICLE to stream in and returns its LIVE world position (assigned to
  /// <see cref="PlayerSpawnController.GetVehicleLiveStreamPosition" />). Each call (throttled
  /// internally): re-requests the vehicle's own ZDO from the server (its position is updated every
  /// frame by the driver — the freshest "where is the boat now"), bulk force-sends every piece to that
  /// live position like a relog, and re-runs piece activation. Returns null until the vehicle ZDO is
  /// known, so the resolver falls back to the bed ZDO position.
  ///
  /// This is the fix for the moving-boat deadlock: the bed ZDO's own position is stale until the boat's
  /// pieces activate (UpdateBedPieces only runs once active), so centring the streaming reference there
  /// loads empty water while the real boat sails away. Centring on the vehicle ZDO follows the boat.
  /// </summary>
  public static Vector3? GetVehicleLiveStreamPositionForBed(ZDO bedZdo)
  {
    if (bedZdo == null || ZNet.instance == null ||
        ZdoWatchController.Instance == null) return null;

    var vehicleId = VehiclePiecesController.GetParentID(bedZdo);
    if (vehicleId == 0) return null;

    if (vehicleId != _streamVehicleId)
    {
      _streamVehicleId = vehicleId;
      _streamTick = 0;
    }
    var tick = _streamTick++;

    // ~every 0.5s ask the server to push back the authoritative live vehicle position (RequestZdoFromServer
    // alone proved unreliable for refreshing a far/unowned ZDO's position). Force-send the PIECES only
    // while the boat is not yet loaded — once it has a live instance, re-sending all the pieces every
    // frame just churns the piece controller and it never settles into a solid deck.
    var loaded = VehiclePiecesController.ActiveInstances.ContainsKey(vehicleId);
    if (tick % 25 == 0)
    {
      VehiclePieceSyncRPC.Request(vehicleId, includePieces: !loaded);
      ZdoWatchController.Instance.RequestZdoFromServer(vehicleId);
    }

    // Re-run activation for pieces that have instantiated but not yet activated.
    if (ZNetScene.instance != null)
    {
      var bedNv = ZNetScene.instance.FindInstance(bedZdo);
      if (bedNv != null)
      {
        var vpc = VehiclePiecesController.GetVehiclePiecesController(bedNv.gameObject);
        if (vpc != null) vpc.StartActivatePendingPieces();
      }
    }

    // Resolve the boat's LIVE position. ORDER MATTERS:
    //  1. the SERVER-PUSHED authoritative position — it always advances with the moving boat.
    //  2. a loaded VPC instance — ONLY as a fallback. A loaded client instance is NOT reliable for a
    //     fast boat: once the streaming reference centres on it, the non-owned replica stops receiving
    //     the owner's position updates and FREEZES, stranding the reference behind the real (still
    //     sailing) boat — observed in the logs as `instance` stuck at one spot while `server-push` kept
    //     advancing, which deadlocked streaming and timed the resolver out.
    //  3. the centroid of the most-populated zone among the piece ZDOs (client-only; can be wrong
    //     mid-move when pieces straddle two zones).
    //  4. the vehicle's own ZDO position (often stale).
    Vector3? chosen = null;
    var source = "none";
    if (VehiclePieceSyncRPC.TryGetServerVehiclePosition(vehicleId,
          out var serverPos))
    {
      chosen = serverPos;
      source = "server-push";
    }
    else if (VehiclePiecesController.ActiveInstances.TryGetValue(vehicleId,
               out var inst) && inst != null)
    {
      chosen = inst.transform.position;
      source = "instance";
    }
    else if (TryGetPieceCloudPosition(vehicleId, out var cloud, out _))
    {
      chosen = cloud;
      source = "piece-cloud";
    }
    else
    {
      var vehicleZdo = ZdoWatchController.Instance.GetZdo(vehicleId);
      if (vehicleZdo != null)
      {
        chosen = vehicleZdo.GetPosition();
        source = "vehicle-zdo";
      }
    }

    if (tick % 25 == 0)
    {
      var hasServer =
        VehiclePieceSyncRPC.TryGetServerVehiclePosition(vehicleId, out var sp);
      var vz = ZdoWatchController.Instance.GetZdo(vehicleId);
      TryGetPieceCloudPosition(vehicleId, out var pc, out var pcCount);
      Logger.LogInfo(
        $"[Respawn] streamPos vehicle={vehicleId} -> {source}:{(chosen.HasValue ? chosen.Value.ToString() : "null")} | bed={bedZdo.GetPosition()} serverPush={(hasServer ? sp.ToString() : "-")} vehicleZdo={(vz != null ? vz.GetPosition().ToString() : "-")} pieceCloud={pc}(n={pcCount}) instanceLoaded={VehiclePiecesController.ActiveInstances.ContainsKey(vehicleId)}");
    }

    return chosen;
  }

  /// <summary>
  /// Client-only estimate of where the boat is, from the piece ZDOs the client already holds. Buckets
  /// the piece positions by zone and returns the centroid of the most-populated zone, so a handful of
  /// stale outliers (e.g. a bed ZDO whose position never refreshed) don't drag the result off the boat.
  /// </summary>
  private static bool TryGetPieceCloudPosition(int vehicleId, out Vector3 pos,
    out int pieceCount)
  {
    pos = Vector3.zero;
    pieceCount = 0;
    if (ZoneSystem.instance == null) return false;
    if (!VehiclePiecesController.m_allPieces.TryGetValue(vehicleId,
          out var list) || list == null || list.Count == 0) return false;

    var zoneCounts = new Dictionary<Vector2i, int>();
    var zoneSums = new Dictionary<Vector2i, Vector3>();
    foreach (var zdo in list)
    {
      if (zdo == null || !zdo.IsValid()) continue;
      var p = zdo.GetPosition();
      var z = ZoneSystem.GetZone(p);
      zoneCounts.TryGetValue(z, out var c);
      zoneCounts[z] = c + 1;
      zoneSums.TryGetValue(z, out var s);
      zoneSums[z] = s + p;
      pieceCount++;
    }
    if (pieceCount == 0) return false;

    var bestCount = -1;
    var bestZone = default(Vector2i);
    foreach (var kv in zoneCounts)
      if (kv.Value > bestCount)
      {
        bestCount = kv.Value;
        bestZone = kv.Key;
      }

    pos = zoneSums[bestZone] / bestCount;
    return true;
  }

  private static int CountRegisteredPieces(VehiclePiecesController? vpc)
  {
    if (vpc == null) return 0;
    return VehiclePiecesController.m_allPieces.TryGetValue(vpc.PersistentZdoId,
      out var list) && list != null
      ? list.Count
      : 0;
  }

  /// <summary>
  /// Strategy B spawn-readiness gate (assigned to <see cref="PlayerSpawnController.IsVehicleSpawnReady" />).
  /// Ready only once a REAL (instantiated + activated) deck collider sits under the bed spawn point, so
  /// vanilla FindSpawnPoint places the player on a solid deck. m_allPieces.Count is NOT a readiness
  /// signal (it is only the ZDO registry, filled by the force-send regardless of whether pieces have
  /// instantiated), and vpc.IsActivationComplete lies (it "completes" at ~15%). The boat is streamed by
  /// <see cref="GetVehicleLiveStreamPositionForBed" /> (driven each frame from the resolver); this only
  /// reports readiness.
  /// </summary>
  private static int _vsrLogTick;

  public static bool IsVehicleSpawnReadyForBed(ZDO bedZdo)
  {
    if (ZNetScene.instance == null) return false;
    var vehicleId = VehiclePiecesController.GetParentID(bedZdo);
    var nv = ZNetScene.instance.FindInstance(bedZdo);
    var vpc = nv != null
      ? VehiclePiecesController.GetVehiclePiecesController(nv.gameObject)
      : null;
    VehiclePiecesController.ActiveInstances.TryGetValue(vehicleId,
      out var activeVpc);
    var useVpc = vpc ?? activeVpc;

    var floor = nv != null && useVpc != null && HasSolidFloorUnderBed(nv, useVpc);

    if (_vsrLogTick++ % 25 == 0)
    {
      var registered =
        VehiclePiecesController.m_allPieces.TryGetValue(vehicleId, out var rl) &&
        rl != null
          ? rl.Count
          : 0;
      var pending =
        VehiclePiecesController.m_pendingPieces.TryGetValue(vehicleId,
          out var pl) && pl != null
          ? pl.Count
          : 0;
      var activated = useVpc != null ? useVpc.m_pieces.Count : 0;
      Logger.LogInfo(
        $"[Respawn] ready? vehicle={vehicleId} bedInstance={nv != null} vpcLoaded={activeVpc != null} initState={(useVpc != null ? useVpc.BaseVehicleInitState.ToString() : "-")} activated={activated} pending={pending} registered={registered} actComplete={(useVpc != null && useVpc.IsActivationComplete)} initialActComplete={(useVpc != null && useVpc.isInitialPieceActivationComplete)} floor={floor}");
    }

    // Spawn as soon as the bed instance + its vehicle controller exist. Do NOT block on the solid
    // floor here: a far boat only finishes activating once the local player is ONBOARD it (same as a
    // relog / standing on it). HoldAndLoadThenOnboard onboards the player and holds them FROZEN on the
    // bed until a real deck exists, so it is safe to spawn before the floor is ready.
    return nv != null && useVpc != null;
  }

  private static bool HasSolidFloorUnderBed(ZNetView bedNv,
    VehiclePiecesController vpc)
  {
    var bed = bedNv.GetComponent<Bed>();
    var spawn = bed != null ? bed.GetSpawnPoint() : bedNv.transform.position;
    var hits = Physics.RaycastAll(spawn + Vector3.up * 1f, Vector3.down, 5f);
    foreach (var hit in hits)
    {
      if (hit.collider == null || hit.collider.isTrigger) continue;
      if (hit.collider.GetComponentInParent<VehiclePiecesController>() == vpc)
        return true;
    }

    return false;
  }
}