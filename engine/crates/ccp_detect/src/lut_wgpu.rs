//! Portable LUT execution. Inference policy is deliberately independent.
//! A cached device and staging buffers serve successive images. Only complete,
//! finite chunks are committed; device loss recovers the untouched suffix.
#[cfg(any(test, not(feature = "wgpu-lut")))]
use crate::lut_chunks::apply_chunks;
use crate::lut_chunks::ChunkExecution;

#[derive(Debug, Clone)]
pub struct LutExecution {
    pub backend: String,
    pub chunks: ChunkExecution,
    pub profile: Option<LutProfile>,
}

/// Opt-in diagnostics; GPU intervals overlap host wait time and must not be summed.
#[derive(Debug, Clone, Default, serde::Serialize)]
pub struct LutProfile {
    pub chunks: usize,
    pub prepare_ms: f64,
    pub upload_ms: f64,
    pub submit_ms: f64,
    pub wait_ms: f64,
    pub readback_ms: f64,
    pub gpu_compute_ms: Option<f64>,
    pub gpu_copy_ms: Option<f64>,
    pub gpu_upload_ms: Option<f64>,
}

pub fn apply(rgb: &mut [f32], lut: &[f32], gs: usize) -> Result<LutExecution, String> {
    #[cfg(feature = "wgpu-lut")]
    {
        native::apply(rgb, lut, gs)
    }
    #[cfg(not(feature = "wgpu-lut"))]
    {
        let chunks = apply_chunks(rgb, lut, gs, 1_048_576, |_| {
            Err("wgpu LUT feature disabled".into())
        })?;
        Ok(LutExecution {
            backend: "cpu".into(),
            chunks,
            profile: None,
        })
    }
}

#[cfg(feature = "wgpu-lut")]
mod native {
    use super::*;
    use rayon::prelude::*;
    use std::{
        future::Future,
        pin::pin,
        sync::{mpsc, Arc, Mutex, OnceLock},
        task::{Context, Wake, Waker},
        time::{Duration, Instant},
    };
    use wgpu::util::DeviceExt;
    const CHUNK: usize = 1_048_576;

    fn block_on<F: Future>(future: F) -> F::Output {
        struct ThreadWake(std::thread::Thread);
        impl Wake for ThreadWake {
            fn wake(self: Arc<Self>) {
                self.0.unpark();
            }
        }
        let waker = Waker::from(Arc::new(ThreadWake(std::thread::current())));
        let mut cx = Context::from_waker(&waker);
        let mut future = pin!(future);
        loop {
            match future.as_mut().poll(&mut cx) {
                std::task::Poll::Ready(out) => return out,
                std::task::Poll::Pending => std::thread::park(),
            }
        }
    }

    fn guarded<T>(f: impl FnOnce() -> Result<T, String>) -> Result<T, String> {
        std::panic::catch_unwind(std::panic::AssertUnwindSafe(f))
            .unwrap_or_else(|_| Err("wgpu execution panicked; chunk was not committed".into()))
    }

    struct Gpu {
        device: wgpu::Device,
        queue: wgpu::Queue,
        pipeline: wgpu::ComputePipeline,
        input: wgpu::Buffer,
        upload: wgpu::Buffer,
        output: wgpu::Buffer,
        readback: wgpu::Buffer,
        count: wgpu::Buffer,
        table: Vec<f32>,
        group: Option<wgpu::BindGroup>,
        label: String,
        queries: Option<(wgpu::QuerySet, wgpu::Buffer, wgpu::Buffer)>,
        profile: LutProfile,
    }
    impl Gpu {
        fn new(selection: &(String, String)) -> Result<Self, String> {
            let backends = match selection.0.as_str() {
                "auto" | "" => {
                    wgpu::Backends::DX12 | wgpu::Backends::VULKAN | wgpu::Backends::METAL
                }
                "dx12" => wgpu::Backends::DX12,
                "vulkan" => wgpu::Backends::VULKAN,
                "metal" => wgpu::Backends::METAL,
                _ => return Err("CCP_LUT_BACKEND must be auto, cpu, dx12, vulkan, or metal".into()),
            };
            let mut descriptor = wgpu::InstanceDescriptor::new_without_display_handle();
            descriptor.flags = descriptor.flags.with_env();
            let instance = wgpu::Instance::new(descriptor);
            let mut adapters = block_on(instance.enumerate_adapters(backends));
            adapters.retain(|a| {
                let i = a.get_info();
                matches!(
                    i.device_type,
                    wgpu::DeviceType::DiscreteGpu | wgpu::DeviceType::IntegratedGpu
                ) && (selection.1.is_empty() || i.name.to_lowercase().contains(&selection.1))
            });
            // Discrete hardware first; on Windows the measured DX12 path wins.
            // Other native platforms use Vulkan/Metal without translation layers.
            adapters.sort_by_key(|a| {
                let i = a.get_info();
                (
                    i.device_type != wgpu::DeviceType::DiscreteGpu,
                    cfg!(target_os = "windows") && i.backend != wgpu::Backend::Dx12,
                    i.vendor,
                    i.device,
                    i.name,
                )
            });
            let adapter = adapters
                .into_iter()
                .next()
                .ok_or("no matching hardware wgpu adapter")?;
            let info = adapter.get_info();
            let profiling = std::env::var_os("CCP_LUT_PROFILE").is_some();
            let query_features =
                wgpu::Features::TIMESTAMP_QUERY | wgpu::Features::TIMESTAMP_QUERY_INSIDE_ENCODERS;
            let timestamps = profiling && adapter.features().contains(query_features);
            let (device, queue) = block_on(adapter.request_device(&wgpu::DeviceDescriptor {
                required_features: if timestamps {
                    query_features
                } else {
                    wgpu::Features::empty()
                },
                ..Default::default()
            }))
            .map_err(|e| e.to_string())?;
            let shader = device.create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some("CCP encoded RGB trilinear LUT"),
                source: wgpu::ShaderSource::Wgsl(include_str!("lut_wgpu.wgsl").into()),
            });
            let pipeline = device.create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
                label: None,
                layout: None,
                module: &shader,
                entry_point: Some("main"),
                compilation_options: Default::default(),
                cache: None,
            });
            let buffer = |usage, size| {
                device.create_buffer(&wgpu::BufferDescriptor {
                    label: None,
                    size,
                    usage,
                    mapped_at_creation: false,
                })
            };
            let input = buffer(
                wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_DST,
                (CHUNK * 12) as u64,
            );
            let upload = device.create_buffer(&wgpu::BufferDescriptor {
                label: Some("Reusable LUT upload"),
                size: (CHUNK * 12) as u64,
                usage: wgpu::BufferUsages::MAP_WRITE | wgpu::BufferUsages::COPY_SRC,
                mapped_at_creation: true,
            });
            let output = buffer(
                wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
                (CHUNK * 12) as u64,
            );
            let readback = buffer(
                wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
                (CHUNK * 12) as u64,
            );
            let count = buffer(
                wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
                16,
            );
            let queries = timestamps.then(|| {
                (
                    device.create_query_set(&wgpu::QuerySetDescriptor {
                        label: Some("LUT timings"),
                        ty: wgpu::QueryType::Timestamp,
                        count: 4,
                    }),
                    buffer(
                        wgpu::BufferUsages::QUERY_RESOLVE | wgpu::BufferUsages::COPY_SRC,
                        32,
                    ),
                    buffer(
                        wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
                        32,
                    ),
                )
            });
            Ok(Self {
                device,
                queue,
                pipeline,
                input,
                upload,
                output,
                readback,
                count,
                table: vec![],
                group: None,
                queries,
                profile: LutProfile::default(),
                label: format!(
                    "wgpu:{}:{}",
                    format!("{:?}", info.backend).to_lowercase(),
                    info.name
                ),
            })
        }

        fn chunk(
            &mut self,
            part: &[f32],
            lut: &[f32],
            gs: usize,
            result: &mut Vec<f32>,
        ) -> Result<(), String> {
            let stage = Instant::now();
            if self.table != lut {
                let buffer = self
                    .device
                    .create_buffer_init(&wgpu::util::BufferInitDescriptor {
                        label: Some("CCP LUT"),
                        contents: bytemuck::cast_slice(lut),
                        usage: wgpu::BufferUsages::STORAGE,
                    });
                self.group = Some(self.device.create_bind_group(&wgpu::BindGroupDescriptor {
                    label: None,
                    layout: &self.pipeline.get_bind_group_layout(0),
                    entries: &[
                        wgpu::BindGroupEntry {
                            binding: 0,
                            resource: self.input.as_entire_binding(),
                        },
                        wgpu::BindGroupEntry {
                            binding: 1,
                            resource: buffer.as_entire_binding(),
                        },
                        wgpu::BindGroupEntry {
                            binding: 2,
                            resource: self.output.as_entire_binding(),
                        },
                        wgpu::BindGroupEntry {
                            binding: 3,
                            resource: self.count.as_entire_binding(),
                        },
                    ],
                }));
                self.table = lut.to_vec();
            }
            let n = part.len() / 3;
            self.profile.prepare_ms += stage.elapsed().as_secs_f64() * 1000.;
            let stage = Instant::now();
            let bytes = (part.len() * 4) as u64;
            bytemuck::cast_slice::<f32, u8>(part)
                .par_chunks(1_048_576)
                .enumerate()
                .try_for_each(|(i, src)| -> Result<(), String> {
                    let start = (i * 1_048_576) as u64;
                    let mut mapped = self
                        .upload
                        .slice(start..start + src.len() as u64)
                        .get_mapped_range_mut()
                        .map_err(|e| e.to_string())?;
                    mapped.copy_from_slice(src);
                    Ok(())
                })?;
            self.upload.unmap();
            self.queue.write_buffer(
                &self.count,
                0,
                bytemuck::cast_slice(&[n as u32, gs as u32, 0, 0]),
            );
            self.profile.upload_ms += stage.elapsed().as_secs_f64() * 1000.;
            let stage = Instant::now();
            let mut encoder = self.device.create_command_encoder(&Default::default());
            if let Some((q, _, _)) = &self.queries {
                encoder.write_timestamp(q, 0);
            }
            encoder.copy_buffer_to_buffer(&self.upload, 0, &self.input, 0, bytes);
            {
                let mut pass = encoder.begin_compute_pass(&wgpu::ComputePassDescriptor {
                    timestamp_writes: self.queries.as_ref().map(|(q, _, _)| {
                        wgpu::ComputePassTimestampWrites {
                            query_set: q,
                            beginning_of_pass_write_index: Some(1),
                            end_of_pass_write_index: Some(2),
                        }
                    }),
                    ..Default::default()
                });
                pass.set_pipeline(&self.pipeline);
                pass.set_bind_group(0, self.group.as_ref().unwrap(), &[]);
                pass.dispatch_workgroups((n as u32).div_ceil(256), 1, 1);
            }
            encoder.copy_buffer_to_buffer(&self.output, 0, &self.readback, 0, bytes);
            if let Some((q, resolve, readback)) = &self.queries {
                encoder.write_timestamp(q, 3);
                encoder.resolve_query_set(q, 0..4, resolve, 0);
                encoder.copy_buffer_to_buffer(resolve, 0, readback, 0, 32);
            }
            let submission = self.queue.submit([encoder.finish()]);
            let slice = self.readback.slice(..bytes);
            let (tx, rx) = mpsc::channel();
            slice.map_async(wgpu::MapMode::Read, move |v| {
                let _ = tx.send(v);
            });
            let (upload_tx, upload_rx) = mpsc::channel();
            self.upload
                .slice(..)
                .map_async(wgpu::MapMode::Write, move |v| {
                    let _ = upload_tx.send(v);
                });
            let (query_tx, query_rx) = mpsc::channel();
            if let Some((_, _, readback)) = &self.queries {
                readback.slice(..).map_async(wgpu::MapMode::Read, move |v| {
                    let _ = query_tx.send(v);
                });
            }
            self.profile.submit_ms += stage.elapsed().as_secs_f64() * 1000.;
            let stage = Instant::now();
            self.device
                .poll(wgpu::PollType::Wait {
                    submission_index: Some(submission),
                    timeout: Some(Duration::from_secs(30)),
                })
                .map_err(|e| e.to_string())?;
            self.profile.wait_ms += stage.elapsed().as_secs_f64() * 1000.;
            let stage = Instant::now();
            rx.recv_timeout(Duration::from_secs(1))
                .map_err(|e| e.to_string())?
                .map_err(|e| e.to_string())?;
            upload_rx
                .recv_timeout(Duration::from_secs(1))
                .map_err(|e| e.to_string())?
                .map_err(|e| e.to_string())?;
            {
                let mapped = slice.get_mapped_range().map_err(|e| e.to_string())?;
                copy_readback(bytemuck::cast_slice::<u8, f32>(&mapped), result);
            }
            self.readback.unmap();
            self.profile.readback_ms += stage.elapsed().as_secs_f64() * 1000.;
            self.profile.chunks += 1;
            if let Some((_, _, readback)) = &self.queries {
                query_rx
                    .recv_timeout(Duration::from_secs(1))
                    .map_err(|e| e.to_string())?
                    .map_err(|e| e.to_string())?;
                {
                    let mapped = readback
                        .slice(..)
                        .get_mapped_range()
                        .map_err(|e| e.to_string())?;
                    let ticks: &[u64] = bytemuck::cast_slice(&mapped);
                    let ms = self.queue.get_timestamp_period() as f64 / 1e6;
                    *self.profile.gpu_upload_ms.get_or_insert(0.) +=
                        ticks[1].saturating_sub(ticks[0]) as f64 * ms;
                    *self.profile.gpu_compute_ms.get_or_insert(0.) +=
                        ticks[2].saturating_sub(ticks[1]) as f64 * ms;
                    *self.profile.gpu_copy_ms.get_or_insert(0.) +=
                        ticks[3].saturating_sub(ticks[2]) as f64 * ms;
                }
                readback.unmap();
            }
            Ok(())
        }
    }

    fn copy_readback(source: &[f32], output: &mut Vec<f32>) {
        output.clear();
        output.reserve(source.len());
        output.spare_capacity_mut()[..source.len()]
            .par_chunks_mut(262_144)
            .zip(source.par_chunks(262_144))
            .for_each(|(dst, src)| {
                for (dst, src) in dst.iter_mut().zip(src) {
                    dst.write(*src);
                }
            });
        // Every element of this exact prefix has just been initialized above.
        // On panic the length remains zero, so no partial result is committed.
        unsafe {
            output.set_len(source.len());
        }
    }

    type Cached = Option<((String, String), Result<Gpu, String>)>;
    pub(super) fn apply(rgb: &mut [f32], lut: &[f32], gs: usize) -> Result<LutExecution, String> {
        static CACHE: OnceLock<Mutex<Cached>> = OnceLock::new();
        let selection = (
            std::env::var("CCP_LUT_BACKEND")
                .unwrap_or_else(|_| "auto".into())
                .to_lowercase(),
            std::env::var("CCP_LUT_ADAPTER")
                .unwrap_or_default()
                .to_lowercase(),
        );
        let mut cache = CACHE
            .get_or_init(|| Mutex::new(None))
            .lock()
            .unwrap_or_else(|e| e.into_inner());
        let mut label = "cpu".to_string();
        if let Some((_, Ok(gpu))) = cache.as_mut() {
            gpu.profile = LutProfile::default();
        }
        // Validation in apply_chunks happens before device creation or mutation.
        let chunks = crate::lut_chunks::apply_chunks_into(rgb, lut, gs, CHUNK, |part, output| {
            if selection.0 == "cpu" {
                return Err("CPU LUT requested".into());
            }
            if cache.as_ref().is_none_or(|(key, _)| key != &selection) {
                *cache = Some((selection.clone(), guarded(|| Gpu::new(&selection))));
            }
            let state = &mut cache.as_mut().unwrap().1;
            let gpu = state.as_mut().map_err(|e| e.clone())?;
            label = gpu.label.clone();
            let result = guarded(|| gpu.chunk(part, lut, gs, output));
            if let Err(error) = &result {
                *state = Err(error.clone());
            }
            result
        })?;
        if chunks.accelerated_pixels == 0 {
            label = "cpu".into();
        } else if chunks.cpu_pixels > 0 {
            label.push_str("+cpu");
        }
        Ok(LutExecution {
            backend: label,
            chunks,
            profile: std::env::var_os("CCP_LUT_PROFILE").and_then(|_| {
                cache
                    .as_ref()
                    .and_then(|(_, gpu)| gpu.as_ref().ok().map(|g| g.profile.clone()))
            }),
        })
    }

    #[cfg(test)]
    mod tests {
        use super::*;
        #[test]
        fn readback_reuses_capacity_and_replaces_shorter_output() {
            let mut scratch = vec![7.; 900_000];
            let capacity = scratch.capacity();
            for n in [800_003, 1, 400_001, 0] {
                let source: Vec<_> = (0..n).map(|i| i as f32 * 0.25).collect();
                copy_readback(&source, &mut scratch);
                assert_eq!(scratch, source);
                assert_eq!(scratch.capacity(), capacity);
            }
        }
        #[test]
        fn driver_panic_after_completed_chunk_recovers_without_double_application() {
            let mut lut = vec![];
            for b in 0..3 {
                for g in 0..3 {
                    for r in 0..3 {
                        lut.extend([r, g, b].map(|v| (v as f32 / 2.).powi(2)));
                    }
                }
            }
            let mut image = vec![0.75; 15];
            let mut calls = 0;
            let result = apply_chunks(&mut image, &lut, 3, 2, |_| {
                guarded(|| {
                    calls += 1;
                    if calls == 2 {
                        panic!("injected driver panic");
                    }
                    Ok(vec![0.625; 6])
                })
            })
            .unwrap();
            assert_eq!(image, vec![0.625; 15]);
            assert_eq!((result.accelerated_pixels, result.cpu_pixels), (2, 3));
            assert!(result.fallback_reason.unwrap().contains("panicked"));
        }
    }
}
