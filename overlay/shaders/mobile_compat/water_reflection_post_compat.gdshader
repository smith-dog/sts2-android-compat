shader_type canvas_item;

uniform sampler2D noise : repeat_enable;
uniform sampler2D mask;
uniform sampler2D SCREEN_TEXTURE : hint_screen_texture, filter_linear_mipmap;
uniform float speed : hint_range(0.002, 0.5) = 0.1;
uniform float strength : hint_range(0.002, 0.5) = 0.1;

void fragment() {
    float noise_value = texture(noise, UV - vec2(TIME * speed)).r;
    vec2 screen_uv = SCREEN_UV - vec2(strength * 0.5) + vec2(noise_value) * strength;
    COLOR = texture(SCREEN_TEXTURE, screen_uv);
    COLOR.a *= texture(mask, UV).a;
}
