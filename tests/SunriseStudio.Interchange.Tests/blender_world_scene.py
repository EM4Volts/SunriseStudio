"""World imports share resource data and preserve full placement matrices and hidden parts."""

import json
import hashlib
from pathlib import Path
import struct
import shutil
import sys
import uuid

import bpy
import numpy as np
from mathutils import Vector

arguments = sys.argv[sys.argv.index('--') + 1:]
manifest_path = Path(arguments[0]).resolve()
plugin = Path(arguments[1]).resolve()
sys.path.insert(0, str(plugin.parent))
from SunriseBlenderPlugin import world, bundle

document = json.loads(manifest_path.read_text())
view = document['Instances']['Rows']
buffer_path = manifest_path.parent / document['Buffers'][view['Buffer']]['Path']
records = buffer_path.read_bytes()[view['Offset']:view['Offset'] + view['Count'] * 128]
rows = list(struct.iter_unpack('<II16fIIiIiIQIIiiII', records))


def capture(mode):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.world = bpy.data.worlds.new('user world')
    scene.view_settings.view_transform = 'Standard'
    collection = world.import_world(manifest_path, mode)
    lights = json.loads((manifest_path.parent / 'world.lights.json').read_text())
    if lights.get('Sky') is None:
        assert scene.world.name == 'user world' and scene.view_settings.view_transform == 'Standard', \
            'An export with no sky replaced the scene world or view.'
    counts = json.loads(collection['sunrise_world_counts'])
    assert counts['geometry'] == len(document['Geometry']) == 1
    assert len(bpy.data.meshes) == 1, 'Geometry was duplicated or rebuilt after import.'
    assert counts['placements'] == len(rows) == 3
    assert len(bpy.data.materials) == len(document['Materials']) == 2
    placements = {obj['sunrise_world_instance']: obj for obj in collection.all_objects if 'sunrise_world_instance' in obj}
    for index, row in enumerate(rows):
        assert bundle._close(bundle._flat(placements[index].matrix_world), bundle._flat(bundle._matrix(row[2:18]))), \
            'World matrix lost reflection, nonuniform scale or shear.'
    part_objects = [obj for obj in bpy.data.objects if obj.type == 'MESH']
    assert all(obj.data == bpy.data.meshes[0] for obj in part_objects)
    for obj in part_objects:
        assert obj.material_slots[0].link == 'OBJECT'
        assert obj.material_slots[0].material['sunrise_world_material'] == obj['sunrise_world_material']
        assert obj.hide_render == (not obj['sunrise_initially_visible'])
    if mode == 'AUTO':
        assert all(obj.instance_type == 'COLLECTION' for obj in placements.values())
        assert len(part_objects) < document['Statistics']['Parts']
    else:
        assert len(part_objects) == document['Statistics']['Parts'] == 6
        hidden = [obj for obj in part_objects if obj.hide_viewport]
        for obj in hidden:
            obj.hide_viewport = False
        bpy.context.view_layer.update()
        for index, row in enumerate(rows):
            expected = document['Prototypes'][row[0]]['Parts']
            children = sorted([obj for obj in placements[index].children if obj.type == 'MESH'], key=lambda obj: obj['sunrise_part_index'])
            for obj, part in zip(children, expected):
                matrix = bundle._matrix(row[2:18]) @ bundle._matrix(part['Transform'])
                assert bundle._close(bundle._flat(obj.matrix_world), bundle._flat(matrix)), \
                    (index, part['PartIndex'], bundle._flat(obj.matrix_world), bundle._flat(matrix),
                     bundle._flat(obj.matrix_basis), bundle._flat(obj.matrix_parent_inverse))
        for obj in hidden:
            obj.hide_viewport = True
        bpy.context.view_layer.update()
    origin = next(obj for obj in collection.objects if obj.parent is None and 'sunrise_world_instance' not in obj)
    marker_collection = next(child for child in collection.children if child.name.endswith(' markers'))
    marker_collection.hide_viewport = False
    marker = next(iter(marker_collection.objects))
    assert marker.parent == origin
    origin.location.x += 2
    bpy.context.view_layer.update()
    assert abs(marker.matrix_world.translation.x - (document['Markers'][0]['Position'][0] + 2)) < .00001
    marker_collection.hide_viewport = True
    points = []
    for instance in bpy.context.evaluated_depsgraph_get().object_instances:
        if instance.object.type != 'MESH' or instance.object.hide_render:
            continue
        points.extend(tuple(round(x, 5) for x in instance.matrix_world @ vertex.co) for vertex in instance.object.data.vertices)
    assert len(points) == 9, ('Visible placement count differs.', len(points))
    return sorted(points), json.loads(collection['sunrise_world_import_seconds'])


automatic, auto_time = capture('AUTO')
linked, linked_time = capture('LINKED')
assert automatic == linked, 'Collection instances changed world geometry.'
before = {kind: set(getattr(bpy.data, kind)) for kind in ('objects', 'collections', 'meshes', 'materials', 'images', 'node_groups')}
try:
    world.import_world(manifest_path)
    raise AssertionError('A duplicate import was accepted.')
except ValueError:
    pass
assert all(set(getattr(bpy.data, kind)) == values for kind, values in before.items())
temporary = manifest_path.parent.parent / ('world-refusal-' + uuid.uuid4().hex)
temporary.mkdir()
try:
    invalid = temporary / 'world'
    shutil.copytree(manifest_path.parent, invalid)
    malformed = json.loads((invalid / manifest_path.name).read_text())
    malformed['Prototypes'][0]['Parts'][0]['Geometry'] = len(malformed['Geometry'])
    (invalid / manifest_path.name).write_text(json.dumps(malformed))
    try:
        world.import_world(invalid / manifest_path.name)
        raise AssertionError('A bad geometry reference was accepted.')
    except ValueError:
        pass
    assert all(set(getattr(bpy.data, kind)) == values for kind, values in before.items()), 'Failed import leaked Blender data.'
finally:
    shutil.rmtree(temporary)
print('blender_world shared=pass matrices=pass visibility=pass materials=pass collection_linked=equal duplicate=refused')
print(json.dumps({'auto': auto_time, 'linked': linked_time}))
temporary = manifest_path.parent.parent / ('world-motion-' + uuid.uuid4().hex)
temporary.mkdir()
try:
    moving = temporary / 'world'
    shutil.copytree(manifest_path.parent, moving)
    motion_document = json.loads((moving / manifest_path.name).read_text())
    colors = struct.pack('<12f', .2, 0, .8, 1, .4, .25, .6, 1, .6, .6, .4, 1)
    (moving / 'motion.bin').write_bytes(colors)
    buffer_index = len(motion_document['Buffers'])
    motion_document['Buffers'].append({'Path': 'motion.bin', 'Length': len(colors), 'Sha256': hashlib.sha256(colors).hexdigest().upper()})
    motion_document['Geometry'][0]['Motion'] = {'Profile': 'VertexWave5',
        'Colors': {'Buffer': buffer_index, 'Offset': 0, 'Count': 3, 'Components': 4, 'ComponentType': 'Float32'},
        'Constants': [[0, 0, 0, 0], [1, 0, 0, 0], [0, 0, 0, 0], [.2, 0, 0, 1], [1, 1, 1, 0]],
        'Rates': [[0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 1, 0], [0, 0, 0, 0]]}
    (moving / manifest_path.name).write_text(json.dumps(motion_document))
    def motion_points(mode):
        bpy.ops.wm.read_factory_settings(use_empty=True)
        world.import_world(moving / manifest_path.name, mode)
        bpy.context.scene.frame_set(10)
        bpy.context.view_layer.update()
        return sorted(tuple(round(x, 5) for x in item.matrix_world @ vertex.co)
                      for item in bpy.context.evaluated_depsgraph_get().object_instances
                      if item.object.type == 'MESH' and not item.object.hide_render
                      for vertex in item.object.data.vertices)
    assert motion_points('AUTO') == motion_points('LINKED'), 'Collection instances changed local passive motion.'
    print('blender_world passive_motion=local collection_linked=equal frame=10')
finally:
    shutil.rmtree(temporary)
temporary = manifest_path.parent.parent / ('world-topology-' + uuid.uuid4().hex)
temporary.mkdir()
try:
    positions = [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0]
    normals = [[0, 0, 1], [.6, 0, .8], [0, .6, .8], [-.6, 0, .8]]
    uvs = [[0, 0], [1, 0], [0, 1], [.25, .75]]
    indices = [0, 1, 2, 1, 1, 2, 3, 1, 2]
    payload = bytearray()
    def view(values, width, kind):
        offset = len(payload)
        payload.extend(struct.pack('<' + str(len(values)) + ('f' if kind == 'Float32' else 'I'), *values))
        return {'Buffer': 0, 'Offset': offset, 'Count': len(values) // width, 'Components': width, 'ComponentType': kind}
    info = {'Id': 42, 'Positions': view(positions, 3, 'Float32'),
            'Normals': view([value for row in normals for value in row], 3, 'Float32'),
            'TexCoords': view([value for row in uvs for value in row], 2, 'Float32'),
            'Indices': view(indices, 1, 'UInt32')}
    source = temporary / 'topology.bin'
    source.write_bytes(payload)
    descriptors = [{'Path': source.name, 'Length': len(payload), 'Sha256': hashlib.sha256(payload).hexdigest().upper()}]
    buffers = world._Buffers(temporary, descriptors)
    try:
        mesh = world._mesh(info, buffers, False)
    finally:
        buffers.close()
    obj = bpy.data.objects.new('Topology preservation', mesh)
    bpy.context.scene.collection.objects.link(obj)
    bpy.context.view_layer.update()
    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    check = evaluated.to_mesh()
    try:
        expected = [0, 1, 2, 3, 1, 2]
        assert len(check.vertices) == 4 and [loop.vertex_index for loop in check.loops] == expected
        assert [list(face.vertices) for face in check.polygons] == [[0, 1, 2], [3, 1, 2]]
        assert np.allclose([tuple(loop.uv) for loop in check.uv_layers[0].data], np.asarray(uvs)[expected], atol=1e-6)
        assert np.allclose([tuple(item.vector) for item in check.corner_normals], np.asarray(normals)[expected], atol=1e-4)
        assert mesh['sunrise_world_geometry'] == 42 and mesh['sunrise_degenerate_triangles'] == 1
        assert source.read_bytes() == payload
    finally:
        evaluated.to_mesh_clear()
    print('blender_world topology=repeated_index_only coincident_vertices=retained face_order_uv_normals=equal source=unchanged')
finally:
    shutil.rmtree(temporary)
if len(arguments) > 2:
    terrain_path = Path(arguments[2]).resolve()
    terrain = json.loads(terrain_path.read_text())
    bpy.ops.wm.read_factory_settings(use_empty=True)
    world.import_world(terrain_path)
    assert len(bpy.data.meshes) == 1 and len(bpy.data.materials) == 2 and len(bpy.data.images) == 2
    for material in bpy.data.materials:
        info = terrain['Materials'][material['sunrise_world_material']]
        expected = info['TextureOverrides']['14']
        images = [node.image for node in material.node_tree.nodes if node.type == 'TEX_IMAGE' and node.image is not None]
        assert images and all(int(image['sunrise_texture_tag'], 16) == expected for image in images)
        assert all(image.colorspace_settings.name == 'Non-Color' for image in images)
        surface = next(node for node in material.node_tree.nodes if node.type == 'BSDF_PRINCIPLED')
        assert surface.inputs['Base Color'].is_linked
    print('blender_world terrain_t14=connected variants=distinct geometry=shared images=shared')


def upstream(socket):
    """Every node that feeds a socket."""
    found, pending = set(), [link.from_node for link in socket.links]
    while pending:
        item = pending.pop()
        if item.name not in found:
            found.add(item.name)
            pending.extend(link.from_node for value in item.inputs for link in value.links)
    return found


if len(arguments) > 3:
    inputs_path = Path(arguments[3]).resolve()
    inputs = json.loads(inputs_path.read_text())
    bpy.ops.wm.read_factory_settings(use_empty=True)
    world.import_world(inputs_path, 'LINKED')
    mesh = bpy.data.meshes[0]
    color, zw = mesh.attributes['sunrise_color'], mesh.attributes['sunrise_uvzw']
    assert color.domain == zw.domain == 'POINT' and len(mesh.uv_layers) == 1
    assert abs(color.data[1].color[1] - 85 / 255) < 1e-6, 'Vertex colour differs from COLOR0.'
    assert abs(zw.data[2].vector[1] - 30000 / 32767) < 1e-4, 'Raw UV w differs from TEXCOORD0.'
    atlases = [image for image in bpy.data.images if image['sunrise_texture_tag'] == '00000ABC']
    assert len(atlases) == 1, 'The plate atlas was not loaded once.'
    for material in bpy.data.materials:
        info = inputs['Materials'][material['sunrise_world_material']]
        assert material.use_backface_culling == bool(info.get('BackfaceCulling')), 'Native back-face culling was not applied.'
        tree = material.node_tree
        surface = next(node for node in tree.nodes if node.type == 'BSDF_PRINCIPLED')
        if 'Plated' not in info['Name']:
            assert not surface.inputs['Base Color'].is_linked, 'A part without plate inputs shows a plate colour.'
            continue
        feeds = upstream(surface.inputs['Base Color'])
        named = {node.name: node for node in tree.nodes}
        assert any(named[name].type == 'ATTRIBUTE' and named[name].attribute_name == 'sunrise_color' for name in feeds), \
            'Plated base colour does not read the vertex colour.'
        assert any(named[name].type == 'TEX_IMAGE' and named[name].image in atlases for name in feeds), \
            'Plated base colour does not sample the plate atlas.'
        assert any(named[name].type == 'NEW_GEOMETRY' for name in feeds), 'Plated base colour does not read the world normal.'
        assert surface.inputs['Emission Color'].is_linked, 'Packed emission was not connected.'
    parts = [obj for obj in bpy.data.objects if obj.type == 'MESH']
    hidden = [obj for obj in parts if obj.hide_render]
    assert len(parts) == inputs['Statistics']['Parts'] == 6, 'Cloth or plated parts are missing.'
    assert len(hidden) == 1 and hidden[0]['sunrise_initial_visibility_reason'] == 'Not in cloth draw set 0'
    print('blender_world plates=connected dyes=variants vertex_color=connected uv_zw=attribute world_normal=connected '
          'emission=connected cloth=draw_set backface_culling=applied')


def render_emission(program, record=None):
    """Renders a plane lit only by the program's emission; returns its linear colour.

    record, when given, is the plane's own sunrise_instance_record placement input.
    """
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    bpy.ops.mesh.primitive_plane_add(size=2)
    if record is not None:
        bpy.context.active_object['sunrise_instance_record'] = record
    material = bpy.data.materials.new('Emission program')
    bundle._build_material_preview(material, {'Program': program, 'BaseColor': [0, 0, 0, 1], 'Metallic': 0})
    bpy.context.active_object.data.materials.append(material)
    camera = bpy.data.objects.new('Camera', bpy.data.cameras.new('Camera'))
    camera.data.type = 'ORTHO'
    camera.data.ortho_scale = 1
    camera.location = (0, 0, 2)
    scene.collection.objects.link(camera)
    scene.camera = camera
    scene.render.engine = 'CYCLES'
    scene.cycles.device = 'CPU'
    scene.cycles.samples = 1
    scene.render.resolution_x = scene.render.resolution_y = 4
    scene.render.image_settings.file_format = 'OPEN_EXR'
    # The denoiser changes a 4-pixel render; the check reads the emitted values unchanged.
    scene.cycles.use_denoising = False
    folder = manifest_path.parent.parent / ('world-log-' + uuid.uuid4().hex)
    folder.mkdir()
    try:
        scene.render.filepath = str(folder / 'emission.exr')
        bpy.ops.render.render(write_still=True)
        image = bpy.data.images.load(scene.render.filepath)
        pixels = np.asarray(image.pixels[:], dtype=np.float32).reshape((-1, 4))
        bpy.data.images.remove(image)
    finally:
        shutil.rmtree(folder)
    return pixels[:, :3].mean(axis=0)


# Direct3D's log2(0) is -inf, so 2^(3 log2 x) is 0 at x = 0; Blender's log of 0 is 0.
nodes = [{'Operation': 'constant', 'Inputs': [], 'Value': value} for value in (0, .5, 3)]
nodes += [{'Operation': 'log', 'Inputs': [0]}, {'Operation': 'log', 'Inputs': [1]},
          {'Operation': 'mul', 'Inputs': [3, 2]}, {'Operation': 'mul', 'Inputs': [4, 2]},
          {'Operation': 'exp', 'Inputs': [5]}, {'Operation': 'exp', 'Inputs': [6]}]
emitted = render_emission({'Nodes': nodes, 'Outputs': {'Emission': [7, 8, 0]}})
assert emitted[0] < 1e-4, ('A power of zero built from log and exp is not zero.', emitted)
assert abs(emitted[1] - .125) < 1e-3, ('A power of a positive value changed.', emitted)
print('blender_world log_of_zero=power_zero positive_power=equal')

if len(arguments) > 4:
    decorators_path = Path(arguments[4]).resolve()
    decorators = json.loads(decorators_path.read_text())
    for mode in ('AUTO', 'LINKED'):
        bpy.ops.wm.read_factory_settings(use_empty=True)
        world.import_world(decorators_path, mode)
        placements = sorted((obj for obj in bpy.data.objects if obj.get('sunrise_world_instance') is not None),
                            key=lambda obj: obj['sunrise_world_instance'])
        records = [list(obj['sunrise_instance_record']) if 'sunrise_instance_record' in obj else None for obj in placements]
        assert np.allclose(records[0], [.1, .2, .3, .4]) and np.allclose(records[1], [0, .25, .5, .75]) and records[2] is None, \
            ('Placement decorator inputs differ from the export.', mode, records)
        assert np.allclose(placements[1]['sunrise_instance_occlusion'], [1, 1, 1, .125])
        material = bpy.data.materials[0]
        surface = next(node for node in material.node_tree.nodes if node.type == 'BSDF_PRINCIPLED')
        named = {node.name: node for node in material.node_tree.nodes}
        assert any(named[name].type == 'ATTRIBUTE' and named[name].attribute_type == 'INSTANCER'
                   and named[name].attribute_name == 'sunrise_instance_record' for name in upstream(surface.inputs['Base Color'])), \
            'Decorator base colour does not read the placement record.'
    nodes = [{'Operation': 'instance', 'Inputs': [], 'Channel': channel} for channel in (1, 2, 3)]
    emitted = render_emission({'Nodes': nodes, 'Outputs': {'Emission': [0, 1, 2]}}, [0, .25, .5, .75])
    assert np.allclose(emitted, [.25, .5, .75], atol=1e-3), ('The placement record did not reach the shader.', emitted)
    print('blender_world decorator_inputs=placed instancer_attribute=connected record_lanes=equal')
