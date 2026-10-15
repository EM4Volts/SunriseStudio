"""Exports owned asymmetric materials through the add-on's original-asset exporter."""

import json
from pathlib import Path
import struct
import sys
import tempfile

import bpy

# The add-on loads from the Blender profile that verify.ps1 sets up.
from SunriseBlenderPlugin import original

target = Path(sys.argv[sys.argv.index('--') + 1]).resolve()
target.mkdir(parents=True, exist_ok=True)
assert not any(target.iterdir()), 'Fixture output must be empty.'
scratch = target / 'scratch'
scratch.mkdir()
tempfile.tempdir = str(scratch)
for obj in list(bpy.data.objects):
    bpy.data.objects.remove(obj, do_unlink=True)


def image(name, values, space):
    result = bpy.data.images.new(name, width=2, height=2, alpha=True)
    result.colorspace_settings.name = space
    result.pixels.foreach_set(values)
    result.update()
    result.pack()
    return result


color = image('Asymmetric RGB', [1, 0, 0, 1, 0, 1, 0, 1,
                                 0, 0, 1, 1, 1, 1, 0, 1], 'sRGB')
surface = image('Metal Rough', [.25, .8, .1, 1] * 4, 'Non-Color')
normal = image('Normal +Y', [.5, .8, .9, 1] * 4, 'Non-Color')
material = bpy.data.materials.new('Painted')
material.use_backface_culling = True
nodes, links = material.node_tree.nodes, material.node_tree.links
bsdf = nodes.get('Principled BSDF')
for source, role in ((color, 'color'), (surface, 'surface'), (normal, 'normal')):
    sample = nodes.new('ShaderNodeTexImage'); sample.image = source
    if role == 'color':
        links.new(sample.outputs['Color'], bsdf.inputs['Base Color'])
    elif role == 'surface':
        channels = nodes.new('ShaderNodeSeparateColor')
        links.new(sample.outputs['Color'], channels.inputs[0])
        links.new(channels.outputs['Green'], bsdf.inputs['Roughness'])
        links.new(channels.outputs['Blue'], bsdf.inputs['Metallic'])
    else:
        unpack = nodes.new('ShaderNodeNormalMap')
        links.new(sample.outputs['Color'], unpack.inputs['Color'])
        links.new(unpack.outputs['Normal'], bsdf.inputs['Normal'])

solid = bpy.data.materials.new('Solid blue')
solid.use_backface_culling = True
solid.node_tree.nodes.get('Principled BSDF').inputs['Base Color'].default_value = (.1, .3, .7, 1)
solid.node_tree.nodes.get('Principled BSDF').inputs['Roughness'].default_value = .35
solid.node_tree.nodes.get('Principled BSDF').inputs['Metallic'].default_value = .65

mesh = bpy.data.meshes.new('Two material panels')
mesh.from_pydata([(-1, -1, 0), (0, -1, 0), (0, 1, 0), (-1, 1, 0),
                  (0, -1, 0), (1, -1, 0), (1, 1, 0), (0, 1, 0)], [], [(0, 1, 2, 3), (4, 5, 6, 7)])
mesh.materials.append(material); mesh.materials.append(solid)
mesh.polygons[1].material_index = 1
uvs = mesh.uv_layers.new(name='UVMap')
for polygon in mesh.polygons:
    for loop, uv in zip(polygon.loop_indices, ((0, 0), (1, 0), (1, 1), (0, 1))):
        uvs.data[loop].uv = uv
obj = bpy.data.objects.new('Owned panels', mesh)
bpy.context.scene.collection.objects.link(obj)
obj.select_set(True); bpy.context.view_layer.objects.active = obj
path = Path(original.export_original(target / 'panels.glb'))
data = path.read_bytes(); length, kind = struct.unpack_from('<II', data, 12)
assert kind == 0x4e4f534a
document = json.loads(data[20:20 + length])
assert len(document['meshes'][0]['primitives']) == 2
assert {p['material'] for p in document['meshes'][0]['primitives']} == {0, 1}
assert all(item.get('mimeType') == 'image/png' for item in document['images'])
assert all(not item.get('doubleSided', False) for item in document['materials'])
bpy.ops.wm.save_as_mainfile(filepath=str(target / 'panels.blend'))
(target / 'export.json').write_text(json.dumps(document, indent=2))
print('original_materials parts=2 images=png uv=asymmetric normal=positive_y source=owned')
