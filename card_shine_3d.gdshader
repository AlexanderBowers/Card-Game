// The Pocket-style shine on a card lying on the 3D table (pass 65): the same gloss streak and
// holo foil as card_shine.gdshader, drawn by an additive quad that sits a hair above each card
// (TableWorld3D). It slides with card_tilt - the phone's tilt from ShineDriver - plus extra_tilt,
// which the table feeds from the card's own turn and lift so a card in flight catches the light.
shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled;

global uniform vec2 card_tilt;

uniform sampler2D card_face : source_color, filter_linear_mipmap;  // alpha = the card's shape
uniform sampler2D foil_mask : hint_default_white, filter_linear;   // white = foil (ART_PIPELINE.md)
uniform float foil_strength = 0.0;
uniform float gloss_strength = 0.22;
uniform float fade = 1.0;
uniform vec2 extra_tilt = vec2(0.0);

void fragment() {
	float shape = texture(card_face, UV).a;
	vec2 t = card_tilt + extra_tilt;

	float d = (UV.x * 0.8 + UV.y * 0.45) - (0.62 + t.x * 0.55 - t.y * 0.35);
	float gloss = exp(-d * d * 55.0) * gloss_strength;

	float m = texture(foil_mask, UV).r;
	float phase = (UV.x * 1.3 + UV.y * 0.9) * 1.6 + t.x * 1.8 + t.y * 1.2 + TIME * 0.06;
	vec3 rainbow = 0.5 + 0.5 * cos(6.2831 * (phase + vec3(0.0, 0.33, 0.67)));
	float band = 0.35 + 0.65 * pow(0.5 + 0.5 * sin(phase * 6.2831 * 0.5), 2.0);
	float sparkle = pow(max(0.0, sin((UV.x * 9.0 - UV.y * 13.0 + t.x * 6.0) * 3.1416)), 24.0);
	vec3 foil = (rainbow * band * 0.5 + vec3(sparkle) * 0.4) * foil_strength * m;

	ALBEDO = foil + vec3(gloss);
	ALPHA = shape * fade;
}
