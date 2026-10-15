using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/** A saved table can carry old copies of arrays it grew; each is rebuilt from the installed bytes. */
public static class SavedTableCompaction
{
    /**
     * Rebuilds the item art, socket type and sandbox perk display tables this project changes.
     * A rebuilt table holds the same rows, so applying the result twice changes nothing.
     * @return Each table whose rebuilt bytes differ from the saved ones.
     */
    public static IReadOnlyDictionary<uint, byte[]> Plan(StudioProject project, PackageCatalog catalog)
    {
        var edits = new Dictionary<uint, byte[]>();
        void Rebuild(uint tag, Func<byte[]> build)
        {
            if (!project.ContainsEdit(tag)) return;
            byte[] rebuilt = build();
            if (!rebuilt.AsSpan().SequenceEqual(catalog.Read(tag))) edits[tag] = rebuilt;
        }
        Rebuild(new InvestmentSources().ItemArt, () => ItemIconAuthoring.PrepareItemArtRepair(project, catalog));
        Rebuild(InvestmentSocketTypes.GameplayTable, () => InvestmentSocketTypes.Rebuild(
            catalog.ReadOriginal(InvestmentSocketTypes.GameplayTable), catalog.Read(InvestmentSocketTypes.GameplayTable)));
        Rebuild(InvestmentSandboxPerkAuthoring.DisplayTableTag, () => InvestmentSandboxPerkAuthoring.Rebuild(
            catalog.ReadOriginal(InvestmentSandboxPerkAuthoring.DisplayTableTag),
            catalog.Read(InvestmentSandboxPerkAuthoring.DisplayTableTag)));
        return edits;
    }
}
