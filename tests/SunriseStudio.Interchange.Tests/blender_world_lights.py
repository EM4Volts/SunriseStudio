"""
World lights import as Blender lights: watts from the game intensity, soft falloff, cone and back spot, line points,
the sun, the sky world, the light volumes and the view.
"""

import json
import math
from pathlib import Path
import shutil
import sys
import tempfile

import bpy
from mathutils import Matrix, Vector

arguments = sys.argv[sys.argv.index('--') + 1:]
source = Path(arguments[0]).resolve()
plugin = Path(arguments[1]).resolve()
sys.path.insert(0, str(plugin.parent))
from SunriseBlenderPlugin import bundle, world, world_lights  # noqa: E402


def write_radiance(path, width, height, color):
    """Radiance RGBE in the export's layout: row 0 at the zenith, column u at azimuth (0.5 - u) 2 pi."""
    rows = []
    for row in range(height):
        z = math.sin((0.5 - (row + 0.5) / height) * math.pi)
        across = math.sqrt(1 - z * z)
        line = bytearray()
        for column in range(width):
            azimuth = (0.5 - (column + 0.5) / width) * 2 * math.pi
            value = color((across * math.cos(azimuth), across * math.sin(azimuth), z))
            exponent = math.frexp(max(value))[1]
            line += bytes([min(255, int(v * 256 / 2 ** exponent)) for v in value] + [exponent + 128])
        rows.append(bytes(line))
    path.write_bytes(f'#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {height} +X {width}\n'.encode() + b''.join(rows))

work = Path(tempfile.mkdtemp(prefix='sunrise-lights-'))
try:
    folder = work / 'export'
    shutil.copytree(source.parent, folder)
    lights_path = folder / world_lights.LIGHTS
    document = json.loads(lights_path.read_text())
    point = document['Lights'][0]
    spot = dict(point, Id=1, Kind='Spot', Direction=[1, 0, 0], SpotAngle=60, SpotBlend=0.5, Omni=0.25, Intensity=3.0,
                Back=0.2, BackAngle=120, BackBlend=0.5)
    line = dict(point, Id=2, Kind='Line', Direction=[0, 1, 0], Length=4.0, Range=2.0, Radius=0.1, Intensity=0.5, Points=5)
    hidden = dict(point, Id=3, Enabled=False)
    aimed = dict(line, Id=4, Aim=[0, 0, -1], SpotAngle=180, SpotBlend=0.5, Omni=0.1, Points=4)
    document['Lights'] += [spot, line, hidden, aimed]
    document['Sun'] = {'Direction': [0, 0, 1], 'Color': [1, 0.9, 0.8], 'Irradiance': 2.5, 'TimeOfDay': 0.35,
                       'CurveSetTag': 1, 'BubbleHash': 0, 'ShadowDirection': [0, 0.6, 0.8]}
    # The camera sky marks +X red, +Y green and up blue; the light image is a flat grey.
    write_radiance(folder / 'world.sky.hdr', 64, 32, lambda d: (4.0 if d[0] > 0.7 else 0.05, 4.0 if d[1] > 0.7 else 0.05,
                                                                4.0 if d[2] > 0.7 else 0.05))
    write_radiance(folder / 'world.sky-light.hdr', 16, 8, lambda d: (0.3, 0.3, 0.3))
    document['Sky'] = {'Path': 'world.sky.hdr', 'LightPath': 'world.sky-light.hdr', 'TimeOfDay': 0.35, 'Keyframe': 5.5,
                       'PlacementListTag': 2, 'LookupTag': 3, 'BubbleHash': 0}
    assert len(document.get('Volumes', [])) == 1, 'The export carries no light volume.'
    lights_path.write_text(json.dumps(document))
    bpy.ops.wm.read_factory_settings(use_empty=True)
    collection = world.import_world(folder / world.MANIFEST, 'AUTO')
    target = next(child for child in collection.children if child.name.endswith(' lights'))
    objects = {obj.name: obj for obj in target.objects}
    watts = 4 * math.pi

    lamp = objects[f'Light 0 (Point)']
    assert lamp.data.type == 'POINT' and not lamp.data.use_shadow
    assert math.isclose(lamp.data.energy, point['Intensity'] * watts, rel_tol=1e-5), 'Point watts differ from 4 pi I.'
    assert (lamp.matrix_world.translation - Vector(point['Position'])).length < 1e-4, 'Point moved.'
    assert lamp.data.use_custom_distance and math.isclose(lamp.data.cutoff_distance, point['Range'], rel_tol=1e-5)
    assert lamp.data.use_soft_falloff and math.isclose(lamp.data.shadow_soft_size, point['Radius'], rel_tol=1e-5)
    assert all(math.isclose(a, b, abs_tol=1e-5) for a, b in zip(lamp.data.color, point['Color']))

    cone = objects['Light 1 (Spot)']
    assert cone.data.type == 'SPOT' and math.isclose(math.degrees(cone.data.spot_size), 60, rel_tol=1e-4)
    axis = cone.matrix_world.to_3x3() @ Vector((0, 0, -1))
    assert (axis - Vector((1, 0, 0))).length < 1e-4, 'Spot does not shine along its direction.'
    omni = objects['Light 1 (Spot) omni']
    assert omni.data.type == 'POINT' and math.isclose(omni.data.energy, 0.25 * watts, rel_tol=1e-5)
    back = objects['Light 1 (Spot) back']
    assert back.data.type == 'SPOT' and math.isclose(back.data.energy, -0.2 * watts, rel_tol=1e-5), 'Back spot is not negative.'
    assert math.isclose(math.degrees(back.data.spot_size), 120, rel_tol=1e-4)
    assert (back.matrix_world.to_3x3() @ Vector((0, 0, -1)) - Vector((-1, 0, 0))).length < 1e-4, 'Back spot does not face back.'

    points = [obj for name, obj in objects.items() if name.startswith('Light 2 (Line)')]
    assert len(points) == 5 and all(obj.data.type == 'POINT' for obj in points), 'Line ignores its point count.'
    assert math.isclose(sum(obj.data.energy for obj in points), 0.5 * 4.0 * watts, rel_tol=1e-5), 'Line watts differ from I times length.'
    ys = sorted(obj.matrix_world.translation.y - point['Position'][1] for obj in points)
    assert ys[0] > -2 and ys[-1] < 2 and math.isclose(ys[0], -ys[-1], abs_tol=1e-4), 'Line points leave the line.'

    beams = [obj for name, obj in objects.items() if name.startswith('Light 4 (Line)') and obj.data.type == 'SPOT']
    fills = [obj for name, obj in objects.items() if name.startswith('Light 4 (Line)') and obj.data.type == 'POINT']
    assert len(beams) == 4 and len(fills) == 4, 'An aimed line does not become spots and omni points.'
    assert all((obj.matrix_world.to_3x3() @ Vector((0, 0, -1)) - Vector((0, 0, -1))).length < 1e-4 for obj in beams)
    assert math.isclose(sum(obj.data.energy for obj in beams), 0.5 * 4.0 * watts, rel_tol=1e-5)
    assert math.isclose(sum(obj.data.energy for obj in fills), 0.1 * 4.0 * watts, rel_tol=1e-5)

    off = objects['Light 3 (Point)']
    assert off.hide_render and off.hide_viewport, 'A disabled light renders.'

    sun = next(obj for obj in target.objects if obj.type == 'LIGHT' and obj.data.type == 'SUN')
    assert math.isclose(sun.data.energy, 2.5, rel_tol=1e-6)
    assert (sun.matrix_world.to_3x3() @ Vector((0, 0, -1)) - Vector((0, 0, -1))).length < 1e-4, 'Sun does not shine away from its direction.'
    assert sun.data.use_shadow and list(sun['sunrise_shadow_direction']) == [0, 0.6, 0.8], 'Sun lost its shadows.'

    # Cycles ignores Custom Distance; each local light's shader repeats the window.
    for obj in (lamp, cone, back, points[0]):
        group = next(node for node in obj.data.node_tree.nodes if node.bl_idname == 'ShaderNodeGroup')
        assert group.node_tree.name == world_lights.WINDOW_GROUP
        assert math.isclose(group.inputs['Range'].default_value, obj.data.cutoff_distance, rel_tol=1e-6)
        assert group.outputs['Window'].links[0].to_node.bl_idname == 'ShaderNodeEmission', 'The window does not scale the light.'

    scene = bpy.context.scene
    assert scene.world.name == target['sunrise_world_sky'] and scene.world['sunrise_atmosphere'] == '00000002'
    assert (scene.view_settings.view_transform, scene.view_settings.look, scene.view_settings.exposure) \
        == (world_lights.VIEW_TRANSFORM, world_lights.VIEW_LOOK, world_lights.EXPOSURE), 'The view was not set.'
    assert scene['sunrise_view_transform'] == 'AgX' and scene['sunrise_exposure'] == 0.0

    volume = next(obj for obj in target.objects if obj.type == 'LIGHT_PROBE')
    info = document['Volumes'][0]
    expected = bundle._matrix(info['Transform']) @ Matrix.Diagonal((*info['Extents'], 1))
    assert all(math.isclose(a, b, abs_tol=1e-4) for row, other in zip(volume.matrix_world, expected) for a, b in zip(row, other)), \
        'The light volume left its box.'
    assert volume.data.type == 'VOLUME' and volume.data.resolution_x == world_lights.VOLUME_MIN
    assert target['sunrise_light_volume_bake'] == '1 light volumes, bake finished', target['sunrise_light_volume_bake']

    # The camera sees the sky image along each axis; surfaces receive the light image.
    for obj in target.objects:
        obj.hide_render = obj.type == 'LIGHT'
    scene.render.engine = 'CYCLES'
    scene.cycles.samples = 16
    scene.cycles.device = 'CPU'
    scene.render.resolution_x = scene.render.resolution_y = 8
    scene.render.image_settings.file_format = 'OPEN_EXR'
    camera = bpy.data.objects.new('camera', bpy.data.cameras.new('camera'))
    scene.collection.objects.link(camera)
    camera.data.angle = 0.05
    scene.camera = camera

    def render(direction, at=(0, 0, 50)):
        camera.location = at
        camera.rotation_mode = 'QUATERNION'
        camera.rotation_quaternion = Vector(direction).to_track_quat('-Z', 'Y')
        scene.render.filepath = str(work / 'probe.exr')
        bpy.ops.render.render(write_still=True)
        image = bpy.data.images.load(scene.render.filepath)
        pixels = list(image.pixels)
        bpy.data.images.remove(image)
        count = len(pixels) // 4
        return [sum(pixels[c::4]) / count for c in range(3)]

    # Up is tilted off the pole, where Blender blends the first and last rows.
    for direction, channel in (((1, 0, 0.01), 0), ((0, 1, 0.01), 1), ((0.2, 0, 1), 2)):
        color = render(direction)
        assert color[channel] > 3 and all(color[c] < 0.2 for c in range(3) if c != channel), f'Sky along {direction}: {color}'
    # A plane with no material renders Blender's default 0.8 grey diffuse.
    bpy.ops.mesh.primitive_plane_add(size=1000, location=(0, 0, 49))
    lit = render((0, 0, -1))
    assert all(math.isclose(c, 0.3 * 0.8, rel_tol=0.1) for c in lit), f'The surface is not lit by the light image: {lit}'
    print(f'blender_world_lights=pass lights={len(target.objects)} line_points={len(points)} sky=pass volume=pass')
finally:
    shutil.rmtree(work, ignore_errors=True)
