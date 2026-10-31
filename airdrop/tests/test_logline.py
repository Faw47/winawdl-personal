# SPDX-License-Identifier: MIT
"""What a sender puts in a name stays on one bounded log line."""
import io
import logging
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import logline  # noqa: E402


class EscapeTest(unittest.TestCase):
    def test_line_breaks_and_invisible_characters_are_escaped(self):
        self.assertEqual(logline.escape('a\nb\rc\x00\x85'), 'a\\x0ab\\x0dc\\x00\\x85')
        self.assertEqual(logline.escape('x y‮z﻿'), 'x\\u2028y\\u202ez\\ufeff')

    def test_a_lone_surrogate_is_escaped_rather_than_failing(self):
        self.assertEqual(logline.escape('a\udcffb'), 'a\\udcffb')

    def test_ordinary_text_is_left_alone(self):
        text = 'IMG_1.HEIC from Ada’s iPhone'
        self.assertEqual(logline.escape(text), text)

    def test_a_long_message_is_cut_at_a_character(self):
        out = logline.escape('é' * logline.MAX_MESSAGE_BYTES)
        head, tail = out.split(' [... ')
        self.assertLessEqual(len(head.encode('utf-8')), logline.MAX_MESSAGE_BYTES)
        self.assertEqual(tail, f'{logline.MAX_MESSAGE_BYTES} more bytes]')


class FormatterTest(unittest.TestCase):
    def test_a_forged_line_stays_in_its_record(self):
        stream = io.StringIO()
        handler = logging.StreamHandler(stream)
        handler.setFormatter(logline.Formatter('%(levelname)s %(message)s'))
        log = logging.getLogger('logline-test')
        log.propagate = False
        log.addHandler(handler)
        self.addCleanup(log.removeHandler, handler)
        log.warning('ask from %s', 'Phone\nINFO upload x: stored evil.exe')
        try:
            raise ValueError('bad\nname')
        except ValueError:
            log.exception('failed')
        lines = stream.getvalue().splitlines()
        self.assertEqual(len(lines), 2)
        self.assertIn('Phone\\x0aINFO upload', lines[0])
        self.assertIn('ValueError: bad\\x0aname', lines[1])


if __name__ == '__main__':
    unittest.main()
