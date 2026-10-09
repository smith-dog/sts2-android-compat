shader_type canvas_item;
render_mode blend_mix;

uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_linear;
uniform float distortion_base_intensity = 0.05;
uniform vec2 tiling = vec2(1.0, 1.0);
uniform vec2 offset_speed;

varying vec4 vertex_color;

vec2 polar_coordinates(vec2 uv, vec2 center, float zoom, float repeat) {
    vec2 direction = uv - center;
    float radius = length(direction) * 2.0;
    float angle = atan(direction.y, direction.x) / (PI * 2.0);
    return mod(vec2(radius * zoom, angle * repeat), 1.0);
}

void vertex() {
    vertex_color = COLOR;
}

void fragment() {
    vec2 direction = normalize(vec2(0.5) - UV);
    vec2 polar_uv = polar_coordinates(UV, vec2(0.5), 1.0, 1.0);
    polar_uv = polar_uv * tiling + TIME * offset_speed;
    float distortion_mask = texture(TEXTURE, UV).a;
    float distortion_polar_mask = texture(TEXTURE, polar_uv).r;
    vec2 offset = distortion_base_intensity * vertex_color.a
        * distortion_mask * distortion_polar_mask * direction;
    COLOR = textureLod(screen_texture, SCREEN_UV + offset, 0.0);
}
