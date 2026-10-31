# SPDX-License-Identifier: MIT
"""Stored TLS identities, using real DPAPI on Windows and real TLS contexts."""
import os
from pathlib import Path
import ssl
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import identity  # noqa: E402
import receiver  # noqa: E402
import sender  # noqa: E402


class IdentityTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.folder = self.temp.name

    def legacy(self):
        config = Path(self.folder, 'legacy.cnf')
        config.write_text(identity.REQUEST_CONFIG, encoding='ascii')
        subprocess.run([identity.find_openssl(), 'req', '-config', str(config),
                        '-newkey', 'rsa:2048', '-nodes', '-x509', '-days', '1',
                        '-keyout', str(Path(self.folder, identity.KEY)),
                        '-out', str(Path(self.folder, identity.CERTIFICATE))],
                       capture_output=True, check=True)
        config.unlink()

    def test_round_trip_and_both_tls_sides(self):
        certificate, key = identity.ensure_self_signed(self.folder)
        before = Path(certificate).read_bytes(), Path(key).read_bytes()
        receiver.tls_context(self.folder)
        sender.client_context(self.folder)
        self.assertEqual(identity.ensure_self_signed(self.folder), (certificate, key))
        self.assertEqual(before, (Path(certificate).read_bytes(), Path(key).read_bytes()))
        if sys.platform == 'win32':
            self.assertTrue(b'BEGIN ENCRYPTED PRIVATE KEY' in before[1])
            with self.assertRaises(ssl.SSLError):
                ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER).load_cert_chain(
                    certificate, key, password=b'wrong password')
        else:
            self.assertTrue(b'BEGIN PRIVATE KEY' in before[1])

    @unittest.skipUnless(sys.platform == 'win32', 'Windows DPAPI migration')
    def test_migration_keeps_identity_and_removes_plaintext(self):
        self.legacy()
        certificate = Path(self.folder, identity.CERTIFICATE).read_bytes()
        original = Path(self.folder, identity.KEY).read_bytes()
        with self.assertLogs(level='INFO') as logs:
            receiver.tls_context(self.folder)
        self.assertEqual(Path(self.folder, identity.CERTIFICATE).read_bytes(), certificate)
        self.assertIn('encrypted', ' '.join(logs.output).lower())
        self.assertIn(b'BEGIN ENCRYPTED PRIVATE KEY', Path(self.folder, identity.KEY).read_bytes())
        self.assertFalse(any(path.read_bytes() == original for path in Path(self.folder).iterdir()))

    @unittest.skipUnless(sys.platform == 'win32', 'Windows DPAPI recovery')
    def test_missing_corrupt_and_wrong_secrets_make_a_new_identity(self):
        for damage in ('missing', 'corrupt', 'wrong'):
            with self.subTest(damage=damage):
                certificate, _ = identity.ensure_self_signed(self.folder)
                before = Path(certificate).read_bytes()
                secret = Path(self.folder, identity.SECRET)
                if damage == 'missing':
                    secret.unlink()
                elif damage == 'corrupt':
                    secret.write_bytes(b'broken DPAPI blob')
                else:
                    secret.write_bytes(identity.protect(b'wrong password'))
                with self.assertLogs(level='WARNING') as logs:
                    receiver.tls_context(self.folder)
                self.assertNotEqual(Path(certificate).read_bytes(), before)
                self.assertIn('new identity', ' '.join(logs.output))
                receiver.tls_context(self.folder)

    @unittest.skipUnless(sys.platform == 'win32', 'Windows DPAPI migration')
    def test_failed_migration_keeps_the_original_key(self):
        self.legacy()
        original = Path(self.folder, identity.KEY).read_bytes()
        with mock.patch.object(identity.os, 'replace', side_effect=OSError('test disk failure')), \
                self.assertRaises(identity.IdentityError):
            identity.ensure_self_signed(self.folder)
        self.assertEqual(Path(self.folder, identity.KEY).read_bytes(), original)
        receiver.tls_context(self.folder)

    @unittest.skipIf(sys.platform == 'win32', 'Non-Windows keeps plaintext storage')
    def test_non_windows_keeps_existing_plaintext_pair(self):
        self.legacy()
        original = Path(self.folder, identity.KEY).read_bytes()
        receiver.tls_context(self.folder)
        self.assertEqual(Path(self.folder, identity.KEY).read_bytes(), original)
