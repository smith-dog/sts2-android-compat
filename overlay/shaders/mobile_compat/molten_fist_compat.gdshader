shader_type canvas_item;
render_mode blend_mix;

uniform vec2 NoiseStep = vec2(0.1, 0.2);
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

void fragment() {
	// Noise2 is multiplied by the incoming particle alpha in the original graph.
	float noise_product = 0.0;
	if (Noise1Strength != 0.0 && Noise2Strength != 0.0 && COLOR.a != 0.0) {
		float noise2 = Noise2Strength * COLOR.a
			* texture(Noise2Texture, Noise2Panning * TIME + UV * Noise2Scaling).b;
		float noise1 = Noise1Strength
			* texture(Noise1Texture, Noise1Panning * TIME + UV * Noise1Scaling).b;
		noise_product = noise2 * noise1;
	}

	vec2 mask_pivot = vec2(0.0, 0.04);
	vec2 noise_mask_uv = (UV - mask_pivot) * NoiseMaskScale + mask_pivot;
    float noise_mask = texture(NoiseMask, NoiseMaskOffset + noise_mask_uv).b;
    if (InvertNoiseMask) {
        noise_mask = 1.0 - noise_mask;
    }

    // The VisualShader's baked image is the GPUParticles2D texture.
	float source_alpha = texture(TEXTURE, UV).a;
	float intensity = (noise_product + noise_mask) * source_alpha;
	COLOR.a = smoothstep(NoiseStep.x, NoiseStep.y, intensity);
}
