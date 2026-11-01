# SPDX-License-Identifier: MIT
"""Sender folders use hostile device names, never paths supplied by a device."""
import io
import os
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import transfer  # noqa: E402
from test_transfer import Budget, archive, dvzip, portable_member  # noqa: E402


class SenderFolderTest(unittest.TestCase):
    def test_sender_names_are_single_windows_components(self):
        cases = {
            'Living Room iPhone': 'Living Room iPhone',
            'CoN.txt': '_CoN.txt', 'NUL': '_NUL', 'COM1.log': '_COM1.log',
            'lPt9.backup.txt': '_lPt9.backup.txt', 'COM¹': '_COM¹',
            'CON .txt': '_CON .txt',
            ' phone.  ': 'phone', '../phone\\../../name': 'phonename',
            'a\x00b\x1fc\x7fd\u00ade\u2060f': 'abcdef',
            '<>:"/\\|?*..\x00\u200b': 'Unknown Device',
            '... ': 'Unknown Device', '': 'Unknown Device',
        }
        for raw, expected in cases.items():
            with self.subTest(raw=raw):
                self.assertEqual(transfer.sender_folder_name(raw), expected)
        self.assertEqual(transfer.sender_folder_name(None), 'Unknown Device')
        for raw in ('😀' * 500, 'x.' + 'y' * 500, 'a' * 254 + ' .'):
            name = transfer.sender_folder_name(raw)
            self.assertLessEqual(transfer.name_units(name), transfer.MAX_NAME_UNITS)
            self.assertFalse(name.endswith(('.', ' ')))
            self.assertNotIn('..', name)

    def test_existing_directory_is_reused_and_files_are_numbered(self):
        with tempfile.TemporaryDirectory() as root:
            folder = os.path.join(root, 'Phone')
            os.mkdir(folder)
            existing = os.path.join(folder, 'note.txt')
            with open(existing, 'wb') as output:
                output.write(b'keep')
            with transfer.receive_destination(root, 'Phone', True) as dest:
                # Windows may expand an 8.3 alias in root to its long path.
                self.assertTrue(os.path.samefile(dest, folder))
                raw = io.BytesIO(dvzip((True, archive(portable_member('note.txt', b'new')))))
                self.assertEqual(transfer.store_upload(raw, dest, 'T', Budget()), ['note-1.txt'])
            with open(existing, 'rb') as source:
                self.assertEqual(source.read(), b'keep')

    def test_off_ignores_sender_and_never_creates_a_folder(self):
        with tempfile.TemporaryDirectory() as root:
            with transfer.receive_destination(root, '../CON', False) as dest:
                self.assertEqual(dest, root)
            self.assertEqual(os.listdir(root), [])

    def test_failed_upload_removes_only_its_files(self):
        with (tempfile.TemporaryDirectory() as root,
              transfer.receive_destination(root, 'Phone', True) as dest):
            keep = os.path.join(dest, 'keep.txt')
            with open(keep, 'wb') as output:
                output.write(b'keep')
            raw = io.BytesIO(dvzip((True, archive(portable_member('new.txt', b'new'),
                                                 portable_member('bad.txt', b'bad')))))
            with self.assertRaises(ValueError):
                transfer.store_upload(raw, dest, 'T', Budget(), approved=['new.txt'])
            self.assertEqual(os.listdir(dest), ['keep.txt'])

    @unittest.skipUnless(os.name == 'nt', 'Windows directory sharing semantics')
    def test_windows_folder_cannot_be_replaced_during_writes(self):
        with tempfile.TemporaryDirectory() as root:
            with transfer.receive_destination(root, 'Phone', True) as dest:
                with self.assertRaises(OSError):
                    os.rename(dest, os.path.join(root, 'moved'))
                with self.assertRaises(OSError):
                    os.rename(root, root + '-moved')
            os.rename(dest, os.path.join(root, 'moved'))

    def test_a_file_at_the_folder_name_is_refused_without_removing_it(self):
        with tempfile.TemporaryDirectory() as root:
            path = os.path.join(root, 'Phone')
            with open(path, 'wb') as output:
                output.write(b'keep')
            with self.assertRaises(OSError), transfer.receive_destination(root, 'Phone', True):
                self.fail('a file was accepted as a destination')
            with open(path, 'rb') as source:
                self.assertEqual(source.read(), b'keep')

    def test_an_existing_link_cannot_escape_the_root(self):
        with tempfile.TemporaryDirectory() as root, tempfile.TemporaryDirectory() as outside:
            link = os.path.join(root, 'Phone')
            if os.name == 'nt':
                # Junctions need no symlink privilege and exercise Windows reparse points.
                subprocess.run(['cmd', '/c', 'mklink', '/J', link, outside],
                               check=True, capture_output=True)
            else:
                os.symlink(outside, link, target_is_directory=True)
            try:
                with self.assertRaises(OSError), transfer.receive_destination(root, 'Phone', True):
                    self.fail('a link was followed')
                self.assertEqual(os.listdir(outside), [])
                self.assertTrue(os.path.isdir(link))
            finally:
                if os.name == 'nt':
                    os.rmdir(link)
                else:
                    os.unlink(link)


if __name__ == '__main__':
    unittest.main()
