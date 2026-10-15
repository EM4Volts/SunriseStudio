using ImGuiNET;
using SunriseStudio.Authoring;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor.Creation;

/** Reimport previews the existing weapon layout before updating its owned resources. */
public sealed class WeaponAssetReimportWorkflow : CreationWorkflow
{
    /** File stamps are read at most this often, in milliseconds. */
    private const int StampInterval = 500;
    private readonly StudioWorkspace workspace;
    private readonly uint weaponTag;
    private readonly CreationField file, review;
    private string? checkedFiles;
    private string stampedPath = "";
    private long nextStamp;
    private (string Text, string Full) fullPath = ("", "");

    public WeaponAssetReimportWorkflow(StudioWorkspace workspace, uint weaponTag)
    {
        this.workspace = workspace; this.weaponTag = weaponTag;
        file = new("Blender asset", CreationFieldKind.File, true, "The weapon's existing model, animation and particle assets")
        {
            Text = workspace.WeaponExportPath(weaponTag),
            FileFilter = ExoticWeaponWorkflow.ExportFilter,
            Hint = ExoticWeaponWorkflow.ExportHint
        };
        review = new("Import preview", CreationFieldKind.Rows, false, "Prepared changes") { DrawRows = DrawReview };
    }

    public override string Title => "Reimport Blender weapon";
    public override string CreateLabel => "Reimport";
    public override uint? AnchorTag => weaponTag;
    public override IReadOnlyList<CreationField> Fields => [file, review];
    public override IReadOnlyList<string> Limits =>
        [WeaponAssetReimporter.LayoutLimit, "Geometry, UVs, textures and motion may change."];

    /** File changes invalidate the review; stamps are read twice a second. */
    public override void Update(StudioWorkspace current)
    {
        string path = file.Text.Trim();
        long now = Environment.TickCount64;
        if (current.IsBusy || path == stampedPath && now < nextStamp) return;
        stampedPath = path; nextStamp = now + StampInterval;
        if (!IsExport(path) || !File.Exists(path)) return;
        string stamp;
        try { stamp = ExoticWeaponWorkflow.SourceStamp(path) + "|" + current.ProjectRevision; }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return; }
        if (stamp == checkedFiles) return;
        checkedFiles = stamp;
        current.PrepareWeaponAssetReimport(weaponTag, path);
    }

    protected override IEnumerable<string> Validate(StudioWorkspace current)
    {
        if (!IsExport(file.Text.Trim())) yield return "Choose a .glb exported with Sunrise weapon.";
        if (Plan is null) yield return current.IsBusy ? "Checking the Blender export..."
            : current.Error.Length != 0 ? current.Error : "Choose an export and review its prepared changes.";
    }

    private static bool IsExport(string path) => Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase);

    private PreparedWeaponAssetReimport? Plan => workspace.LastWeaponAssetReimport is { } prepared
        && prepared.Profile.WeaponTag == weaponTag
        && FullPath().Equals(prepared.SurfaceReport.Path, StringComparison.OrdinalIgnoreCase) ? prepared : null;

    /** The field's full path, resolved once per text change. */
    private string FullPath()
    {
        string text = file.Text.Trim();
        if (text == fullPath.Text) return fullPath.Full;
        string full;
        try { full = text.Length == 0 ? "" : Path.GetFullPath(text); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { full = ""; }
        fullPath = (text, full);
        return full;
    }

    private void DrawReview()
    {
        if (Plan is { } prepared)
        {
            ImGui.TextUnformatted($"{prepared.ChangedAssets.Count} assets change");
            var size = prepared.Dimensions;
            const int shown = 20;
            StudioUi.Tip($"Size {size.X:0.###} x {size.Y:0.###} x {size.Z:0.###} m\n"
                + "Identity, perks and clip slots stay the same.\n\n"
                + string.Join("\n", prepared.ChangedAssets.Take(shown))
                + (prepared.ChangedAssets.Count > shown ? $"\n{prepared.ChangedAssets.Count - shown} more" : ""));
        }
        else ImGui.TextDisabled(workspace.IsBusy ? "Checking..." : "No preview yet");
        ImGui.SameLine();
        if (StudioUi.IconButton("check-again", StudioIcon.Reset, "Check again", !workspace.IsBusy))
        { checkedFiles = null; nextStamp = 0; }
    }

    public override CreationFollowUp? Create(StudioWorkspace current)
    {
        var prepared = Plan ?? throw new InvalidOperationException("Check the Blender export before reimporting.");
        current.ApplyWeaponAssetReimport(prepared);
        return new(Tag: weaponTag, Page: "sunrise.weapon", Background: true, Status: "Blender weapon reimported");
    }
}
