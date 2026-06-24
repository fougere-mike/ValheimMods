using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using DynamicLocations.Config;
using DynamicLocations.Constants;
using JetBrains.Annotations;
using UnityEngine;
using ZdoWatcher;
using Zolantris.Shared.Debug;
using Logger = Jotunn.Logger;

namespace DynamicLocations.Controllers;

/// <summary>
/// This component does the following
/// - Associates the player id with the vehicle id for logout point
/// - Associates the player id with the beds on a vehicle if they die
/// - Unsets / breaks association if player leaves the vehicle for logout point
/// - Unsets / breaks association if player interacts with a bed that is not within the vehicle
///
/// - Syncs all this data on demand to prevent spamming zdo apis.
/// </summary>
public class PlayerSpawnController : MonoBehaviour
{
  // mostly for debugging, this should not be kept other it will retain the logout point even when it's possible the point could be inaccurate.
  internal bool CanUpdateLogoutPoint = true;
  internal bool CanRemoveLogoutAfterSync = true;
  internal bool IsTeleportingToDynamicLocation = false;
  private bool IsRunningFindDynamicZdo = false;

  public static Dictionary<long, PlayerSpawnController> Instances = new();

  public static PlayerSpawnController? Instance;

  /// <summary>
  /// Assigned by the vehicle mod (ValheimVehicles) at init so DynamicLocations can detect whether a
  /// bed is on a moving vehicle WITHOUT referencing the vehicle assembly (keeps the dependency
  /// direction correct). Returns true when the bed is parented to a vehicle. When null/false the bed
  /// is treated as a land bed and dynamic spawn is skipped (pure vanilla spawn).
  /// </summary>
  public static Func<Bed, bool>? IsBedOnDynamicVehicle;

  /// <summary>
  /// Assigned by the vehicle mod (ValheimVehicles) so the death-respawn path can finish placement on
  /// a MOVING boat: it parents and onboards the player to the live vehicle (plain MovePlayerToZdo
  /// sets the position only once, so a moving boat slides out from under the player). A coroutine the
  /// spawn flow yields on. Fully qualified System.Func to avoid the single-arg Func delegate declared
  /// lower in this file. When null, DynamicLocations falls back to the plain teleport (graceful
  /// degradation when the vehicle mod is absent).
  /// </summary>
  public static System.Func<ZDO, Vector3?, PlayerSpawnController, IEnumerator>?
    OnSpawnMoveToVehicle;

  // internal Stopwatch UpdateLocationTimer = new();
  private static Player? player => Player.m_localPlayer;
  public static Coroutine? MoveToLogoutRoutine;
  public static Coroutine? MoveToSpawnRoutine;

  internal static List<DebugSafeTimer> Timers = [];

  private void Awake()
  {
    Setup();
  }

  private void Update()
  {
    DebugSafeTimer.UpdateTimersFromList(Timers);
  }

  public void DEBUG_MoveTo(LocationVariation locationVariationType)
  {
    CanUpdateLogoutPoint = true;
    CanRemoveLogoutAfterSync = false;
    switch (locationVariationType)
    {
      case LocationVariation.Spawn:
        Instance?.MovePlayerToSpawnPoint();
        break;
      case LocationVariation.Logout:
        Instance?.MovePlayerToLogoutPoint();
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(locationVariationType),
          locationVariationType, null);
    }
  }

  internal void Reset()
  {
    IsRunningFindDynamicZdo = false;
    IsTeleportingToDynamicLocation = false;
    CanUpdateLogoutPoint = true;
    CanRemoveLogoutAfterSync = true;
    MovePlayerToZdoComplete = true;

    ResetRoutine(ref MoveToLogoutRoutine);
    ResetRoutine(ref MoveToSpawnRoutine);
  }

  private bool MovePlayerToZdoComplete = false;

  // Events
  internal void OnMovePlayerToZdoComplete(bool success = false,
    string errorMessage = "OnMovePlayerToZdo exited but failed")
  {
    Reset();
    Timers.Clear();
    IsTeleportingToDynamicLocation = false;
    MovePlayerToZdoComplete = true;

    if (!success)
    {
      Logger.LogError(errorMessage);
    }
  }

  internal static void ResetRoutine(ref Coroutine? routine)
  {
    if (routine == null) return;
    Instance?.StopCoroutine(routine);
    routine = null;
  }

  private void OnDestroy()
  {
    Reset();
    Logger.LogDebug("Called onDestroy");
  }

  private void OnDisable()
  {
    Reset();
    StopAllCoroutines();
  }

  private void Setup()
  {
    Instance = this;
    if (player == null) return;

#if DEBUG
    Logger.LogDebug("listing all player custom keys");
    foreach (var key in player.m_customData.Keys)
    {
      Logger.LogDebug($"key: {key} val: {player.m_customData[key]}");
    }
#endif
  }

  /// <summary>
  /// Persists beds across sessions, requires the bed netview input
  /// </summary>
  /// <param name="zdo"></param>
  /// <param name="locationVariationType"></param>
  /// <returns></returns>
  public bool PersistDynamicPoint(ZDO zdo,
    LocationVariation locationVariationType, out int id)
  {
    id = 0;
    if (ZdoWatchController.Instance == null) return false;
    // Beds must be persisted when syncing spawns otherwise they cannot be retrieved directly across sessions / on server shutdown and would require a deep search of all objects.

    id = ZdoWatchController.Instance.GetOrCreatePersistentID(zdo);

    if (id == 0)
    {
      if (locationVariationType == LocationVariation.Spawn)
      {
        Logger.LogError(
          "No persistent ID returned for bed, this should not be possible. Please report this error");
        RemoveDynamicPoint(zdo, locationVariationType);
      }

      return false;
    }

    AddDynamicPoint(zdo, locationVariationType);
    return true;
  }

  // required for setting a persistent bed. Without this value set it will persist the ID but the actual item will not be known so if the bed is deleted during session it would not match
  public void AddDynamicPoint(ZDO zdo, LocationVariation locationVariationType)
  {
    zdo.Set(ZdoVarKeys.DynamicLocationsPoint, 1);
  }

  public void RemoveDynamicPoint(ZDO? zdo,
    LocationVariation locationVariationType)
  {
    LocationController.RemoveZdoTarget(locationVariationType,
      player);

#if DEBUG
    if (DynamicLocationsConfig.DEBUG_ShouldNotRemoveTargetKey.Value)
    {
      return;
    }
#endif
    zdo?.RemoveInt(ZdoVarKeys.DynamicLocationsPoint);
  }

  /// <summary>
  /// Clears any stored dynamic spawn (bed) point + offset for the current player. Called when the
  /// player sets their spawn at a NON-vehicle (land) bed, so a previously-registered vehicle bed
  /// does not linger and vanilla spawn mechanics take over completely.
  /// </summary>
  public void ClearDynamicSpawnPoint()
  {
    if (player == null) return;
    LocationController.RemoveZdoTarget(LocationVariation.Spawn, player);
  }

  /// <summary>
  /// Sets or removes the spawnPointZdo to the bed it is associated with a moving zdo
  /// - Only should be called when the bed is interacted with
  /// - This id is used to poke a zone and load it, then teleport the player to their bed like they are spawning
  /// </summary>
  /// <param name="zdo"></param>
  /// <param name="bed"></param>
  /// <returns>bool</returns>
  public bool SyncBedSpawnPoint(ZDO zdo, Bed bed)
  {
    // should sync the zdo just in case it doesn't match player
    if (player == null) return false;

    if (!bed.IsMine())
    {
      // exit b/c this is another player's bed, this should not set as a spawn
      return false;
    }

    PersistDynamicPoint(zdo, LocationVariation.Spawn, out _);

    var wasSuccessful = LocationController.SetLocationTypeData(
      LocationVariation.Spawn, player, zdo,
      bed.transform.position - player.transform.position);

    return wasSuccessful;
  }

  /// <summary>
  /// Must be called on logout, and should be fired optimistically to avoid desync if crashes happen.
  /// </summary>
  /// <returns>bool</returns>
  public bool SyncLogoutPoint(ZDO? zdo, bool shouldRemove = false,
    Vector3? localOffset = null)
  {
    if (ZNet.instance == null) return false;
    if (zdo == null && !shouldRemove)
    {
      Logger.LogError(
        "ZDO not found for netview, this likely means something is wrong with the area it is being called in");
      return false;
    }

    if (shouldRemove)
    {
      RemoveDynamicPoint(zdo, LocationVariation.Logout);
      Game.instance.m_playerProfile.SavePlayerData(player);
      return true;
    }

    if (player == null) return false;

    var isPersistent =
      PersistDynamicPoint(zdo, LocationVariation.Logout, out var id);
    if (!isPersistent)
    {
      Logger.LogDebug("vehicleZdoId is invalid");
      return false;
    }

    // Always (re)store the boat-relative standing offset. The offset is the player's position
    // relative to the vehicle pieces transform (computed by the caller at logout). Writing it
    // BEFORE the "already-stored zdo" check ensures re-logging-out on the same boat without moving
    // still refreshes the standing spot. We write it raw so a legitimate ~zero offset (standing at
    // the pieces origin) is preserved (LocationController.SetOffset scrubs Vector3.zero).
    var offsetToStore = localOffset ?? player.transform.localPosition;
    LocationController.SetLogoutOffsetRaw(player, offsetToStore);

    var storedPersistentZdo =
      LocationController.GetZdoFromStore(LocationVariation.Logout, player);
    if (storedPersistentZdo == id)
    {
      Logger.LogDebug(
        "Matching ZDOID already stored, refreshed offset, skipping zdo re-save");
      Game.instance.m_playerProfile.SavePlayerData(player);
      return true;
    }

    LocationController.SetZdo(LocationVariation.Logout, player, zdo);

    Game.instance.m_playerProfile.SavePlayerData(player);
    return true;
  }

  [UsedImplicitly]
  public bool DynamicTeleport(Vector3 position, Quaternion rotation)
  {
    if (player == null) return false;

    player.m_teleportCooldown = 15;
    player.m_teleporting = false;

    return player.TeleportTo(
      position,
      rotation,
      !DynamicLocationsConfig
        .DebugDisableDistancePortal.Value);
  }

  public void MovePlayerToLogoutPoint()
  {
    MoveToLogoutRoutine =
      StartCoroutine(UpdateLocation(LocationVariation.Logout));
  }

  /// <summary>
  /// Looks for the ZDO (mostly performant)
  /// </summary>
  /// <param name="locationVariationType"></param>
  /// <param name="onComplete"></param>
  /// <param name="shouldAdjustReferencePoint"></param>
  /// <returns></returns>
  public IEnumerator FindDynamicZdo(
    LocationVariation locationVariationType, Action<ZDO?> onComplete,
    bool shouldAdjustReferencePoint = false)
  {
    IsRunningFindDynamicZdo = true;

    ZDO? zdoOutput = null;
    yield return LocationController.GetZdoFromStoreAsync(locationVariationType,
      player,
      (output) => { zdoOutput = output; });

    onComplete(zdoOutput);

    if (shouldAdjustReferencePoint && ZNet.instance != null &&
        zdoOutput != null)
    {
      ZNet.instance.SetReferencePosition(zdoOutput.GetPosition());
    }

    IsRunningFindDynamicZdo = false;
  }

  private IEnumerator UpdateLocation(
    LocationVariation locationVariationType)
  {
    var timer = DebugSafeTimer.StartNew(Timers);

    IsTeleportingToDynamicLocation = false;

    var offset = LocationController.GetOffset(locationVariationType, player);
    ZDO? zdoOutput = null;
    // shouldAdjustReferencePoint: true sets ZNet reference position to the target ZDO so the boat's
    // zone streams in for the respawning/logging-in CLIENT — required for dedicated servers where the
    // boat lives in a zone the client has not loaded yet.
    yield return FindDynamicZdo(locationVariationType,
      output => { zdoOutput = output; }, shouldAdjustReferencePoint: true);

    if (
      zdoOutput == null)
    {
      yield break;
    }

    switch (locationVariationType)
    {
      case LocationVariation.Spawn:
        // Route the death-respawn through the vehicle finalizer (parents + onboards the player on
        // the live, possibly moving boat) when ValheimVehicles is present; otherwise fall back to
        // the plain teleport. The finalizer itself calls MovePlayerToZdo for the coarse placement.
        if (OnSpawnMoveToVehicle != null)
          yield return OnSpawnMoveToVehicle(zdoOutput, offset, this);
        else
          yield return MovePlayerToZdo(zdoOutput, offset);
        break;
      case LocationVariation.Logout:
        yield return LoginAPIController.RunAllIntegrations_OnLoginMoveToZdo(
          zdoOutput,
          offset,
          this);
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(locationVariationType),
          locationVariationType, null);
    }


    IsTeleportingToDynamicLocation = false;
    switch (locationVariationType)
    {
      // must be another coroutine AND only fired after the Move coroutine completes otherwise it WILL break the move coroutine as it deletes the required key.
      // remove logout point after moving the player.
      case LocationVariation.Logout when player != null:
      {
        // Consume the one-time logout point after a successful restore so the next session does not
        // re-teleport to the boat. Remove UNLESS the debug "keep data" flag is set (was inverted).
        if (CanRemoveLogoutAfterSync &&
            !DynamicLocationsConfig.DEBUG_ShouldNotRemoveTargetKey.Value)
        {
          LocationController.RemoveZdoTarget(
            LocationVariation.Logout,
            player);
        }

        break;
      }
      case LocationVariation.Spawn:
        break;
      default:
        throw new ArgumentOutOfRangeException(nameof(locationVariationType),
          locationVariationType, null);
    }

    timer.Clear();
    yield return true;
  }

  public Coroutine MovePlayerToSpawnPoint()
  {
    ResetRoutine(ref MoveToSpawnRoutine);
    // logout routine if activated must be cancelled as respawn takes priority
    ResetRoutine(ref MoveToLogoutRoutine);
    MoveToSpawnRoutine =
      StartCoroutine(UpdateLocation(LocationVariation.Spawn));
    return MoveToSpawnRoutine;
  }

  public void SyncPlayerPosition(Vector3 newPosition)
  {
    Logger.LogDebug("Running PlayerPosition Sync");
    if (ZNetView.m_forceDisableInit || player == null) return;
    var playerZdo = player.m_nview.GetZDO();
    if (playerZdo == null)
    {
      Logger.LogDebug("Player zdo invalid exiting");
      return;
    }

    Logger.LogDebug($"Syncing Player Position and sector, {newPosition}");
    var isLoaded =
      ZoneSystem.instance.IsZoneLoaded(
        ZoneSystem.GetZone(newPosition));

    if (!isLoaded)
    {
      Logger.LogDebug(
        $"zone not loaded, exiting SyncPlayerPosition for position: {newPosition}");
      return;
    }

    ZNet.instance.SetReferencePosition(newPosition);
    playerZdo.SetPosition(newPosition);
    playerZdo.SetSector(ZoneSystem.GetZone(newPosition));
    player.transform.position = newPosition;
  }

  public delegate TResult Func<in T, out TResult>(T arg);

  // Meant for being overridden by the ValheimRAFT mod
  // public static Func<ZNetView?, IEnumerator> PlayerMoveToVehicleCallback =
  // OnPlayerMoveToVehiclePlaceholder;

  // private static IEnumerator OnPlayerMoveToVehiclePlaceholder(ZNetView? obj)
  // {
  //   yield return null;
  // }
  //
  // private static IEnumerator OnPlayerMoveToVehicle(ZNetView? netView)
  // {
  //   var output = PlayerMoveToVehicleCallback(netView);
  //   yield return output;
  // }

  public static bool HasExpiredTimer(Stopwatch timer, int timeInMs = 1000)
  {
    var time = timeInMs > 1000
      ? timeInMs
      : DynamicLocationsConfig.LocationControlsTimeoutInMs.Value;

    var hasExpiredTimer = timer.ElapsedMilliseconds > time;

    return hasExpiredTimer;
  }

  public static bool HasExpiredTimer(DebugSafeTimer timer, int timeInMs = 1000)
  {
    var time = timeInMs > 1000
      ? timeInMs
      : DynamicLocationsConfig.LocationControlsTimeoutInMs.Value;

    var hasExpiredTimer = timer.ElapsedMilliseconds > time;

    return hasExpiredTimer;
  }

  [UsedImplicitly]
  public bool CanFreezePlayer(bool val)
  {
    return !DynamicLocationsConfig.DebugDisableFreezePlayerTeleportMechanics
      .Value && val;
  }

  /// <summary>
  /// Does not work, zdoids are not persistent across game and loading content outside a zone does not work well without a reference that persists.
  /// </summary>
  /// <remarks>Whenever calling yield break call OnMovePlayerToZdoComplete() otherwise there is no way to check if this has completed its run</remarks>
  /// <param name="zdo"></param>
  /// <param name="offset"></param>
  /// <param name="freezePlayerOnTeleport"></param>
  /// <param name="shouldKeepPlayerFrozen"></param>
  /// <returns></returns>
  public IEnumerator MovePlayerToZdo(ZDO? zdo, Vector3? offset,
    bool freezePlayerOnTeleport = false, bool shouldKeepPlayerFrozen = false)
  {
    if (!player || zdo == null)
    {
      OnMovePlayerToZdoComplete();
      yield break;
    }

    var timer = DebugSafeTimer.StartNew();
    var hasKinematicPlayerFreeze = CanFreezePlayer(freezePlayerOnTeleport);
    var hasKeepPlayerFrozen = CanFreezePlayer(shouldKeepPlayerFrozen);
    if (DynamicLocationsConfig.IsDebug)
    {
      Logger.LogDebug("Running MovePlayerToZdo");
    }

    var teleportHeightOffset = Vector3.up * DynamicLocationsConfig
      .RespawnHeightOffset.Value;
    var teleportPosition = zdo.GetPosition() + teleportHeightOffset;

    // yield return new WaitUntil(() => ZoneSystem.instance.IsZoneLoaded(zoneId));
    // var item = new WaitUntil(() => ZNetScene.instance.FindInstance(zdo));
    // yield return item;
    // TODO add check for item and confirm it has a valid ZDO DynamicLocationPoint var

    IsTeleportingToDynamicLocation =
      DynamicTeleport(teleportPosition, zdo.GetRotation());

    // Stream-chase the boat while waiting for its zone to load. The boat may be moving — and far
    // away if another player is sailing it — so we re-center the streaming reference on the boat's
    // LIVE zdo position every frame. A one-shot reference set (the old behaviour) lets a moving
    // boat drift out of the streamed zone so it never instantiates and we end up dumped at a stale
    // spot. Bounded by the same timeout so this loop can never hang (it previously had none).
    var zoneId = ZoneSystem.GetZone(zdo.GetPosition());
    var zoneLoaded = false;
    while (!zoneLoaded)
    {
      var livePos = zdo.GetPosition();
      if (ZNet.instance != null) ZNet.instance.SetReferencePosition(livePos);
      zoneId = ZoneSystem.GetZone(livePos);
      ZoneSystem.instance.PokeLocalZone(zoneId);
      zoneLoaded = ZoneSystem.instance.IsZoneLoaded(zoneId);
      if (zoneLoaded || HasExpiredTimer(timer,
            DynamicLocationsConfig.LocationControlsTimeoutInMs.Value)) break;
      yield return new WaitForFixedUpdate();
    }


    if (!IsTeleportingToDynamicLocation)
    {
      OnMovePlayerToZdoComplete();
      Logger.LogError(
        "Teleport command failed for player, exiting dynamic spawn MovePlayerToZdo.");
      yield break;
    }

    if (player != null && CanFreezePlayer(freezePlayerOnTeleport))
    {
      if (player.IsDebugFlying())
      {
        player.ToggleDebugFly();
      }
    }

    ZNetView? zdoNetViewInstance = null;
    var isZoneLoaded = false;

    zoneId = ZoneSystem.GetZone(zdo.GetPosition());
    ZoneSystem.instance.PokeLocalZone(zoneId);

    yield return new WaitUntil(() =>
      Player.m_localPlayer.IsTeleporting() == false || HasExpiredTimer(timer,
        DynamicLocationsConfig.LocationControlsTimeoutInMs.Value));

    zdoNetViewInstance = ZNetScene.instance.FindInstance(zdo);

    yield return new WaitUntil(() =>
    {
      // Keep chasing the (possibly moving) boat while its instance streams in — same reasoning as
      // the zone-wait loop above. Without this a boat sailed away by another player never appears.
      var livePos = zdo.GetPosition();
      if (ZNet.instance != null) ZNet.instance.SetReferencePosition(livePos);
      ZoneSystem.instance.PokeLocalZone(ZoneSystem.GetZone(livePos));
      zdoNetViewInstance = ZNetScene.instance.FindInstance(zdo);
      return zdoNetViewInstance != null || HasExpiredTimer(timer,
        DynamicLocationsConfig.LocationControlsTimeoutInMs.Value);
    });

    if (HasExpiredTimer(timer,
          DynamicLocationsConfig.LocationControlsTimeoutInMs.Value))
    {
      // The boat never streamed in / its instance was never found (e.g. it was destroyed or sank,
      // or the zone failed to load on a dedicated server). Unfreeze the player and bail to wherever
      // vanilla already placed them, so they are never trapped frozen on the death/login screen.
      Logger.LogError("Error attempting to find NetView instance of the ZDO");
      if (player != null)
      {
        if (player.IsDebugFlying()) player.ToggleDebugFly();
        if (player.m_body != null) player.m_body.isKinematic = false;
      }

      OnMovePlayerToZdoComplete(false,
        "Timed out finding NetView instance of the ZDO");
      yield break;
    }

    if (player != null && hasKinematicPlayerFreeze && !hasKeepPlayerFrozen)
    {
      if (player.IsDebugFlying())
      {
        player.ToggleDebugFly();
      }
    }

    // Optional settle delay before final placement (lets the boat finish activating its pieces).
    if (DynamicLocationsConfig.DebugForceUpdatePositionDelay.Value > 0f)
    {
      yield return new WaitForSeconds(DynamicLocationsConfig
        .DebugForceUpdatePositionDelay.Value);
    }

    // Final placement at the LIVE target transform. For a bed (death respawn) this uses the bed's
    // current spawn point, which follows the boat — so the player lands ON the bed wherever the boat
    // has moved (acceptance #2). This must run in normal play; it was previously gated behind the
    // DebugForceUpdatePositionAfterTeleport flag (default false), which dropped the placement and
    // left the player at the raw ZDO origin. For the logout path the precise standing-spot placement
    // happens afterward in the vehicle login integration, so this acts as a safe coarse position.
    if (player != null && zdoNetViewInstance != null)
    {
      var bed = zdoNetViewInstance.GetComponent<Bed>();
      var basePosition = bed != null
        ? bed.GetSpawnPoint()
        : zdoNetViewInstance.transform.position;
      teleportPosition = basePosition +
                         Vector3.up *
                         DynamicLocationsConfig.RespawnHeightOffset.Value;
      player.transform.position = teleportPosition;
      if (player.m_body != null)
      {
        player.m_body.velocity = Vector3.zero;
        player.m_body.angularVelocity = Vector3.zero;
      }

      SyncPlayerPosition(teleportPosition);
    }

    timer.Clear();
    yield return null;
  }
}