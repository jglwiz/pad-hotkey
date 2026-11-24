"""Dry-run lifecycle regression: empty initialization, explicit JSON import, restart preservation."""
import argparse
import json
import subprocess
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--exe', type=Path, required=True)
    parser.add_argument('--port', type=int, default=17846)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    data = root / 'local' / ('profile-test-' + uuid.uuid4().hex)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    process = None
    token = ''
    count = 0

    def check(label, condition):
        nonlocal count
        assert condition, label
        count += 1
        print('PASS ' + label, flush=True)

    def api(method, route, body=None, headers=None):
        h = {'Authorization': 'Bearer ' + token, 'Content-Type': 'application/json'}
        h.update(headers or {})
        request = urllib.request.Request(f'http://127.0.0.1:{args.port}' + route,
                                        data=None if body is None else json.dumps(body).encode(), headers=h, method=method)
        with opener.open(request, timeout=5) as response:
            return json.loads(response.read())

    def start():
        nonlocal process, token
        process = subprocess.Popen([str(args.exe.resolve()), 'serve', '--backend', 'dry-run', '--data', str(data),
                                    '--port', str(args.port), '--seconds', '60'],
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                   creationflags=subprocess.CREATE_NO_WINDOW)
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise AssertionError('Server exited before ready')
            if (data / 'api-token.txt').exists():
                token = (data / 'api-token.txt').read_text().strip()
                try:
                    api('GET', '/v1/status')
                    return
                except (OSError, urllib.error.URLError):
                    pass
            time.sleep(.05)
        raise AssertionError('Server not ready')

    def stop():
        if process and process.poll() is None:
            api('POST', '/v1/shutdown')
            process.wait(timeout=10)
            assert process.returncode == 0

    def cli(*command):
        subprocess.run([str(args.exe.resolve()), *command, '--data', str(data), '--port', str(args.port)],
                       check=True, stdout=subprocess.DEVNULL, timeout=15,
                       creationflags=subprocess.CREATE_NO_WINDOW)

    try:
        start()
        initial = api('GET', '/v1/profile')
        check('startup directly enables mapping', api('GET', '/v1/status')['paused'] is False)
        check('fresh start persists empty profile', initial['bindings'] == [] and
              json.loads((data / 'profile.json').read_text()) == initial)
        check('discovery contains no automatic bindings', api('GET', '/v1/schema')['example']['bindings'] == [])
        try:
            api('GET', '/v1/profiles/vibe')
            raise AssertionError('Old scenario-specific route remains')
        except urllib.error.HTTPError as error:
            check('scenario-specific route removed', error.code == 404)
        cli('bind', '--button', 'A', '--tap', 'F13')
        check('CLI can configure fresh empty profile', api('GET', '/v1/profile')['bindings'][0]['keys'] == ['F13'])
        cli('apply', '--file', str(root / 'config' / 'vibe.json'))
        example = json.loads((root / 'config' / 'vibe.json').read_text(encoding='utf-8-sig'))
        check('personal JSON imported only on request', api('GET', '/v1/profile')['bindings'] == example['bindings'])
        current = api('GET', '/v1/profile')
        current['name'] = 'my-editor'
        saved = api('PUT', '/v1/profile', current, {'If-Match': str(current['version'])})
        raw = (data / 'profile.json').read_bytes()
        stop()
        start()
        check('restart directly enables saved mapping', api('GET', '/v1/status')['paused'] is False)
        check('restart preserves custom name, bindings, revision and bytes', api('GET', '/v1/profile') == saved and
              (data / 'profile.json').read_bytes() == raw)
        saved['bindings'] = []
        empty = api('PUT', '/v1/profile', saved, {'If-Match': str(saved['version'])})
        stop()
        start()
        check('cleared profile remains empty across restart', api('GET', '/v1/profile') == empty)
        subprocess.run([__import__('sys').executable, str(root / 'tests' / 'api_smoke.py'), '--data', str(data),
                        '--port', str(args.port)], check=True, timeout=30)
    finally:
        try:
            stop()
        finally:
            if process and process.poll() is None:
                process.terminate()  # Only this test's dry-run child; retains all data.
                process.wait(timeout=5)
    print(f'{count}/{count} lifecycle checks passed; data retained at {data}')


if __name__ == '__main__':
    main()
