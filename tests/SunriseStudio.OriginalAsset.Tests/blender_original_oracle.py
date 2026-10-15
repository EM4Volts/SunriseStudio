"""The owned Blender material graph supplies linear color and world-normal reference pixels."""

import json
import math
from pathlib import Path
import sys

import bpy

folder = Path(sys.argv[sys.argv.index('--') + 1]).resolve()
bpy.ops.wm.open_mainfile(filepath=str(folder / 'panels.blend'))
scene = bpy.context.scene
scene.render.engine = 'CYCLES'
scene.cycles.device = 'CPU'
scene.cycles.samples = 1
scene.cycles.use_denoising = False
scene.render.resolution_x = scene.render.resolution_y = 512
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = 'OPEN_EXR'
scene.render.image_settings.color_depth = '32'
scene.render.image_settings.color_mode = 'RGBA'
scene.view_settings.view_transform = 'Standard'
scene.view_settings.exposure = 0
scene.view_settings.gamma = 1
camera = bpy.data.objects.new('Oracle camera', bpy.data.cameras.new('Oracle camera'))
scene.collection.objects.link(camera)
camera.location = (0, 0, 5)
camera.data.type = 'PERSP'
camera.data.sensor_fit = 'HORIZONTAL'
camera.data.angle = math.pi / 3
scene.camera = camera
report = json.loads((folder / 'logs/native-materials.json').read_text())
materials = [bpy.data.materials['Painted'], bpy.data.materials['Solid blue']]
results = {}
for role in ('Color', 'Normal'):
    for material in materials:
        nodes, links = material.node_tree.nodes, material.node_tree.links
        output = next(node for node in nodes if node.type == 'OUTPUT_MATERIAL')
        bsdf = next(node for node in nodes if node.type == 'BSDF_PRINCIPLED')
        emit = nodes.new('ShaderNodeEmission')
        links.new(emit.outputs[0], output.inputs['Surface'])
        if role == 'Color':
            color = bsdf.inputs['Base Color']
            if color.is_linked:
                links.new(color.links[0].from_socket, emit.inputs['Color'])
            else:
                emit.inputs['Color'].default_value = color.default_value
        else:
            normal = bsdf.inputs['Normal']
            source = normal.links[0].from_socket if normal.is_linked else nodes.new('ShaderNodeNewGeometry').outputs['Normal']
            scale = nodes.new('ShaderNodeVectorMath'); scale.operation = 'SCALE'; scale.inputs['Scale'].default_value = .5
            bias = nodes.new('ShaderNodeVectorMath'); bias.operation = 'ADD'; bias.inputs[1].default_value = (.5, .5, .5)
            links.new(source, scale.inputs[0]); links.new(scale.outputs[0], bias.inputs[0]); links.new(bias.outputs[0], emit.inputs['Color'])
    path = folder / ('blender-' + role.lower() + '.exr')
    scene.render.filepath = str(path)
    bpy.ops.render.render(write_still=True)
    image = bpy.data.images.load(str(path), check_existing=False)
    pixels = list(image.pixels)
    values = []
    for sample in report['Samples']:
        point = sample['Point']; x, y = point['Item1'], point['Item2']
        offset = 4 * ((511 - y) * 512 + x)
        values.append(pixels[offset:offset + 3])
    results[role] = values
    bpy.data.images.remove(image)


def linear(value):
    return value / 12.92 if value <= .04045 else ((value + .055) / 1.055) ** 2.4


color_error = max(abs(linear(sample['Albedo'][axis]) - results['Color'][i][c])
                  for i, sample in enumerate(report['Samples']) for c, axis in enumerate(('X', 'Y', 'Z')))
normal_error = max(abs(sample['Normal'][axis] - (results['Normal'][i][c] * 2 - 1))
                   for i, sample in enumerate(report['Samples']) for c, axis in enumerate(('X', 'Y', 'Z')))
results['NativeColorError'] = color_error
results['NativeNormalError'] = normal_error
(folder / 'logs/blender-oracle.json').write_text(json.dumps(results, indent=2))
assert color_error < .04, color_error
assert normal_error < .035, normal_error
print('original_oracle color_error=%.7f normal_error=%.7f parts=2 result=pass' % (color_error, normal_error))
