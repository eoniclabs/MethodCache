"""Optional real SDK transport test; all credentials and packages are synthetic."""
import http.server
import json
import os
import pathlib
import subprocess
import tempfile
import threading
import unittest
import zipfile

SDK = os.environ.get('METHODCACHE_TEST_PUBLISH_DOTNET')


@unittest.skipUnless(SDK, 'Set METHODCACHE_TEST_PUBLISH_DOTNET to a .NET SDK 10.0.300+ executable')
class PublishSdkTests(unittest.TestCase):
    def upload_pair(self, symbol_key):
        received = []

        class Feed(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                base = 'http://127.0.0.1:' + str(self.server.server_port)
                data = json.dumps({'version': '3.0.0', 'resources': [
                    {'@id': base + '/packages', '@type': 'PackagePublish/2.0.0'},
                    {'@id': base + '/symbols', '@type': 'SymbolPackagePublish/4.9.0'}]}).encode()
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def do_PUT(self):
                self.rfile.read(int(self.headers.get('Content-Length', '0')))
                received.append((self.path.rstrip('/'), self.headers.get('X-NuGet-ApiKey')))
                self.send_response(201)
                self.send_header('Content-Length', '0')
                self.end_headers()

            def log_message(self, *_):
                pass

        with http.server.ThreadingHTTPServer(('127.0.0.1', 0), Feed) as feed:
            thread = threading.Thread(target=feed.serve_forever, daemon=True)
            thread.start()
            try:
                with tempfile.TemporaryDirectory() as directory:
                    package = pathlib.Path(directory) / 'MethodCache.TransportTest.1.0.0.nupkg'
                    for path in (package, package.with_suffix('.snupkg')):
                        with zipfile.ZipFile(path, 'w') as archive:
                            archive.writestr('MethodCache.TransportTest.nuspec',
                                '<package><metadata><id>MethodCache.TransportTest</id>'
                                '<version>1.0.0</version><authors>Local test</authors>'
                                '<description>Loopback transport test</description></metadata></package>')
                            archive.writestr('lib/net10.0/TransportTest.pdb', b'Synthetic symbol payload')
                    env = dict(os.environ, NUGET_API_KEY='synthetic-package-key',
                               DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
                    env.pop('NUGET_SYMBOL_API_KEY', None)
                    if symbol_key is not None:
                        env['NUGET_SYMBOL_API_KEY'] = symbol_key
                    base = 'http://127.0.0.1:' + str(feed.server_port)
                    result = subprocess.run([SDK, 'nuget', 'push', str(package),
                        '--source', base + '/v3/index.json',
                        '--allow-insecure-connections', '--skip-duplicate'],
                        cwd=directory, env=env, text=True, capture_output=True, timeout=30)
                    self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                    self.assertEqual([('/packages', 'synthetic-package-key'),
                                      ('/symbols', symbol_key or 'synthetic-package-key')], received)
            finally:
                feed.shutdown()
                thread.join(timeout=5)

    def test_primary_key_fallback_for_discovered_symbol_service(self):
        self.upload_pair(None)

    def test_explicit_symbol_environment_key_is_used(self):
        self.upload_pair('synthetic-symbol-key')


if __name__ == '__main__':
    unittest.main()
