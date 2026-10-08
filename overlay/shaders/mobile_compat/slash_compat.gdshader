shader_type canvas_item;
render_mode blend_mix;

uniform vec4 ColorParameter : source_color;
uniform vec2 step;
uniform float master_step = 1.0;
uniform sampler2D shape_mask_tex;
uniform float noise_1_strength = 1.0;
uniform vec2 noise_1_scale = vec2(1.0);
uniform vec2 noise_1_panning = vec2(2.0, 3.0);
uniform sampler2D noise_1_tex : repeat_enable;
uniform vec2 noise_2_scale = vec2(1.0);
uniform vec2 noise_2_panning = vec2(0.2, 0.3);
uniform sampler2D noise_2_tex : repeat_enable;
uniform float noise_2_strength = 1.0;
uniform float opacity = 1.0;

void fragment() {
	float alpha = 0.0;
	// Both noise layers are multiplied, so zero strength makes the result empty.
	if (noise_1_strength != 0.0 && noise_2_strength != 0.0 && opacity != 0.0) {
		vec2 noise_pivot = vec2(-0.275, 0.0);
		vec2 noise_base = UV - noise_pivot;
		float noise1 = noise_1_strength
			* texture(noise_1_tex, noise_base * noise_1_scale + noise_pivot + TIME * noise_1_panning).r;
		float noise2 = noise_2_strength
			* texture(noise_2_tex, noise_base * noise_2_scale + noise_pivot + TIME * noise_2_panning).r;
		float shaped_noise = texture(shape_mask_tex, UV).b * noise1 * noise2;
		vec2 thresholds = step * master_step;
		alpha = smoothstep(thresholds.x, thresholds.y, shaped_noise) * opacity;
	}
	COLOR.rgb = ColorParameter.rgb;
	COLOR.a = alpha;
}
