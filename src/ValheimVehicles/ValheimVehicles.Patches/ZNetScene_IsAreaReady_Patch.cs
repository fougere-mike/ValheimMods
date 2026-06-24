using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimVehicles.Shared.Constants;
using ValheimVehicles.SharedScripts;
using Zolantris.Shared;

namespace ValheimVehicles.Patches;

/// <summary>
/// Fixes the infinite loading screen on death-respawn / distant-teleport when a raft is near the
/// destination (a known longstanding ValheimRAFT bug — dev commit 4b0b45a3, 2024).
///
/// Vanilla <see cref="Game.FindSpawnPoint" /> (respawn) and <see cref="Player.UpdateTeleport" />
/// (distant teleport) both gate completion on <see cref="ZNetScene.IsAreaReady" />, which returns
/// false until EVERY valid-prefab ZDO in the destination zone has a live instance. A raft's many
/// pieces can fail to all instantiate, so IsAreaReady never returns true and the player is stuck on
/// the loading screen forever (UpdateTeleport even returns before reaching its own 15s timeout).
///
/// This postfix treats un-instantiated VEHICLE pieces as non-blocking: the area is "ready" once all
/// NON-vehicle objects are loaded. The raft then streams in normally afterward — exactly as on a
/// fresh world join, which already works. Land beds (not vehicle pieces) still require their own
/// readiness, so the player still spawns on the bed; only the surrounding raft pieces are ignored.
/// </summary>
[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.IsAreaReady))]
public static class ZNetScene_IsAreaReady_Patch
{
  private static readonly List<ZDO> ZdoBuffer = new();
  private static float _lastLogTime;

  [HarmonyPostfix]
  private static void Postfix(ZNetScene __instance, Vector3 point, ref bool __result)
  {
    if (__result) return; // vanilla already reports ready
    if (ZoneSystem.instance == null || ZDOMan.instance == null) return;

    var zone = ZoneSystem.GetZone(point);
    // If the zone itself isn't loaded yet we are genuinely not ready — leave vanilla's result.
    if (!ZoneSystem.instance.IsZoneLoaded(zone)) return;

    ZdoBuffer.Clear();
    ZDOMan.instance.FindSectorObjects(zone, 1, 0, ZdoBuffer);

    var missingVehiclePieces = 0;
    var missingOther = 0;
    string firstOtherPrefab = null;

    foreach (var zdo in ZdoBuffer)
    {
      if (!__instance.IsPrefabZDOValid(zdo)) continue;
      if (__instance.FindInstance(zdo)) continue; // already has a live instance

      if (IsVehicleRelatedZdo(__instance, zdo))
      {
        missingVehiclePieces++;
        continue;
      }

      // A non-vehicle object is still missing — keep vanilla's "not ready" so we don't spawn the
      // player before the terrain/bed/buildings around them exist.
      missingOther++;
      if (firstOtherPrefab == null)
        firstOtherPrefab = SafePrefabName(__instance, zdo);
    }

    // Only override when the *sole* thing blocking readiness is un-loaded vehicle pieces.
    if (missingOther == 0 && missingVehiclePieces > 0)
      __result = true;

    if ((missingVehiclePieces > 0 || missingOther > 0) &&
        Time.realtimeSinceStartup - _lastLogTime > 3f)
    {
      _lastLogTime = Time.realtimeSinceStartup;
      LoggerProvider.LogInfo(
        $"[ValheimVehicles] IsAreaReady@{point}: missing vehicle-piece={missingVehiclePieces}, other={missingOther}" +
        (firstOtherPrefab != null ? $" (e.g. '{firstOtherPrefab}')" : "") +
        $" -> {(__result ? "OVERRIDE READY (raft pieces ignored)" : "still waiting")}");
    }
  }

  /// <summary>
  /// True for any raft/vehicle ZDO: a piece parented to a vehicle (MBParentId / TempPieceParentId /
  /// cultivatable parent), or the vehicle ship object itself (by prefab name). These are the objects
  /// that can fail to all instantiate at once and stall vanilla's IsAreaReady.
  /// </summary>
  private static bool IsVehicleRelatedZdo(ZNetScene scene, ZDO zdo)
  {
    if (zdo.GetInt(VehicleZdoVars.MBParentId, 0) != 0
        || zdo.GetInt(VehicleZdoVars.TempPieceParentId, 0) != 0
        || zdo.GetInt(VehicleZdoVars.MBCultivatableParentIdHash, 0) != 0)
      return true;

    var prefab = scene.GetPrefab(zdo.GetPrefab());
    return prefab != null && PrefabNames.IsVehicle(prefab.name);
  }

  private static string SafePrefabName(ZNetScene scene, ZDO zdo)
  {
    var prefab = scene.GetPrefab(zdo.GetPrefab());
    return prefab != null ? prefab.name : zdo.GetPrefab().ToString();
  }
}
