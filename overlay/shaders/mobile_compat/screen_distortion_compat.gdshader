shader_type canvas_item;
render_mode blend_mix;

uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_linear;
uniform float distortion_base_intensity = 0.05;

varying vec4 vertex_color;

void vertex() {
    vertex_color = COLOR;
}

void fragment() {
    vec2 direction = normalize(vec2(0.5) - UV);
    float distortion_mask = texture(TEXTURE, UV).r;
    vec2 offset = distortion_base_intensity * vertex_color.a * distortion_mask * direction;
    COLOR = textureLod(screen_texture, SCREEN_UV + offset, 0.0);
}
