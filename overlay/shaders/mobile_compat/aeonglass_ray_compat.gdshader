shader_type canvas_item;
render_mode blend_mix;

uniform vec4 CoreColor : source_color;
uniform float Progress;
uniform sampler2D RayTex : repeat_disable;
uniform vec4 BlurColor : source_color;
uniform vec2 EdgeMaskScale = vec2(1.0);
uniform sampler2D EdgeMaskTex : filter_linear, repeat_disable;

void fragment() {
	vec4 ray = texture(RayTex, UV + vec2(Progress, 0.0));
	float edge_mask = texture(EdgeMaskTex, UV * EdgeMaskScale).g;
	COLOR.rgb = CoreColor.rgb * ray.r + BlurColor.rgb * ray.g;
	COLOR.a = ray.g * edge_mask;
}
