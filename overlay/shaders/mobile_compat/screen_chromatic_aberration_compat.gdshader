shader_type canvas_item;
render_mode blend_mix;

instance uniform float base_intensity = 1.0;

uniform sampler2D screen : hint_screen_texture, filter_linear_mipmap;
uniform sampler2D intensity_curve : repeat_disable;
uniform sampler2D distance_curve : repeat_disable;

varying vec4 vertex_color;
varying float lifetime;

void vertex() {
	vertex_color = COLOR;
	lifetime = INSTANCE_CUSTOM.y / INSTANCE_CUSTOM.w;
}

void fragment() {
	// A zero instance intensity is a real no-op.  In particular, do not
	// synthesize a white color when the effect is disabled.
	if (base_intensity == 0.0) {
		vec4 original = texture(screen, SCREEN_UV);
		COLOR = vec4(original.rgb, 1.0);
	} else {
		vec4 main_tex = texture(TEXTURE, UV);
		vec2 dir = normalize(vec2(0.5) - UV);
		float dist = distance(UV, vec2(0.5));
		float dist_curve = texture(distance_curve, vec2(dist)).r;
		float intensity = texture(intensity_curve, vec2(lifetime)).r
			* base_intensity * dist_curve * main_tex.r;

		// Two taps approximate the original half-offset red, centered green,
		// and full-offset blue samples while retaining RGB separation.
		vec4 screen_near = texture(screen, SCREEN_UV + (dir * 0.5 * intensity));
		vec4 screen_far = texture(screen, SCREEN_UV + (dir * intensity));
		float green = 0.5 * (screen_near.g + screen_far.g);
		COLOR = vec4(screen_near.r, green, screen_far.b, 1.0);
	}
}
