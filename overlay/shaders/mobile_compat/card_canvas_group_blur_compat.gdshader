shader_type canvas_item;

// This variant is used by the ordinary CanvasGroup blur material.  The
// Ancient-card mask path deliberately remains on its original shader; keep
// the mask uniforms here only so the material parameter contract is intact.
const float DEFAULT_RADIUS = 32.0;

uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_nearest;
uniform sampler2D mask_texture : repeat_disable;
uniform vec4 mask_region;
uniform vec2 mask_offset;

// Unit vector for the horizontal or vertical blur pass.
uniform vec2 step;
// Desired blur radius.
uniform float radius = 32.0;

uniform bool mask;
uniform bool blur;

vec4 sample_screen(vec2 uv) {
	vec4 col = textureLod(screen_texture, uv, 0.0);
	if (col.a > 0.0001) {
		col.rgb /= col.a;
	}
	return col;
}

void fragment() {
	vec2 s = radius / DEFAULT_RADIUS * step / vec2(textureSize(screen_texture, 0));
	if (!mask && blur) {
		COLOR.rgb =
			0.027630551 * sample_screen(SCREEN_UV - 32.0 * s).rgb +
			0.066282245 * sample_screen(SCREEN_UV - 24.0 * s).rgb +
			0.123831537 * sample_screen(SCREEN_UV - 16.0 * s).rgb +
			0.180173823 * sample_screen(SCREEN_UV - 8.0 * s).rgb +
			0.204163689 * sample_screen(SCREEN_UV).rgb +
			0.180173823 * sample_screen(SCREEN_UV + 8.0 * s).rgb +
			0.123831537 * sample_screen(SCREEN_UV + 16.0 * s).rgb +
			0.066282245 * sample_screen(SCREEN_UV + 24.0 * s).rgb +
			0.027630551 * sample_screen(SCREEN_UV + 32.0 * s).rgb;
	} else {
		COLOR.rgb = sample_screen(SCREEN_UV).rgb;
	}

	// mask=false is the supported CanvasGroup path.  The mask uniforms above
	// are intentionally not sampled: ancient-card masking stays unmodified.
}
