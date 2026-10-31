# SPDX-License-Identifier: MIT
"""Security regression cases for manual AirDrop file approval."""
import gzip
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import transfer  # noqa: E402
from test_transfer import Budget, archive, portable_member  # noqa: E402


class ApprovedArchiveTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)

    def upload(self, names, announced):
        body = gzip.compress(archive(*(portable_member('./' + name, b'content')
                                       for name in names)))
        with tempfile.TemporaryFile(dir=self.tmp.name) as raw:
            raw.write(body)
            return transfer.store_upload(raw, self.tmp.name, 'test', Budget(),
                                         approved=announced)

    def test_identical_name_accepted(self):
        self.assertEqual(self.upload(['photo.jpg'], ['photo.jpg']), ['photo.jpg'])

    def test_different_extension_is_rejected_and_rolled_back(self):
        with self.assertRaisesRegex(ValueError, 'unapproved'):
            self.upload(['photo.jpg', 'app.exe'], ['photo.jpg'])
        self.assertEqual(os.listdir(self.tmp.name), [])

    def test_script_is_not_a_photo(self):
        with self.assertRaisesRegex(ValueError, 'unapproved'):
            self.upload(['holiday.ps1'], ['holiday.jpg'])
        self.assertEqual(os.listdir(self.tmp.name), [])

    def test_missing_approved_member_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'omitted'):
            self.upload(['one.txt'], ['one.txt', 'two.txt'])
        self.assertEqual(os.listdir(self.tmp.name), [])

    def test_extra_duplicate_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'unapproved'):
            self.upload(['one.txt', 'one.txt'], ['one.txt'])
        self.assertEqual(os.listdir(self.tmp.name), [])

    def test_heic_to_jpeg_is_allowed(self):
        self.assertEqual(self.upload(['photo.jpg'], ['photo.heic']), ['photo.jpg'])

    def test_take_all_remains_possible_without_manifest(self):
        self.assertEqual(self.upload(['whatever.txt'], None), ['whatever.txt'])

    def test_empty_manifest_rejects_upload(self):
        with self.assertRaisesRegex(ValueError, 'unapproved'):
            self.upload(['whatever.txt'], [])
        self.assertEqual(os.listdir(self.tmp.name), [])

    def upload_bundle(self, announced, parts):
        members = [portable_member('./IMG_9679.pvt', mode=0o040755)]
        for name in parts:
            members.append(portable_member('./IMG_9679.pvt/' + name, b'asset'))
        body = gzip.compress(archive(*members))
        with tempfile.TemporaryFile(dir=self.tmp.name) as raw:
            raw.write(body)
            return transfer.store_upload(raw, self.tmp.name, 'test', Budget(),
                                         approved=announced)

    def test_approved_live_photo_bundle_is_saved(self):
        files = self.upload_bundle(['IMG_9679.pvt'],
                                   ['IMG_9679.HEIC', 'IMG_9679.MOV', 'metadata.plist'])
        self.assertEqual(files, ['IMG_9679.HEIC', 'IMG_9679.MOV'])
        self.assertEqual(transfer.live_photos(files),
                         [['IMG_9679.HEIC', 'IMG_9679.MOV']])

    def test_approved_live_photo_bundle_with_jpeg_is_saved(self):
        files = self.upload_bundle(['IMG_9679.pvt'],
                                   ['IMG_9679.jpg', 'IMG_9679.MOV'])
        self.assertEqual(files, ['IMG_9679.jpg', 'IMG_9679.MOV'])

    def test_unannounced_executable_inside_bundle_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'unapproved'):
            self.upload_bundle(['IMG_9679.pvt'],
                               ['IMG_9679.jpg', 'IMG_9679.MOV', 'IMG_9679.exe'])
        self.assertEqual(os.listdir(self.tmp.name), [])

    def test_wrong_stem_inside_bundle_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'unapproved'):
            self.upload_bundle(['IMG_9679.pvt'],
                               ['IMG_9679.jpg', 'IMG_9679.MOV', 'other.jpg'])
        self.assertEqual(os.listdir(self.tmp.name), [])

    def test_duplicate_photo_inside_bundle_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'unapproved'):
            self.upload_bundle(['IMG_9679.pvt'],
                               ['IMG_9679.jpg', 'IMG_9679.HEIC', 'IMG_9679.MOV'])
        self.assertEqual(os.listdir(self.tmp.name), [])

    def test_incomplete_live_photo_bundle_rolls_back(self):
        with self.assertRaisesRegex(ValueError, 'omitted'):
            self.upload_bundle(['IMG_9679.pvt'], ['IMG_9679.jpg'])
        self.assertEqual(os.listdir(self.tmp.name), [])

    def test_bundle_without_approval_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'unapproved'):
            self.upload_bundle(['another.pvt'], ['IMG_9679.jpg', 'IMG_9679.MOV'])
        self.assertEqual(os.listdir(self.tmp.name), [])



if __name__ == '__main__':
    unittest.main()
