"""Shared world geometry keeps the per-asset native point basis under each full instance matrix."""

import json
from pathlib import Path
import sys

import bpy
import numpy as np
from mathutils import Matrix

arguments = sys.argv[sys.argv.index('--') + 1:]
oracle = json.loads(Path(arguments[0]).read_text())
plugin = Path(arguments[1]).resolve()
sys.path.insert(0, str(plugin.parent))
from SunriseBlenderPlugin import world, bundle

bpy.ops.wm.read_factory_settings(use_empty=True)
legacy = bundle.import_bundle(oracle['AssetManifest'])
source = next(obj for obj in legacy.all_objects if obj.get('sunrise_mesh_id') == oracle['AssetMeshId'])
modern = world.import_world(oracle['WorldManifest'])
placements = {obj['sunrise_world_instance']: obj for obj in modern.all_objects if 'sunrise_world_instance' in obj}
parts = {index: next(obj for obj in parent.children if obj.type == 'MESH') for index, parent in placements.items()}
assert len({obj.data for obj in parts.values()}) == 1
assert len({obj.modifiers[-1].node_group for obj in parts.values()}) == 1
assert all(parent.instance_type == 'NONE' for parent in placements.values())
anchor = bpy.data.objects.new('Per-asset oracle origin', None)
bpy.context.scene.collection.objects.link(anchor)
source.hide_viewport = False
source.hide_set(False)


def values(obj):
    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = evaluated.to_mesh()
    try:
        output = {}
        for name in ('normal', 'tangent', 'bitangent', 'basis', 'reflected'):
            attribute = mesh.attributes['sunrise_shader_' + name]
            width = 3 if attribute.data_type == 'FLOAT_VECTOR' else 1
            data = np.zeros(len(attribute.data) * width, dtype=np.float32)
            attribute.data.foreach_get('vector' if width == 3 else 'value', data)
            assert np.isfinite(data).all()
            output[name] = data
        return output
    finally:
        evaluated.to_mesh_clear()


errors = {}
for index, transform in enumerate(oracle['WorldTransforms']):
    world._place(source, anchor, bundle._matrix(transform))
    bpy.context.view_layer.update()
    expected, actual = values(source), values(parts[index])
    for key in expected:
        assert expected[key].shape == actual[key].shape
        error = float(np.max(np.abs(expected[key] - actual[key])))
        errors[key] = max(errors.get(key, 0), error)
        assert error < .00002, (index, key, error)
    assert bundle._close(bundle._flat(parts[index].matrix_world), bundle._flat(bundle._matrix(transform)))
print('blender_world_basis shared_geometry=pass shared_group=pass per_asset=matched instances=4 transforms=reflection_nonuniform_shear')
print(json.dumps(errors))
