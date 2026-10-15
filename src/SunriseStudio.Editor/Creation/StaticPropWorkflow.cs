using ImGuiNET;
using SunriseStudio.Authoring;

namespace SunriseStudio.Editor.Creation;

/** A standalone static model from a mesh file, or an original Blender asset with its supported surfaces. */
public sealed class StaticPropWorkflow : CreationWorkflow
{
    private static readonly CreationChoice[] Sources =
    [
        new(0, "Mesh file", "OBJ, glTF or GLB geometry with one installed shader"),
        new(1, "Original Blender asset", "A GLB or glTF exported from Blender")
    ];
    private readonly CreationField source, file, name, package, template, skip, surfaces;
    private (string Path, FileStamp Scene, FileStamp Sidecar)? inspectedFiles;
    private readonly record struct FileStamp(bool Exists, long Length, long WriteTime);
    private OriginalAssetInspection? inspection;
    private string inspectionError = "";
    private bool inspecting;

    public StaticPropWorkflow(StudioWorkspace workspace)
    {
        source = new("Source", CreationFieldKind.Scalar, true, "Which importer reads the file")
        { Text = "0", Choices = () => Sources };
        file = new("Mesh file", CreationFieldKind.File, true, "New static model, geometry buffers and part entries")
        {
            FileFilter = "Mesh files|*.obj;*.gltf;*.glb",
            Evidence = "MeshAssetImporter writes the one verified shader and declaration profile."
        };
        name = new("Name", CreationFieldKind.Text, true, "Project asset name and package entry name");
        package = PackageField(workspace);
        template = new("Template part", CreationFieldKind.Tag, false, "Installed geometry headers; mesh-only import also keeps its material")
        {
            Rows = () => workspace.ClassRows(PickClass.StaticMesh), RowFilter = row => !workspace.IsProjectTag(row.Entry.TagHash),
            Part = new("New", "Shaders cannot be authored", []),
            Hint = "Empty uses the first installed mesh with a supported shader.",
            Evidence = "The importer refuses a template whose shader is not the verified profile."
        };
        skip = new("Skip unsupported surfaces", CreationFieldKind.Scalar, false, "Original asset import option")
        {
            Text = "0", Choices = () => [new(0, "No: refuse the import"), new(1, "Yes: import the supported surfaces only")],
            Hint = "Static geometry only; use Import skinned model for rigged meshes."
        };
        surfaces = new("Surfaces", CreationFieldKind.Rows, false, "Report only; nothing is written from this list")
        { DrawRows = DrawSurfaces };
    }

    public override string Title => "New static prop";
    public override IReadOnlyList<CreationField> Fields => [source, file, name, package, template, skip, surfaces];
    public override IReadOnlyList<string> Limits =>
    [
        "Standalone model; not placed in a map.",
        Original ? "One opaque shader; each part keeps its material and textures."
            : "Geometry only, with the template's material.",
        "Only supported surfaces are kept."
    ];

    private bool Original => source.Text == "1";

    /** Stamps the files each frame; the read runs on a worker when they change. */
    public override void Update(StudioWorkspace workspace)
    {
        skip.Visible = surfaces.Visible = Original;
        // A read that ended without its result waits for the files to change.
        if (inspecting && !workspace.IsBusy)
        {
            inspecting = false;
            inspectionError = workspace.Error.Length != 0 ? workspace.Error : "Read stopped. Export again to retry.";
        }
        if (!Original) return;
        string path = file.Text.Trim();
        if (path.Length == 0) { inspectedFiles = null; inspection = null; inspectionError = ""; return; }
        if (workspace.IsBusy) return;
        try
        {
            path = Path.GetFullPath(path);
            var current = (path, Stamp(path), Stamp(Path.ChangeExtension(path, null) + OriginalAssetImporter.SidecarSuffix));
            if (current == inspectedFiles) return;
            inspectedFiles = current; inspection = null; inspectionError = "";
            if (!current.Item2.Exists) return;
            inspecting = true;
            workspace.InspectOriginalAsset(path, (result, error) =>
            {
                inspecting = false;
                if (current == inspectedFiles) { inspection = result; inspectionError = error; }
            });
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UnauthorizedAccessException
            or ArgumentException)
        { inspection = null; inspectionError = error.Message; }
    }

    private static FileStamp Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new(true, info.Length, info.LastWriteTimeUtc.Ticks) : default;
    }

    protected override IEnumerable<string> Validate(StudioWorkspace workspace)
    {
        if (PackageProblem(workspace, package) is { } problem) yield return problem;
        if (template.Tag is { } tag && workspace.IsProjectTag(tag)) yield return "The template part must be installed.";
        string extension = Path.GetExtension(file.Text.Trim()).ToLowerInvariant();
        if (Original)
        {
            if (extension is not (".glb" or ".gltf")) yield return "An original asset must be a GLB or glTF file.";
            if (inspectionError.Length != 0) yield return inspectionError;
            else if (inspecting) yield return "Reading the original asset.";
            else if (inspection is null) yield return "The original asset has not been read yet.";
            else if (skip.Text != "1")
                foreach (var surface in inspection.Surfaces.Where(surface => surface.Present && !surface.Supported))
                    yield return $"{surface.Name} is not supported: {surface.Detail}";
        }
        else if (extension is not (".obj" or ".gltf" or ".glb")) yield return "The mesh file must be OBJ, glTF or GLB.";
    }

    private void DrawSurfaces()
    {
        if (inspection is null)
        {
            ImGui.TextDisabled(inspectionError.Length != 0 ? "Read failed" : "Choose a GLB or glTF file");
            StudioUi.Tip(inspectionError);
            return;
        }
        CreationWidgets.Surfaces("##surfaces", inspection.Surfaces);
    }

    public override CreationFollowUp? Create(StudioWorkspace workspace)
    {
        string path = file.Text.Trim(), assetName = name.Text.Trim();
        ushort packageId = PackageId(package);
        uint? templateTag = template.Tag;
        int first = workspace.NextImportEntry(packageId);
        if (!Original)
        {
            workspace.ImportAsset((project, catalog, token) =>
                new(MeshAssetImporter.PrepareFile(catalog, path, assetName, packageId, first, templateTag, token).Assets));
            return new(Background: true, Status: "Static prop created");
        }
        var options = new OriginalAssetImportOptions(path, packageId, assetName, templateTag, skip.Text == "1");
        workspace.ImportAsset((project, catalog, token) => new(OriginalAssetImporter.Prepare(catalog, options, first, token).Assets));
        return new(Background: true, Status: "Original asset imported");
    }
}
