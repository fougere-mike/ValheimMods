using System;
using DynamicLocations.Config;
using DynamicLocations.Constants;
using UnityEngine;
using ZdoWatcher;
using Logger = Jotunn.Logger;

namespace DynamicLocations.Controllers;

/// <summary>
/// Strategy B for respawning on a moving boat. Instead of letting vanilla spawn the player and then
/// teleporting them onto the boat (a race against the netcode that fails for a far / continuously
/// moving boat), this makes vanilla <see cref="Game.FindSpawnPoint" /> itself spawn the player
/// directly on the LIVE boat bed.
///
/// Why it works where the post-spawn teleport did not: FindSpawnPoint runs BEFORE the player entity
/// exists, and is the only thing setting the stream reference position during respawn — so streaming
/// the (moving) boat in here has no competing reference, unlike the post-spawn chase. Vanilla's
/// <c>FindBedNearby</c> already returns <c>bed.GetSpawnPoint()</c> (the live transform) for a loaded
/// boat bed; the only missing piece for an unloaded/moving boat is streaming it in first.
///
/// The local player is DESTROYED during respawn, so the boat-bed persistent id (which lives on
/// <c>Player.m_customData</c>, not the profile) is captured on death via <see cref="BeginDeathRespawn" />.
/// Each frame <see cref="TryResolveDeathSpawn" /> re-requests the bed ZDO from the server (so a boat
/// another player is sailing keeps refreshing its position) and re-centres the reference on it, then
/// hands vanilla the live bed spawn point once the bed instance has streamed in. On timeout it returns
/// <see cref="Result.NotOurs" /> so vanilla takes over — the player is never trapped on the death
/// screen. When no boat-bed spawn is registered it is a no-op (pure vanilla respawn).
/// </summary>
public static class DynamicSpawnResolver
{
  public enum Result
  {
    /// Not a dynamic boat-bed respawn (or timed out) — run vanilla FindSpawnPoint.
    NotOurs,

    /// Streaming the boat in — keep the player waiting (FindSpawnPoint returns false this frame).
    Waiting,

    /// The live bed is ready — vanilla should spawn the player at the returned point.
    Ready
  }

  private static int _pendingBedPersistentId;
  private static float _elapsed;
  private static int _refreshTick;

  /// <summary>True once <see cref="TryResolveDeathSpawn" /> has placed the player on the live bed,
  /// so the spawn postfix onboards (parents to the boat) instead of running the legacy teleport.</summary>
  public static bool LastRespawnHandledByStrategyB { get; private set; }

  /// <summary>The resolved bed ZDO, handed to the onboard step after the spawn.</summary>
  public static ZDO? ResolvedBedZdo { get; private set; }

  /// <summary>
  /// Captures the dynamic boat-bed spawn target while the player is still alive (called from the
  /// Player.OnDeath patch) — it cannot be read during FindSpawnPoint because the player is destroyed.
  /// </summary>
  public static void BeginDeathRespawn(Player player)
  {
    Reset();
    if (player == null) return;
    var id = LocationController.GetZdoFromStore(LocationVariation.Spawn, player);
    _pendingBedPersistentId = id ?? 0;
    if (DynamicLocationsConfig.IsDebug && _pendingBedPersistentId != 0)
      Logger.LogDebug(
        $"[DynamicSpawnResolver] Captured boat-bed spawn id {_pendingBedPersistentId} for respawn.");
  }

  public static void Reset()
  {
    _pendingBedPersistentId = 0;
    _elapsed = 0f;
    _refreshTick = 0;
    ResolvedBedZdo = null;
    LastRespawnHandledByStrategyB = false;
  }

  /// <summary>Per-frame resolve, called from the Game.FindSpawnPoint prefix with the frame dt.</summary>
  public static Result TryResolveDeathSpawn(Game game, float dt, out Vector3 point)
  {
    point = Vector3.zero;
    if (_pendingBedPersistentId == 0) return Result.NotOurs;
    if (game == null || !game.m_respawnAfterDeath) return Result.NotOurs;
    if (!DynamicLocationsConfig.EnableDynamicSpawnPoint.Value) return Result.NotOurs;
    if (ZNet.instance == null || ZNetScene.instance == null ||
        ZoneSystem.instance == null || ZdoWatchController.Instance == null)
      return Result.NotOurs;

    _elapsed += dt;
    if (_elapsed > DynamicLocationsConfig.LocationControlsTimeoutInMs.Value / 1000f)
    {
      // Could not stream the boat in time (sailed too far / unreachable). Fall back to vanilla so the
      // player respawns at the normal spawn instead of hanging on the death screen.
      Logger.LogWarning(
        "[DynamicSpawnResolver] Timed out streaming the boat for respawn; falling back to vanilla spawn.");
      _pendingBedPersistentId = 0;
      return Result.NotOurs;
    }

    // ~every 0.5s ask the server to force-send the bed's current ZDO. The boat is owned by the player
    // driving it, so its live position only reaches us if we keep asking; this also pulls the ZDO in
    // when it was unloaded because the boat sailed out of range.
    if (_refreshTick++ % 25 == 0)
      ZdoWatchController.Instance.RequestZdoFromServer(_pendingBedPersistentId);

    var zdo = ZdoWatchController.Instance.GetZdo(_pendingBedPersistentId);
    if (zdo == null) return Result.Waiting; // waiting for the server to send the bed ZDO
    ResolvedBedZdo = zdo;

    // Stream the boat's LIVE zone in (re-centred every frame so it follows a moving boat).
    //
    // CRITICAL: centre the reference on the parent VEHICLE's live position, NOT the bed ZDO's own
    // position. A bed's ZDO position is only kept current once the boat's pieces have activated
    // (client-side UpdateBedPieces), which itself needs the boat streamed in — a deadlock for a moving
    // boat: the bed position is frozen wherever the boat last unloaded, so pinning the reference there
    // loads empty water while the real boat sails on and only the ~15% near the stale spot ever loads.
    // GetVehicleLiveStreamPosition (vehicle mod) returns the boat's main ZDO position — updated every
    // frame by the driver — and force-sends the pieces so they arrive at that live position.
    var livePos = zdo.GetPosition();
    if (PlayerSpawnController.GetVehicleLiveStreamPosition != null)
    {
      var boatPos = PlayerSpawnController.GetVehicleLiveStreamPosition(zdo);
      if (boatPos.HasValue) livePos = boatPos.Value;
    }
    ZNet.instance.SetReferencePosition(livePos);
    ZoneSystem.instance.PokeLocalZone(ZoneSystem.GetZone(livePos));

    if (!ZNetScene.instance.IsAreaReady(livePos)) return Result.Waiting;

    // Vehicle path (ValheimVehicles present): spawn through the VEHICLE, not the standalone bed
    // instance. A MOVING boat does not reliably instantiate the bed's own ZNetView — FindInstance(
    // bedZdo) stays null even though the vehicle is loaded — so the old bed-instance-gated path stalled
    // here forever and timed out. IsVehicleSpawnReady reports once the vehicle is loaded; the player is
    // then held FROZEN + ONBOARDED by the post-spawn finalizer until a real deck exists, so it is safe
    // to spawn before the floor is ready.
    if (PlayerSpawnController.GetVehicleSpawnPoint != null)
    {
      if (PlayerSpawnController.IsVehicleSpawnReady != null &&
          !PlayerSpawnController.IsVehicleSpawnReady(zdo))
        return Result.Waiting;
      var vehiclePoint = PlayerSpawnController.GetVehicleSpawnPoint(zdo);
      if (!vehiclePoint.HasValue) return Result.Waiting;
      point = vehiclePoint.Value;
      LastRespawnHandledByStrategyB = true;
      return Result.Ready;
    }

    // Legacy path (DynamicLocations standalone, no vehicle mod): require the bed instance.
    var instance = ZNetScene.instance.FindInstance(zdo);
    if (instance == null) return Result.Waiting;
    var bed = instance.GetComponent<Bed>();
    var basePos = bed != null ? bed.GetSpawnPoint() : instance.transform.position;
    point = basePos + Vector3.up * DynamicLocationsConfig.RespawnHeightOffset.Value;
    LastRespawnHandledByStrategyB = true;
    return Result.Ready;
  }
}
