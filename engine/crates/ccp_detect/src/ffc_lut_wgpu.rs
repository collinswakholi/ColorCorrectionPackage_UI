//! Fast-mode flat-field correction fused with the combined LUT, on the GPU.
//!
//! One pass per pixel instead of a CPU f64 FFC pass followed by a separate LUT
//! upload: FFC was 0.68 s of the 0.79 s spent processing a 24 MP photo. Work is
//! split into whole-row chunks with two in flight. A chunk is committed only
//! once it has been read back complete and finite; after any failure the rows
//! not yet committed are processed on the CPU with the same formulas
//! (`ccp_core::fused`), so an image is never corrected twice or partly.

use ccp_core::fused::{apply_ffc_lut_rows, GainGrid};

use crate::lut_chunks::ChunkExecution;
use crate::lut_wgpu::LutExecution;

/// FFC (and the LUT, when given) for a packed `width`×`height` RGB image.
/// `gain_id` identifies the gain map's contents so the GPU copy is reused
/// across a batch; callers must give different maps different ids.
pub fn apply(
    rgb: &mut [f32],
    width: usize,
    height: usize,
    gain: GainGrid<'_>,
    gain_id: u64,
    lut: Option<(&[f32], usize)>,
) -> Result<LutExecution, String> {
    if width == 0 || height == 0 || rgb.len() != width * height * 3 {
        return Err("image size does not match its pixel buffer".into());
    }
    if gain.width == 0 || gain.height == 0 || gain.values.len() != gain.width * gain.height {
        return Err("invalid FFC gain map".into());
    }
    if let Some((table, gs)) = lut {
        if gs < 2 || gs.checked_pow(3).map(|n| n * 3) != Some(table.len()) {
            return Err("invalid LUT".into());
        }
        if !table.iter().all(|v| v.is_finite()) {
            return Err("LUT values must be finite".into());
        }
    }
    #[cfg(feature = "wgpu-lut")]
    {
        native::apply(rgb, width, height, gain, gain_id, lut)
    }
    #[cfg(not(feature = "wgpu-lut"))]
    {
        let _ = gain_id;
        Ok(cpu(
            rgb,
            width,
            height,
            0,
            gain,
            lut,
            Some("wgpu LUT feature disabled".into()),
        ))
    }
}

/// Process rows `first_row..` on the CPU and describe the whole image's run.
fn cpu(
    rgb: &mut [f32],
    width: usize,
    height: usize,
    first_row: usize,
    gain: GainGrid<'_>,
    lut: Option<(&[f32], usize)>,
    reason: Option<String>,
) -> LutExecution {
    apply_ffc_lut_rows(
        &mut rgb[first_row * width * 3..],
        width,
        height,
        first_row,
        gain,
        lut,
    );
    LutExecution {
        backend: "cpu".into(),
        chunks: ChunkExecution {
            accelerated_pixels: first_row * width,
            cpu_pixels: (height - first_row) * width,
            fallback_reason: reason,
        },
        profile: None,
    }
}

#[cfg(feature = "wgpu-lut")]
mod native {
    use super::*;
    use crate::lut_wgpu::native::{block_on, guarded, pick_adapter, selection};
    use ccp_core::fused::FfcConstants;
    use rayon::prelude::*;
    use std::collections::VecDeque;
    use std::sync::{mpsc, Mutex, OnceLock};
    use std::time::Duration;
    use wgpu::util::DeviceExt;

    /// Pixels per chunk: large enough to amortize submission, small enough
    /// for shared-memory integrated GPUs (six buffers of 24 MB).
    const CHUNK_PIXELS: usize = 2 * 1024 * 1024;
    const IDENTITY: [f32; 24] = [
        0., 0., 0., 1., 0., 0., 0., 1., 0., 1., 1., 0., 0., 0., 1., 1., 0., 1., 0., 1., 1., 1., 1.,
        1.,
    ];

    #[repr(C)]
    #[derive(Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
    struct Params {
        pixels: u32,
        width: u32,
        height: u32,
        first: u32,
        gw: u32,
        gh: u32,
        gs: u32,
        use_lut: u32,
        rgb_to_xyz: [[f32; 4]; 3],
        xyz_to_rgb: [[f32; 4]; 3],
        white: [f32; 4],
    }

    fn rows(m: &[f32; 9]) -> [[f32; 4]; 3] {
        [
            [m[0], m[1], m[2], 0.],
            [m[3], m[4], m[5], 0.],
            [m[6], m[7], m[8], 0.],
        ]
    }

    struct Slot {
        input: wgpu::Buffer,
        output: wgpu::Buffer,
        readback: wgpu::Buffer,
        params: wgpu::Buffer,
        group: Option<wgpu::BindGroup>,
    }

    struct FfcGpu {
        device: wgpu::Device,
        queue: wgpu::Queue,
        pipeline: wgpu::ComputePipeline,
        slots: [Slot; 2],
        gain: Option<(u64, usize, wgpu::Buffer)>,
        lut: Option<(Vec<f32>, wgpu::Buffer)>,
        label: String,
    }

    impl FfcGpu {
        fn new(selection: &(String, String)) -> Result<Self, String> {
            let adapter = pick_adapter(selection)?;
            let info = adapter.get_info();
            let available = adapter.limits();
            // A full-resolution gain map of a 50 MP photo is 200 MB; ask for
            // what the adapter offers rather than the 128 MB WebGPU default.
            let limits = wgpu::Limits {
                max_storage_buffer_binding_size: available.max_storage_buffer_binding_size,
                max_buffer_size: available.max_buffer_size,
                ..Default::default()
            };
            let (device, queue) = block_on(adapter.request_device(&wgpu::DeviceDescriptor {
                required_limits: limits,
                ..Default::default()
            }))
            .map_err(|e| e.to_string())?;
            let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some("CCP fused FFC + LUT"),
                source: wgpu::ShaderSource::Wgsl(include_str!("ffc_lut_wgpu.wgsl").into()),
            });
            let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
                label: None,
                layout: None,
                module: &shader,
                entry_point: Some("main"),
                compilation_options: Default::default(),
                cache: None,
            });
            let bytes = (CHUNK_PIXELS * 12) as u64;
            let buffer = |usage, size| {
                device.create_buffer(&wgpu::BufferDescriptor {
                    label: None,
                    size,
                    usage,
                    mapped_at_creation: false,
                })
            };
            let slot = || Slot {
                input: buffer(
                    wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
                    bytes,
                ),
                output: buffer(
                    wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
                    bytes,
                ),
                readback: buffer(
                    wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
                    bytes,
                ),
                params: buffer(
                    wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
                    std::mem::size_of::<Params>() as u64,
                ),
                group: None,
            };
            Ok(Self {
                slots: [slot(), slot()],
                device,
                queue,
                pipeline,
                gain: None,
                lut: None,
                label: format!(
                    "wgpu:{}:{}",
                    format!("{:?}", info.backend).to_lowercase(),
                    info.name
                ),
            })
        }

        /// Upload the gain map and LUT if they changed, rebuilding bind groups.
        fn bind(&mut self, gain: GainGrid<'_>, gain_id: u64, lut: &[f32]) -> Result<(), String> {
            let gain_bytes = (gain.values.len() * 4) as u64;
            let limit = self.device.limits().max_storage_buffer_binding_size;
            if gain_bytes > limit {
                return Err(format!(
                    "FFC gain map ({} MB) exceeds the GPU's {} MB buffer limit",
                    gain_bytes >> 20,
                    limit >> 20
                ));
            }
            let mut changed = false;
            if self
                .gain
                .as_ref()
                .is_none_or(|(id, len, _)| *id != gain_id || *len != gain.values.len())
            {
                self.gain = None; // free the old map before allocating the new one
                let buffer = self
                    .device
                    .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                        label: Some("CCP FFC gain"),
                        contents: bytemuck::cast_slice(gain.values),
                        usage: wgpu::BufferUsages::STORAGE,
                    });
                self.gain = Some((gain_id, gain.values.len(), buffer));
                changed = true;
            }
            if self.lut.as_ref().is_none_or(|(table, _)| table != lut) {
                let buffer = self
                    .device
                    .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                        label: Some("CCP LUT"),
                        contents: bytemuck::cast_slice(lut),
                        usage: wgpu::BufferUsages::STORAGE,
                    });
                self.lut = Some((lut.to_vec(), buffer));
                changed = true;
            }
            if changed || self.slots[0].group.is_none() {
                let layout = self.pipeline.get_bind_group_layout(0);
                let gain = &self.gain.as_ref().unwrap().2;
                let lut = &self.lut.as_ref().unwrap().1;
                for slot in &mut self.slots {
                    slot.group = Some(self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                        label: None,
                        layout: &layout,
                        entries: &[
                            wgpu::BindGroupEntry {
                                binding: 0,
                                resource: slot.input.as_entire_binding(),
                            },
                            wgpu::BindGroupEntry {
                                binding: 1,
                                resource: lut.as_entire_binding(),
                            },
                            wgpu::BindGroupEntry {
                                binding: 2,
                                resource: gain.as_entire_binding(),
                            },
                            wgpu::BindGroupEntry {
                                binding: 3,
                                resource: slot.output.as_entire_binding(),
                            },
                            wgpu::BindGroupEntry {
                                binding: 4,
                                resource: slot.params.as_entire_binding(),
                            },
                        ],
                    }));
                }
            }
            Ok(())
        }

        /// Read back one finished chunk and commit it into `rgb[start..end]`.
        fn commit(
            &self,
            slot: usize,
            submission: wgpu::SubmissionIndex,
            rgb: &mut [f32],
            start: usize,
            end: usize,
        ) -> Result<(), String> {
            let bytes = ((end - start) * 4) as u64;
            let slice = self.slots[slot].readback.slice(..bytes);
            let (tx, rx) = mpsc::channel();
            slice.map_async(wgpu::MapMode::Read, move |v| {
                let _ = tx.send(v);
            });
            self.device
                .poll(wgpu::PollType::Wait {
                    submission_index: Some(submission),
                    timeout: Some(Duration::from_secs(30)),
                })
                .map_err(|e| e.to_string())?;
            rx.recv_timeout(Duration::from_secs(5))
                .map_err(|e| e.to_string())?
                .map_err(|e| e.to_string())?;
            let result = (|| {
                let mapped = slice.get_mapped_range().map_err(|e| e.to_string())?;
                let values: &[f32] = bytemuck::cast_slice(&mapped);
                if !values
                    .par_iter()
                    .with_min_len(65_536)
                    .all(|v| v.is_finite())
                {
                    return Err("accelerator returned nonfinite FFC output".to_string());
                }
                rgb[start..end]
                    .par_chunks_mut(262_144)
                    .zip(values.par_chunks(262_144))
                    .for_each(|(dst, src)| dst.copy_from_slice(src));
                Ok(())
            })();
            self.slots[slot].readback.unmap();
            result
        }

        /// Run every chunk, advancing `committed` (rows) as chunks commit.
        #[allow(clippy::too_many_arguments)]
        fn run(
            &mut self,
            rgb: &mut [f32],
            width: usize,
            height: usize,
            gain: GainGrid<'_>,
            gain_id: u64,
            lut: Option<(&[f32], usize)>,
            committed: &mut usize,
        ) -> Result<(), String> {
            let rows_per_chunk = CHUNK_PIXELS / width;
            if rows_per_chunk == 0 {
                return Err("image rows are too wide for the GPU chunk size".into());
            }
            let (table, gs) = lut.unwrap_or((&IDENTITY, 2));
            self.bind(gain, gain_id, table)?;
            let c = FfcConstants::srgb_d65();
            let mut in_flight: VecDeque<(usize, wgpu::SubmissionIndex, usize)> = VecDeque::new();
            let mut first_row = 0;
            let mut chunk = 0;
            while first_row < height || !in_flight.is_empty() {
                if in_flight.len() == 2 || first_row >= height {
                    let (slot, submission, end_row) = in_flight.pop_front().unwrap();
                    self.commit(
                        slot,
                        submission,
                        rgb,
                        *committed * width * 3,
                        end_row * width * 3,
                    )?;
                    *committed = end_row;
                    continue;
                }
                let slot = chunk % 2;
                let end_row = (first_row + rows_per_chunk).min(height);
                let (start, end) = (first_row * width * 3, end_row * width * 3);
                let pixels = (end_row - first_row) * width;
                let s = &self.slots[slot];
                self.queue
                    .write_buffer(&s.input, 0, bytemuck::cast_slice(&rgb[start..end]));
                let params = Params {
                    pixels: pixels as u32,
                    width: width as u32,
                    height: height as u32,
                    first: (first_row * width) as u32,
                    gw: gain.width as u32,
                    gh: gain.height as u32,
                    gs: gs as u32,
                    use_lut: lut.is_some() as u32,
                    rgb_to_xyz: rows(&c.rgb_to_xyz),
                    xyz_to_rgb: rows(&c.xyz_to_rgb),
                    white: [c.white[0], c.white[1], c.white[2], 0.],
                };
                self.queue
                    .write_buffer(&s.params, 0, bytemuck::bytes_of(&params));
                let mut encoder = self.device.create_command_encoder(&Default::default());
                {
                    let mut pass = encoder.begin_compute_pass(&Default::default());
                    pass.set_pipeline(&self.pipeline);
                    pass.set_bind_group(0, s.group.as_ref(), &[]);
                    pass.dispatch_workgroups((pixels as u32).div_ceil(256), 1, 1);
                }
                encoder.copy_buffer_to_buffer(&s.output, 0, &s.readback, 0, (pixels * 12) as u64);
                let submission = self.queue.submit([encoder.finish()]);
                in_flight.push_back((slot, submission, end_row));
                first_row = end_row;
                chunk += 1;
            }
            Ok(())
        }
    }

    type Cached = Option<((String, String), Result<FfcGpu, String>)>;

    pub(super) fn apply(
        rgb: &mut [f32],
        width: usize,
        height: usize,
        gain: GainGrid<'_>,
        gain_id: u64,
        lut: Option<(&[f32], usize)>,
    ) -> Result<LutExecution, String> {
        static CACHE: OnceLock<Mutex<Cached>> = OnceLock::new();
        let selection = selection();
        if selection.0 == "cpu" {
            return Ok(cpu(
                rgb,
                width,
                height,
                0,
                gain,
                lut,
                Some("CPU LUT requested".into()),
            ));
        }
        if u32::try_from(width * height).is_err() {
            return Ok(cpu(
                rgb,
                width,
                height,
                0,
                gain,
                lut,
                Some("image too large for one GPU pass".into()),
            ));
        }
        let mut cache = CACHE
            .get_or_init(|| Mutex::new(None))
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        if cache.as_ref().is_none_or(|(key, _)| key != &selection) {
            *cache = Some((selection.clone(), guarded(|| FfcGpu::new(&selection))));
        }
        let state = &mut cache.as_mut().unwrap().1;
        let gpu = match state {
            Ok(gpu) => gpu,
            Err(error) => return Ok(cpu(rgb, width, height, 0, gain, lut, Some(error.clone()))),
        };
        let label = gpu.label.clone();
        let mut committed = 0;
        let result = guarded(|| gpu.run(rgb, width, height, gain, gain_id, lut, &mut committed));
        match result {
            Ok(()) => Ok(LutExecution {
                backend: label,
                chunks: ChunkExecution {
                    accelerated_pixels: width * height,
                    cpu_pixels: 0,
                    fallback_reason: None,
                },
                profile: None,
            }),
            Err(error) => {
                // A limit the map exceeds is not a device fault; anything else is.
                if !error.contains("buffer limit") {
                    *state = Err(error.clone());
                }
                let mut execution = cpu(rgb, width, height, committed, gain, lut, Some(error));
                if committed > 0 {
                    execution.backend = format!("{label}+cpu");
                }
                Ok(execution)
            }
        }
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn gpu_matches_cpu_or_falls_back_whole_rows() {
            let (w, h) = (1531, 1403); // two chunks with a partial tail
            let mut image: Vec<f32> = (0..w * h * 3)
                .map(|i| ((i * 2_654_435_761usize) % 1000) as f32 / 999.0)
                .collect();
            let gain: Vec<f32> = (0..64 * 48)
                .map(|i| 0.85 + 0.3 * ((i % 64) as f32 / 63.0) + 0.1 * ((i / 64) as f32 / 47.0))
                .collect();
            let grid = GainGrid {
                values: &gain,
                width: 64,
                height: 48,
            };
            let gs = 9;
            let lut: Vec<f32> = (0..gs * gs * gs * 3)
                .map(|i| {
                    let p = i / 3;
                    let v = [p % gs, (p / gs) % gs, p / gs / gs][i % 3] as f32 / (gs - 1) as f32;
                    (v * 0.9 + 0.05).powf(1.1)
                })
                .collect();
            let mut expected = image.clone();
            apply_ffc_lut_rows(&mut expected, w, h, 0, grid, Some((&lut, gs)));
            let execution = apply(&mut image, w, h, grid, 7, Some((&lut, gs))).unwrap();
            assert_eq!(
                execution.chunks.accelerated_pixels + execution.chunks.cpu_pixels,
                w * h
            );
            assert_eq!(execution.chunks.accelerated_pixels % w, 0);
            let worst = expected
                .iter()
                .zip(&image)
                .map(|(a, b)| (a - b).abs())
                .fold(0f32, f32::max);
            assert!(
                worst < 2e-3,
                "{execution:?}: max GPU/CPU difference {worst}"
            );
        }
    }
}
