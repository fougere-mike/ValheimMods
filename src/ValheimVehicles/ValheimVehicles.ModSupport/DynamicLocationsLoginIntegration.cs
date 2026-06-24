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
using ValheimVehicles.Shared.Constants;
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
  /// <see cref="PlayerSpawnController.OnSpawnMoveToVehicle" /> in ValheimRaftPlugin). The plain
  /// spawn path only set the player position once, so on a moving boat the player was left behind
  /// in the water. This mirrors the login finalizer (<see cref="OnLoginMoveToZDO" />) but keyed on
  /// the bed: coarse-place + stream-chase via MovePlayerToZdo, wait for the bed's vehicle pieces to
  /// activate, then place the player on the LIVE bed spawn point and ONBOARD them so the moving boat
  /// carries them (Character_Patch.UpdateGroundContact un-parents anyone not registered onboard, so
  /// a bare SetParent is undone next frame). The player is held kinematic across the wait so they
  /// cannot fall into the water, and is ALWAYS unfrozen on exit (never trapped on the death screen).
  /// </summary>
  public static IEnumerator OnSpawnMoveToVehicleZdo(ZDO zdo, Vector3? offset,
    PlayerSpawnController playerSpawnController)
  {
    var localTimer = Stopwatch.StartNew();

    // Coarse placement + freeze. MovePlayerToZdo now stream-chases a moving/far boat until its
    // instance loads, and leaves the player on the bed at a coarse position.
    yield return playerSpawnController.MovePlayerToZdo(zdo, offset, true, true);

    var player = Player.m_localPlayer;
    // Hold the player still while the vehicle pieces activate so a moving boat / the water cannot
    // drag them off before we do the final placement + onboard below.
    if (player != null && player.m_body != null) player.m_body.isKinematic = true;

    // Resolve the bed instance's vehicle pieces controller (retry while it streams in).
    VehiclePiecesController? vpc = null;
    while (vpc == null && localTimer.ElapsedMilliseconds < 5000)
    {
      var nv = ZNetScene.instance.FindInstance(zdo);
      if (nv != null)
        vpc = VehiclePiecesController.GetVehiclePiecesController(nv.gameObject);
      if (vpc == null) yield return new WaitForFixedUpdate();
    }

    if (vpc == null)
    {
      // Boat never streamed in (sank / destroyed / unreachable). Unfreeze and leave the player at
      // the coarse position — never trapped frozen on the death screen.
      UnfreezeSpawnedPlayer(player);
      yield break;
    }

    // Wait for the pieces to finish activating so the bed (and its live m_spawnPoint) is in place.
    yield return new WaitUntil(() =>
      vpc == null ||
      vpc.isInitialPieceActivationComplete || vpc.IsActivationComplete ||
      localTimer.ElapsedMilliseconds > 8000);

    if (vpc != null && player != null)
    {
      var bedNetView = ZNetScene.instance.FindInstance(zdo);
      var bed = bedNetView != null ? bedNetView.GetComponent<Bed>() : null;
      // Use the LIVE bed spawn point (it follows the moving boat). The stored Spawn offset is a
      // meaningless world-delta from SyncBedSpawnPoint, so it is intentionally not used here.
      var basePos = bed != null ? bed.GetSpawnPoint() : vpc.transform.position;
      var worldPos = basePos +
                     Vector3.up * DynamicLocationsConfig.RespawnHeightOffset.Value;
      player.transform.position = worldPos;

      // Onboard: parents the player to the pieces transform AND registers them onboard so the
      // parent sticks (a bare SetParent is undone by Character_Patch.UpdateGroundContact).
      if (vpc.OnboardController != null)
        vpc.OnboardController.TryAddPlayerIfMissing(player);
      else
        player.transform.SetParent(vpc.transform);

      if (player.m_body != null)
      {
        player.m_body.velocity = Vector3.zero;
        player.m_body.angularVelocity = Vector3.zero;
      }

      // Keep the server/zdo in sync so the player isn't streamed back to the pre-teleport position.
      playerSpawnController.SyncPlayerPosition(worldPos);
    }

    UnfreezeSpawnedPlayer(player);
  }

  private static void UnfreezeSpawnedPlayer(Player? player)
  {
    if (player == null) return;
    if (player.IsDebugFlying()) player.ToggleDebugFly();
    if (player.m_body != null) player.m_body.isKinematic = false;
  }

  /// <summary>
  /// Strategy B onboard step (assigned to <see cref="PlayerSpawnController.OnSpawnOnboardToVehicle" />
  /// in ValheimRaftPlugin). Vanilla FindSpawnPoint already spawned the player ON the live bed, so
  /// there is NO teleport here: resolve the bed's vehicle, refine the placement once pieces have
  /// activated, and onboard the player so the moving boat carries them
  /// (Character_Patch.UpdateGroundContact un-parents anyone not registered onboard).
  /// </summary>
  public static IEnumerator OnSpawnOnboardToVehicleZdo(ZDO bedZdo,
    PlayerSpawnController playerSpawnController)
  {
    var player = Player.m_localPlayer;
    if (player == null) yield break;

    // Freeze immediately: the player was spawned at the bed's world position, but the boat may be
    // moving, so an un-parented player would slide off the deck (or drop a frame) before we parent
    // them. Held kinematic until parented + re-placed below; released on every exit.
    if (player.m_body != null) player.m_body.isKinematic = true;

    var localTimer = Stopwatch.StartNew();

    // The boat was streamed AND its pieces activated before the spawn (the resolver gated on
    // IsVehicleSpawnReady), so the pieces controller should already exist; allow a brief retry.
    VehiclePiecesController? vpc = null;
    while (vpc == null && localTimer.ElapsedMilliseconds < 5000)
    {
      var nv = ZNetScene.instance.FindInstance(bedZdo);
      if (nv != null)
        vpc = VehiclePiecesController.GetVehiclePiecesController(nv.gameObject);
      if (vpc == null) yield return new WaitForFixedUpdate();
    }

    if (vpc != null)
    {
      yield return new WaitUntil(() =>
        vpc == null || vpc.IsActivationComplete ||
        localTimer.ElapsedMilliseconds > 8000);

      if (vpc != null && player != null)
      {
        // Refine onto the LIVE bed (it may have shifted while activating) and onboard so the parent
        // sticks (Character_Patch.UpdateGroundContact un-parents anyone not registered onboard).
        var bedNetView = ZNetScene.instance.FindInstance(bedZdo);
        var bed = bedNetView != null ? bedNetView.GetComponent<Bed>() : null;
        if (bed != null)
          player.transform.position = bed.GetSpawnPoint() +
                                      Vector3.up *
                                      DynamicLocationsConfig.RespawnHeightOffset.Value;

        if (vpc.OnboardController != null)
          vpc.OnboardController.TryAddPlayerIfMissing(player);
        else
          player.transform.SetParent(vpc.transform);

        playerSpawnController.SyncPlayerPosition(player.transform.position);
      }
    }

    // Always release the freeze (every exit path leads here).
    if (player != null && player.m_body != null)
    {
      player.m_body.isKinematic = false;
      player.m_body.velocity = Vector3.zero;
      player.m_body.angularVelocity = Vector3.zero;
    }
  }

  /// <summary>
  /// Strategy B spawn-readiness gate (assigned to <see cref="PlayerSpawnController.IsVehicleSpawnReady" />).
  /// True only once the bed's vehicle pieces are fully activated, so the player spawns onto a solid
  /// deck instead of falling through into the water while the boat is still streaming in.
  /// </summary>
  public static bool IsVehicleSpawnReadyForBed(ZDO bedZdo)
  {
    if (ZNetScene.instance == null) return false;
    var nv = ZNetScene.instance.FindInstance(bedZdo);
    if (!nv) return false;
    var vpc = VehiclePiecesController.GetVehiclePiecesController(nv.gameObject);
    if (vpc == null) return false;
    return vpc.IsActivationComplete;
  }
}