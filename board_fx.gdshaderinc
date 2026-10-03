// The board's living layer (2026-10-02: "add some flair to the table by animating the border and
// patterns on the board"). Shared by board_3d.gdshader (the 3D table) and board_2d.gdshader (the
// flat table), so both look the same.
//
// The playmat art is a rounded board: a coloured rim round a pale field, with the "20" emblem in
// the middle (tools/cardgen build_playmat). On top of it, drawn live:
//   - a soft gleam that travels round the rim,
//   - a fine diamond lattice round the emblem, shimmering outward in slow waves,
//   - a faint ring that leaves the emblem every few seconds and fades as it spreads.
// Everything is measured in "board units", where the board's short side is 1, so it looks the
// same on portrait and landscape boards of any size.

global uniform float board_fx_time;   // seconds (wraps every 70: 14 s laps, 5 s waves), from ShineDriver
global uniform float board_fx_on;     // 0..1: eases to 0 when card animations or battery saver say so

uniform vec2 board_size = vec2(9.0, 19.5);   // any units; only the aspect matters
uniform float corner_frac = 0.10;            // = cardgen BOARD_CORNER
uniform float rim_frac = 0.045;              // = cardgen BOARD_RIM

float board_sdf(vec2 p, vec2 half_size, float r) {
	vec2 q = abs(p) - half_size + r;
	return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

// The rim's colour, read from the art itself (the middle of the top rim).
vec3 board_rim_color(sampler2D tex) {
	float s = min(board_size.x, board_size.y);
	return texture(tex, vec2(0.5, rim_frac * 0.5 * s / board_size.y)).rgb;
}

// `base` is the art's colour at `uv`; returns it with the live layer on top.
vec3 board_fx(vec3 base, vec2 uv, vec3 rim) {
	float s = min(board_size.x, board_size.y);
	vec2 p = (uv - 0.5) * board_size / s;
	vec2 half_size = 0.5 * board_size / s;
	float inside = -board_sdf(p, half_size, corner_frac);
	float t = board_fx_time;
	float on = board_fx_on;
	float r = length(p);

	// Rim gleam: two soft highlights chasing each other round the border, one lap ~14 s.
	float in_rim = 1.0 - smoothstep(rim_frac * 0.8, rim_frac * 1.05, inside);
	float ang = atan(p.y, p.x);
	float head = fract(t / 14.0) * TAU;
	float g = 0.0;
	for (int k = 0; k < 2; k++) {
		float d = abs(mod(ang - head - float(k) * PI + PI, TAU) - PI);
		g += exp(-d * d * 10.0);
	}
	base += vec3(1.0) * g * in_rim * 0.32 * on;

	// Lattice: thin diamond lines in the rim colour, round the emblem and gone before the card
	// rows; bright bands roll outward through it.
	float fade = 1.0 - smoothstep(0.24, 0.46, r);
	vec2 q = vec2(p.x + p.y, p.x - p.y) * 9.0;
	vec2 dist = 0.5 - abs(fract(q) - 0.5);           // distance to the nearest line, in cells
	vec2 aa = fwidth(q) * 0.75 + 0.012;
	float line = max(1.0 - smoothstep(0.0, aa.x, dist.x), 1.0 - smoothstep(0.0, aa.y, dist.y));
	float wave = 0.55 + 0.45 * sin(r * 16.0 - t * TAU / 5.0);
	float lattice = line * fade * (0.08 + 0.16 * wave * on);
	base = mix(base, rim, clamp(lattice, 0.0, 1.0) * step(rim_frac, inside));

	// Ripple: every 5 s a ring leaves the emblem's edge (0.30) and fades out by 0.58.
	float life = fract(t / 5.0);
	float rr = mix(0.30, 0.58, life);
	float ring = exp(-pow((r - rr) / 0.010, 2.0)) * (1.0 - life) * (1.0 - life);
	base = mix(base, rim, ring * 0.35 * on * step(rim_frac, inside));

	return base;
}
