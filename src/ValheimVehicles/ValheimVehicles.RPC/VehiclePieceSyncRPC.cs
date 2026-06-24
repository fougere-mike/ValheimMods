using System.Collections;
using ValheimVehicles.Controllers;
using ValheimVehicles.SharedScripts;
using ZdoWatcher;
using Zolantris.Shared;

namespace ValheimVehicles.RPC;

/// <summary>
/// Lets a client that is arriving at a far / moving vehicle ask the server to bulk force-send ALL of
/// that vehicle's piece ZDOs at once — exactly like a relog does — instead of waiting for the
/// rate-limited, churn-prone sector sync (which only trickles ~10-15% of a moving boat's pieces to a
/// respawning client, so the rest of the boat is open space they fall through).
///
/// The server first snaps every piece ZDO to the vehicle's CURRENT world position so they land in the
/// sector the client is loading around the boat (otherwise pieces with stale positions stay in
/// unloaded sectors and never instantiate even after a force-send), then force-sends them. Used by the
/// Strategy B respawn path. No-op on the host/server (it already has every piece locally).
/// </summary>
public static class VehiclePieceSyncRPC
{
  public static RPCEntity? RequestPieces_RPCInstance;

  public static void RegisterAll()
  {
    RequestPieces_RPCInstance =
      RPCManager.RegisterRPC(nameof(RPC_RequestVehiclePieces),
        RPC_RequestVehiclePieces);
  }

  /// <summary>Client -> server: force-send me every piece of this vehicle.</summary>
  public static void Request(int vehiclePersistentId)
  {
    if (vehiclePersistentId == 0) return;
    if (RequestPieces_RPCInstance == null) return;
    if (ZRoutedRpc.instance == null || ZNet.instance == null) return;
    // The host/server already has every piece ZDO locally — nothing to request.
    if (ZNet.instance.IsServer()) return;

    var pkg = new ZPackage();
    pkg.Write(vehiclePersistentId);
    RequestPieces_RPCInstance.Send(ZRoutedRpc.instance.GetServerPeerID(), pkg,
      false);
  }

  private static IEnumerator RPC_RequestVehiclePieces(long sender, ZPackage pkg)
  {
    pkg.SetPos(0);
    var vehicleId = pkg.ReadInt();
    if (vehicleId == 0) yield break;
    if (ZNet.instance == null || !ZNet.instance.IsServer()) yield break;
    if (ZDOMan.instance == null) yield break;

    var peer = ZDOMan.instance.GetPeer(sender);
    if (peer == null) yield break;

    var vehicleZdo = ZdoWatchController.Instance != null
      ? ZdoWatchController.Instance.GetZdo(vehicleId)
      : null;

    if (!VehiclePiecesController.m_allPieces.TryGetValue(vehicleId,
          out var pieces) || pieces == null)
    {
      // At least force-send the vehicle itself so the client can instantiate the hull.
      if (vehicleZdo != null) peer.ForceSendZDO(vehicleZdo.m_uid);
      yield break;
    }

    // Snap every piece ZDO to the vehicle's CURRENT position so they sit in the sector the client is
    // loading around the boat; then force-send them so they arrive in one burst like a fresh join.
    if (vehicleZdo != null)
      VehiclePiecesController.SyncAllPrefabsToVehiclePosition(vehicleZdo, pieces);

    if (vehicleZdo != null) peer.ForceSendZDO(vehicleZdo.m_uid);

    var sent = 0;
    foreach (var zdo in pieces)
    {
      if (zdo == null || !zdo.IsValid()) continue;
      peer.ForceSendZDO(zdo.m_uid);
      if (++sent % 100 == 0) yield return null; // spread very large boats across frames
    }

    LoggerProvider.LogDebug(
      $"[VehiclePieceSyncRPC] Force-sent vehicle {vehicleId} + {sent} pieces to peer {sender}.");
  }
}
