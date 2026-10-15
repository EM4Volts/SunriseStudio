"""Exports an original asset in Blender and checks the GLB axes and the sidecar."""

import json
from pathlib import Path
import struct
import tempfile

import addon_utils
import bpy

NAME = "SunriseBlenderPlugin"

module = addon_utils.enable(NAME, default_set=True)
assert module is not None, "Blender did not load the add-on folder."
assert hasattr(bpy.types, "SUNRISE_OT_export_original_asset"), "The original asset operator is missing."
export = module.original.export_original

for obj in list(bpy.data.objects):
    bpy.data.objects.remove(obj, do_unlink=True)
output = Path(tempfile.mkdtemp(prefix="sunrise-original-"))
try:
    export(str(output / "empty.glb"))
    raise AssertionError("An export without a mesh was accepted.")
except ValueError:
    pass

mesh = bpy.data.meshes.new("Triangle")
mesh.from_pydata([(0, 0, 0), (1, 0, 0), (0, 1, 0)], [], [(0, 1, 2)])
mesh.update()
obj = bpy.data.objects.new("Triangle", mesh)
bpy.context.scene.collection.objects.link(obj)
obj.modifiers.new("Smoke", 'PARTICLE_SYSTEM')
obj.particle_systems[0].settings.count = 7
for item in bpy.context.scene.objects:
    item.select_set(False)

scene = Path(export(str(output / "asset.glb")))
assert scene.name == "asset.glb" and scene.is_file(), scene
sidecar = json.loads((output / "asset.sunrise-original.json").read_text(encoding="utf-8"))
assert sidecar["format"] == "sunrise-original-asset" and sidecar["version"] == 1, sidecar
assert sidecar["scene"] == "asset.glb" and sidecar["name"] == "asset", sidecar
assert sidecar["generator"] == "Sunrise Studio " + ".".join(map(str, module.bl_info["version"])), sidecar["generator"]
emitters = sidecar["emitters"]
assert len(emitters) == 1 and emitters[0]["object"] == "Triangle" and emitters[0]["count"] == 7, emitters
assert set(emitters[0]) == {"object", "system", "type", "count", "frameStart", "frameEnd", "lifetime", "emitFrom"}

data = scene.read_bytes()
magic, version, length = struct.unpack_from("<III", data, 0)
assert magic == 0x46546C67 and version == 2 and length == len(data), "The export is not a GLB."
chunk, kind = struct.unpack_from("<II", data, 12)
assert kind == 0x4E4F534A, "The GLB has no JSON chunk first."
document = json.loads(data[20:20 + chunk].decode("utf-8"))
assert not document.get("extensionsRequired"), document.get("extensionsRequired")
position = document["accessors"][document["meshes"][0]["primitives"][0]["attributes"]["POSITION"]]
# Blender +Y becomes glTF -Z when the export is Y-up.
assert abs(position["min"][2] + 1) < 1e-6 and abs(position["max"][1]) < 1e-6, position
print("blender_original export=glb sidecar=pass emitters=%d yup=pass empty=refused" % len(emitters))
