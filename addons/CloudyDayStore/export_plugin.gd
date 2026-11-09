@tool
extends EditorPlugin

# Puts the CloudyDayStore Android library (tools/android-plugin, built to bin/) and the Play
# review library it uses into Android exports. The C# side reaches it as the engine singleton
# "CloudyDayStore" (StoreLinks.cs); other platforms simply don't have it.

var _export_plugin: AndroidExportPlugin

func _enter_tree() -> void:
	_export_plugin = AndroidExportPlugin.new()
	add_export_plugin(_export_plugin)

func _exit_tree() -> void:
	remove_export_plugin(_export_plugin)
	_export_plugin = null

class AndroidExportPlugin extends EditorExportPlugin:
	func _get_name() -> String:
		return "CloudyDayStore"

	func _supports_platform(platform: EditorExportPlatform) -> bool:
		return platform is EditorExportPlatformAndroid

	func _get_android_libraries(platform: EditorExportPlatform, debug: bool) -> PackedStringArray:
		return PackedStringArray(["CloudyDayStore/bin/CloudyDayStore-release.aar"])

	func _get_android_dependencies(platform: EditorExportPlatform, debug: bool) -> PackedStringArray:
		return PackedStringArray(["com.google.android.play:review:2.0.2"])
