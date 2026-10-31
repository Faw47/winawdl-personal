# SPDX-License-Identifier: MIT
"""Folder selection stays flat, bounded, and inside the chosen tree."""
import io
import os
import sys
import subprocess
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import cpio  # noqa: E402
import sender  # noqa: E402


class SelectionTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = self.temp.name

    def file(self, relative, size=1):
        path = os.path.abspath(os.path.join(self.root, relative))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, 'wb') as target:
            target.truncate(size)
        return path

    def test_folder_is_flat_sorted_and_deduplicated(self):
        second = self.file('folder/z/b.txt')
        self.file('folder/a.txt')
        files = sender.outgoing([os.path.join(self.root, 'folder'), second])
        self.assertEqual([item.name for item in files], ['a.txt', 'b.txt'])
        raw = b''.join(cpio.write_archive(sender._members(files)))
        entries = list(cpio.read_entries(io.BytesIO(raw)))
        self.assertEqual([item.pathname for item in entries], ['.', './a.txt', './b.txt'])

    def test_combined_count_and_size_limits(self):
        first = self.file('folder/a.txt', 2)
        second = self.file('b.txt', 2)
        for field, limit in [('MAX_SEND_FILES', 1), ('MAX_SEND_BYTES', 3)]:
            with self.subTest(field=field), mock.patch.object(sender, field, limit):
                with self.assertRaises(sender.Unsendable) as caught:
                    sender.outgoing([os.path.dirname(first), second])
                self.assertEqual(caught.exception.kind, 'limit')
        with mock.patch.object(sender, 'MAX_SEND_FILES', 2), \
                mock.patch.object(sender, 'MAX_SEND_BYTES', 4):
            self.assertEqual(len(sender.outgoing([first, second])), 2)

    def test_reparse_directories_and_files_are_skipped(self):
        good = self.file('folder/good.txt')
        outside = self.file('outside/private.txt')
        linked = self.file('folder/link.txt')
        branch = os.path.join(self.root, 'folder', 'junction')
        os.mkdir(branch)
        self.file('folder/junction/private.txt')
        real = sender.os.lstat

        def status(path):
            value = real(path)
            if path in (linked, branch):
                return mock.Mock(st_mode=value.st_mode, st_file_attributes=0x400)
            return value

        with mock.patch.object(sender.os, 'lstat', side_effect=status):
            self.assertEqual([item.path for item in sender.outgoing([os.path.dirname(good)])],
                             [good])
        self.assertTrue(os.path.isfile(outside))

    def test_hostile_unix_basename_cannot_create_archive_path(self):
        path = self.file('ordinary.txt')
        with mock.patch.object(sender.os.path, 'basename', return_value='..\\outside.txt'):
            files = sender.outgoing([path])
        self.assertNotIn('\\', files[0].name)
        self.assertNotIn('/', files[0].name)
        self.assertNotIn(files[0].name, ('.', '..'))

    def test_real_directory_link_never_exports_outside_file(self):
        good = self.file('chosen/good.txt')
        outside = self.file('outside/private.txt')
        junction = os.path.join(self.root, 'chosen', 'escape')
        if os.name == 'nt':
            result = subprocess.run(['cmd.exe', '/c', 'mklink', '/J', junction,
                                     os.path.dirname(outside)], capture_output=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr.decode(errors='replace'))
        else:
            os.symlink(os.path.dirname(outside), junction)
        self.assertEqual([item.path for item in sender.outgoing([os.path.dirname(good)])], [good])
        self.assertEqual(sender.outgoing([good, good])[0].name, 'good.txt')
        with self.assertRaises(sender.Unsendable):
            sender.outgoing([junction])


if __name__ == '__main__':
    unittest.main()
