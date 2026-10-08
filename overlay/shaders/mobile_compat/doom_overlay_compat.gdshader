shader_type canvas_item;

uniform sampler2D SCREEN_TEXTURE : hint_screen_texture, filter_linear_mipmap;
uniform float s : hint_range(0, 5) = 1.0;
uniform float v = 1.0;
uniform vec4 gradient_color : source_color;

void fragment() {
    mat3 rgb_to_yiq = mat3(
        vec3(0.2989,  0.5959,  0.2115),
        vec3(0.5870, -0.2774, -0.5229),
        vec3(0.1140, -0.3216,  0.3114));
    vec4 color = texture(SCREEN_TEXTURE, SCREEN_UV);
    color.rgb = rgb_to_yiq * color.rgb;
    float saturation = mix(1.0, s, COLOR.a);
    color.rgb = mat3(
        vec3(1.0, 0.0, 0.0),
        vec3(0.0, saturation, 0.0),
        vec3(0.0, 0.0, saturation)) * color.rgb;
    color.rgb = mix(vec3(0.0), color.rgb, mix(1.0, v, COLOR.a));
    color.rgb = inverse(rgb_to_yiq) * color.rgb;
    float pulse = pow(SCREEN_UV.y, 6.0) * 0.7 * COLOR.a
        * smoothstep(-10.0, 1.0, sin(TIME * 20.0) + sin(TIME * 10.0));
    COLOR = color + pulse * gradient_color;
}
