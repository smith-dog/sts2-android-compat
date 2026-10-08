shader_type canvas_item;
render_mode blend_add;

uniform vec4 AddColor : source_color = vec4(0.0, 0.483333, 1.0, 1.0);
uniform vec2 NoisePanSpeed = vec2(0.0);
uniform vec2 NoiseScale = vec2(10.0);
uniform sampler2D NoiseTexture : repeat_enable;
uniform float NoiseStrength = 0.01;
uniform sampler2D tex_frg_30;
uniform vec2 TexturePanSpeed = vec2(1.0);
uniform vec2 TextureOffset;
uniform vec2 SidesMaskStep = vec2(0.1, 0.3);
uniform sampler2D Sides_Mask;
uniform vec2 TopBottomMaskStep = vec2(0.5, 0.7);
uniform vec2 TopBottomMaskOffset;
uniform sampler2D TopBottomMask;

void fragment() {
	vec2 noise_uv = (UV + TIME * NoisePanSpeed) * NoiseScale;
	vec2 noise_offset = texture(NoiseTexture, noise_uv).xy * NoiseStrength;
	vec2 texture_uv = UV + TIME * TexturePanSpeed + TextureOffset;
	vec2 baked_offset = texture(tex_frg_30, UV).xy * vec2(0.0, -0.075);
	vec4 source = texture(TEXTURE, texture_uv + noise_offset + baked_offset);
	vec3 color = AddColor.rgb + source.rgb;
	float side = smoothstep(SidesMaskStep.x, SidesMaskStep.y, texture(Sides_Mask, UV).r);
	float top_bottom = smoothstep(
		TopBottomMaskStep.x,
		TopBottomMaskStep.y,
		texture(TopBottomMask, UV + TopBottomMaskOffset).r);
	COLOR = vec4(color, source.a * side * top_bottom);
}
