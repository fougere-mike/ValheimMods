using System.Collections;
using System.Collections.Generic;
using UnityEngine;
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
/// The server also REPLIES with the vehicle's authoritative LIVE world position. This is the key bit
/// for a moving boat: the respawning client's own copy of the boat/bed ZDO is stale (frozen wherever
/// the boat last unloaded), so it cannot tell where the boat actually is now. Valheim's normal ZDO
/// position sync does not reliably refresh a far/unowned ZDO on demand, so we push the position
/// explicitly from the authoritative server instead of hoping the ZDO refreshes. The client centres
/// its respawn streaming reference on that pushed position.
///
/// No-op on the host/server (it already has every piece + the live position locally). REQUIRES the
/// server to run a build with this RPC (4.3.2+ for the force-send, 4.3.4+ for the position push) —
/// ValheimRAFT's NetworkCompatibility is only Minor-strict, so a stale server silently no-ops this.
/// </summary>
public static class VehiclePieceSyncRPC
{
  public static RPCEntity? RequestPieces_RPCInstance;
  public static RPCEntity? VehiclePosResponse_RPCInstance;

  /// <summary>Server-pushed live vehicle positions, keyed by vehicle persistent id (client-side).</summary>
  private static readonly Dictionary<int, Vector3> ServerVehiclePositions = new();

  public static void RegisterAll()
  {
    RequestPieces_RPCInstance =
      RPCManager.RegisterRPC(nameof(RPC_RequestVehiclePieces),
        RPC_RequestVehiclePieces);
    VehiclePosResponse_RPCInstance =
      RPCManager.RegisterRPC(nameof(RPC_VehiclePosResponse),
        RPC_VehiclePosResponse);
  }

  /// <summary>
  /// Client -> server: always reply with the vehicle's live position; ALSO bulk force-send every
  /// piece only when <paramref name="includePieces" /> is true. The piece send snaps all piece ZDOs to
  /// the (server-collapsed) vehicle centre and re-sends all of them — useful ONCE to bulk-load an
  /// unloaded boat, but HARMFUL if repeated every frame: it churns the client's piece controller and
  /// the boat never settles into a solid deck. So callers send pieces only while the boat is not yet
  /// loaded, then switch to position-only.
  /// </summary>
  public static void Request(int vehiclePersistentId, bool includePieces)
  {
    if (vehiclePersistentId == 0) return;
    if (RequestPieces_RPCInstance == null) return;
    if (ZRoutedRpc.instance == null || ZNet.instance == null) return;
    // The host/server already has every piece ZDO + the live position locally — nothing to request.
    if (ZNet.instance.IsServer()) return;

    var pkg = new ZPackage();
    pkg.Write(vehiclePersistentId);
    pkg.Write(includePieces);
    RequestPieces_RPCInstance.Send(ZRoutedRpc.instance.GetServerPeerID(), pkg,
      false);
  }

  /// <summary>The last server-pushed live position for this vehicle, if we have received one.</summary>
  public static bool TryGetServerVehiclePosition(int vehiclePersistentId,
    out Vector3 pos)
  {
    return ServerVehiclePositions.TryGetValue(vehiclePersistentId, out pos);
  }

  private static IEnumerator RPC_RequestVehiclePieces(long sender, ZPackage pkg)
  {
    pkg.SetPos(0);
    var vehicleId = pkg.ReadInt();
    if (vehicleId == 0) yield break;
    var includePieces = pkg.ReadBool();
    if (ZNet.instance == null || !ZNet.instance.IsServer()) yield break;
    if (ZDOMan.instance == null) yield break;

    var peer = ZDOMan.instance.GetPeer(sender);
    if (peer == null) yield break;

    var vehicleZdo = ZdoWatchController.Instance != null
      ? ZdoWatchController.Instance.GetZdo(vehicleId)
      : null;

    // Reply with the authoritative live vehicle position so the client can centre its respawn
    // streaming reference on where the boat actually is now (its own ZDO copy is stale).
    if (vehicleZdo != null && VehiclePosResponse_RPCInstance != null)
    {
      var resp = new ZPackage();
      resp.Write(vehicleId);
      resp.Write(vehicleZdo.GetPosition());
      VehiclePosResponse_RPCInstance.Send(sender, resp, false);
    }

    // Position-only request (boat already loading on the client) — do NOT re-snap / re-send the
    // pieces, which would churn the client's piece controller and stop it ever settling.
    if (!includePieces) yield break;

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
      $"[VehiclePieceSyncRPC] Force-sent vehicle {vehicleId} (pos {(vehicleZdo != null ? vehicleZdo.GetPosition().ToString() : "?")}) + {sent} pieces to peer {sender}.");
  }

  /// <summary>Server -> client: the authoritative live position of a vehicle we asked about.</summary>
  private static IEnumerator RPC_VehiclePosResponse(long sender, ZPackage pkg)
  {
    pkg.SetPos(0);
    var vehicleId = pkg.ReadInt();
    if (vehicleId == 0) yield break;
    var pos = pkg.ReadVector3();
    ServerVehiclePositions[vehicleId] = pos;
    yield break;
  }
}
