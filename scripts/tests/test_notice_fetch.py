from pathlib import Path
import io
import http.client
import sys
import unittest
from unittest.mock import patch
import urllib.error
import urllib.request

sys.path.insert(0, str(Path(__file__).parents[1]))
from collect_dependency_notices import fetch_notice_bytes, NoticeRedirectHandler


class NoticeFetchTests(unittest.TestCase):
    def test_token_is_only_sent_to_https_github_api(self):
        for url, authorized in [('https://api.github.com/repos/public/repo/git/trees/sha', True),
                                ('https://raw.githubusercontent.com/public/repo/sha/LICENSE', False),
                                ('https://example.test/LICENSE', False),
                                ('https://api.github.com:444/notice', False)]:
            with self.subTest(url=url), patch('collect_dependency_notices.urllib.request.build_opener') as build:
                build.return_value.open.return_value = io.BytesIO(b'license')
                self.assertEqual(fetch_notice_bytes(url, 'test-readonly-token'), b'license')
                request = build.return_value.open.call_args.args[0]
                self.assertEqual(request.get_header('Authorization'), 'Bearer test-readonly-token' if authorized else None)

    def test_redirects_drop_authorization_off_api_and_reject_downgrade(self):
        handler = NoticeRedirectHandler()
        request = urllib.request.Request('https://api.github.com/repos/public/repo/git/trees/sha',
                                         headers={'Authorization': 'Bearer test-readonly-token'})
        external = handler.redirect_request(request, None, 302, 'Found', {}, 'https://raw.githubusercontent.com/public/repo/sha/LICENSE')
        self.assertIsNone(external.get_header('Authorization'))
        same = handler.redirect_request(request, None, 301, 'Moved', {}, 'https://api.github.com/repos/renamed/repo/git/trees/sha')
        self.assertEqual(same.get_header('Authorization'), 'Bearer test-readonly-token')
        with self.assertRaisesRegex(ValueError, 'HTTPS'):
            handler.redirect_request(request, None, 302, 'Found', {}, 'http://example.test/LICENSE')

    def test_retryable_http_failures_are_bounded_and_preserve_bytes(self):
        for code, reason, headers in [(429, 'Too Many Requests', {'Retry-After': '0'}),
                                      (403, 'rate limit exceeded', {'X-RateLimit-Remaining': '0', 'X-RateLimit-Reset': '0'}),
                                      (503, 'Unavailable', {})]:
            with self.subTest(code=code), patch('collect_dependency_notices.urllib.request.build_opener') as build, patch('collect_dependency_notices.time.sleep') as sleep:
                build.return_value.open.side_effect = [urllib.error.HTTPError('https://api.github.com/notice', code, reason, headers, None), io.BytesIO(b'exact license bytes')]
                self.assertEqual(fetch_notice_bytes('https://api.github.com/notice', 'test-readonly-token'), b'exact license bytes')
                self.assertEqual(build.return_value.open.call_count, 2)
                sleep.assert_called_once_with(1)

    def test_permission_failure_does_not_retry_or_omit_notice(self):
        failure = urllib.error.HTTPError('https://api.github.com/notice', 403, 'Forbidden', {}, None)
        with patch('collect_dependency_notices.urllib.request.build_opener') as build, patch('collect_dependency_notices.time.sleep') as sleep:
            build.return_value.open.side_effect = failure
            with self.assertRaises(urllib.error.HTTPError) as raised:
                fetch_notice_bytes('https://api.github.com/notice', 'test-readonly-token')
            self.assertIs(raised.exception, failure)
            self.assertEqual(build.return_value.open.call_count, 1)
            sleep.assert_not_called()

    def test_rate_reset_beyond_bound_fails_without_hammering_service(self):
        failure = urllib.error.HTTPError('https://api.github.com/notice', 403, 'rate limit exceeded',
                                        {'X-RateLimit-Remaining': '0', 'X-RateLimit-Reset': '160'}, None)
        with patch('collect_dependency_notices.urllib.request.build_opener') as build, patch('collect_dependency_notices.time.sleep') as sleep, patch('collect_dependency_notices.time.time', return_value=100):
            build.return_value.open.side_effect = failure
            with self.assertRaisesRegex(RuntimeError, 'No notice was omitted'):
                fetch_notice_bytes('https://api.github.com/notice', 'test-readonly-token')
            self.assertEqual(build.return_value.open.call_count, 1)
            sleep.assert_not_called()

    def test_exhausted_network_failures_remain_failures(self):
        with patch('collect_dependency_notices.urllib.request.build_opener') as build, patch('collect_dependency_notices.time.sleep') as sleep:
            build.return_value.open.side_effect = urllib.error.URLError('network unavailable')
            with self.assertRaises(urllib.error.URLError):
                fetch_notice_bytes('https://raw.githubusercontent.com/public/repo/sha/LICENSE')
            self.assertEqual(build.return_value.open.call_count, 3)
            self.assertEqual([call.args[0] for call in sleep.call_args_list], [1, 2])

    def test_unhinted_rate_limit_does_not_retry_before_required_cooldown(self):
        for code in (403, 429):
            with self.subTest(code=code), patch('collect_dependency_notices.urllib.request.build_opener') as build, patch('collect_dependency_notices.time.sleep') as sleep:
                build.return_value.open.side_effect = urllib.error.HTTPError('https://api.github.com/notice', code, 'rate limit exceeded', {}, None)
                with self.assertRaisesRegex(RuntimeError, 'No notice was omitted'):
                    fetch_notice_bytes('https://api.github.com/notice', 'test-readonly-token')
                self.assertEqual(build.return_value.open.call_count, 1)
                sleep.assert_not_called()

    def test_oversized_or_insecure_notices_fail_without_integrity_retry(self):
        with patch('collect_dependency_notices.urllib.request.build_opener') as build, patch('collect_dependency_notices.time.sleep') as sleep:
            build.return_value.open.return_value = io.BytesIO(b'x' * (8 * 1024 * 1024 + 1))
            with self.assertRaisesRegex(ValueError, '8 MiB'):
                fetch_notice_bytes('https://example.test/LICENSE')
            self.assertEqual(build.return_value.open.call_count, 1)
            sleep.assert_not_called()
        with patch('collect_dependency_notices.urllib.request.build_opener') as build:
            with self.assertRaisesRegex(ValueError, 'HTTPS'):
                fetch_notice_bytes('http://api.github.com/notice', 'test-readonly-token')
            build.assert_not_called()

    def test_real_http_response_short_body_is_not_accepted_or_retried(self):
        class Socket:
            def makefile(self, *args):
                return io.BytesIO(b'HTTP/1.1 200 OK\r\nContent-Length: 7\r\n\r\nabc')
        response = http.client.HTTPResponse(Socket())
        response.begin()
        with patch('collect_dependency_notices.urllib.request.build_opener') as build, patch('collect_dependency_notices.time.sleep') as sleep:
            build.return_value.open.return_value = response
            with self.assertRaisesRegex(ValueError, 'Incomplete.*Content-Length'):
                fetch_notice_bytes('https://raw.githubusercontent.com/public/repo/sha/LICENSE')
            self.assertEqual(build.return_value.open.call_count, 1)
            sleep.assert_not_called()


if __name__ == '__main__':
    unittest.main()
