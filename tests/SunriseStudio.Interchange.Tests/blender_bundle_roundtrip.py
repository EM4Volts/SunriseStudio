"""Check a real exported bundle without changing its original files."""

import json
import math
from pathlib import Path
import runpy
import struct
import sys
import time

import bpy
from mathutils import Quaternion


args = sys.argv[sys.argv.index("--") + 1:]
manifest = Path(args[0]).absolute()
addon = runpy.run_path(str(Path(args[1]).absolute()), run_name="sunrise_bundle_addon")
addon["register"]()
started = time.perf_counter()
collection = addon["import_bundle"](str(manifest))
print(f'blender_bundle_import seconds={time.perf_counter() - started:.2f} '
      f'materials={len(json.loads(collection["sunrise_loaded_materials"]))}', flush=True)
path = Path(addon["export_bundle_edits"]())
initial = json.loads(path.read_text())
assert all(not initial[key] for key in ("Meshes", "Textures", "Animations", "Materials", "Emitters")), {
    key: len(initial[key]) for key in ("Meshes", "Textures", "Animations", "Materials", "Emitters")}
meshes = [o for o in collection.all_objects if o.get("sunrise_mesh_id")]
assert meshes, "Owned fixture has no tagged mesh."
target = meshes[0]
extent = [(max(v.co[a] for v in target.data.vertices) - min(v.co[a] for v in target.data.vertices), a)
          for a in range(3)]
span, axis = max(extent)
middle = (max(v.co[axis] for v in target.data.vertices) + min(v.co[axis] for v in target.data.vertices)) / 2
vertex = min(target.data.vertices, key=lambda v: abs(v.co[axis] - middle))
vertex_index = int(vertex.index)
amount = min(.001, span * .001)
assert amount > .00001
vertex.co[axis] += amount
target.data.update()
surfaces = []
if "--combined" in args:
    materials = sorted({m for obj in meshes for m in obj.data.materials
                        if m and "sunrise_material_tag" in m}, key=lambda m: m["sunrise_material_tag"])
    choices = [(m, n) for m in materials for n in m.node_tree.nodes if "sunrise_constant_offset" in n
               and math.isfinite(n.outputs[0].default_value) and abs(n.outputs[0].default_value) < 1000]
    assert choices, "Owned fixture has no connected material with an editable scalar."
    material, scalar = choices[0]
    scalar.outputs[0].default_value += max(.001, abs(scalar.outputs[0].default_value) * .01)
    surfaces.append("material")
    texture = next(i for i in bpy.data.images if "sunrise_texture_tag" in i and i.get("sunrise_mip", 0) == 0)
    texture.pixels[0] = .25 if texture.pixels[0] > .5 else .75
    surfaces.append("texture")
    emitter = next((o for o in collection.all_objects if "sunrise_emitter_tag" in o), None)
    if emitter is not None:
        key = next(k for k in emitter.keys() if k.startswith("native_"))
        values = list(emitter[key]); values[0] += .125; emitter[key] = values
        surfaces.append("emitter")
    addon["import_controls"]("80FE2C08")
    action = next(a for a in bpy.data.actions if a.get("sunrise_native_controls") == "80FE2C08")
    bags = {bag.slot_handle: bag for bag, _ in addon["_curves"](action)}
    rotations = sorted(next([c for c in bag.fcurves if c.data_path == "rotation_quaternion"]
        for bag in bags.values() if any(c.data_path == "rotation_quaternion" for c in bag.fcurves)),
        key=lambda c: c.array_index)
    source_document = json.loads(manifest.read_text())
    source_clip = next(a for a in source_document["NativeAssets"] if a["TagHash"] == 0x80FE2C08)
    native_bytes = (manifest.parent / source_clip["Path"]).read_bytes()
    static_resource = 0x10 + struct.unpack_from("<q", native_bytes, 0x10)[0]
    assert struct.unpack_from("<H", native_bytes, static_resource)[0] == 3
    assert all(len(c.keyframe_points) == 1 for c in rotations)
    rotation = Quaternion([c.keyframe_points[0].co.y for c in rotations])
    rotation = (Quaternion((1, 0, 0), .002) @ rotation).normalized()
    for index, curve in enumerate(rotations):
        curve.keyframe_points[0].co.y = rotation[index]
        curve.update()
    surfaces.append("animation")
edited = json.loads(Path(addon["export_bundle_edits"]()).read_text())
document = json.loads(manifest.read_text())
assert len(edited["Meshes"]) == len(document["Meshes"])
assert all(mesh["Normals"] is None for mesh in edited["Meshes"])
if "--combined" in args:
    assert len(edited["Materials"]) > 0 and len(edited["Textures"]) > 0 and len(edited["Animations"]) == 1
    if "emitter" in surfaces:
        assert edited["Emitters"]
print(f'blender_bundle_real noop=pass meshes={len(meshes)} rig={sum(o.type == "ARMATURE" for o in collection.all_objects)} '
      f'actions={sum(bool(a.get("sunrise_pose_preview")) for a in bpy.data.actions)} '
      f'edit_mesh={target["sunrise_mesh_id"]} vertex={vertex_index} axis={axis} delta={amount} '
      f'surfaces={",".join(surfaces)} output={path}')
