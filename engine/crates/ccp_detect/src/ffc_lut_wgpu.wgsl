// Fast-mode flat-field correction fused with the combined 3-D LUT, in f32.
// The same formulas as ccp_core::fused (see there for the Lab shift identity).
struct Params {
    pixels: u32, width: u32, height: u32, first: u32,
    gw: u32, gh: u32, gs: u32, use_lut: u32,
    rgb_to_xyz0: vec4<f32>, rgb_to_xyz1: vec4<f32>, rgb_to_xyz2: vec4<f32>,
    xyz_to_rgb0: vec4<f32>, xyz_to_rgb1: vec4<f32>, xyz_to_rgb2: vec4<f32>,
    white: vec4<f32>,
};
@group(0) @binding(0) var<storage, read> pixels: array<f32>;
@group(0) @binding(1) var<storage, read> lut: array<f32>;
@group(0) @binding(2) var<storage, read> gain: array<f32>;
@group(0) @binding(3) var<storage, read_write> output: array<f32>;
@group(0) @binding(4) var<uniform> p: Params;

const EPS: f32 = 0.008856451679;   // 216/24389
const KAPPA: f32 = 903.2962963;    // 24389/27
const DELTA: f32 = 0.2068965517;   // 6/29

fn lab_f(t: vec3<f32>) -> vec3<f32> {
    let cube = pow(max(t, vec3<f32>(1e-30)), vec3<f32>(1.0 / 3.0));
    return select((KAPPA * t + 16.0) / 116.0, cube, t > vec3<f32>(EPS));
}
fn lab_f_inv(f: vec3<f32>) -> vec3<f32> {
    return select((116.0 * f - 16.0) / KAPPA, f * f * f, f > vec3<f32>(DELTA));
}
fn to_linear(c: vec3<f32>) -> vec3<f32> {
    let v = clamp(c, vec3<f32>(0.0), vec3<f32>(1.0));
    return select(pow((v + 0.055) / 1.055, vec3<f32>(2.4)), v / 12.92, v <= vec3<f32>(0.04045));
}
fn encode(lin: vec3<f32>) -> vec3<f32> {
    let v = clamp(lin, vec3<f32>(0.0), vec3<f32>(1.0));
    return select(1.055 * pow(max(v, vec3<f32>(1e-30)), vec3<f32>(1.0 / 2.4)) - 0.055, v * 12.92, v <= vec3<f32>(0.0031308));
}
fn g_at(y: u32, x: u32) -> f32 {
    return gain[y * p.gw + x];
}
fn gain_at(x: u32, y: u32) -> f32 {
    if p.gw == p.width && p.gh == p.height {
        return g_at(y, x);
    }
    // As ccp_core::ffc::resize_gain (bilinear).
    let sy = f32(y) * f32(p.gh - 1u) / f32(max(p.height, 2u) - 1u);
    let sx = f32(x) * f32(p.gw - 1u) / f32(max(p.width, 2u) - 1u);
    let y0 = min(u32(floor(sy)), p.gh - 1u);
    let x0 = min(u32(floor(sx)), p.gw - 1u);
    let y1 = min(y0 + 1u, p.gh - 1u);
    let x1 = min(x0 + 1u, p.gw - 1u);
    let fy = sy - f32(y0);
    let fx = sx - f32(x0);
    return g_at(y0, x0) * (1.0 - fy) * (1.0 - fx) + g_at(y1, x0) * fy * (1.0 - fx)
        + g_at(y0, x1) * (1.0 - fy) * fx + g_at(y1, x1) * fy * fx;
}
fn at(c: vec3<u32>) -> vec3<f32> {
    let i = ((c.z * p.gs + c.y) * p.gs + c.x) * 3u;
    return vec3<f32>(lut[i], lut[i + 1u], lut[i + 2u]);
}
fn sample_lut(rgb: vec3<f32>) -> vec3<f32> {
    let xyz = clamp(rgb, vec3<f32>(0.0), vec3<f32>(1.0)) * f32(p.gs - 1u);
    let lo = min(vec3<u32>(floor(xyz)), vec3<u32>(p.gs - 1u));
    let hi = min(lo + vec3<u32>(1u), vec3<u32>(p.gs - 1u));
    let t = xyz - vec3<f32>(lo);
    let c00 = mix(at(lo), at(vec3<u32>(hi.x, lo.y, lo.z)), t.x);
    let c10 = mix(at(vec3<u32>(lo.x, hi.y, lo.z)), at(vec3<u32>(hi.x, hi.y, lo.z)), t.x);
    let c01 = mix(at(vec3<u32>(lo.x, lo.y, hi.z)), at(vec3<u32>(hi.x, lo.y, hi.z)), t.x);
    let c11 = mix(at(vec3<u32>(lo.x, hi.y, hi.z)), at(hi), t.x);
    return mix(mix(c00, c10, t.y), mix(c01, c11, t.y), t.z);
}
@compute @workgroup_size(256)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let i = id.x;
    if i >= p.pixels { return; }
    let index = p.first + i;
    let x = index % p.width;
    let y = index / p.width;
    let j = i * 3u;
    let lin = to_linear(vec3<f32>(pixels[j], pixels[j + 1u], pixels[j + 2u]));
    let xyz = vec3<f32>(dot(p.rgb_to_xyz0.xyz, lin), dot(p.rgb_to_xyz1.xyz, lin), dot(p.rgb_to_xyz2.xyz, lin));
    let f = lab_f(xyz / p.white.xyz);
    let l = clamp((116.0 * f.y - 16.0) * gain_at(x, y), 0.0, 100.0);
    let xyz2 = p.white.xyz * lab_f_inv(f + vec3<f32>((l + 16.0) / 116.0 - f.y));
    var c = encode(vec3<f32>(dot(p.xyz_to_rgb0.xyz, xyz2), dot(p.xyz_to_rgb1.xyz, xyz2), dot(p.xyz_to_rgb2.xyz, xyz2)));
    if p.use_lut != 0u {
        c = sample_lut(c);
    }
    output[j] = c.x; output[j + 1u] = c.y; output[j + 2u] = c.z;
}
