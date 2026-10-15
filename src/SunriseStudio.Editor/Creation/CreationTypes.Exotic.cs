using SunriseStudio.Packages;

namespace SunriseStudio.Editor.Creation;

public static partial class CreationTypes
{
    static partial void AddExoticTypes() =>
        Add(InvestmentItemKinds.Weapons, CreationMode.New, new("Exotic weapon",
            "A new exotic auto rifle from a Blender export.", "", workspace => new ExoticWeaponWorkflow(workspace)));
}
