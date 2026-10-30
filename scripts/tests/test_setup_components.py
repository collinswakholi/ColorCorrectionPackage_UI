"""Component contracts and real bootstrap diagnostics; no GPU certification."""
from pathlib import Path
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).parents[1]))
from setup_components import component_manifest

ROOT = Path(__file__).parents[2]

class SetupComponentTests(unittest.TestCase):
    def test_cpu_closure_retains_linked_directml_but_not_webgpu(self):
        with tempfile.TemporaryDirectory() as folder:
            payload = Path(folder)
            for name in ('onnxruntime.dll','DirectML.dll','onnxruntime_providers_webgpu.dll'):
                (payload / name).write_bytes(name.encode())
            manifest = component_manifest(payload, 'win-arm64', {'packages':[{'id':'pinned','version':'1'}]}, '0.2.1')
            components = {component['id']:component for component in manifest['components']}
            self.assertTrue(components['cpu']['required'])
            self.assertEqual({file['path'] for file in components['cpu']['files']}, {'onnxruntime.dll','DirectML.dll'})
            self.assertFalse(components['webgpu']['required'])
            self.assertEqual(manifest['architecture'], 'arm64')
            for component in components.values():
                for file in component['files']:
                    self.assertEqual(file['sha256'], hashlib.sha256((payload/file['path']).read_bytes()).hexdigest())

    def test_optional_plugin_absent_does_not_remove_cpu_contract(self):
        with tempfile.TemporaryDirectory() as folder:
            payload = Path(folder)
            (payload/'libonnxruntime.so').write_bytes(b'CPU')
            manifest = component_manifest(payload,'linux-x64',{},'0.2.1')
            self.assertEqual(manifest['components'][1]['files'],[])
            self.assertEqual(manifest['components'][0]['files'][0]['path'],'libonnxruntime.so')
            self.assertEqual(manifest['system_support'],['Ubuntu 24.04'])

    @unittest.skipUnless(os.name == 'nt', 'Windows PowerShell bootstrap')
    def test_windows_cpu_inventory_is_json_without_launching_worker(self):
        with tempfile.TemporaryDirectory() as folder:
            payload = Path(folder)
            shutil.copy2(ROOT/'installer/windows/setup.ps1',payload/'setup.ps1')
            (payload/'prerequisites').mkdir()
            (payload/'prerequisites/vc-redist.json').write_text(json.dumps({'version':'14.44.35211.0','sha256':'0'*64}))
            (payload/'setup-components.json').write_text(json.dumps({'architecture':'arm64' if os.environ.get('PROCESSOR_ARCHITECTURE')=='ARM64' else 'x64','components':[]}))
            completed = subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(payload/'setup.ps1'),'-Mode','cpu','-Diagnose'],capture_output=True,text=True,timeout=40)
            self.assertEqual(completed.returncode,0,completed.stderr)
            result=json.loads(completed.stdout)
            self.assertEqual(result['selected_components'],['cpu'])
            self.assertEqual(result['probes'],[])
            self.assertFalse((payload/'setup-state.json').exists())
            script=(payload/'setup.ps1').read_text()
            (payload/'setup.ps1').write_text(script.replace('$adapters = @();', 'function Get-CimInstance { throw "Inventory unavailable" }\n$adapters = @();'))
            unknown=subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(payload/'setup.ps1'),'-Diagnose'],capture_output=True,text=True,timeout=40)
            self.assertEqual(unknown.returncode,0,unknown.stderr)
            unknown_result=json.loads(unknown.stdout)
            self.assertEqual(unknown_result['inventory_status'],'not_checked')
            self.assertEqual(unknown_result['selected_components'],['cpu','directml','webgpu'])

    @unittest.skipUnless(os.name == 'nt', 'Windows setup repair')
    def test_optional_support_restores_from_verified_offline_payload(self):
        with tempfile.TemporaryDirectory() as folder:
            payload=Path(folder)
            shutil.copy2(ROOT/'installer/windows/setup.ps1',payload/'setup.ps1')
            (payload/'prerequisites').mkdir()
            (payload/'prerequisites/vc-redist.json').write_text(json.dumps({'version':'0.0','sha256':'0'*64}))
            (payload/'onnxruntime.dll').write_bytes(b'core')
            (payload/'DirectML.dll').write_bytes(b'shared')
            (payload/'onnxruntime_providers_webgpu.dll').write_bytes(b'optional')
            architecture='arm64' if os.environ.get('PROCESSOR_ARCHITECTURE')=='ARM64' else 'x64'
            manifest=component_manifest(payload,'win-'+architecture,{},'0.2.1')
            (payload/'setup-components.json').write_text(json.dumps(manifest))
            backup=payload/'optional-payloads/webgpu/onnxruntime_providers_webgpu.dll'
            backup.parent.mkdir(parents=True)
            shutil.move(str(payload/'onnxruntime_providers_webgpu.dll'),str(backup))
            command=['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(payload/'setup.ps1')]
            inventory=subprocess.run(command+['-Diagnose','-Mode','cpu'],capture_output=True,text=True,timeout=40)
            self.assertEqual(inventory.returncode,0,inventory.stderr)
            if not json.loads(inventory.stdout)['vc_runtime']['ready']:
                self.skipTest('Host VC runtime unavailable; installer must be exercised in guest')
            environment=dict(os.environ,LOCALAPPDATA=str(payload/'user'))
            cpu=subprocess.run(command+['-Mode','cpu','-Install','-NonInteractive'],capture_output=True,text=True,timeout=40,env=environment)
            self.assertEqual(cpu.returncode,0,cpu.stderr)
            self.assertFalse((payload/'onnxruntime_providers_webgpu.dll').exists())
            custom=subprocess.run(command+['-Mode','custom','-Components','webgpu','-Install','-NonInteractive'],capture_output=True,text=True,timeout=40,env=environment)
            self.assertEqual(custom.returncode,0,custom.stderr)
            self.assertEqual((payload/'onnxruntime_providers_webgpu.dll').read_bytes(),b'optional')
            state_bytes=(payload/'user/ColorCorrectStudio/setup-state.json').read_bytes()
            self.assertFalse(state_bytes.startswith(b'\xef\xbb\xbf'))
            state=json.loads(state_bytes.decode('utf-8'))
            self.assertEqual(state['components'],['cpu','webgpu'])

    @unittest.skipUnless(os.name == 'nt', 'Windows PowerShell native probe encoding')
    def test_probe_ignores_inherited_utf8_bom_and_rejects_failed_exit(self):
        compiler=Path(os.environ.get('WINDIR','C:/Windows'))/'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
        if not compiler.is_file():self.skipTest('Built-in .NET Framework fixture compiler unavailable')
        with tempfile.TemporaryDirectory() as folder:
            payload=Path(folder)
            code=payload/'worker.cs'
            code.write_text(r'''using System;
using System.IO;
using System.Text;
class Worker { static int Main() {
 Console.OutputEncoding=new UTF8Encoding(false);
 string pidFile=Environment.GetEnvironmentVariable("FIXTURE_PID_FILE");
 if(!String.IsNullOrEmpty(pidFile)) File.WriteAllText(pidFile,System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
 var input=new StreamReader(Console.OpenStandardInput(),new UTF8Encoding(false),false);
 string line=input.ReadLine();
 if (String.IsNullOrEmpty(line) || line[0]!='{' || Environment.GetEnvironmentVariable("FIXTURE_INVALID")=="1")
  Console.WriteLine("{\"ok\":false,\"error\":\"invalid JSON preamble\"}");
 else Console.WriteLine("{\"ok\":true,\"setup_verified\":true,\"inference\":{\"warmed\":true},\"correction\":{\"status\":\"verified\",\"backend\":\"cpu\"}}");
 input.ReadLine();
 return Environment.GetEnvironmentVariable("FIXTURE_FAILED_EXIT")=="1"?42:0;
} }
''',encoding='utf-8')
            compiled=subprocess.run([str(compiler),'/nologo','/target:exe','/out:'+str(payload/'ccp-engine.exe'),str(code)],capture_output=True,text=True,timeout=30)
            self.assertEqual(compiled.returncode,0,compiled.stdout+compiled.stderr)
            source=str(ROOT/'installer/windows/setup.ps1').replace("'","''")
            script=payload/'probe.ps1'
            script.write_text("$ErrorActionPreference='Stop'\n[Console]::InputEncoding=New-Object Text.UTF8Encoding $true\n"+
                "$text=Get-Content -LiteralPath '"+source+"' -Raw\n"+
                "$ast=[System.Management.Automation.Language.Parser]::ParseInput($text,[ref]$null,[ref]$null)\n"+
                "$function=$ast.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Probe'},$true)\n"+
                "$body=$function.Extent.Text\nif($env:FIXTURE_THROW_AFTER_START -eq '1'){$body=$body.Replace('$stdout =',\"Start-Sleep -Milliseconds 500; throw 'Injected input failure'; `$stdout =\")}\nInvoke-Expression $body\n$packageRoot=$PSScriptRoot;$selected=@('cpu');$TimeoutSeconds=15\nInvoke-Probe 'cpu-inference' | ConvertTo-Json -Depth 12\n",encoding='utf-8')
            for failed,invalid,interrupted in [(False,False,False),(True,False,False),(False,True,False),(False,False,True)]:
                with self.subTest(failed_exit=failed,invalid=invalid,interrupted=interrupted):
                    pidfile=payload/'worker-pid.txt'
                    if pidfile.exists():pidfile.unlink()
                    environment=dict(os.environ,FIXTURE_FAILED_EXIT='1' if failed else '0',FIXTURE_INVALID='1' if invalid else '0',FIXTURE_THROW_AFTER_START='1' if interrupted else '0',FIXTURE_PID_FILE=str(pidfile))
                    result=subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(script)],capture_output=True,text=True,timeout=30,env=environment,creationflags=subprocess.CREATE_NO_WINDOW)
                    self.assertEqual(result.returncode,0,result.stderr)
                    reply=json.loads(result.stdout)
                    self.assertEqual(reply['status'],'failed' if failed or invalid or interrupted else 'verified')
                    if failed:self.assertEqual(reply['exit_code'],42)
                    if invalid:self.assertIn('invalid JSON preamble',reply['reason'])
                    if interrupted:
                        self.assertIn('Injected input failure',reply['reason'])
                        self.assertTrue(pidfile.is_file())
                        # Query the native process directly. Starting another
                        # PowerShell can incur emulation/startup delays on ARM64
                        # unrelated to whether the probe was already reaped.
                        import ctypes
                        from ctypes import wintypes
                        kernel=ctypes.WinDLL('kernel32',use_last_error=True)
                        kernel.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD]
                        kernel.OpenProcess.restype=wintypes.HANDLE
                        kernel.WaitForSingleObject.argtypes=[wintypes.HANDLE,wintypes.DWORD]
                        kernel.WaitForSingleObject.restype=wintypes.DWORD
                        kernel.CloseHandle.argtypes=[wintypes.HANDLE]
                        kernel.CloseHandle.restype=wintypes.BOOL
                        probe_pid=int(pidfile.read_text().strip())
                        self.assertGreater(probe_pid,0)
                        handle=kernel.OpenProcess(0x00100000,False,probe_pid) # SYNCHRONIZE
                        if not handle:
                            self.assertEqual(ctypes.get_last_error(),87,
                                'Could not verify probe exit (expected an absent process)')
                        else:
                            try:
                                self.assertEqual(kernel.WaitForSingleObject(handle,0),0,
                                    'Interrupted probe worker survived cleanup')
                            finally:
                                kernel.CloseHandle(handle)

    def test_linux_symlink_launcher_resolves_target_and_optional_gpu_is_not_launch_requirement(self):
        launcher=(ROOT/'installer/linux/ColorCorrect-Studio').read_text()
        self.assertIn('readlink -f -- "$0"',launcher)
        self.assertNotIn('libonnxruntime_providers_webgpu.so', (ROOT/'installer/linux/check-system').read_text())
        self.assertNotIn("'onnxruntime_providers_webgpu.dll'", (ROOT/'installer/windows/check-system.ps1').read_text())

if __name__ == '__main__': unittest.main()
