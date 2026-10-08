shader_type canvas_item;
render_mode blend_add;

uniform vec4 OuterColor : source_color = vec4(1.0, 0.0, 0.0, 1.0);
uniform vec2 InnerColorStep = vec2(0.3, 0.4);
uniform float Noise2Strength = 1.0;
uniform vec2 Noise2Scaling = vec2(1.0);
uniform vec2 Noise2Panning = vec2(1.0);
uniform sampler2D Noise2Texture : repeat_enable;
uniform float Noise1Strength = 1.0;
uniform vec2 Noise1Scaling = vec2(1.0);
uniform vec2 Noise1Panning = vec2(1.0);
uniform sampler2D Noise1Texture : repeat_enable;
uniform bool InvertNoiseMask = true;
uniform vec2 NoiseMaskScale = vec2(1.0);
uniform vec2 NoiseMaskOffset;
uniform sampler2D NoiseMask : repeat_disable;
uniform vec2 Distortion2Scale = vec2(1.0);
uniform vec2 Distortion2Panning = vec2(1.0);
uniform sampler2D Distortion2Texture : repeat_enable;
uniform float Distortion2Strength = 0.02;
uniform vec2 Distortion1Scale = vec2(1.0);
uniform vec2 Distortion1Panning = vec2(1.0);
uniform sampler2D Distortion1Texture : repeat_enable;
uniform float Distortion1Strength = 0.02;
uniform vec2 DistortionMaskScale = vec2(1.0);
uniform vec2 DistortionMaskOffset;
uniform sampler2D DistortionMask : repeat_disable;
uniform vec4 InnerColor : source_color = vec4(1.0, 0.85, 0.0, 1.0);
uniform vec2 OuterStep = vec2(0.1, 0.2);

void fragment() {
	// The noise inputs multiply: either zero strength removes both samples.
	float noise_product = 0.0;
	if (Noise1Strength != 0.0 && Noise2Strength != 0.0) {
		float noise2 = Noise2Strength * texture(Noise2Texture, Noise2Panning * TIME + UV * Noise2Scaling).b;
		float noise1 = Noise1Strength * texture(Noise1Texture, Noise1Panning * TIME + UV * Noise1Scaling).b;
		noise_product = noise2 * noise1;
	}

	vec2 mask_pivot = vec2(0.0, 0.04);
	vec2 noise_mask_uv = (UV - mask_pivot) * NoiseMaskScale + mask_pivot;
    float noise_mask = texture(NoiseMask, NoiseMaskOffset + noise_mask_uv).b;
    if (InvertNoiseMask) {
        noise_mask = 1.0 - noise_mask;
    }

	vec2 main_uv = UV;
	if (Distortion1Strength != 0.0 || Distortion2Strength != 0.0) {
		vec2 distortion = vec2(0.0);
		if (Distortion2Strength != 0.0) {
			distortion = texture(Distortion2Texture, Distortion2Panning * TIME + UV * Distortion2Scale).rg * Distortion2Strength;
		}
		if (Distortion1Strength != 0.0) {
			distortion += texture(Distortion1Texture, Distortion1Panning * TIME + UV * Distortion1Scale).rg * Distortion1Strength;
		}
		// The mask is needed only when at least one distortion contributes.
		distortion *= texture(DistortionMask, DistortionMaskOffset + UV * DistortionMaskScale).rg;
		main_uv += distortion;
	}

	float intensity = (noise_product + noise_mask) * texture(TEXTURE, main_uv).b;
	float inner = smoothstep(InnerColorStep.x, InnerColorStep.y, intensity);
	COLOR.rgb = OuterColor.rgb + inner * InnerColor.rgb;
	COLOR.a = smoothstep(OuterStep.x, OuterStep.y, intensity);
}
