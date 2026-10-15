"""Collision section of an asset bundle in background Blender: import, identity, edits and refusals.

Arguments after --: an empty output folder, bundle.py, and optionally an owned bundle manifest to
move one collision object in (its edits file is then written beside it).
"""

import hashlib
import json
from pathlib import Path
import runpy
import sys

import bmesh
import bpy
from mathutils import Vector


arguments = sys.argv[sys.argv.index("--") + 1:]
root = Path(arguments[0]).absolute()
addon_path = Path(arguments[1]).absolute()
owned = Path(arguments[2]).absolute() if len(arguments) > 2 else None
addon = runpy.run_path(str(addon_path), run_name="sunrise_bundle_addon")
addon["register"]()


def refused(action, expected):
    try:
        action()
    except ValueError as error:
        assert expected.lower() in str(error).lower(), (expected, str(error))
    else:
        raise AssertionError("Unsupported collision edit was accepted: " + expected)


def cube_object(name, group, at):
    """A new 1 m cube in the Collision collection, with no Sunrise identity."""
    data = bpy.data.meshes.new(name)
    mesh = bmesh.new()
    bmesh.ops.create_cube(mesh, size=1.0)
    mesh.to_mesh(data)
    mesh.free()
    obj = bpy.data.objects.new(name, data)
    group.objects.link(obj)
    obj.matrix_world.translation = at
    return obj


if owned is not None:
    # Move the first editable collision object one metre along x, add a cube above it, and write the edits file.
    collection = addon["import_bundle"](str(owned))
    target = next(o for o in collection.all_objects if o.get("sunrise_collision_editable"))
    target.matrix_world.translation += Vector((1, 0, 0))
    group = next(c for c in collection.children if c.get("sunrise_collision"))
    cube = cube_object("added cube", group, target.matrix_world.translation + Vector((0, 0, 2)))
    edits = json.loads(Path(addon["export_bundle_edits"](str(owned))).read_text())
    collision = edits["Collision"]
    assert len(collision["Placements"]) == 1 and not collision["Shapes"], collision
    assert len(collision["Added"]) == 1 and collision["Added"][0]["Source"] is None, collision["Added"]
    assert len(collision["Added"][0]["Positions"]) == 8 and len(collision["Added"][0]["Indices"]) == 36
    print(f'blender_collision_owned moved={target["sunrise_collision_id"]} compound={target["sunrise_collision_compound"]} '
          f'instance={target["sunrise_collision_instance"]} added={cube.name} triangles=12')
    sys.exit(0)

root.mkdir(parents=True, exist_ok=True)
if any(root.iterdir()):
    raise ValueError("Smoke output folder must be empty.")


def write(relative, document):
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document), encoding="utf-8")
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


corners = [[x, y, z] for z in (-1, 1) for y in (-1, 1) for x in (-1, 1)]
box = [0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5]


def placed(x, y, z):
    return [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, x, y, z, 1]


pieces = [{"Id": "piece_0", "Tag": 0x80A00200, "Offset": 16, "Class": "hkpBvCompressedMeshShape", "Kind": "Mesh",
           "Positions": corners, "Indices": box},
          {"Id": "piece_1", "Tag": 0x80A00201, "Offset": 16, "Class": "hkpSphereShape", "Kind": "Sphere",
           "Positions": [], "Indices": []}]
placements = [
    {"Id": "placement_0", "Piece": "piece_0", "Name": "Mesh 80A00200 @80A00100#0", "Transform": placed(0, 0, 0),
     "Source": "StaticEntity", "Entity": 0x80A00101, "CompoundTag": 0x80A00100, "CompoundOffset": 16, "Instance": 0, "Editable": True},
    {"Id": "placement_1", "Piece": "piece_0", "Name": "Mesh 80A00200 @80A00100#1", "Transform": placed(2000, 0, 0),
     "Source": "StaticEntity", "Entity": 0x80A00101, "CompoundTag": 0x80A00100, "CompoundOffset": 16, "Instance": 1, "Editable": True},
    {"Id": "placement_2", "Piece": "piece_1", "Name": "Sphere 80A00201 @80A00100#2", "Transform": placed(0, 5, 0),
     "Source": "StaticEntity", "Entity": 0x80A00101, "CompoundTag": 0x80A00100, "CompoundOffset": 16, "Instance": 2, "Editable": True},
    {"Id": "placement_3", "Piece": "piece_0", "Name": "Mesh 80A00200", "Transform": placed(0, 0, 5),
     "Source": "Asset", "Entity": 0x80A00200, "CompoundTag": 0, "CompoundOffset": -1, "Instance": -1, "Editable": False}]
placements[3]['Transform'][1] = 0.00004
collision_hash = write("collision/collision.json", {"Format": "sunrise-collision", "Version": 1, "RootTag": 0x80A00001,
    "Sources": {}, "Pieces": pieces, "Placements": placements, "Notes": []})
write("asset.sunrise-bundle.json", {"Format": "sunrise-asset-bundle", "Version": 1, "RootTag": 0x80A00001,
    "SelectedTag": 0x80A00001, "Name": "Collision bundle", "Context": None, "ScenePath": None, "Meshes": [],
    "Materials": [], "Textures": [], "Shaders": [], "Animations": [], "NativeAssets": [], "Notices": [],
    "ParticleMetadataPath": None, "CollisionPath": "collision/collision.json", "CollisionSha256": collision_hash})
manifest = root / "asset.sunrise-bundle.json"
collection = addon["import_bundle"](str(manifest))


def export():
    return json.loads(Path(addon["export_bundle_edits"](str(manifest))).read_text())["Collision"]


group = next(c for c in collection.children if c.get("sunrise_collision"))
assert group.name.startswith("Collision") and len(group.objects) == 4
objects = {o["sunrise_collision_id"]: o for o in group.objects}
first, second, sphere, alone = (objects[f"placement_{i}"] for i in range(4))
assert first.display_type == 'WIRE' and first.hide_render and sphere.type == 'EMPTY' and sphere.hide_render
assert first.data is second.data and first.data is alone.data, "Placements of one shape must share its mesh."
assert first["sunrise_collision_compound"] == "80A00100" and second["sunrise_collision_instance"] == 1
assert first["sunrise_collision_shape"] == "80A00200" and first["sunrise_collision_kind"] == "Mesh"
assert tuple(second.matrix_world.translation) == (2000, 0, 0) and len(first.data.polygons) == 12
NONE = {"Placements": [], "Shapes": [], "Added": []}
edits = export()
assert edits == NONE, edits
canonical = alone.matrix_world.copy()
assert max(abs(a - b) for a, b in zip(addon['_flat'](canonical.transposed()), placements[3]['Transform'])) > .00001
alone.matrix_world.translation.x += .01
refused(export, 'cannot move')
alone.matrix_world = canonical
alone.rotation_euler.z += .001
bpy.context.view_layer.update()
refused(export, 'cannot move')
alone.matrix_world = canonical
assert export() == NONE
baseline = alone['sunrise_collision_baseline_transform']
del alone['sunrise_collision_baseline_transform']
assert export() == NONE
alone['sunrise_collision_baseline_transform'] = baseline

second.matrix_world.translation += Vector((0.01, 0, 0))
edits = export()
assert [p["Id"] for p in edits["Placements"]] == ["placement_1"], edits
second.matrix_world.translation.x = 2000
linked = first.data
first.data = linked.copy()
first.data.vertices[0].co.z -= 0.5
before = (root / "asset.edited.sunrise-bundle.json").read_bytes()
refused(export, "different meshes")
assert (root / "asset.edited.sunrise-bundle.json").read_bytes() == before
first.data = linked
assert export() == NONE

second.matrix_world.translation += Vector((0, 0, 2))
edits = export()
assert [p["Id"] for p in edits["Placements"]] == ["placement_1"] and abs(edits["Placements"][0]["Transform"][14] - 2) < 1e-6
second.matrix_world.translation -= Vector((0, 0, 2))

first.data.vertices[0].co.z -= .5
first.data.update()
edits = export()
assert not edits["Placements"] and edits["Shapes"][0]["Piece"] == "piece_0"
assert abs(edits["Shapes"][0]["Positions"][0][2] + 1.5) < 1e-6 and edits["Shapes"][0]["Indices"] == box
first.data.vertices[0].co.z += .5
first.data.update()
assert export() == NONE

# A copy that shares the mesh is another instance; a new cube is a new shape with its properties.
copy = second.copy()
group.objects.link(copy)
copy.matrix_world.translation += Vector((0, 3, 0))
cube = cube_object("new cube", group, Vector((0, 0, 9)))
cube["sunrise_collision_kind"] = "Convex"
cube["sunrise_collision_material"] = "80A00300"
edits = export()
assert not edits["Placements"] and not edits["Shapes"] and len(edits["Added"]) == 2, edits
by_name = {a["Name"]: a for a in edits["Added"]}
assert by_name[copy.name]["Source"] == "placement_1" and "Positions" not in by_name[copy.name]
assert abs(by_name[copy.name]["Transform"][13] - 3) < 1e-6
added = by_name["new cube"]
assert added["Source"] is None and added["Kind"] == "Convex" and added["Material"] == "80A00300"
assert len(added["Positions"]) == 8 and len(added["Indices"]) == 36 and added["Transform"][14] == 9
alone_copy = alone.copy()
group.objects.link(alone_copy)
refused(export, "no stored transform")
for obj in (copy, cube, alone_copy):
    bpy.data.objects.remove(obj)
assert export() == NONE

mesh = bmesh.new()
mesh.from_mesh(first.data)
mesh.verts.ensure_lookup_table()
a, b = mesh.verts[4], mesh.verts[5]
mesh.faces.new((a, b, mesh.verts.new((0, 0, 3))))
mesh.to_mesh(first.data)
mesh.free()
edits = export()
assert len(edits["Shapes"][0]["Positions"]) == 9 and len(edits["Shapes"][0]["Indices"]) == 39, edits["Shapes"]

alone.matrix_world.translation += Vector((1, 0, 0))
refused(export, "no stored transform")
alone.matrix_world.translation -= Vector((1, 0, 0))
group.objects.unlink(sphere)
refused(export, "removed")
group.objects.link(sphere)
print("blender_collision import=pass shared-mesh=pass wire=pass identity=pass noop=pass move=pass vertex=pass "
      "copy=added new-cube=added copy-alone=refused topology=pass alone=refused removed=refused")
