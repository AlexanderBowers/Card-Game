// The flat table's board: the playmat TextureRect (TableUi.ApplyRankTheme) with the same live
// layer as the 3D board.
shader_type canvas_item;

#include "res://board_fx.gdshaderinc"

void fragment() {
	vec4 art = texture(TEXTURE, UV);
	vec3 rim = board_rim_color(TEXTURE);
	COLOR = vec4(board_fx(art.rgb, UV, rim), art.a) * COLOR;
}
