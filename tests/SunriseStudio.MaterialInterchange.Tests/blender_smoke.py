import json
import runpy
import sys
from pathlib import Path

import bpy

args = sys.argv[sys.argv.index("--") + 1:]
root, plugin = Path(args[0]).resolve(), Path(args[1]).resolve()
namespace = runpy.run_path(str(plugin / "material.py"), run_name="sunrise_material_smoke")
namespace["register"]()
material = namespace["import_material"](root / "material.sunrise-material.json", bpy.context.object)
nodes = list(material.node_tree.nodes)
slots = [node for node in nodes if "sunrise_slot_offset" in node]
constants = [node for node in nodes if "sunrise_constant_offset" in node]
assert len(slots) == 2 and len(constants) == 8
assert all(node.image is not None for node in slots)
assert all(not node.outputs[0].is_linked for node in slots)
assert bpy.context.object.active_material == material
assert hasattr(bpy.types, "SUNRISE_PT_material")
assert hasattr(bpy.ops.sunrise, "import_material")
document = json.loads((root / "material.sunrise-material.json").read_text())
constants[0].outputs[0].default_value = 2.5
slots[0]["sunrise_texture_tag"] = f'{document["Slots"][1]["TextureTag"]:08X}'
output = Path(namespace["export_material_edits"](material))
edited = json.loads(output.read_text())
assert edited["BaseSha256"] == document["BaseSha256"]
assert edited["Slots"][0]["TextureTag"] == document["Slots"][1]["TextureTag"]
assert edited["Slots"][0]["SourceTextureTag"] == document["Slots"][0]["SourceTextureTag"]
assert float(edited["Constants"][0]["Value"]) == 2.5
assert all(a["Offset"] == b["Offset"] for a, b in zip(edited["Constants"], document["Constants"]))
bpy.ops.wm.save_as_mainfile(filepath=str(root / "material-smoke.blend"))
print("blender_material nodes=pass images=pass constants=pass assignments=pass ui=registered")
