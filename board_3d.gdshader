// The 3D table's board (TableWorld3D.BuildBoard). Unshaded: the playmat art carries its own
// light, and the mesh's vertex colour darkens its rounded edge and sides - so the board looks
// the same on every GPU, with no shadow map to go wrong (2026-10-02).
shader_type spatial;
render_mode unshaded, cull_disabled;

#include "res://board_fx.gdshaderinc"

uniform sampler2D board_tex : source_color, filter_linear_mipmap_anisotropic;

void fragment() {
	vec3 art = texture(board_tex, UV).rgb;
	vec3 rim = board_rim_color(board_tex);
	ALBEDO = board_fx(art, UV, rim) * COLOR.rgb;
}
