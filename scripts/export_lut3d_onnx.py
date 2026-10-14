#!/usr/bin/env python3
"""Export lut3d_trilinear.onnx — trilinear sample of gs³ encoded-RGB LUT (DirectML-safe)."""

from __future__ import annotations

import argparse
from pathlib import Path

import torch
import torch.nn as nn

GS = 17
CHUNK = 262144


class Lut3dTrilinear(nn.Module):
  def forward(self, pixels: torch.Tensor, lut: torch.Tensor) -> torch.Tensor:
    """pixels [N,3] in [0,1]; lut [gs,gs,gs,3] layout (b,g,r) channel order."""
    gs = lut.shape[0]
    maxv = float(gs - 1)
    rgb = pixels.clamp(0.0, 1.0) * maxv
    r = rgb[:, 0]
    g = rgb[:, 1]
    b = rgb[:, 2]
    r0 = r.floor().long().clamp(0, gs - 1)
    g0 = g.floor().long().clamp(0, gs - 1)
    b0 = b.floor().long().clamp(0, gs - 1)
    r1 = (r0 + 1).clamp(max=gs - 1)
    g1 = (g0 + 1).clamp(max=gs - 1)
    b1 = (b0 + 1).clamp(max=gs - 1)
    fr = (r - r0.float()).unsqueeze(1)
    fg = (g - g0.float()).unsqueeze(1)
    fb = (b - b0.float()).unsqueeze(1)

    def at(ri, gi, bi):
      return lut[bi, gi, ri]

    c000 = at(r0, g0, b0)
    c100 = at(r1, g0, b0)
    c010 = at(r0, g1, b0)
    c110 = at(r1, g1, b0)
    c001 = at(r0, g0, b1)
    c101 = at(r1, g0, b1)
    c011 = at(r0, g1, b1)
    c111 = at(r1, g1, b1)

    c00 = c000 * (1 - fr) + c100 * fr
    c10 = c010 * (1 - fr) + c110 * fr
    c01 = c001 * (1 - fr) + c101 * fr
    c11 = c011 * (1 - fr) + c111 * fr
    c0 = c00 * (1 - fg) + c10 * fg
    c1 = c01 * (1 - fg) + c11 * fg
    return c0 * (1 - fb) + c1 * fb


def main() -> None:
  parser = argparse.ArgumentParser()
  parser.add_argument(
    "-o",
    "--output",
    type=Path,
    default=Path(__file__).resolve().parents[1] / "models" / "apply" / "lut3d_trilinear.onnx",
  )
  parser.add_argument("--gs", type=int, default=GS)
  parser.add_argument("--chunk", type=int, default=CHUNK)
  args = parser.parse_args()
  args.output.parent.mkdir(parents=True, exist_ok=True)

  model = Lut3dTrilinear().eval()
  gs = args.gs
  pixels = torch.zeros(args.chunk, 3, dtype=torch.float32)
  lut = torch.zeros(gs, gs, gs, 3, dtype=torch.float32)
  torch.onnx.export(
    model,
    (pixels, lut),
    str(args.output),
    input_names=["pixels", "lut"],
    output_names=["output"],
    dynamic_axes=None,
    opset_version=17,
  )
  import onnx

  m = onnx.load(str(args.output))
  m.ir_version = 9
  onnx.save(m, str(args.output))
  print(f"wrote {args.output} ({args.output.stat().st_size} bytes)")


if __name__ == "__main__":
  main()
