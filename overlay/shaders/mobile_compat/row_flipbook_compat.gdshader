shader_type canvas_item;
render_mode blend_mix;

uniform vec2 pivot_offset = vec2(0.0, 0.0);

uniform sampler2D lut : repeat_disable;
uniform float frame_count = 9;
uniform vec2 flipbook_size = vec2(3, 3);
uniform sampler2D flipbook_curve : repeat_disable, hint_default_black;

uniform float use_erosion_texture = 0.0;
uniform sampler2D erosion_texture : repeat_disable;
uniform sampler2D erosion_curve : hint_default_black;
uniform float erosion_offset = 0.1;

uniform float use_hue_shift = 0.0;
uniform sampler2D hue_shift_curve : repeat_disable, hint_default_black;

varying float lifetime;
varying float flipbook_offset;
varying vec4 vertex_color;
varying vec2 flipbook_uv;

vec2 flipbook(vec2 uv, vec2 size, float progress) {
	float h_frames = size.x;
	float v_frames = size.y;
	float particle_total_frames = size.x * size.y;
	float particle_frame = mod(floor(progress), particle_total_frames);

	vec2 result_uv = uv / vec2(h_frames, v_frames);
	result_uv += vec2(
		mod(particle_frame, h_frames) / h_frames,
		floor((particle_frame + 0.5) / h_frames) / v_frames
	);
	return result_uv;
}

vec4 shift_hue(in vec4 color, in float shift) {
	vec3 gray = vec3(0.57735);
	vec3 projection = gray * dot(gray, color.rgb);
	vec3 u = color.rgb - projection;
	vec3 v = cross(gray, u);
	vec3 shifted = u * cos(shift * 2.0 * PI) + v * sin(shift * 2.0 * PI) + projection;
	return vec4(shifted, color.a);
}

void vertex() {
	vertex_color = COLOR;
	lifetime = INSTANCE_CUSTOM.y / INSTANCE_CUSTOM.w;
	flipbook_offset = INSTANCE_CUSTOM.z;

	VERTEX += pivot_offset * (1.0 / TEXTURE_PIXEL_SIZE);

	float flipbook_curve_val = textureLod(flipbook_curve, vec2(lifetime), 0.0).r;

	float particle_frame_x = flipbook_offset * flipbook_size.y;
	particle_frame_x = floor(particle_frame_x) / flipbook_size.y;

	float particle_frame_y = flipbook_curve_val * flipbook_size.x;
	particle_frame_y = floor(particle_frame_y) / flipbook_size.x;
	particle_frame_y /= flipbook_size.y;

	float frame_progress = floor((particle_frame_x + particle_frame_y) * frame_count);
	flipbook_uv = flipbook(UV, flipbook_size, frame_progress);
}

void fragment() {
	vec4 flipbook_texture_color = texture(TEXTURE, flipbook_uv);

	float erosion = 1.0;
	if (use_erosion_texture > 0.0) {
		vec4 erosion_color = texture(erosion_texture, UV);
		float erosion_curve_val = texture(erosion_curve, vec2(lifetime)).r;
		float erosion_val = smoothstep(
			erosion_curve_val,
			erosion_curve_val + erosion_offset,
			erosion_color.r
		);
		erosion = mix(1.0, erosion_val, use_erosion_texture);
	}

	float alpha = flipbook_texture_color.a * vertex_color.a * erosion;
	vec4 lut_color = texture(lut, flipbook_texture_color.rr);
	vec4 initial_color = lut_color * vertex_color;

	vec4 final_color = initial_color;
	if (use_hue_shift > 0.0) {
		float hue_shift_val = texture(hue_shift_curve, flipbook_texture_color.rr).r;
		vec4 shifted_color = shift_hue(initial_color, hue_shift_val);
		final_color = mix(initial_color, shifted_color, use_hue_shift);
	}

	COLOR = vec4(final_color.rgb, alpha);
}
