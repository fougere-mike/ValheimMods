using System;
using System.Collections.Generic;
using System.Linq;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.Serialization;
using ValheimVehicles.Attributes;
using ValheimVehicles.Components;
using ValheimVehicles.BepInExConfig;
using ValheimVehicles.Helpers;
using ValheimVehicles.Prefabs;
using ValheimVehicles.SharedScripts;
using ValheimVehicles.Controllers;
using ValheimVehicles.Shared.Constants;
using ValheimVehicles.Structs;
using ZdoWatcher;
using Zolantris.Shared;
using Logger = Jotunn.Logger;

namespace ValheimVehicles.Components;

public class WaterZoneController : CreativeModeColliderComponent
{
  public ZNetView? netView;
  public static readonly Dictionary<ZDOID, WaterZoneController> Instances = [];

  private VehicleDebugHelpers? _debugComponent;

  public static Dictionary<ZDOID, WaterZoneCharacterData>
    WaterZoneCharacterData = new();

  private ZDOID instanceZdoid = ZDOID.None;

  // can be null as this may not be the type of controller
  private VehicleOnboardController? _onboardController = null;

  public enum WaterZoneControllerType
  {
    Vehicle,
    Static
  }

  public WaterZoneControllerType zoneType = WaterZoneControllerType.Static;
  public Vector3 defaultScale = Vector3.one;

  private new void Awake()
  {
    base.Awake();
    netView = GetComponent<ZNetView>();
    _onboardController = GetComponentInParent<VehicleOnboardController>();
  }

  public void Start()
  {
    if (ZNetView.m_forceDisableInit) return;

    if (netView == null) netView = GetComponent<ZNetView>();

    if (netView != null)
    {
      instanceZdoid = netView.GetZDO().m_uid;
      if (!Instances.ContainsKey(instanceZdoid))
        Instances.Add(instanceZdoid, this);
    }

    if (_onboardController) zoneType = WaterZoneControllerType.Vehicle;

    InitMaskFromNetview();

    ScheduleStrayCleanupSweep();
  }

  private static bool IsInWaterFreeZone(Character character)
  {
    return WaterZoneCharacterData.ContainsKey(character.GetZDOID());
  }

  // todo to make an overload that takes vehicleonboard controller.
  public static bool GetWaterZoneController(Character character,
    out WaterZoneController? waterZoneController
    // out VehicleOnboardController? onboardController
  )
  {
    waterZoneController = null;
    if (WaterZoneCharacterData.TryGetValue(character.GetZDOID(),
          out var characterData))
    {
      waterZoneController = characterData.WaterZoneController;
      return characterData.WaterZoneController != null;
    }
    //
    //
    // if (VehicleOnboardController.GetCharacterVehicleMovementController(zdoid,
    //       out VehicleOnboardController? vehicleOnboardController))
    // {
    //   onboardController = vehicleOnboardController;
    //   return true;
    // }

    return false;
  }

  /// <summary>
  /// Main calc for checking zones where player can be underwater
  /// </summary>
  /// <param name="character"></param>
  /// <returns></returns>
  /// <exception cref="ArgumentOutOfRangeException"></exception>
  [GameCacheValue]
  public static bool IsCharacterInWaterFreeZone(Character character)
  {
    return WaterConfig.UnderwaterAccessMode.Value switch
    {
      WaterConfig.UnderwaterAccessModeType.Disabled => false,
      WaterConfig.UnderwaterAccessModeType.Everywhere => true,
      WaterConfig.UnderwaterAccessModeType.OnboardOnly =>
        WaterZoneUtils.IsOnboard(character),
      WaterConfig.UnderwaterAccessModeType.DEBUG_WaterZoneOnly =>
        IsInWaterFreeZone(
          character),
      _ => throw new ArgumentOutOfRangeException()
    };
  }

  /// <summary>
  /// To be combined with onboard data. Avoid complicated logic delegating to other controllers.
  /// </summary>
  /// <param name="character"></param>
  /// <param name="waterZoneData"></param>
  /// <returns></returns>
  /// <exception cref="ArgumentOutOfRangeException"></exception>
  public static bool GetCharacterDataFromWaterZone(Character character,
    out WaterZoneCharacterData? waterZoneData)
  {
    waterZoneData = null;
    switch (WaterConfig.UnderwaterAccessMode.Value)
    {
      case WaterConfig.UnderwaterAccessModeType.Disabled:
      case WaterConfig.UnderwaterAccessModeType.Everywhere:
        waterZoneData = null;
        return false;
      case WaterConfig.UnderwaterAccessModeType.OnboardOnly:
        waterZoneData =
          VehicleOnboardController.GetOnboardCharacterData(character);
        return waterZoneData != null;
      case WaterConfig.UnderwaterAccessModeType.DEBUG_WaterZoneOnly:
        waterZoneData = GetCharacterWaterZoneData(character);
        return waterZoneData != null;
      default:
        throw new ArgumentOutOfRangeException();
    }
  }

  public static WaterZoneCharacterData? GetCharacterWaterZoneData(
    Character character)
  {
    WaterZoneCharacterData.TryGetValue(character.GetZDOID(), out var data);
    return data;
  }

  private new void OnDestroy()
  {
    base.OnDestroy();
    Instances.Remove(instanceZdoid);
  }

  public void OnTriggerEnter(Collider collider)
  {
    var character = collider.GetComponent<Character>();
    if (character == null) return;
    var characterZdoid = character.GetZDOID();

    // we do not need to keep transitioning the player between areas. This avoids an exit/entry call continuously fighting for ownership
    if (WaterZoneCharacterData.ContainsKey(characterZdoid)) return;

    WaterZoneCharacterData.Add(characterZdoid,
      new WaterZoneCharacterData(character, this));
  }

  public void OnTriggerExit(Collider collider)
  {
    var character = collider.GetComponent<Character>();
    if (character == null) return;

    var characterZdoid = character.GetZDOID();
    if (!WaterZoneCharacterData.TryGetValue(characterZdoid, out var data))
      return;

    // only remove the character if it was registered to THIS zone, so overlapping zones
    // do not yank each other's characters out.
    if (data.controllerZdoId != instanceZdoid) return;

    WaterZoneCharacterData.Remove(characterZdoid);
  }

  public static void OnToggleEditMode(bool isDebug)
  {
    foreach (var waterMaskComponent in Instances.Values.ToList())
      if (isDebug)
        waterMaskComponent?.UseDebugComponents();
      else
        waterMaskComponent?.UseHiddenComponents();
  }

  private void CreateDebugHelperComponent()
  {
    if (_debugComponent == null)
      _debugComponent = gameObject.AddComponent<VehicleDebugHelpers>();

    if (!collider) collider = GetComponent<BoxCollider>();

    if (collider == null) return;

    _debugComponent.AddColliderToRerender(new DrawTargetColliders
    {
      collider = collider,
      parent = transform,
      lineColor = new Color(0, 0.5f, 1f, 0.8f),
      width = 1f
    });
    _debugComponent.autoUpdateColliders = true;
  }

  /// <summary>
  /// Meant to show the mask in a "Debug mode" by swapping shaders
  /// Makes the component breakable
  /// </summary>
  public void UseDebugComponents()
  {
    CreateDebugHelperComponent();
    gameObject.layer = LayerHelpers.PieceNonSolidLayer;
  }

  /// <summary>
  /// Makes the component untouchable.
  /// </summary>
  public void UseHiddenComponents()
  {
    if (_debugComponent != null) Destroy(_debugComponent);

    gameObject.layer = LayerHelpers.IgnoreRaycastLayer;
  }

  private static PrimitiveType GetPrimitiveTypeFromZdo(ZDO zdo)
  {
    var primitiveType = zdo.GetInt(
      VehicleZdoVars.CustomMeshPrimitiveType,
      -1);

    // update zdo if invalid
    if (primitiveType == -1)
    {
      zdo.Set(VehicleZdoVars.CustomMeshPrimitiveType,
        (int)PrimitiveType.Cube);
      return PrimitiveType.Cube;
    }

    return (PrimitiveType)primitiveType;
  }

  public void InitPrimitive()
  {
    var zdo = netView!.GetZDO();
    if (zdo == null) return;
    var primitiveType = GetPrimitiveTypeFromZdo(zdo);
    var size = zdo.GetVec3(VehicleZdoVars.CustomMeshScale, defaultScale);
    if (size == Vector3.zero)
    {
      // invalid mesh, destroy it
      Destroy(gameObject);
      return;
    }

    var renderer = GetComponent<MeshRenderer>();
    var meshFilter = GetComponent<MeshFilter>();
    collider = GetComponent<BoxCollider>();
    collider.isTrigger = true;
    collider.includeLayers = LayerHelpers.CharacterLayer;

    var primitive = GameObject.CreatePrimitive(primitiveType);
    var primitiveMeshFilter = primitive.GetComponent<MeshFilter>();

    gameObject.layer = LayerHelpers.IgnoreRaycastLayer;
    renderer.sharedMaterial = CubeMaskMaterial;
    meshFilter.sharedMesh = primitiveMeshFilter.sharedMesh;
    transform.localScale = size;

    Destroy(primitive);
  }

  public void InitMaskFromNetview()
  {
    if (ZNetView.m_forceDisableInit || netView?.GetZDO() == null) return;
    InitPrimitive();

    if (IsEditMode)
      UseDebugComponents();
    else
      UseHiddenComponents();
  }

  #region Stray-mask cleanup

  // Delay before running the sweep so nearby masks register into Instances and parent
  // vehicles have a chance to load/re-parent before we judge anything an orphan.
  private const float StrayCleanupDelaySeconds = 5f;

  private static bool _hasScheduledStrayCleanup;

  private void ScheduleStrayCleanupSweep()
  {
    if (_hasScheduledStrayCleanup) return;
    _hasScheduledStrayCleanup = true;
    Invoke(nameof(InvokeStrayCleanupSweep), StrayCleanupDelaySeconds);
  }

  private void InvokeStrayCleanupSweep()
  {
    // allow rescheduling if more masks load later in the session
    _hasScheduledStrayCleanup = false;
    RunStrayCleanupSweep();
  }

  /// <summary>
  /// Removes leftover "water mask" volumes that are not attached to a live vehicle.
  /// Safety: there should only ever be a single stray volume. If more than one candidate is
  /// found this refuses to delete anything (a sign the detection logic is wrong) and warns.
  /// </summary>
  public static void RunStrayCleanupSweep()
  {
    if (ZNetScene.instance == null) return;

    var candidates = Instances.Values
      .Where(controller => controller != null && controller.IsStrayCandidate())
      .ToList();

    if (candidates.Count == 0) return;

    if (candidates.Count > 1)
    {
      Logger.LogWarning(
        $"[WaterMask cleanup] Expected at most ONE stray water mask but found {candidates.Count}. Refusing to delete anything to avoid removing valid volumes. Candidates: {string.Join(" | ", candidates.Select(c => c.DescribeForLog()))}");
      return;
    }

    var stray = candidates[0];
    Logger.LogInfo(
      $"[WaterMask cleanup] Removing 1 stray water mask: {stray.DescribeForLog()}");
    stray.DestroySelfNetworked();
  }

  /// <summary>
  /// A mask is a stray-cleanup candidate when it is not attached to a live vehicle AND either
  /// its parent vehicle is confirmed gone (true orphan), or the one-time
  /// RemoveAllStaticWaterMasks toggle is enabled (catches fully-detached garbage that never
  /// had a parent link).
  /// </summary>
  private bool IsStrayCandidate()
  {
    if (netView == null) return false;
    var zdo = netView.GetZDO();
    if (zdo == null) return false;

    // Attached to a live vehicle -> never a candidate.
    if (_onboardController != null) return false;
    if (zoneType == WaterZoneControllerType.Vehicle) return false;
    if (GetComponentInParent<VehiclePiecesController>() != null) return false;

    var parentId = VehiclePiecesController.GetParentID(zdo);

    // True orphan: it had a parent vehicle, but that vehicle no longer exists.
    if (parentId != 0 && ZdoWatchController.Instance != null &&
        ZdoWatchController.Instance.GetZdo(parentId) == null)
    {
      return true;
    }

    // Opt-in purge of all non-vehicle masks (covers detached parentId == 0 garbage).
    return WaterConfig.RemoveAllStaticWaterMasks.Value;
  }

  private void DestroySelfNetworked()
  {
    if (netView == null || netView.GetZDO() == null)
    {
      Destroy(gameObject);
      return;
    }

    if (!netView.IsOwner()) netView.ClaimOwnership();
    ZNetScene.instance.Destroy(gameObject);
  }

  private string DescribeForLog()
  {
    var id = netView != null && netView.GetZDO() != null
      ? netView.GetZDO().m_uid.ToString()
      : "<no-zdo>";
    return $"zdoid={id} pos={transform.position}";
  }

  #endregion
}