"""A guardian bundle's gear parts import with their draw textures, detail UVs and a wired base colour; face decals blend."""

import json
from pathlib import Path
import runpy
import sys

import bpy


def surface_sockets(material):
    """The colour and alpha inputs of the shader at the active output, or None for an unknown shader."""
    tree = material.node_tree
    target = next(node for node in tree.nodes if node.bl_idname == "ShaderNodeOutputMaterial" and node.is_active_output)
    shader = target.inputs["Surface"].links[0].from_node if target.inputs["Surface"].is_linked else None
    if shader is None:
        return None
    if shader.bl_idname == "ShaderNodeBsdfPrincipled":
        return shader.inputs["Base Color"], shader.inputs["Alpha"]
    # A forward blend has no Principled BSDF: an emission adds the colour to a transparent mix.
    sources = {link.from_node.bl_idname: link.from_node for socket in shader.inputs for link in socket.links}
    if shader.bl_idname != "ShaderNodeAddShader" or not {"ShaderNodeEmission", "ShaderNodeMixShader"} <= sources.keys():
        return None
    return sources["ShaderNodeEmission"].inputs["Color"], sources["ShaderNodeMixShader"].inputs[0]


arguments = sys.argv[sys.argv.index("--") + 1:]
bpy.ops.wm.read_factory_settings(use_empty=True)
manifest = Path(arguments[0]).absolute()
addon = runpy.run_path(str(Path(arguments[1]).absolute()), run_name="sunrise_bundle_addon")
render = Path(arguments[2]).absolute() if len(arguments) > 2 else None
document = json.loads(manifest.read_text(encoding="utf-8"))
collection = addon["import_bundle"](str(manifest))
objects = {obj.get("sunrise_mesh_id"): obj for obj in collection.all_objects if obj.get("sunrise_mesh_id")}
variants = {(m["TagHash"], m["Variant"]): m for m in document["Materials"] if m.get("Variant")}
checked, failures = 0, []
for info in document["Meshes"]:
    if not info.get("MaterialVariant"):
        continue
    entry = variants[(info["MaterialTag"], info["MaterialVariant"])]
    obj = objects[info["Id"]]
    label = f'{info["Id"]} ({entry.get("Name")})'
    material = obj.data.materials[0] if obj.data.materials else None
    if material is None or not material.name.startswith(entry["Name"]):
        failures.append(f"{label}: the part does not use its variant material")
        continue
    sockets = surface_sockets(material)
    outputs = (entry["Preview"].get("Program") or {}).get("Outputs") or {}
    if sockets is None:
        failures.append(f"{label}: the output has no Principled BSDF or forward blend surface")
    elif "BaseColor" in outputs and not sockets[0].is_linked:
        failures.append(f"{label}: the base colour program is not wired")
    elif "Alpha" in outputs and entry["Preview"].get("BlendMode") != "Additive" and not sockets[1].is_linked:
        failures.append(f"{label}: the alpha program is not wired")
    overrides = {int(node["sunrise_preview_slot"]): node for node in material.node_tree.nodes
                 if node.get("sunrise_preview_override")}
    for slot in (entry["Preview"].get("SlotTextures") or {}):
        if int(slot) not in overrides or overrides[int(slot)].image is None:
            failures.append(f"{label}: draw texture t{slot} has no image")
    geometry = json.loads((manifest.parent / info["Path"]).read_text(encoding="utf-8"))
    if geometry.get("TexCoords1") is not None and "UVMap1" not in obj.data.uv_layers:
        failures.append(f"{label}: the detail UVs were not imported")
    checked += 1
assert checked, "The bundle has no part variants."
# Face decals blend lit albedo with alpha, stage 1 face parts show, and texture alpha stays a data channel.
decals = 0
for info in document["Meshes"]:
    entry = next((m for m in document["Materials"] if m["TagHash"] == info["MaterialTag"]
                  and m.get("Variant") == info.get("MaterialVariant")), None)
    obj = objects[info["Id"]]
    material = obj.data.materials[0] if obj.data.materials else None
    if entry is None or material is None:
        continue
    label = f'{info["Id"]} ({entry.get("Name")})'
    preview = entry.get("Preview") or {}
    outputs = (preview.get("Program") or {}).get("Outputs") or {}
    if info.get("RenderStage") == 1 and obj.hide_render:
        failures.append(f"{label}: a stage 1 guardian part is hidden")
    tree = material.node_tree
    target = next(node for node in tree.nodes if node.bl_idname == "ShaderNodeOutputMaterial" and node.is_active_output)
    if preview.get("Deferred") and preview.get("BlendMode") in ("InverseAlpha", "PremultipliedAlpha") and "BaseColor" in outputs:
        decals += 1
        surface = target.inputs["Surface"].links[0].from_node if target.inputs["Surface"].is_linked else None
        lit = surface is not None and surface.bl_idname == "ShaderNodeBsdfPrincipled"
        if not (lit and surface.inputs["Base Color"].is_linked and surface.inputs["Alpha"].is_linked
                and material.surface_render_method == "BLENDED"):
            failures.append(f"{label}: the decal is not a lit, blended surface with wired colour and alpha")
    for node in tree.nodes:
        if node.bl_idname == "ShaderNodeTexImage" and node.image is not None and node.image.alpha_mode != "CHANNEL_PACKED":
            failures.append(f"{label}: image {node.image.name} premultiplies its alpha channel")
            break
assert decals, "The bundle has no face decal."
assert not failures, "\n".join(failures)
if render is not None:
    # Front view of the whole guardian, for a side-by-side look against Studio.
    bpy.ops.object.camera_add(location=(4.5, 0, 1.0), rotation=(1.5708, 0, 1.5708))
    camera = bpy.context.object
    camera.data.lens = 40
    scene = bpy.context.scene
    scene.camera = camera
    bpy.ops.object.light_add(type="SUN", rotation=(0.6, 0.2, 1.2))
    scene.world = scene.world or bpy.data.worlds.new("World")
    scene.world.color = (0.3, 0.3, 0.3)
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x, scene.render.resolution_y = 480, 640
    scene.render.filepath = str(render)
    bpy.ops.render.render(write_still=True)
print(f"blender_guardian_materials variants={len(variants)} parts={checked} decals={decals} result=pass")
