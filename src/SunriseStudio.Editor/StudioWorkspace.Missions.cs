using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    /** Owned source changes refresh project lists without invalidating installed package data. */
    public void MissionSourceSaved(StudioProject savedProject)
    {
        if (!ReferenceEquals(Project, savedProject)) return;
        ProjectRevision++; AssetRevision++;
        Status = "Mission script saved";
        Changed?.Invoke();
    }
}
