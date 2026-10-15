"""Blender's own add-on loader enables the installed folder, disables it and enables it again."""

import addon_utils
import bpy

NAME = "SunriseBlenderPlugin"
OPERATORS = ("import_bundle", "export_bundle_edits", "import_controls", "import_material", "export_material_edits", "export_original_asset", "import_world")


def registered():
    return [name for name in OPERATORS if hasattr(bpy.types, "SUNRISE_OT_" + name)]


def menu_entries(menu, name="_import_menu"):
    return sum(1 for callback in getattr(menu.draw, "_draw_funcs", []) if getattr(callback, "__name__", "") == name)


def original_export_entries():
    return sum(1 for callback in getattr(bpy.types.TOPBAR_MT_file_export.draw, "_draw_funcs", [])
               if getattr(callback, "__name__", "") == "_export_menu"
               and getattr(callback, "__globals__", {}).get("SIDECAR") == ".sunrise-original.json")


module = addon_utils.enable(NAME, default_set=True)
assert module is not None, "Blender did not load the add-on folder."
assert module.bl_info["name"] == "Sunrise Studio"
assert registered() == list(OPERATORS), registered()
assert menu_entries(bpy.types.TOPBAR_MT_file_import) == 2
assert original_export_entries() == 1
assert menu_entries(bpy.types.TOPBAR_MT_file_import, '_import_world_menu') == 1
addon_utils.disable(NAME, default_set=True)
assert registered() == [] and menu_entries(bpy.types.TOPBAR_MT_file_import) == 0, "Disabling left add-on classes behind."
assert original_export_entries() == 0, "Disabling left the original-export menu behind."
assert menu_entries(bpy.types.TOPBAR_MT_file_import, '_import_world_menu') == 0, "Disabling left the world-import menu behind."
assert addon_utils.enable(NAME, default_set=True) is not None
assert registered() == list(OPERATORS) and menu_entries(bpy.types.TOPBAR_MT_file_import) == 2, "Re-enabling duplicated or lost classes."
assert original_export_entries() == 1, "Re-enabling duplicated the original-export menu."
assert menu_entries(bpy.types.TOPBAR_MT_file_import, '_import_world_menu') == 1
print("blender_plugin install=folder enable=pass disable=clean reenable=pass operators=%d" % len(OPERATORS))
