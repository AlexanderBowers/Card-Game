// The board seen at an angle, like a table top (pass 39, after Pokemon TCG Pocket).
//
// Applied by PerspectiveBoard to the board and - through use_parent_material - to every slot and
// card on it. It bends each vertex in canvas space: the far edge of the board is narrowed toward
// the board's centre line and the whole board is shortened toward its near edge, so the grid
// reads as a trapezoid lying on the table. PerspectiveBoard feeds it the board's rect every
// frame; the two strength values are on PerspectiveBoard in the inspector.
shader_type canvas_item;
render_mode world_vertex_coords;

uniform vec2 board_pos;          // the board's rect in canvas space
uniform vec2 board_size = vec2(1.0);
uniform float far_scale = 0.8;   // width of the far edge, relative to the near edge
uniform float depth = 0.88;      // how much of its height the board keeps
uniform bool far_at_top = true;  // which edge is far from the player reading this board

void vertex() {
	float v = (VERTEX.y - board_pos.y) / max(board_size.y, 1.0);   // 0 top .. 1 bottom
	float nearness = far_at_top ? v : 1.0 - v;                      // 0 far .. 1 near
	float s = mix(far_scale, 1.0, clamp(nearness, 0.0, 1.0));
	float cx = board_pos.x + board_size.x * 0.5;
	float near_y = far_at_top ? board_pos.y + board_size.y : board_pos.y;
	VERTEX = vec2(cx + (VERTEX.x - cx) * s, near_y + (VERTEX.y - near_y) * depth);
}
