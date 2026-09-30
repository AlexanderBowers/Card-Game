// The Pocket-style shine on a card (pass 57): a gloss streak and, on special cards, a holo foil.
//
// Drawn by a ColorRect laid over the card (TableUi.AddShine), additively, so it only ever
// brightens. Both effects slide with card_tilt - how the phone is being tilted, fed every frame
// by ShineDriver from the gravity sensor, with a slow idle sway so it lives on desktop too.
//
// On a board the overlay must bend with the board's perspective like the card under it, so it
// carries the same vertex warp as board_perspective.gdshader; PerspectiveBoard feeds it the
// board's uniforms and sets on_board.
shader_type canvas_item;
render_mode blend_add, world_vertex_coords;

global uniform vec2 card_tilt;

uniform sampler2D card_face : filter_linear;                      // alpha = the card's shape
uniform sampler2D foil_mask : hint_default_white, filter_linear;  // white = foil (ART_PIPELINE.md)
uniform float foil_strength = 0.0;
uniform float gloss_strength = 0.18;

uniform bool on_board = false;
uniform vec2 board_pos;
uniform vec2 board_size = vec2(1.0);
uniform float far_scale = 0.8;
uniform float depth = 0.88;
uniform bool far_at_top = true;

void vertex() {
	if (on_board) {
		float v = (VERTEX.y - board_pos.y) / max(board_size.y, 1.0);
		float nearness = far_at_top ? v : 1.0 - v;
		float s = mix(far_scale, 1.0, clamp(nearness, 0.0, 1.0));
		float cx = board_pos.x + board_size.x * 0.5;
		float near_y = far_at_top ? board_pos.y + board_size.y : board_pos.y;
		VERTEX = vec2(cx + (VERTEX.x - cx) * s, near_y + (VERTEX.y - near_y) * depth);
	}
}

void fragment() {
	float shape = texture(card_face, UV).a;
	vec2 t = card_tilt;

	// Gloss: one soft diagonal band of light that slides across as the card tilts.
	float d = (UV.x * 0.8 + UV.y * 0.45) - (0.62 + t.x * 0.55 - t.y * 0.35);
	float gloss = exp(-d * d * 55.0) * gloss_strength * (on_board ? 0.35 : 1.0);

	// Foil: rainbow bands that shift with the tilt, plus fine sparkle lines.
	float m = texture(foil_mask, UV).r;
	float phase = (UV.x * 1.3 + UV.y * 0.9) * 1.6 + t.x * 1.8 + t.y * 1.2 + TIME * 0.06;
	vec3 rainbow = 0.5 + 0.5 * cos(6.2831 * (phase + vec3(0.0, 0.33, 0.67)));
	float band = 0.35 + 0.65 * pow(0.5 + 0.5 * sin(phase * 6.2831 * 0.5), 2.0);
	float sparkle = pow(max(0.0, sin((UV.x * 9.0 - UV.y * 13.0 + t.x * 6.0) * 3.1416)), 24.0);
	vec3 foil = (rainbow * band * 0.5 + vec3(sparkle) * 0.4) * foil_strength * m;

	COLOR = vec4(foil + vec3(gloss), shape * COLOR.a);
}
