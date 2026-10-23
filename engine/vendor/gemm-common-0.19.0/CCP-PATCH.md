# gemm-common 0.19.0 AArch64 portability patch

This is the published MIT-licensed `gemm-common` 0.19.0 source from crates.io,
archive SHA-256 `88027625910cc9b1085aaaa1c4bc46bb3a36aad323452b33c25b5e4e7c8e2a3e`.
The original package metadata, license, and upstream commit identity are retained.
`upstream-sha256.json` records the original bytes of every copied upstream file.

The only source changes add `#[target_feature(enable = "fp16,neon")]` to
`vmulq_f16`, `vaddq_f16`, `vfmaq_f16`, and `vfmaq_laneq_f16` in `src/simd.rs`.
These unsafe functions contain FP16 inline assembly but lacked its function-local
CPU feature declaration. Unoptimized host dependencies used during ONNX model
generation consequently failed to assemble on generic AArch64 Linux.

The existing `NeonFp16::try_new` runtime checks remain unchanged. FP16 is enabled
only inside the annotated functions; no global CPU feature or host-native target
flag raises the application baseline. There are no arithmetic or dispatch changes.
Remove this patch when a reviewed upstream release supplies the same correction.

References: [upstream issue #31](https://github.com/sarah-quinones/gemm/issues/31),
[original source revision](https://github.com/sarah-quinones/gemm/tree/86102c5b712737978371ac9ef7a11982f686d7bc/gemm-common),
and [Rust function-local target features](https://doc.rust-lang.org/reference/attributes/codegen.html#the-target_feature-attribute).
