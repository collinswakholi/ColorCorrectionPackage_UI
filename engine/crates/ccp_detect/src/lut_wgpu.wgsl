@group(0) @binding(0) var<storage, read> pixels: array<f32>;
@group(0) @binding(1) var<storage, read> lut: array<f32>;
@group(0) @binding(2) var<storage, read_write> output: array<f32>;
@group(0) @binding(3) var<uniform> count: vec4<u32>;
fn at(p: vec3<u32>) -> vec3<f32> {
    let i = ((p.z * count.y + p.y) * count.y + p.x) * 3u;
    return vec3<f32>(lut[i], lut[i + 1u], lut[i + 2u]);
}
@compute @workgroup_size(256)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
    let i = id.x;
    if i >= count.x { return; }
    let j = i * 3u;
    let xyz = clamp(vec3<f32>(pixels[j], pixels[j+1u], pixels[j+2u]), vec3<f32>(0.0), vec3<f32>(1.0)) * f32(count.y - 1u);
    let lo = vec3<u32>(floor(xyz));
    let hi = min(lo + vec3<u32>(1u), vec3<u32>((count.y - 1u)));
    let t = xyz - vec3<f32>(lo);
    let c00 = mix(at(lo), at(vec3<u32>(hi.x, lo.y, lo.z)), t.x);
    let c10 = mix(at(vec3<u32>(lo.x, hi.y, lo.z)), at(vec3<u32>(hi.x, hi.y, lo.z)), t.x);
    let c01 = mix(at(vec3<u32>(lo.x, lo.y, hi.z)), at(vec3<u32>(hi.x, lo.y, hi.z)), t.x);
    let c11 = mix(at(vec3<u32>(lo.x, hi.y, hi.z)), at(hi), t.x);
    let c = mix(mix(c00, c10, t.y), mix(c01, c11, t.y), t.z);
    output[j] = c.x; output[j+1u] = c.y; output[j+2u] = c.z;
}
