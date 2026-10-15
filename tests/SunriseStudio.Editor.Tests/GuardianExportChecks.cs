using SunriseStudio.Animation;
using SunriseStudio.Editor;
using SunriseStudio.Interchange;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using SunriseStudio.Viewport;

/** A guardian's Blender export carries the plate atlases, dye buffers and dye bit each gear part is drawn with. */
internal static class GuardianExportChecks
{
    /**
     * Exports a hunter with real gauntlets, a cloak and a weapon, the way the Guardian tab does.
     * @param output Receives guardian-export; its manifest path is printed for the Blender check.
     */
    internal static void Run(string game, string output)
    {
        using var catalog = PackageCatalog.Open(game);
        var hunter = GuardianClasses.All[1];
        var loadout = GuardianLoadout.Default with { ShowHelmet = false, Gauntlets = 0x813197AB, ClassArmor = 0x8131F7CB, Weapon = 0x8132017D };
        var prepared = new GuardianAssembler(catalog).Prepare(hunter.BodyTag, loadout);
        var plan = NativeScenePlan.Prepare(catalog, prepared.Scene, assetPreview: true);
        var inputs = GuardianShaderInputs.Read(plan);
        Check(inputs.Values.Any(value => new uint[] { 0, 1, 2 }.All(slot => value.Textures.TryGetValue(slot, out uint tag)
            && value.ComposedTextures.ContainsKey(tag))), "No gear part carries its three plate atlases.");
        Check(inputs.Values.Any(value => value.ConstantBuffers.Count != 0), "No gear part carries a dye scope buffer.");
        var assembly = StudioWorkspace.GuardianAssembly(hunter.DisplayName, hunter.BodyTag, prepared,
            new AssetOpenContext(hunter.BodyTag) { Guardian = loadout }) ?? throw new InvalidDataException("The hunter rig did not read.");
        string folder = Path.Combine(output, "guardian-export");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = AssetBundle.ExportAssembly(catalog, assembly with { ShaderInputs = inputs }, folder);
        clock.Stop();
        var manifest = result.Manifest;
        long bytes = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        Console.WriteLine($"guardian_export_size clips={manifest.Animations.Count} seconds={clock.Elapsed.TotalSeconds:F1} "
            + $"bytes={bytes} scene_bytes={new FileInfo(Path.Combine(folder, manifest.ScenePath!)).Length}");
        Actions(catalog, prepared, manifest, folder);
        var variants = manifest.Materials.Where(value => value.Variant is not null).ToArray();
        Check(manifest.Meshes.Where(mesh => mesh.MaterialVariant is not null).All(mesh =>
            variants.Any(value => value.TagHash == mesh.MaterialTag && value.Variant == mesh.MaterialVariant)),
            "A part names a material variant the bundle does not hold.");
        var textures = manifest.Textures.ToDictionary(value => value.TagHash);
        var plated = variants.Where(value => value.Preview?.SlotTextures is { } slots && slots.ContainsKey(0)).ToArray();
        Check(plated.Length != 0, "No variant binds a plate atlas.");
        foreach (var material in plated)
        {
            foreach (uint slot in new uint[] { 0, 1, 2 })
                Check(material.Preview!.SlotTextures!.TryGetValue(slot, out uint tag) && textures.TryGetValue(tag, out var texture)
                    && texture.Images.Count != 0 && File.Exists(Path.Combine(folder, texture.Images[0].Path)),
                    $"Variant {material.Variant} of 0x{material.TagHash:X8} has no plate image at t{slot}.");
            Check(material.Name?.StartsWith("Material", StringComparison.Ordinal) == false, $"Variant {material.Variant} kept a generic name.");
        }
        var colourless = plated.Where(value => value.Preview!.Program?.Outputs.ContainsKey("BaseColor") != true).ToArray();
        Check(colourless.Length == 0, "Plated variants without a colour program: "
            + string.Join(", ", colourless.Select(value => $"0x{value.TagHash:X8} {value.Variant} ({string.Join(" ", value.Preview!.Notes.TakeLast(2))})")));
        Check(manifest.Notices.All(value => value.Surface != "Dyes"), "The bundle still says per-part dyes are missing.");
        int detail = manifest.Meshes.Count(mesh => System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, mesh.Path)))
            .RootElement.TryGetProperty("TexCoords1", out var uvs) && uvs.ValueKind == System.Text.Json.JsonValueKind.Array);
        Check(detail != 0, "No gear part carries its detail UVs.");
        Console.WriteLine($"guardian_export variants={variants.Length} plated={plated.Length} colour_programs={plated.Length - colourless.Length} "
            + $"detail_uv_meshes={detail} textures={manifest.Textures.Count} path={result.ManifestPath}");
        Face(prepared, inputs, manifest, folder);
        FaceDecals(catalog);
    }

    /**
     * Every body clip is one action, the hold pose first, and clips the game's tables name read as words. Under a
     * middle frame of several clips, the exported face, body and weapon skin to where the viewer draws them.
     */
    private static void Actions(PackageCatalog catalog, PreparedGuardian prepared, AssetBundleManifest manifest, string folder)
    {
        var animation = prepared.Animation!;
        uint[] clips = animation.Clips.Select(clip => clip.ClipTag).Distinct().ToArray();
        Check(clips.Length > 1 && manifest.Animations.Count == clips.Length,
            $"The export has {manifest.Animations.Count} actions for {clips.Length} body clips.");
        var failed = manifest.Animations.Where(value => value.Error is not null).ToArray();
        Check(failed.Length == 0, $"{failed.Length} clips did not export: "
            + string.Join("; ", failed.Take(5).Select(value => $"{value.ClipTag:X8} {value.Error}")));
        Check(manifest.Animations.All(value => value.PosePath is null) && !Directory.Exists(Path.Combine(folder, "animations")),
            "Actions are written twice; the scene glTF holds them.");
        Check(prepared.PoseClipTag != 0 && manifest.Animations[0].ClipTag == prepared.PoseClipTag
            && manifest.Animations[0].Name == "Weapon hold pose", "The hold pose is not the first action.");
        var gltf = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(folder, manifest.ScenePath!)))!;
        var actions = gltf["animations"]!.AsArray();
        Check(actions.Count == manifest.Animations.Count, $"The scene holds {actions.Count} of {manifest.Animations.Count} actions.");
        string[] names = manifest.Animations.Select(value => value.Name).ToArray();
        Check(names.Any(name => name.StartsWith("ready", StringComparison.Ordinal))
            && names.Any(name => name.StartsWith("stand ", StringComparison.Ordinal))
            && names.Any(name => name.StartsWith("Clip ", StringComparison.Ordinal)), "Known clips are not named, or unknown ones lost their slot.");
        Check(names.All(name => !name.Contains("mannequin", StringComparison.Ordinal)), "A chance hash match named a clip.");
        Check(names.Distinct(StringComparer.Ordinal).Count() == names.Length, "Two actions share a name.");
        int readable = names.Count(name => !name.StartsWith("Clip ", StringComparison.Ordinal));
        // The hold pose, then clips that move the head and the arms.
        var picks = new List<uint> { prepared.PoseClipTag };
        foreach (string prefix in new[] { "ready", "dodge_back", "hunter_melee", "throw_grenade" })
            if (manifest.Animations.FirstOrDefault(value => value.Name.StartsWith(prefix, StringComparison.Ordinal)) is { } pick)
                picks.Add(pick.ClipTag);
        float face = 0, body = 0, weapon = -1, head = 0;
        foreach (uint tag in picks.Distinct())
        {
            var result = Mirror(catalog, prepared, manifest, folder, actions, tag);
            face = MathF.Max(face, result.Face); body = MathF.Max(body, result.Body);
            weapon = MathF.Max(weapon, result.Weapon); head = MathF.Max(head, result.Head);
        }
        Check(head > 0.05f, $"No checked clip moves the head more than {head * 100:F1} cm, so the face check proves nothing.");
        Check(weapon >= 0, "No weapon part was checked.");
        // A face vertex blended over bones the rest moves differently keeps a sub-millimetre error once the rest is baked.
        Check(face < 0.001f && body < 0.00005f && weapon < 0.00005f,
            $"Posed export is off the viewer: face {face * 1000:F2} mm, body {body * 1000:F2} mm, weapon {weapon * 1000:F2} mm.");
        Console.WriteLine($"guardian_actions actions={actions.Count} readable={readable} checked={picks.Distinct().Count()} "
            + $"head_move_cm={head * 100:F1} face_mm={face * 1000:F3} body_mm={body * 1000:F3} weapon_mm={weapon * 1000:F3}");
    }

    /**
     * Skins the exported geometry with one exported action frame and compares it to the viewer at the same time.
     * @return Worst distances in metres by part kind, and how far the head bone moved from its rest.
     */
    private static (float Face, float Body, float Weapon, float Head) Mirror(PackageCatalog catalog, PreparedGuardian prepared,
        AssetBundleManifest manifest, string folder, System.Text.Json.Nodes.JsonArray actions, uint tag)
    {
        var animation = prepared.Animation!;
        var rig = animation.Rig;
        var action = actions.Single(value => (string?)value!["extras"]!["sunriseClipTag"] == tag.ToString("X8"))!;
        var gltf = action.Root;
        string uri = (string)gltf["buffers"]![0]!["uri"]!;
        byte[] data = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
        float[] Floats(int accessor)
        {
            var info = gltf["accessors"]![accessor]!;
            var view = gltf["bufferViews"]![(int)info["bufferView"]!]!;
            int width = (string)info["type"]! switch { "SCALAR" => 1, "VEC3" => 3, "VEC4" => 4, _ => 16 };
            int offset = (int)view["byteOffset"]!, count = (int)info["count"]! * width;
            return Enumerable.Range(0, count).Select(index => BitConverter.ToSingle(data, offset + index * 4)).ToArray();
        }
        var samplers = action["samplers"]!.AsArray();
        // Channels that never change keep two keys, so the clip's times are the longest input.
        var times = samplers.Select(sampler => Floats((int)sampler!["input"]!)).MaxBy(values => values.Length)!;
        float time = times[times.Length / 2];
        var local = rig.Bones.Select(bone => bone.BindLocal).ToArray();
        foreach (var channel in action["channels"]!.AsArray())
        {
            var sampler = samplers[(int)channel!["sampler"]!]!;
            var input = Floats((int)sampler["input"]!);
            var values = Floats((int)sampler["output"]!);
            int key = Array.FindIndex(input, value => MathF.Abs(value - time) < 1e-5f);
            if (key < 0) key = 0;
            int bone = (int)channel["target"]!["node"]!;
            local[bone] = (string)channel["target"]!["path"]! switch
            {
                "translation" => local[bone] with { Translation = new(values[key * 3], values[key * 3 + 1], values[key * 3 + 2]) },
                "rotation" => local[bone] with { Rotation = new(values[key * 4], values[key * 4 + 1], values[key * 4 + 2], values[key * 4 + 3]) },
                _ => local[bone] with { Scale = values[key * 3] }
            };
        }
        var objects = new System.Numerics.Matrix4x4[rig.Bones.Count];
        System.Numerics.Matrix4x4 Object(int bone) => rig.Bones[bone].ParentIndex < 0 ? local[bone].Matrix
            : local[bone].Matrix * Object(rig.Bones[bone].ParentIndex);
        for (int bone = 0; bone < objects.Length; bone++) objects[bone] = Object(bone);
        var skin = objects.Select((value, bone) => rig.Bones[bone].InverseBindObject.Matrix * value).ToArray();

        var viewer = new Dictionary<MeshData, RenderVertex[]>(ReferenceEqualityComparer.Instance);
        var pose = animation.Rig.CreateBindPose();
        using (var preview = new AssetAnimationPreview(animation, prepared.Scene, (mesh, vertices) => viewer[mesh] = vertices.ToArray(), () => { }))
        {
            preview.SetClip(new AnimationControlReader(catalog).Read(tag), play: false);
            preview.Seek(time);
            for (int bone = 0; bone < rig.Bones.Count; bone++) pose.SetLocalTransform(bone, preview.Pose.LocalTransforms[bone]);
            pose.Evaluate();
        }
        // The head is the nearest ancestor of the face bones that the rest layer leaves alone.
        var restBones = animation.Binding!.RestBones;
        int headBone = rig.Bones[restBones[0]].ParentIndex;
        while (restBones.Contains(headBone)) headBone = rig.Bones[headBone].ParentIndex;
        var rest = rig.CreateBindPose();
        animation.Binding.ApplyBindPose(rest);
        float head = System.Numerics.Vector3.Distance(pose.ObjectTransforms[headBone].Translation, rest.ObjectTransforms[headBone].Translation);

        string[] faceLabels = ["Head:", "Hair:", "Eyebrows:", "Markings:"];
        var attached = animation.AttachedSkins.Keys.Select(value => value.ModelTag).ToHashSet();
        var cloth = animation.SkinBindings.Where(value => value.PaletteKind == SkinPaletteKind.ClothInput).Select(value => value.ModelTag).ToHashSet();
        float face = 0, body = 0, weapon = -1;
        foreach (var instance in prepared.Scene.Instances)
        {
            if (!viewer.TryGetValue(instance.Mesh, out var posed) || cloth.Contains(instance.Mesh.TagHash)) continue;
            var mesh = manifest.Meshes.Single(value => value.ModelTag == instance.Mesh.TagHash && value.PartIndex == instance.Mesh.PartIndex);
            if (!mesh.Skinned) continue;
            using var geometry = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, mesh.Path)));
            var positions = geometry.RootElement.GetProperty("Positions").EnumerateArray().ToArray();
            var joints = geometry.RootElement.GetProperty("Joints").EnumerateArray().ToArray();
            var weights = geometry.RootElement.GetProperty("Weights").EnumerateArray().ToArray();
            float worst = 0;
            for (int vertex = 0; vertex < positions.Length; vertex++)
            {
                var point = new System.Numerics.Vector3(positions[vertex][0].GetSingle(), positions[vertex][1].GetSingle(), positions[vertex][2].GetSingle());
                var moved = System.Numerics.Vector3.Zero;
                for (int lane = 0; lane < 4; lane++)
                {
                    float weight = weights[vertex][lane].GetSingle();
                    if (weight != 0) moved += System.Numerics.Vector3.Transform(point, skin[joints[vertex][lane].GetInt32()]) * weight;
                }
                worst = MathF.Max(worst, System.Numerics.Vector3.Distance(moved, posed[vertex].Position));
            }
            if (attached.Contains(instance.Mesh.TagHash)) weapon = MathF.Max(weapon, worst);
            else if (faceLabels.Any(label => instance.Name.StartsWith(label, StringComparison.Ordinal))) face = MathF.Max(face, worst);
            else body = MathF.Max(body, worst);
        }
        Console.WriteLine($"guardian_action clip={tag:X8} time={time:F2} head_move_cm={head * 100:F1} face_mm={face * 1000:F3} "
            + $"body_mm={body * 1000:F3} weapon_mm={weapon * 1000:F3}");
        return (face, body, weapon, head);
    }

    /**
     * Every face part is written in the viewer's neutral face, brows and hair included; decal parts keep the blend their
     * decal dye forces; and no face material covers the face with an uncoloured surface.
     */
    private static void Face(PreparedGuardian prepared, IReadOnlyDictionary<MeshInstance, BundleShaderInputs> inputs,
        AssetBundleManifest manifest, string folder)
    {
        string[] labels = ["Head:", "Hair:", "Eyebrows:", "Markings:"];
        var face = prepared.Scene.Instances.Where(instance => labels.Any(label => instance.Name.StartsWith(label, StringComparison.Ordinal))).ToArray();
        Check(face.Any(instance => instance.Name.StartsWith("Eyebrows:", StringComparison.Ordinal)), "The default face has no eyebrows.");
        var viewer = new Dictionary<MeshData, RenderVertex[]>(ReferenceEqualityComparer.Instance);
        using (var preview = new AssetAnimationPreview(prepared.Animation!, prepared.Scene, (mesh, vertices) => viewer[mesh] = vertices.ToArray(), () => { }))
            preview.ResetBindPose();
        float worst = 0;
        int baked = 0, decals = 0, lit = 0;
        foreach (var instance in face)
        {
            var mesh = manifest.Meshes.Single(value => value.ModelTag == instance.Mesh.TagHash && value.PartIndex == instance.Mesh.PartIndex);
            using var geometry = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, mesh.Path)));
            var positions = geometry.RootElement.GetProperty("Positions").EnumerateArray()
                .Select(value => new System.Numerics.Vector3(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle())).ToArray();
            var posed = viewer[instance.Mesh];
            Check(positions.Length == posed.Length, $"{instance.Name} wrote {positions.Length} of {posed.Length} vertices.");
            float moved = 0;
            for (int vertex = 0; vertex < positions.Length; vertex++)
            {
                worst = MathF.Max(worst, System.Numerics.Vector3.Distance(positions[vertex], posed[vertex].Position));
                moved = MathF.Max(moved, System.Numerics.Vector3.Distance(instance.Mesh.Vertices[vertex].Position, posed[vertex].Position));
            }
            // Brows and hair hairlines ride the face bones, so the neutral face moves them off bind.
            if (moved > 0.001f && !instance.Name.StartsWith("Head:", StringComparison.Ordinal)) baked++;
            var material = manifest.Materials.FirstOrDefault(value => value.TagHash == mesh.MaterialTag && value.Variant == mesh.MaterialVariant)
                ?? throw new InvalidDataException($"{instance.Name} has no material 0x{mesh.MaterialTag:X8}.");
            var surface = material.Preview ?? throw new InvalidDataException($"Material 0x{mesh.MaterialTag:X8} has no preview.");
            if (inputs.TryGetValue(instance, out var part) && part.ForcedBlend is { } forced)
            {
                decals++;
                Check(surface.BlendMode == MaterialPreviewAnalyzer.ResolveBlendMode(0, -1, forced) && surface.Deferred
                    && surface.Program?.Outputs.ContainsKey("Alpha") == true && surface.Program.Outputs.ContainsKey("BaseColor"),
                    $"{instance.Name} 0x{mesh.MaterialTag:X8} draws {surface.BlendMode}, not the blend its decal dye forces, with alpha.");
            }
            bool coloured = surface.Program?.Outputs.ContainsKey("BaseColor") == true || surface.Bindings.Any(value => value.Role == "BaseColor")
                || surface.Palette is not null || surface.Recipe is not null;
            // A forward premultiplied layer's colour is runtime light; it may darken by its alpha but never cover the face.
            bool layer = !surface.Deferred && surface.BlendMode is "PremultipliedAlpha" or "Additive"
                && (surface.Alpha == 0 || surface.Program?.Outputs.ContainsKey("Alpha") == true);
            if (!coloured && layer) lit++;
            Check(coloured || layer, $"{instance.Name} 0x{mesh.MaterialTag:X8} has no base colour and covers what is behind it.");
        }
        Check(worst < 0.0001f, $"A face part is {worst * 1000:F2} mm off the viewer's neutral face.");
        Check(baked != 0, "No brow or hair part moved to the neutral face.");
        Check(decals != 0, "No face part carries a forced decal blend.");
        Console.WriteLine($"guardian_face parts={face.Length} worst_mm={worst * 1000:F3} baked_brow_hair={baked} decals={decals} light_layers={lit}");
    }

    /** Each race and gender, with several hair styles: every hair and brow decal previews with its forced blend and alpha. */
    private static void FaceDecals(PackageCatalog catalog)
    {
        var assembler = new GuardianAssembler(catalog);
        var tfx = MaterialPreviewAnalyzer.CreateInputs(catalog);
        var textures = new TextureDataReader(catalog);
        var seen = new HashSet<string>();
        int faces = 0;
        foreach (var row in assembler.Appearance.Rows)
            foreach (int hair in new[] { 0, 5, 10 })
            {
                var prepared = assembler.Prepare(GuardianClasses.Hunter, GuardianLoadout.Default with { RaceGender = row.Row, Variant2 = hair, ShowHelmet = false });
                var scene = new SceneDocument { TagHash = prepared.Scene.TagHash };
                scene.Instances.AddRange(prepared.Scene.Instances.Where(instance =>
                    instance.Name.StartsWith("Hair:", StringComparison.Ordinal) || instance.Name.StartsWith("Eyebrows:", StringComparison.Ordinal)));
                faces++;
                foreach (var (instance, part) in GuardianShaderInputs.Read(NativeScenePlan.Prepare(catalog, scene, assetPreview: true)))
                {
                    if (part.ForcedBlend is not { } forced || !seen.Add($"{instance.Mesh.MaterialTag:X8}|{part.Key}")) continue;
                    var overrides = part.Textures.Select(pair => new MaterialPreviewAnalyzer.TextureInput(pair.Key, pair.Value,
                        part.ComposedTextures.TryGetValue(pair.Value, out var composed) ? composed.Format : textures.Read(pair.Value).Format)).ToArray();
                    tfx.Vectors[MaterialPreviewAnalyzer.GearDyeBit] = new System.Numerics.Vector4(part.DyeBit);
                    var preview = MaterialPreviewAnalyzer.Read(catalog, instance.Mesh.MaterialTag, tfx, [instance.Mesh.Native!.RenderStage],
                        overrides, part.ConstantBuffers, forced);
                    Check(preview.BlendMode == "InverseAlpha" && preview.Program?.Outputs.ContainsKey("Alpha") == true,
                        $"Row {row.Row} hair {hair}: {instance.Name} 0x{instance.Mesh.MaterialTag:X8} previews {preview.BlendMode} without its decal alpha.");
                }
            }
        Check(seen.Count != 0, "No hair or brow decal was found.");
        Console.WriteLine($"guardian_face_decals faces={faces} decal_inputs={seen.Count}");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
}
