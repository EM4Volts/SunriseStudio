using System.Diagnostics;
using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Editor;
using SunriseStudio.Projects;
using SunriseStudio.Scene;

/** A group transform moves each member about one pivot through its own parent, in one undo step. */
internal static class GroupTransformWorkflowChecks
{
    private sealed record Member(Vector3 Position, Quaternion Rotation, float Scale);

    /** Two scaled placements and an unscaled spawn: move, turn, uniform scale and axis scale. */
    internal static void Run(string root)
    {
        string install = Path.Combine(root, "group-workflow-install");
        WorldFixture.Create(install);
        using var workspace = new StudioWorkspace { CacheOptions = new() { Enabled = false } };
        workspace.OpenInstallation(install); Wait(workspace);
        workspace.OpenMission(WorldFixture.Base, WorldFixture.Base + 4); Wait(workspace);
        workspace.CreateProject(Path.Combine(root, "group-workflow-project"), "Group workflow"); Wait(workspace);
        var catalog = workspace.Catalog!;
        var scene = workspace.World!.Scene;
        object[] sources =
        [
            scene.Markers.First(marker => marker.SourceTag == WorldFixture.Base + 6 && marker.Layer == SceneLayer.Objects),
            scene.Markers.First(marker => marker.SourceTag == WorldFixture.Base + 7 && marker.Layer == SceneLayer.Objects),
            scene.Markers.First(marker => marker.Layer == SceneLayer.Spawns)
        ];
        var targets = sources.Select(source => SceneEdits.Describe(catalog, source, workspace.World).Target!).ToArray();
        Require(targets.Select(target => target.Layout).SequenceEqual(
            [ObjectEditLayout.Placement, ObjectEditLayout.Placement, ObjectEditLayout.SpawnPoint]),
            "The group fixture did not pick two placements and a spawn.");
        string id = workspace.CreateObjectGroup("Mixed", targets);
        Require(workspace.Project!.FindObjectGroup(id)!.Members.SequenceEqual(targets) && !workspace.CanUndo,
            "Creating a group lost a member or made an undo step.");

        // A move lands every member by the delta, in place, as one undo step.
        var start = State(workspace, targets);
        var move = new Vector3(1, -2, .5f);
        Apply(workspace, targets, TransformChange.Identity with { Move = move });
        var moved = State(workspace, targets);
        Require(workspace.LastWorldRefresh is { Incremental: true }, "A group move reloaded the world: " + workspace.LastWorldRefresh?.Reason);
        Require(Enumerable.Range(0, 3).All(i => Near(moved[i].Position, start[i].Position + move))
            && workspace.Project.ObjectEdits.Count == 3, "A group move did not move every member by its delta.");
        workspace.Undo(); Wait(workspace);
        Require(!workspace.CanUndo && workspace.Project.ObjectEdits.Count == 0
            && Enumerable.Range(0, 3).All(i => Near(State(workspace, targets)[i].Position, start[i].Position)),
            "One undo did not restore every member.");
        workspace.Redo(); Wait(workspace);
        Require(Enumerable.Range(0, 3).All(i => Near(State(workspace, targets)[i].Position, moved[i].Position)),
            "Redo did not move every member again.");
        PlacementUpdateWorkflowChecks.Compare(workspace, "group-move");

        // A quarter turn about Z turns the positions about the pivot and every member's rotation.
        var pivot = Frame(workspace, targets).Pivot;
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        Apply(workspace, targets, TransformChange.Identity with { Turn = turn });
        var turned = State(workspace, targets);
        for (int i = 0; i < 3; i++)
        {
            var offset = moved[i].Position - pivot;
            Require(Near(turned[i].Position, pivot + new Vector3(-offset.Y, offset.X, offset.Z))
                && MathF.Abs(Quaternion.Dot(turned[i].Rotation, turn * moved[i].Rotation)) > 1 - 1e-5f
                && turned[i].Scale == moved[i].Scale, $"A group turn misplaced member {i}.");
        }
        PlacementUpdateWorkflowChecks.Compare(workspace, "group-turn");
        TiltedParentTurn(workspace, targets);

        // A uniform scale spreads the positions and resizes only rows that store a scale.
        pivot = Frame(workspace, targets).Pivot;
        Apply(workspace, targets, TransformChange.Identity with { Scale = new(2) });
        var scaled = State(workspace, targets);
        Require(Enumerable.Range(0, 3).All(i => Near(scaled[i].Position, pivot + (turned[i].Position - pivot) * 2))
            && scaled[0].Scale == turned[0].Scale * 2 && scaled[1].Scale == turned[1].Scale * 2 && scaled[2].Scale == 1,
            "A uniform group scale did not spread positions and resize the scaled rows.");
        PlacementUpdateWorkflowChecks.Compare(workspace, "group-scale");

        // An axis scale spreads positions on that axis and keeps every size.
        pivot = Frame(workspace, targets).Pivot;
        Apply(workspace, targets, TransformChange.Identity with { Scale = new(2, 1, 1) });
        var stretched = State(workspace, targets);
        Require(Enumerable.Range(0, 3).All(i => Near(stretched[i].Position,
                pivot + (scaled[i].Position - pivot) * new Vector3(2, 1, 1)) && stretched[i].Scale == scaled[i].Scale),
            "An axis group scale changed a size or another axis.");
        PlacementUpdateWorkflowChecks.Compare(workspace, "group-axis-scale");

        // A member without handles refuses the whole group; a failing edit saves nothing.
        var world = workspace.World!;
        object light = world.Objects.Single(row => row.Kind == WorldObjectKind.Light).Source;
        var members = targets.Select(target => Find(world, target)).Append(light).ToArray();
        Require(GroupTransforms.Read(catalog, members, world, Label, out string reason) is null
            && reason.StartsWith("Light:", StringComparison.Ordinal), "A group with a light got handles: " + reason);
        byte[] manifest = File.ReadAllBytes(workspace.Project.ManifestPath);
        var edits = workspace.Project.ObjectEdits.ToArray();
        var good = GroupTransforms.CreateEdits(catalog, Frame(workspace, targets), TransformChange.Identity with { Move = move }, world)[0];
        var bad = new ObjectEdit(ObjectEditKind.PlacementTransform, targets[2], Position: [float.NaN, 0, 0]);
        Throws<InvalidDataException>(() => workspace.ApplyObjectEdits([good, bad]));
        Throws<InvalidDataException>(() => workspace.ApplyObjectEdits([good, good]));
        Require(File.ReadAllBytes(workspace.Project.ManifestPath).SequenceEqual(manifest)
            && workspace.Project.ObjectEdits.SequenceEqual(edits), "A failed group edit saved part of the group.");
        Console.WriteLine("editor_groups members=placement,placement,spawn move=in-place undo=one-step turn=pass tilted_turn=pass scale=pass "
            + "axis_scale=positions-only refused=light partial=none");
    }

    /** On pvp_sabre a trunk static instance and its canopy record move and turn together. */
    internal static void Owned(string gameRoot, string root)
    {
        const uint Map = 0x80FAA003, Payload = 0x80FAA33A;
        using var workspace = new StudioWorkspace();
        workspace.OpenInstallation(gameRoot); Wait(workspace, 1200);
        workspace.OpenMap(Map); Wait(workspace, 1200);
        workspace.CreateProject(Path.Combine(root, "owned-group-workflow"), "Owned group workflow"); Wait(workspace, 1200);
        var catalog = workspace.Catalog!;
        var world = workspace.World!;
        object[] sources =
        [
            world.Scene.Instances.First(item => item.InstanceSourceTag == 0x80FAA28E && item.InstanceSourceIndex == 4544),
            world.Scene.Instances.First(item => item.ObjectPlacement is { SourceTag: Payload, SourceIndex: 42 })
        ];
        var targets = sources.Select(source => SceneEdits.Describe(catalog, source, world).Target!).ToArray();
        Require(targets.Select(target => target.Layout).SequenceEqual([ObjectEditLayout.StaticInstance, ObjectEditLayout.DecoratorInstance]),
            "The owned group did not pick a static instance and a canopy record.");
        workspace.CreateObjectGroup("Tree", targets);
        float[] tolerance = [1e-4f, .005f];
        var start = targets.Select(target => ((MeshInstance)Find(workspace.World!, target)).Transform).ToArray();
        var move = new Vector3(.5f, -.25f, 1);
        Apply(workspace, targets, TransformChange.Identity with { Move = move }, 1200);
        Require(workspace.LastWorldRefresh is { Incremental: true }, "An owned group move reloaded the world: " + workspace.LastWorldRefresh?.Reason);
        var moved = targets.Select(target => ((MeshInstance)Find(workspace.World!, target)).Transform).ToArray();
        for (int i = 0; i < 2; i++)
            Require((moved[i].Translation - start[i].Translation - move).Length() <= tolerance[i], $"Owned member {i} did not move by the delta.");
        var pivot = Frame(workspace, targets).Pivot;
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6);
        Apply(workspace, targets, TransformChange.Identity with { Turn = turn }, 1200);
        Require(workspace.LastWorldRefresh is { Incremental: true }, "An owned group turn reloaded the world: " + workspace.LastWorldRefresh?.Reason);
        for (int i = 0; i < 2; i++)
        {
            var now = ((MeshInstance)Find(workspace.World!, targets[i])).Transform;
            var expected = pivot + Vector3.Transform(moved[i].Translation - pivot, turn);
            var axis = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, now));
            var expectedAxis = Vector3.Normalize(Vector3.Transform(Vector3.TransformNormal(Vector3.UnitX, moved[i]), turn));
            Require((now.Translation - expected).Length() <= tolerance[i] && Vector3.Distance(axis, expectedAxis) <= .01f,
                $"Owned member {i} did not turn about the group pivot.");
        }
        // A reload lists an isolated static instance last, so instances compare as a set.
        PlacementUpdateWorkflowChecks.Compare(workspace, "owned-group", 1200,
            unordered: workspace.CompiledObjects.GeneratedAssets.Count != 0);
        workspace.Undo(); Wait(workspace, 1200);
        workspace.Undo(); Wait(workspace, 1200);
        Require(workspace.Project!.ObjectEdits.Count == 0 && !workspace.CanUndo, "Two undos did not remove both group steps.");
        Console.WriteLine("editor_groups_owned map=80FAA003 members=static,canopy move=in-place turn=in-place undo=pass");
    }

    /** A world turn under a tilted parent; the fixture's own parents have no rotation. */
    private static void TiltedParentTurn(StudioWorkspace workspace, ObjectEditTarget[] targets)
    {
        var group = Frame(workspace, targets);
        var member = group.Members[0];
        var parent = Matrix4x4.CreateFromYawPitchRoll(.4f, .9f, -.3f) * Matrix4x4.CreateTranslation(3, -1, 2);
        var stored = Quaternion.Normalize(new(.2f, -.4f, .1f, .9f));
        var frame = member.Frame with
        {
            Pivot = Vector3.Transform(member.Frame.Info.Position, parent), Parent = parent,
            Info = member.Frame.Info with { Rotation = stored }
        };
        var turn = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(1, 2, 0)), .7f);
        var edit = TransformHandles.CreateEdit(workspace.Catalog!, member.Source, frame, group.Pivot,
            TransformChange.Identity with { Turn = turn }, workspace.World, null);
        if (edit is not { Position: { } moved, Rotation: { } turned })
            throw new InvalidOperationException("A turn under a tilted parent made no position and rotation edit.");
        var local = new Quaternion(turned[0], turned[1], turned[2], turned[3]);
        Matrix4x4 before = Matrix4x4.CreateFromQuaternion(stored) * parent, after = Matrix4x4.CreateFromQuaternion(local) * parent;
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            Require(Vector3.Distance(Vector3.TransformNormal(axis, after),
                Vector3.Transform(Vector3.TransformNormal(axis, before), turn)) <= 1e-4f, "A turn under a tilted parent misturned the row.");
        var position = Vector3.Transform(new Vector3(moved[0], moved[1], moved[2]), parent);
        Require(Near(position, group.Pivot + Vector3.Transform(frame.Pivot - group.Pivot, turn)),
            "A turn under a tilted parent misplaced the row.");
    }

    private static void Apply(StudioWorkspace workspace, ObjectEditTarget[] targets, TransformChange change, int timeoutSeconds = 30)
    {
        workspace.ApplyObjectEdits(GroupTransforms.CreateEdits(workspace.Catalog!, Frame(workspace, targets), change, workspace.World),
            "Transformed group");
        Wait(workspace, timeoutSeconds);
    }

    private static GroupTransformFrame Frame(StudioWorkspace workspace, ObjectEditTarget[] targets)
    {
        var world = workspace.World!;
        var frame = GroupTransforms.Read(workspace.Catalog!, targets.Select(target => Find(world, target)).ToArray(), world,
            Label, out string reason);
        Require(frame is not null, "The group got no handles: " + reason);
        return frame!;
    }

    /** Drawn position, stored rotation and stored scale of each member, from the current world. */
    private static Member[] State(StudioWorkspace workspace, ObjectEditTarget[] targets) => targets.Select(target =>
    {
        var source = Find(workspace.World!, target);
        var info = SceneEdits.Describe(workspace.Catalog!, source, workspace.World);
        var position = source is SceneMarker marker ? marker.Position : ((MeshInstance)source).Transform.Translation;
        return new Member(position, Quaternion.Normalize(info.Rotation), info.Scale);
    }).ToArray();

    private static object Find(WorldDocument world, ObjectEditTarget target) => target.Layout switch
    {
        ObjectEditLayout.StaticInstance => world.Scene.Instances.First(item =>
            item.InstanceSourceTag == target.SourceTag && item.InstanceSourceIndex == target.Index),
        ObjectEditLayout.DecoratorInstance => world.Scene.Instances.First(item =>
            item.ObjectPlacement?.SourceTag == target.Identity && item.ObjectPlacement.SourceIndex == target.Index),
        _ => world.Scene.Markers.First(marker => marker.SourceTag == target.SourceTag && marker.SourceOffset == target.Offset)
    };

    private static string Label(object source) => source is WorldLight ? "Light" : "Member";

    private static bool Near(Vector3 actual, Vector3 expected) => (actual - expected).Length() <= 1e-4f;

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Wait(StudioWorkspace workspace, int timeoutSeconds = 30)
    {
        var watch = Stopwatch.StartNew();
        while (workspace.IsBusy && watch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        { workspace.Poll(); Thread.Sleep(1); }
        Require(!workspace.IsBusy && workspace.Error.Length == 0, workspace.Error);
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
