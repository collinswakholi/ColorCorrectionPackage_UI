"""Exercise optional WSL routing without requiring GPU devices or system changes."""
from pathlib import Path
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).parents[2]


@unittest.skipUnless(sys.platform.startswith('linux'), 'Native Linux launcher contract')
class LinuxWorkerLauncherTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name) / 'portable app'
        self.root.mkdir()
        self.config = Path(self.folder.name) / 'configuration'
        (self.config / 'colorcorrect').mkdir(parents=True)
        shutil.copy2(ROOT / 'installer/linux/ccp-engine-launch', self.root / 'ccp-engine-launch')
        self.root.joinpath('ccp-engine').write_text('''#!/bin/sh
printf 'ACTIVE=%s\\nVK=%s\\nALLOW=%s\\nLD=%s\\nERROR=%s\\nARG=%s\\n' "${CCP_WSL_GPU_ADDON_ACTIVE-}" "${VK_DRIVER_FILES-}" "${WGPU_ALLOW_UNDERLYING_NONCOMPLIANT_ADAPTER-}" "${LD_LIBRARY_PATH-}" "${CCP_WSL_GPU_ADDON_ERROR-}" "${1-}"
printf 'INPUT=%s\\n' "$(cat)"
''', encoding='utf-8', newline='\n')
        self.root.joinpath('wsl-gpu-setup').write_text('''#!/bin/sh
: > "$CHECK_RECEIPT"
if [ "$CHECK_RESULT" = success ]; then echo 'Add-on integrity verified'; exit 0; fi
if [ "$CHECK_RESULT" = hang ]; then exec sleep 30; fi
if [ "$CHECK_RESULT" != silent ]; then echo 'The optional WSL GPU add-on is not installed.' >&2; fi
exit 9
''', encoding='utf-8', newline='\n')
        for name in ('ccp-engine-launch', 'ccp-engine', 'wsl-gpu-setup'):
            (self.root / name).chmod(0o755)
        self.env = {'PATH': '/usr/bin:/bin', 'HOME': str(Path(self.folder.name)),
                    'XDG_CONFIG_HOME': str(self.config), 'CHECK_RESULT': 'fail',
                    'CHECK_RECEIPT': str(Path(self.folder.name) / 'checked')}

    def invoke(self, policy='auto', **environment):
        result = subprocess.run([str(self.root / 'ccp-engine-launch'), 'forwarded argument'],
                                input=json.dumps({'cmd': 'warmup', 'policy': policy}) + '\n',
                                text=True, capture_output=True, env={**self.env, **environment}, timeout=10)
        values = dict(line.split('=', 1) for line in result.stdout.splitlines() if '=' in line)
        return result, values

    def test_missing_enabled_addon_keeps_cpu_and_auto_worker_reachable(self):
        self.config.joinpath('colorcorrect/wsl-gpu.enabled').write_text('previously enabled')
        for policy in ('cpu-inference', 'auto'):
            with self.subTest(policy=policy):
                result, values = self.invoke(policy,
                    CCP_WSL_GPU_ADDON_ACTIVE='1',
                    VK_DRIVER_FILES=str(self.root / 'addons/wsl-gpu/icd/dzn_icd.json'),
                    LD_LIBRARY_PATH=str(self.root / 'addons/wsl-gpu/lib') + ':/usr/lib/wsl/lib:/explicit/library',
                    WGPU_ALLOW_UNDERLYING_NONCOMPLIANT_ADAPTER='1')
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(values['ACTIVE'], '0')
                self.assertEqual(values['VK'], '')
                self.assertEqual(values['ALLOW'], '')
                self.assertEqual(values['LD'], str(self.root) + ':/explicit/library')
                self.assertIn('not installed', values['ERROR'])
                self.assertIn('Auto can use CPU', result.stderr)
                self.assertEqual(values['ARG'], 'forwarded argument')
                self.assertEqual(json.loads(values['INPUT'])['policy'], policy)

    def test_failed_optional_check_preserves_explicit_unowned_routes(self):
        result, values = self.invoke(CCP_WSL_GPU_ADDON='1',
            VK_DRIVER_FILES='/explicit/icd.json', LD_LIBRARY_PATH='/explicit/library:/usr/lib/wsl/lib',
            WGPU_ALLOW_UNDERLYING_NONCOMPLIANT_ADAPTER='7', CCP_WSL_GPU_ADDON_ERROR='old error')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(values['ACTIVE'], '0')
        self.assertEqual(values['VK'], '/explicit/icd.json')
        self.assertEqual(values['ALLOW'], '7')
        self.assertEqual(values['LD'], str(self.root) + ':/explicit/library:/usr/lib/wsl/lib')
        self.assertIn('not installed', values['ERROR'])
        self.assertNotIn('old error', values['ERROR'])

    def test_success_replaces_old_owned_route_and_clears_failure(self):
        result, values = self.invoke(CCP_WSL_GPU_ADDON='1', CHECK_RESULT='success',
            CCP_WSL_GPU_ADDON_ACTIVE='1', VK_DRIVER_FILES='/old/app/addons/wsl-gpu/icd/dzn_icd.json',
            LD_LIBRARY_PATH='/old/app/addons/wsl-gpu/lib:/usr/lib/wsl/lib:/explicit/library',
            WGPU_ALLOW_UNDERLYING_NONCOMPLIANT_ADAPTER='1', CCP_WSL_GPU_ADDON_ERROR='old error')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(values['ACTIVE'], '1')
        self.assertEqual(values['VK'], str(self.root / 'addons/wsl-gpu/icd/dzn_icd.json'))
        self.assertEqual(values['ALLOW'], '1')
        self.assertEqual(values['LD'], str(self.root / 'addons/wsl-gpu/lib') + ':/usr/lib/wsl/lib:' + str(self.root) + ':/explicit/library')
        self.assertEqual(values['ERROR'], '')
        self.assertNotIn('unavailable', result.stderr)

    def test_disabled_addon_clears_owned_route_without_checking(self):
        self.config.joinpath('colorcorrect/wsl-gpu.enabled').write_text('previously enabled')
        result, values = self.invoke(CCP_WSL_GPU_ADDON='0', CCP_WSL_GPU_ADDON_ACTIVE='1',
            VK_DRIVER_FILES=str(self.root / 'addons/wsl-gpu/icd/dzn_icd.json'),
            LD_LIBRARY_PATH=str(self.root / 'addons/wsl-gpu/lib') + ':/usr/lib/wsl/lib:/explicit/library',
            WGPU_ALLOW_UNDERLYING_NONCOMPLIANT_ADAPTER='1', CCP_WSL_GPU_ADDON_ERROR='old error')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(values['ACTIVE'], '0')
        self.assertEqual(values['VK'], '')
        self.assertEqual(values['ALLOW'], '')
        self.assertEqual(values['ERROR'], '')
        self.assertEqual(values['LD'], str(self.root) + ':/explicit/library')
        self.assertFalse(Path(self.env['CHECK_RECEIPT']).exists())

    def test_silent_optional_check_failure_has_actionable_reason(self):
        result, values = self.invoke(CCP_WSL_GPU_ADDON='1', CHECK_RESULT='silent')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('check failed (exit 9)', values['ERROR'])
        self.assertIn('Check setup', values['ERROR'])

    def test_invalid_explicit_setting_remains_a_configuration_error(self):
        result, values = self.invoke(CCP_WSL_GPU_ADDON='invalid')
        self.assertEqual(result.returncode, 64)
        self.assertEqual(values, {})
        self.assertIn('must be 0 or 1', result.stderr)
        self.assertFalse(Path(self.env['CHECK_RECEIPT']).exists())

    def test_stuck_optional_check_cannot_block_worker_start(self):
        result, values = self.invoke(CCP_WSL_GPU_ADDON='1', CHECK_RESULT='hang')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(values['ACTIVE'], '0')
        self.assertIn('check failed (exit 124)', values['ERROR'])
        self.assertIn('Auto can use CPU', result.stderr)


if __name__ == '__main__':
    unittest.main()
