"""Final installed-package stress checks; run in Sandbox/WSL, never on the host.

Only the Python standard library and installed SDK are required. The fixture
bundle contains small/chart.jpg, small/white.png, small/uniform.png, four
24 MP photographs in batch/, chart.jpg, blank.jpg and corrupt.jpg.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import struct
import sys
import time
import traceback


def worker_count():
    if os.name != 'nt':
        count=0
        for path in Path('/proc').glob('[0-9]*/comm'):
            try: count += path.read_text().strip() == 'ccp-engine'
            except (FileNotFoundError, PermissionError): pass
        return count
    from ctypes import wintypes as w
    class ProcessEntry(ctypes.Structure):
        _fields_=[('size',w.DWORD),('usage',w.DWORD),('pid',w.DWORD),('heap',ctypes.c_size_t),
                  ('module',w.DWORD),('threads',w.DWORD),('parent',w.DWORD),('priority',w.LONG),
                  ('flags',w.DWORD),('name',w.WCHAR*260)]
    k=ctypes.WinDLL('kernel32',use_last_error=True)
    k.CreateToolhelp32Snapshot.argtypes=[w.DWORD,w.DWORD]; k.CreateToolhelp32Snapshot.restype=w.HANDLE
    k.Process32FirstW.argtypes=[w.HANDLE,ctypes.POINTER(ProcessEntry)]
    k.Process32NextW.argtypes=[w.HANDLE,ctypes.POINTER(ProcessEntry)]
    k.CloseHandle.argtypes=[w.HANDLE]
    handle=k.CreateToolhelp32Snapshot(2,0)
    if handle==ctypes.c_void_p(-1).value: raise ctypes.WinError(ctypes.get_last_error())
    try:
        entry=ProcessEntry(); entry.size=ctypes.sizeof(entry); count=0
        valid=k.Process32FirstW(handle,ctypes.byref(entry))
        while valid:
            count += entry.name.lower()=='ccp-engine.exe'
            valid=k.Process32NextW(handle,ctypes.byref(entry))
        return count
    finally: k.CloseHandle(handle)


def resources(pid):
    """Observe the worker, not the Python driver's memory."""
    if os.name != "nt":
        status = Path(f"/proc/{pid}/status").read_text()
        values = {line.split(':', 1)[0]: line.split(':', 1)[1].strip() for line in status.splitlines() if ':' in line}
        return {"rss_bytes": int(values["VmRSS"].split()[0]) * 1024,
                "peak_rss_bytes": int(values["VmHWM"].split()[0]) * 1024,
                "handles": len(list(Path(f"/proc/{pid}/fd").iterdir())),
                "threads": int(values["Threads"])}
    from ctypes import wintypes as w
    class Memory(ctypes.Structure):
        _fields_ = [("cb", w.DWORD), ("faults", w.DWORD)] + [(name, ctypes.c_size_t) for name in
            ("peak", "rss", "peak_paged", "paged", "peak_nonpaged", "nonpaged", "pagefile", "peak_pagefile")]
    k = ctypes.WinDLL("kernel32", use_last_error=True)
    k.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]; k.OpenProcess.restype = w.HANDLE
    k.CloseHandle.argtypes = [w.HANDLE]
    handle = k.OpenProcess(0x410, False, pid)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        memory = Memory(); memory.cb = ctypes.sizeof(memory)
        get_memory = ctypes.WinDLL("psapi").GetProcessMemoryInfo
        get_memory.argtypes = [w.HANDLE, ctypes.POINTER(Memory), w.DWORD]
        if not get_memory(handle, ctypes.byref(memory), memory.cb):
            raise ctypes.WinError(ctypes.get_last_error())
        count = w.DWORD()
        k.GetProcessHandleCount.argtypes = [w.HANDLE, ctypes.POINTER(w.DWORD)]
        if not k.GetProcessHandleCount(handle, ctypes.byref(count)):
            raise ctypes.WinError(ctypes.get_last_error())
        return {"rss_bytes": memory.rss, "peak_rss_bytes": memory.peak, "handles": count.value}
    finally:
        k.CloseHandle(handle)


def loaded_libraries(pid):
    if os.name != "nt":
        return sorted({line.split()[-1] for line in Path(f"/proc/{pid}/maps").read_text().splitlines()
                       if len(line.split()) >= 6 and line.split()[-1].startswith('/')})
    from ctypes import wintypes as w
    k = ctypes.WinDLL("kernel32"); p = ctypes.WinDLL("psapi")
    k.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]; k.OpenProcess.restype = w.HANDLE
    k.CloseHandle.argtypes = [w.HANDLE]
    handle = k.OpenProcess(0x410, False, pid)
    if not handle:
        raise ctypes.WinError()
    try:
        modules = (w.HMODULE * 2048)(); needed = w.DWORD()
        p.EnumProcessModulesEx.argtypes = [w.HANDLE, ctypes.POINTER(w.HMODULE), w.DWORD, ctypes.POINTER(w.DWORD), w.DWORD]
        if not p.EnumProcessModulesEx(handle, modules, ctypes.sizeof(modules), ctypes.byref(needed), 3):
            raise ctypes.WinError()
        p.GetModuleFileNameExW.argtypes = [w.HANDLE, w.HMODULE, w.LPWSTR, w.DWORD]
        paths = []
        for module in modules[:needed.value // ctypes.sizeof(w.HMODULE)]:
            buffer = ctypes.create_unicode_buffer(32768)
            if p.GetModuleFileNameExW(handle, module, buffer, len(buffer)):
                paths.append(buffer.value)
        return sorted(paths)
    finally:
        k.CloseHandle(handle)


def finite(value):
    if isinstance(value, float):
        assert math.isfinite(value), "Nonfinite model/report value"
    elif isinstance(value, dict):
        for item in value.values(): finite(item)
    elif isinstance(value, list):
        for item in value: finite(item)


def rgb(path):
    data = path.read_bytes()
    assert data[:8] == b"CCPRGB8\n"
    width, height = struct.unpack('<II', data[8:16])
    assert len(data) == 16 + width * height * 3
    return data


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('package', 'fixtures', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    parser.add_argument('--applications', type=int, default=100)
    args = parser.parse_args()
    assert args.applications >= 100, "Release stress requires at least 100 applications"
    args.package = args.package.resolve(); args.fixtures = args.fixtures.resolve()
    args.output = args.output.resolve(); args.output.mkdir(parents=True, exist_ok=False)
    os.chdir(args.output)
    import colorcorrect
    from colorcorrect import Client, CalibrationOptions, ApplyOptions, FFCOptions, NeuralNetworkOptions
    assert sys.prefix != sys.base_prefix, "Use a clean virtual environment"
    assert Path(colorcorrect.__file__).resolve().is_relative_to(Path(sys.prefix).resolve()), colorcorrect.__file__
    report = {"status": "running", "module": colorcorrect.__file__, "package": str(args.package),
              "methods": [], "applications": [], "providers": [], "checks": []}
    def save():
        (args.output / 'stress.json').write_text(json.dumps(report, indent=2), encoding='utf8')
    try:
        small = args.fixtures / 'small/chart.jpg'; white = args.fixtures / 'small/white.png'
        small_meta = json.loads((args.fixtures / 'small/manifest.json').read_text())
        width, height = small_meta['dimensions']; crop = f'0,0,{width},{height}'
        with Client(root=args.package, backend='auto', timeout=600) as client:
            assert client.engine.executable.parent == args.package
            detection = client.detect(small)
            for method in ('ccm', 'rpcc2', 'linear', 'pls', 'nn'):
                for ffc_method in ('off', 'linear', 'poly', 'pls', 'nn'):
                    start = time.perf_counter()
                    opts = CalibrationOptions(model=method, degree=2, do_ffc=ffc_method != 'off', crop_rect=crop,
                        ffc=FFCOptions(method='poly' if ffc_method == 'off' else ffc_method, bins=12, degree=3,
                                       nn_max_iter=1000, tol=1e-6),
                        nn=NeuralNetworkOptions(hidden_layers=(24,), epochs=200, patience=40),
                        cross_validate=False, phase2_teacher=False)
                    fitted = client.calibrate(small, detection=detection, white=white if ffc_method != 'off' else None,
                                              output_dir=args.output, options=opts)
                    saved = json.loads(fitted.path.read_text()); finite(saved)
                    assert fitted.metrics['ffc_method'] == ffc_method, fitted.metrics
                    assert fitted.metrics['do_ffc'] == (ffc_method != 'off')
                    restored = client.load_calibration(fitted.path)
                    applied = restored.apply(small, output_dir=args.output)
                    assert len(applied.outputs) == 1 and not applied.failures
                    # Compare encoded preview pixels from the SAME fitted model.
                    # Raw f32 LUT tolerance is checked by engine component tests;
                    # 8-bit encoding can differ by one rounding step.
                    previews = []
                    executions = []
                    for device in ('cpu', 'gpu'):
                        folder = args.output / f'{method}-{ffc_method}-{device}'
                        reply = client.engine.request('preview_stage', model=fitted.path, image=small, stage='cc',
                            preview_dir=folder, lut_device=device, fast_mode=True, do_ffc=ffc_method != 'off',
                            white=white if ffc_method != 'off' else None, crop_rect=crop,
                            ffc=opts.ffc.to_dict())
                        previews.append(rgb(folder / 'corrected.rgb'))
                        execution = reply.get('execution') or {}
                        assert execution.get('cpu_pixels' if device == 'cpu' else 'accelerated_pixels') == width * height, reply
                        executions.append(execution)
                    assert previews[0][:16] == previews[1][:16]
                    difference = max(abs(a-b) for a,b in zip(previews[0][16:], previews[1][16:]))
                    assert difference <= 1, (method, ffc_method, difference)
                    report['methods'].append({'cc': method, 'ffc': ffc_method, 'status': 'passed',
                        'seconds': time.perf_counter()-start, 'max_encoded_rgb_difference': difference,
                        'model_sha256': hashlib.sha256(fitted.path.read_bytes()).hexdigest(),
                        'backends': fitted.backends.actual, 'execution': executions})
                    save()
            report['loaded_libraries'] = loaded_libraries(client.engine._process.pid)
            for library in report['loaded_libraries']:
                assert 'torch' not in Path(library).name.lower() and not Path(library).name.lower().startswith('c10'), library
                if Path(library).name.lower().startswith(('onnxruntime', 'libonnxruntime', 'directml')):
                    assert Path(library).resolve().is_relative_to(args.package), library
            # A genuine automatic plane-detection fit, followed by large-image reuse.
            big_model = client.calibrate(args.fixtures / 'chart.jpg', white=args.fixtures / 'blank.jpg',
                                         output_dir=args.output, options=CalibrationOptions(write_output=False))
            assert big_model.backends.actual.get('plane') and 'cpu' not in big_model.backends.actual['plane'].lower()
            report['automatic_plane'] = big_model.backends.actual
            photos = sorted((args.fixtures / 'batch').glob('*.JPG'))
            assert len(photos) == 4
            for index in range(args.applications):
                device = ('auto', 'cpu', 'gpu')[index % 3]
                enabled_ffc = index % 2 == 0
                output = args.output / f'soak-{index:03d}'
                start = time.perf_counter()
                reply = client.engine.request('apply', model=big_model.path, images=[photos[index % 4]],
                    output_dir=output, output_format='jpg', lut_device=device, fast_mode=True,
                    do_ffc=enabled_ffc, white=args.fixtures / 'blank.jpg' if enabled_ffc else None,
                    ffc=FFCOptions().to_dict())
                row = reply['results'][0]; execution = row['execution']
                assert row['ok'] and Path(row['output']).is_file()
                assert execution['accelerated_pixels'] + execution['cpu_pixels'] == 24000000
                assert execution['method'] == 'combined-lut65'
                assert execution['cpu_pixels' if device == 'cpu' else 'accelerated_pixels'] == 24000000, execution
                report['applications'].append({'index': index, 'image': photos[index%4].name, 'device': device,
                    'ffc': enabled_ffc, 'seconds': time.perf_counter()-start, 'execution': execution,
                    'worker_pid': client.engine._process.pid, 'worker_count':worker_count(), **resources(client.engine._process.pid)})
                save()
            times = [row['seconds'] for row in report['applications']]
            report['timing'] = {'median_seconds': statistics.median(times),
                                'p95_seconds': sorted(times)[math.ceil(len(times)*.95)-1]}
            report['timing_by_mode'] = []
            report['resources']={
                'rss_median_growth_bytes':statistics.median(row['rss_bytes'] for row in report['applications'][-10:])-statistics.median(row['rss_bytes'] for row in report['applications'][10:20]),
                'handles_growth':report['applications'][-1]['handles']-report['applications'][10]['handles'],
                'worker_counts':sorted(set(row['worker_count'] for row in report['applications'])),
                'peak_rss_bytes':max(row['peak_rss_bytes'] for row in report['applications'])}
            for device in ('auto', 'cpu', 'gpu'):
                for ffc in (False, True):
                    group = [row['seconds'] for row in report['applications'][4:] if row['device'] == device and row['ffc'] == ffc]
                    report['timing_by_mode'].append({'device': device, 'ffc': ffc, 'n': len(group),
                        'median_seconds': statistics.median(group), 'p95_seconds': sorted(group)[math.ceil(len(group)*.95)-1],
                        'scope': 'stress observations; not controlled microbenchmark certification'})
        for provider in ('directml', 'webgpu', 'vulkan'):
            with Client(root=args.package, backend=provider, timeout=600) as client:
                reply = client.engine.request('detect', image=small, bypass_cache=True, check=False)
                expected_unavailable = os.name != 'nt' and provider == 'directml'
                assert bool(reply.get('ok')) != expected_unavailable, reply
                report['providers'].append({'provider': provider, 'status': 'unavailable on this OS' if expected_unavailable else 'passed',
                                            'actual': reply.get('device'), 'error': reply.get('error')})
                save()
        report['status'] = 'passed'
    except BaseException:
        report['status'] = 'failed'; report['error'] = traceback.format_exc()
        raise
    finally:
        save()


if __name__ == '__main__':
    main()
