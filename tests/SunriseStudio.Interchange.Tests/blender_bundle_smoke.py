"""Exercise bundle identity, editable fields, and rejected edits in background Blender."""

import base64
import hashlib
import json
from pathlib import Path
import runpy
import struct
import sys

import bpy
from mathutils import Matrix, Vector


arguments = sys.argv[sys.argv.index("--") + 1:]
root = Path(arguments[0]).absolute()
addon_path = Path(arguments[1]).absolute()
root.mkdir(parents=True, exist_ok=True)
if any(root.iterdir()):
    raise ValueError("Smoke output folder must be empty.")


def write(relative, document):
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document), encoding="utf-8")
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


positions = [[-1, -1, 0], [1, -1, 0], [0, 1, 0], [0, 1, 0]]
indices = [0, 1, 2, 0, 1, 3]
identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
placed = identity[:]
placed[12:15] = [2, 3, 4]
# The last triangle repeats a vertex, as game meshes do; Blender cannot hold it.
geometry = {"Id": "static", "Positions": positions, "Normals": [[0, 0, 1]] * 4,
            "TexCoords": [[0, 0], [1, 0], [.5, 1], [.5, 1]], "Indices": indices + [0, 0, 1],
            "Joints": None, "Weights": None}
skinned = dict(geometry, Id="skinned", Joints=[[0, 0, 0, 0]] * 4, Weights=[[.125, .25, .375, .25]] * 4)
meshes = []
for index, data in enumerate((geometry, skinned)):
    path = f'geometry/{data["Id"]}.json'
    digest = write(path, data)
    meshes.append({"Id": data["Id"], "ModelTag": 0x80A00010 + index, "PartIndex": index,
                   "ConfigTag": 0, "MaterialTag": 0x80A00020, "Lod": 0, "Path": path,
                   "Sha256": digest, "Transform": placed if index == 0 else identity,
                   "Skinned": index == 1, "SkinSha256": "F" * 64 if index else None,
                   "RenderStage": 7 if index else 0})
meshes[0]["InitiallyVisible"] = None
meshes[1]["InitiallyVisible"] = False
meshes[1]["InitialVisibilityReason"] = "Inactive initial part"
# One material at stage 0 and stage 7: the stage-7 part needs its additive variant.
opaque_profile = {"Mode": "Fixture", "Bindings": [], "BaseColor": [.4, .5, .6, 1], "Metallic": 0,
                  "Roughness": .5, "Alpha": 1, "Emission": [0, 0, 0], "Notes": [], "BlendMode": "Opaque"}
# The additive colour reads time, camera and world position, and the surface tangent.
timed_program = {"Nodes": [{"Operation": "time", "Inputs": []}, {"Operation": "camera", "Inputs": [], "Channel": 0},
                           {"Operation": "position", "Inputs": [], "Channel": 0},
                           {"Operation": "surface", "Inputs": [], "Slot": 1, "Channel": 2},
                           {"Operation": "add", "Inputs": [0, 1]}, {"Operation": "mul", "Inputs": [4, 2]},
                           {"Operation": "add", "Inputs": [5, 3]}, {"Operation": "round_ne", "Inputs": [0]},
                           {"Operation": "future_operation", "Inputs": [0]}],
                 # An operation this add-on does not know drops only its own output.
                 "Outputs": {"BaseColor": [0, 6, 7], "Alpha": [8]}}
stage_profile = dict(opaque_profile, StageBlendModes={"0": "Opaque", "7": "Additive"},
                     BlendVariants={"Additive": dict(opaque_profile, BlendMode="Additive", Alpha=0, Program=timed_program)})

binary = bytearray()
views, accessors = [], []


def accessor(values, kind, width, component=5126):
    while len(binary) % 4:
        binary.append(0)
    offset = len(binary)
    code = {5126: "f", 5125: "I", 5123: "H"}[component]
    binary.extend(struct.pack("<" + code * len(values), *values))
    view = len(views)
    views.append({"buffer": 0, "byteOffset": offset, "byteLength": len(binary) - offset})
    item = {"bufferView": view, "componentType": component, "count": len(values) // width, "type": kind}
    if kind == "VEC3":
        item["min"] = [min(values[i::width]) for i in range(width)]
        item["max"] = [max(values[i::width]) for i in range(width)]
    accessors.append(item)
    return len(accessors) - 1


attributes = {"POSITION": accessor(sum(positions, []), "VEC3", 3),
              "NORMAL": accessor([0, 0, 1] * 4, "VEC3", 3),
              "TEXCOORD_0": accessor([0, 1, 1, 1, .5, 0, .5, 0], "VEC2", 2)}
index_accessor = accessor(indices, "SCALAR", 1, 5125)
skin_attributes = dict(attributes, JOINTS_0=accessor([0] * 16, "VEC4", 4, 5123),
                       WEIGHTS_0=accessor([1, 0, 0, 0] * 4, "VEC4", 4))
inverse = accessor(identity, "MAT4", 16)
time = accessor([0, 1 / 30], "SCALAR", 1)
translation = accessor([0, 0, 0, 0, 0, .1], "VEC3", 3)
nodes = [{"name": "bone_0_root", "extras": {"sunriseBoneIndex": 0, "sunriseEntityTag": "80A00001"}}]
for index, info in enumerate(meshes):
    node = {"name": info["Id"], "mesh": index,
            "extras": {"sunriseMeshId": info["Id"], "sunriseMaterialTag": "80A00020"}}
    if index:
        node["skin"] = 0
    else:
        node["matrix"] = placed
    nodes.append(node)
nodes.append({"name": "Sunrise coordinates", "matrix": [1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1],
              "children": [0, 1, 2]})
gltf = {"asset": {"version": "2.0"}, "scene": 0, "scenes": [{"nodes": [3]}], "nodes": nodes,
        "meshes": [{"primitives": [{"attributes": a, "indices": index_accessor, "material": 0}]}
                   for a in (attributes, skin_attributes)],
        "materials": [{"name": "Material_80A00020", "extras": {"sunriseMaterialTag": "80A00020"}}],
        "skins": [{"joints": [0], "inverseBindMatrices": inverse}],
        "animations": [{"name": "Pose_80A00030", "extras": {"sunrisePosePreview": True},
                        "samplers": [{"input": time, "output": translation, "interpolation": "LINEAR"}],
                        "channels": [{"sampler": 0, "target": {"node": 0, "path": "translation"}}]}],
        "bufferViews": views, "accessors": accessors,
        "buffers": [{"byteLength": len(binary), "uri": "data:application/octet-stream;base64," + base64.b64encode(binary).decode()}]}
write("scene.gltf", gltf)
image_path = root / "textures/image.png"
image_path.parent.mkdir(parents=True)
image = bpy.data.images.new("fixture", 2, 2)
image.pixels.foreach_set([.4, .5, .6, 1] * 4)
image.filepath_raw = str(image_path)
image.file_format = 'PNG'
image.save()
bpy.data.images.remove(image)
image_hash = hashlib.sha256(image_path.read_bytes()).hexdigest().upper()
material = {"Format": "sunrise-material", "Version": 1, "TagHash": 0x80A00020, "ClassHash": 0x808071E8,
            "Name": "Native material", "BaseSha256": "A" * 64, "SchemaSha256": "B" * 64,
            "Slots": [{"Stage": "Pixel", "Slot": 0, "Offset": 4, "SourceTextureTag": 0x80A00040, "TextureTag": 0x80A00040}],
            "Constants": [{"Stage": "Pixel", "Bank": "Constant template", "Vector": 0, "Component": "X", "Offset": 8, "Value": "1"}],
            "Textures": []}
write("materials/material.sunrise-material.json", material)
write("materials/unused.sunrise-material.json", dict(material, TagHash=0x80A00021, Name="Unused variant"))
write("particles/emitters.json", {"Emitters": [{"TagHash": 0x80A00050,
    "Constants": [{"Channel": 31, "Name": "Gravity", "Offset": 32, "Value": [1, 2, 3, 4]}]}], "Effects": []})
control_bytes = struct.pack("<8f", 0, 1 / 30, 0, 0, 0, 1, 0, 0)
controls = {"asset": {"version": "2.0"}, "scene": 0, "scenes": [{"nodes": [1]}],
    "extras": {"sunriseAnimationControls": {"clipTag": "80A00030", "frameCount": 2, "controlCount": 1, "sampleRate": 30}},
    "nodes": [{"name": "Control 0000", "extras": {"sunriseControlIndex": 0}, "translation": [0, 0, 0]},
              {"name": "Sunrise control coordinates", "rotation": [-.70710678, 0, 0, .70710678], "children": [0],
               "extras": {"sunriseCoordinates": True, "sunriseClipTag": "80A00030", "sunriseFrameCount": 2, "sunriseControlCount": 1}}],
    "bufferViews": [{"buffer": 0, "byteOffset": 0, "byteLength": 8}, {"buffer": 0, "byteOffset": 8, "byteLength": 24}],
    "accessors": [{"bufferView": 0, "componentType": 5126, "count": 2, "type": "SCALAR"},
                  {"bufferView": 1, "componentType": 5126, "count": 2, "type": "VEC3"}],
    "buffers": [{"byteLength": len(control_bytes), "uri": "data:application/octet-stream;base64," + base64.b64encode(control_bytes).decode()}],
    "animations": [{"samplers": [{"input": 0, "output": 1, "interpolation": "LINEAR"}],
                    "channels": [{"sampler": 0, "target": {"node": 0, "path": "translation"}}]}]}
control_hash = write("animations/source.controls.gltf", controls)
manifest = {"Format": "sunrise-asset-bundle", "Version": 1, "RootTag": 0x80A00001, "SelectedTag": 0x80A00001,
    "Name": "Synthetic bundle", "Context": None, "ScenePath": "scene.gltf", "Meshes": meshes,
    "Materials": [{"TagHash": 0x80A00020, "Path": "materials/material.sunrise-material.json", "ShaderProfile": None,
                   "Preview": stage_profile},
                  {"TagHash": 0x80A00021, "Path": "materials/unused.sunrise-material.json", "ShaderProfile": None}],
    "Textures": [{"TagHash": 0x80A00040, "DdsPath": "textures/source.dds", "DdsSha256": "C" * 64,
                  "Images": [{"Mip": 0, "Slice": 0, "DepthSlice": 0, "Path": "textures/image.png", "Sha256": image_hash}]}],
    "Shaders": [], "Animations": [{"ClipTag": 0x80A00030, "Name": "Fixture controls", "PosePath": None,
         "ControlsPath": "animations/source.controls.gltf", "ControlsSha256": control_hash}],
    "NativeAssets": [], "Notices": [{"Surface": "Materials", "Status": "Initial state fallback",
        "Message": "Fixture state is unresolved", "TagHash": 0x80A00010}], "ParticleMetadataPath": "particles/emitters.json"}
write("asset.sunrise-bundle.json", manifest)
addon = runpy.run_path(str(addon_path), run_name="sunrise_bundle_addon")
addon["register"]()
collection = addon["import_bundle"](str(root / "asset.sunrise-bundle.json"))
assert json.loads(collection["sunrise_material_warnings"]) == manifest["Notices"]


def export():
    return json.loads(Path(addon["export_bundle_edits"]()).read_text())


def refused(action, expected):
    try:
        action()
    except ValueError as error:
        assert expected.lower() in str(error).lower(), (expected, str(error))
    else:
        raise AssertionError("Unsupported edit was accepted: " + expected)


assert all(not export()[key] for key in ("Meshes", "Textures", "Animations", "Materials", "Emitters"))
weighted = next(obj for obj in collection.all_objects if obj.get("sunrise_mesh_id") == "skinned")
assert weighted.hide_get() and weighted.hide_render
assert not next(obj for obj in collection.all_objects if obj.get("sunrise_mesh_id") == "static").hide_get()
bpy.context.scene.sunrise_show_inactive_parts = True
assert not weighted.hide_get() and not weighted.hide_render
assert all(not export()[key] for key in ("Meshes", "Textures", "Animations", "Materials", "Emitters"))
bpy.context.scene.sunrise_show_inactive_parts = False
assert weighted.hide_get() and weighted.hide_render
assert len([obj for obj in collection.all_objects if obj.get("sunrise_mesh_id")]) == len(meshes)
assert all(len(vertex.groups) == 1 and abs(vertex.groups[0].weight - 1) < .000002
           for vertex in weighted.data.vertices)
assert json.loads((root / "geometry/skinned.json").read_text())["Weights"][0] == [.125, .25, .375, .25]
assert len(json.loads(collection["sunrise_loaded_materials"])) == 1


def stage_materials():
    parts = {o.get("sunrise_mesh_id"): o for o in collection.all_objects if o.get("sunrise_mesh_id")}
    base, variant = parts["static"].active_material, parts["skinned"].active_material
    assert variant is not base and variant.get("sunrise_variant_of") == base["sunrise_id"], variant.name
    assert "sunrise_material_tag" not in variant and variant["sunrise_blend_mode"] == "Additive"
    assert base.surface_render_method == "DITHERED" and variant.surface_render_method == "BLENDED"
    assert any(n.type == 'ADD_SHADER' for n in variant.node_tree.nodes)
    tree = variant.node_tree
    clock = next(n for n in tree.nodes if n.get("sunrise_preview_node") and n.label == "Shader time in seconds")
    assert {"Shader world camera", "Shader world position", "Shader world tangent"} <= {n.label for n in tree.nodes}
    # The Scene Time node feeds seconds, so no driver is left in the tree.
    assert clock.bl_idname == "GeometryNodeInputSceneTime" and clock.outputs["Seconds"].is_linked, clock.bl_idname
    assert not (tree.animation_data and tree.animation_data.drivers)
    print("blender_shader_time source=scene_time_node")
    return variant.name


first_variant = stage_materials()
addon["rebuild_materials"]()
assert stage_materials() == first_variant and len(json.loads(collection["sunrise_blend_variants"])) == 1
assert all(not export()[key] for key in ("Meshes", "Textures", "Animations", "Materials", "Emitters"))
addon["import_material"](0x80A00021)
assert len(json.loads(collection["sunrise_loaded_materials"])) == 2 and not export()["Materials"]
static = next(o for o in collection.all_objects if o.get("sunrise_mesh_id") == "static")
skin = next(o for o in collection.all_objects if o.get("sunrise_mesh_id") == "skinned")
assert len(static.data.vertices) == 4 and len(static.data.polygons) == 2
static.data.vertices[1].co.z += .25
static.data.update()
edit = export()["Meshes"]
assert len(edit) == 2 and abs(edit[0]["Positions"][1][2] - .25) < 1e-5 and edit[0]["Normals"] is None
static.data.vertices[1].co.z = 0
static.data.update()
static.matrix_world.translation += Vector((1, 0, 0))
edit = export()["Meshes"][0]
assert abs(edit["Positions"][0][0] - positions[0][0] - 1) < 1e-5
static.matrix_world = addon["_matrix"](placed)
static.data.attributes["sunrise_vertex_id"].data[0].value = 1
refused(export, "reordered")
static.data.attributes["sunrise_vertex_id"].data[0].value = 0
source_mesh = static.data
static.data = source_mesh.copy()
static.data.flip_normals()
refused(export, "topology")
invalid_mesh = static.data
static.data = source_mesh
bpy.data.meshes.remove(invalid_mesh)
static.data.normals_split_custom_set_from_vertices([(.2, 0, .98)] * 4)
assert export()["Meshes"][0]["Normals"] is not None
static.data.normals_split_custom_set_from_vertices(geometry["Normals"])
skin.vertex_groups[0].add([0], .5, 'REPLACE')
refused(export, "Skin weights")
skin.vertex_groups[0].add([0], 1, 'REPLACE')
new_object = bpy.data.objects.new("Unmapped", None)
collection.objects.link(new_object)
refused(export, "added")
bpy.data.objects.remove(new_object, do_unlink=True)
collection.objects.unlink(static)
refused(export, "removed")
collection.objects.link(static)
collection.objects.unlink(static)
collection.objects.unlink(skin)
refused(export, "removed")
collection.objects.link(static)
collection.objects.link(skin)
static.data.uv_layers[0].data[0].uv.x += .25
refused(export, "Split corner")
static.data.uv_layers[0].data[3].uv.x += .25
assert abs(export()["Meshes"][0]["TexCoords"][0][0] - .25) < 1e-5
static.data.uv_layers[0].data[0].uv.x = static.data.uv_layers[0].data[3].uv.x = 0
preview = next(a for a in bpy.data.actions if a.get("sunrise_pose_preview"))
curve = next(addon["_curves"](preview))[1]
old = curve.keyframe_points[0].co.y
curve.keyframe_points[0].co.y += .2
refused(export, "preview only")
curve.keyframe_points[0].co.y = old
material = static.active_material
value = next(n for n in material.node_tree.nodes if "sunrise_constant_offset" in n)
value.outputs[0].default_value = 2.5
edited = export()
material_edit = json.loads((root / edited["Materials"][0]["Path"]).read_text())
assert material_edit["BaseSha256"] == "A" * 64 and float(material_edit["Constants"][0]["Value"]) == 2.5
image = next(n.image for n in material.node_tree.nodes if "sunrise_slot_offset" in n)
image.pixels[0] = .8
assert len(export()["Textures"]) == 1 and len(export()["Textures"]) == 1
emitter = next(o for o in collection.all_objects if "sunrise_emitter_tag" in o)
emitter["native_31_20"] = [4.0, 3.0, 2.0, 1.0]
assert export()["Emitters"][0]["Value"] == [4, 3, 2, 1]
control_root = addon["import_controls"](0x80A00030)
assert not export()["Animations"]
control = control_root.children[0]
control["sunrise_control_index"] = 9
refused(export, "indices")
control["sunrise_control_index"] = 0
native = next(a for a in bpy.data.actions if a.get("sunrise_native_controls"))
curve = next(c for _, c in addon["_curves"](native) if c.data_path == "location" and c.array_index == 0)
curve.keyframe_points[1].co.y = 2
curve.update()
edited = export()
control_edit = json.loads((root / edited["Animations"][0]["Path"]).read_text())
assert control_edit["extras"] == controls["extras"]
output_accessor = control_edit["animations"][0]["samplers"][0]["output"]
values = addon["_gltf_values"](root, control_edit, root / edited["Animations"][0]["Path"], output_accessor)
assert abs(values[1][0] - 2) < 1e-5
refused(lambda: addon["_path"](root, "../escape.json"), "leaves")
escaped = dict(gltf, buffers=[{"byteLength": 16, "uri": "%2e%2e/outside.bin"}])
write("escaped.gltf", escaped)
refused(lambda: addon["_validate_gltf"](root, root / "escaped.gltf"), "leaves")
bpy.ops.wm.save_as_mainfile(filepath=str(root / "bundle-smoke.blend"))
saved_manifest = export()
bpy.ops.wm.open_mainfile(filepath=str(root / "bundle-smoke.blend"))
reopened_collection = next(c for c in bpy.data.collections if c.get("sunrise_manifest") == str(root / "asset.sunrise-bundle.json"))
assert json.loads(reopened_collection["sunrise_material_warnings"]) == manifest["Notices"]
assert next(obj for obj in reopened_collection.all_objects if obj.get("sunrise_mesh_id") == "skinned").hide_get()
reopened = export()
assert len(reopened["Meshes"]) == len(saved_manifest["Meshes"])
assert len(reopened["Animations"]) == len(saved_manifest["Animations"])
assert hasattr(bpy.types, "SUNRISE_PT_bundle") and hasattr(bpy.types, "SUNRISE_PT_bundle_material")
print("blender_bundle noop=pass positions=pass transform=pass normals=preserved uv=pass skin=guarded topology=guarded objects=guarded material=pass texture=pass controls=pass emitter=pass stage_variant=pass")
