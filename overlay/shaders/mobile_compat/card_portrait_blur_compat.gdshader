shader_type canvas_item;

// The original portrait blur is a wide first pass.  Keep the radius/step
// contract, but use a fixed nine-tap Gaussian approximation for mobile GPUs.
const float DEFAULT_RADIUS = 32.0;

// Unit vector for the horizontal or vertical blur pass.
uniform vec2 step;
// Desired blur radius.
uniform float radius = 32.0;

void fragment() {
	vec2 s = radius / DEFAULT_RADIUS * step / vec2(textureSize(TEXTURE, 0));
	vec3 blurred =
		0.027630551 * texture(TEXTURE, UV - 32.0 * s).rgb +
		0.066282245 * texture(TEXTURE, UV - 24.0 * s).rgb +
		0.123831537 * texture(TEXTURE, UV - 16.0 * s).rgb +
		0.180173823 * texture(TEXTURE, UV - 8.0 * s).rgb +
		0.204163689 * texture(TEXTURE, UV).rgb +
		0.180173823 * texture(TEXTURE, UV + 8.0 * s).rgb +
		0.123831537 * texture(TEXTURE, UV + 16.0 * s).rgb +
		0.066282245 * texture(TEXTURE, UV + 24.0 * s).rgb +
		0.027630551 * texture(TEXTURE, UV + 32.0 * s).rgb;
	COLOR.rgb = blurred;
	COLOR.a = 1.0;
}
