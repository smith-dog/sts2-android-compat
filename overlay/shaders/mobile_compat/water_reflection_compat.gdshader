shader_type canvas_item;

uniform sampler2D noise : repeat_enable;
uniform sampler2D mask;
uniform float speed : hint_range(0.002, 0.5) = 0.1;
uniform float strength : hint_range(0.002, 0.5) = 0.1;

void fragment() {
	// Keep the source step boundary (mask alpha <= 0.1 is not distorted)
	// before doing any noise or distorted TEXTURE fetch.
	float use_original = step(texture(mask, UV).a, 0.1);
	if (use_original > 0.5) {
		COLOR = texture(TEXTURE, UV);
	} else {
		float noise_value = texture(noise, UV - vec2(TIME * speed)).r;
		vec2 distorted_uv = UV - (strength * 0.5) + vec2(noise_value) * (strength * 0.5);
		COLOR = texture(TEXTURE, distorted_uv);
	}
}
