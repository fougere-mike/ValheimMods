using BepInEx.Configuration;
using ValheimVehicles.Helpers;
using Zolantris.Shared;

namespace ValheimVehicles.BepInExConfig;

public class CustomMeshConfig : BepInExBaseConfig<CustomMeshConfig>
{
  public static ConfigEntry<bool> EnableCustomWaterMeshCreators =
    null!;

  public static ConfigEntry<bool> EnableCustomWaterMeshTestPrefabs =
    null!;

  private const string SectionKey = "CustomMesh";


  public override void OnBindConfig(ConfigFile config)
  {
    EnableCustomWaterMeshCreators = config.BindUnique(
      SectionKey,
      "Water Mask Prefabs Enabled",
      false,
      ConfigHelpers.CreateConfigDescription(
        "DEPRECATED/EXPERIMENTAL. Adds the manual water-mask creator tool (place 8 corners to carve a water-free box) to the hammer build menu. This tool is unfinished and buggy and has been replaced by the automatic OnboardOnly underwater mode, which shapes the water-free area to the vehicle automatically. Leave disabled unless you know what you are doing.",
        true));
    EnableCustomWaterMeshTestPrefabs = config.BindUnique(
      SectionKey,
      "Enable Testing 4x4 Water Mask Prefabs, these are meant for demoing water obstruction.",
      false,
      ConfigHelpers.CreateConfigDescription(
        "login/logoff point moves player to last interacted bed or first bed on ship",
        true));
  }
}