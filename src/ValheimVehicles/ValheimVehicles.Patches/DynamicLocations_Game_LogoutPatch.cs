#region

  using DynamicLocations.Controllers;
  using HarmonyLib;
  using UnityEngine;
  using ValheimVehicles.Controllers;

#endregion

  namespace ValheimVehicles.Patches;

  public class DynamicLocations_Game_LogoutPatch
  {
    /// <summary>
    /// Removes logouts that are done off the ship
    /// </summary>
    [HarmonyPatch(typeof(Game), nameof(Game.ContinueLogout))]
    [HarmonyPrefix]
    private static void Game_OnContinueLogout()
    {
      if (Player.m_localPlayer == null) return;
      var playerZdoid = Player.m_localPlayer.GetZDOID();
      if (playerZdoid == ZDOID.None) return;
      if (PlayerSpawnController.Instance == null) return;

      var onboardData =
        VehicleOnboardController.GetOnboardCharacterData(Player.m_localPlayer
          .GetZDOID());

      if (onboardData == null || onboardData.OnboardController == null || onboardData.OnboardController.m_nview == null || onboardData.OnboardController.m_nview.GetZDO() == null)
      {
        PlayerSpawnController.Instance.SyncLogoutPoint(null, true);
        return;
      }

      // Capture the player's position relative to the vehicle pieces transform so login can restore
      // the exact standing spot on the (possibly moved/rotated) boat — rather than snapping to a bed.
      Vector3? localOffset = null;
      var piecesController = onboardData.OnboardController.PiecesController;
      if (piecesController != null)
      {
        localOffset = piecesController.transform.InverseTransformPoint(
          Player.m_localPlayer.transform.position);
      }

      PlayerSpawnController.Instance.SyncLogoutPoint(
        onboardData.OnboardController.m_nview.GetZDO(), false, localOffset);
    }
  }