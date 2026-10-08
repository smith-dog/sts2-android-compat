shader_type canvas_item;
render_mode blend_mix;

uniform vec4 ColorParameter : source_color;
uniform vec2 Smooth_Step = vec2(0.48, 0.5);
uniform float Noise_Intensity_1 = 1.0;
uniform float NoiseScale1 = 1.0;
uniform float TimeOffset = 0.0;
uniform vec2 Noise_Scroll_1 = vec2(0.2, 0.2);
uniform sampler2D NoiseTexture1 : repeat_enable;
uniform float NoiseScale2 = 1.0;
uniform vec2 Noise_Scroll_2 = vec2(0.2, 0.2);
uniform sampler2D NoiseTexture2 : repeat_enable;
uniform float Noise_Intensity_2 = 1.0;
uniform sampler2D top_gradient;
uniform float MaskNoiseIntensity;
uniform vec2 MaskNoiseScroll;
uniform sampler2D MaskNoiseTexture : repeat_enable;
uniform sampler2D Shape_Mask;

void fragment() {
	float t = TIME + TimeOffset;
	vec2 noise1_uv = UV * NoiseScale1 + t * Noise_Scroll_1;
	vec2 noise2_uv = UV * NoiseScale2 + t * Noise_Scroll_2 * 3.0;
	float noise1 = texture(NoiseTexture1, noise1_uv).r;
	float noise2 = texture(NoiseTexture2, noise2_uv).r;
	float combined_noise = (1.0 - Noise_Intensity_1 + noise1)
		* (1.0 - Noise_Intensity_2 + noise2);
	vec2 gradient_uv = UV + vec2(-1.065, -0.24) + vec2(combined_noise);
	float gradient = texture(top_gradient, gradient_uv).r;
	float mask_noise = texture(MaskNoiseTexture, UV + TIME * MaskNoiseScroll).r;
	vec2 shape_uv = UV + mask_noise * MaskNoiseIntensity;
	float shape = texture(Shape_Mask, shape_uv).r;
	float alpha = smoothstep(Smooth_Step.x, Smooth_Step.y, gradient * shape);
	COLOR = vec4(ColorParameter.rgb, alpha);
}
