shader_type canvas_item;
render_mode blend_mix;

uniform vec2 blur_center = vec2(0.45, 0.55);
uniform float blur_power : hint_range(0.0, 1.0) = 0.005;
uniform int sampling_count : hint_range(1, 64) = 12;
uniform sampler2D SCREEN_TEXTURE : hint_screen_texture, filter_linear_mipmap;

void fragment() {
    // Four fixed taps retain the radial direction while bounding costly
    // backbuffer fetches. sampling_count remains part of the caller contract.
    int taps = clamp(sampling_count, 1, 4);
    vec2 direction = SCREEN_UV - blur_center;
    vec3 color = vec3(0.0);
    float weight = 1.0 / float(taps);
    for (int i = 0; i < 4; i++) {
        if (i >= taps)
            break;
        color += texture(SCREEN_TEXTURE, SCREEN_UV - blur_power * direction * float(i)).rgb * weight;
    }
    COLOR = vec4(color, 1.0);
}
