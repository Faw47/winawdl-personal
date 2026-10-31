# SPDX-License-Identifier: MIT
# Adapted from Omdrop's tests/test_safeplist.py, Copyright (c) 2026 Brent Kearney.
"""Malicious property lists must not expand without bounds."""
import plistlib
import struct
import sys
import time
import tracemalloc
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import safeplist


def binary(value):
    return plistlib.dumps(value, fmt=plistlib.FMT_BINARY)


def many_ids(members=200_000, ids=1000):
    count = ids + 3
    body = bytearray(b'bplist00')
    offsets = [len(body)]
    body += b'\x10\x01'
    array_at = len(body)
    body += b'\xaf\x12' + struct.pack('>I', members) + struct.pack('>H', 0) * members
    offsets += [array_at] * (ids + 1)
    offsets.append(len(body))
    body += b'\xaf\x12' + struct.pack('>I', ids)
    body += b''.join(struct.pack('>H', i) for i in range(2, ids + 2))
    table = len(body)
    body += b''.join(struct.pack('>I', o) for o in offsets)
    body += bytes(6) + bytes([4, 2]) + struct.pack('>QQQ', count, count - 1, table)
    return bytes(body)


def nested_overlapping(members):
    body = bytearray(b'bplist00')
    offsets = []
    for i in range(34):
        offsets.append(len(body))
        body += b'\xaf\x12' + struct.pack('>I', members) + struct.pack('>H', i + 1)
    scalar = len(body)
    body += b'\x00' + struct.pack('>H', 300) * members
    offsets += [scalar] * (301 - len(offsets))
    table = len(body)
    body += b''.join(struct.pack('>I', o) for o in offsets)
    body += bytes(6) + bytes([4, 2]) + struct.pack('>QQQ', 301, 0, table)
    return bytes(body)


class SafePlistTests(unittest.TestCase):
    def test_ordinary_ask(self):
        ask = {'SenderComputerName': 'Mac', 'TransferType': {'files': 1}, 'Items': [],
               'SenderRecordData': b'\x01\x02\x03',
               'Files': [{'FileName': f'{i}.jpg', 'FileType': 'public.jpeg', 'FileSize': i}
                         for i in range(512)]}
        self.assertEqual(safeplist.loads(binary(ask)), ask)
        self.assertEqual(safeplist.loads(plistlib.dumps(ask, fmt=plistlib.FMT_XML)), ask)
        with self.assertRaises(plistlib.InvalidFileException):
            safeplist.loads(b'not a plist')

    def test_repeated_references_exponential(self):
        value = []
        for _ in range(4):
            value = [value] * 200
        body = binary(value)
        self.assertLess(len(body), 1024)
        with self.assertRaises(safeplist.TooComplex):
            safeplist.loads(body)
        self.assertEqual(len(safeplist.loads(binary([[]] * 200))), 200)

    def test_54_gigabyte_ask_log_bomb(self):
        entry = {'FileName': 'A' * 60_000, 'FileType': 'public.data', 'FileSize': 1}
        bomb = binary({'Files': [entry] * 900_000})
        self.assertLess(len(bomb), 1 << 20)
        started = time.monotonic()
        with self.assertRaises(safeplist.TooComplex):
            safeplist.loads(bomb)
        self.assertLess(time.monotonic() - started, 5)
        self.assertEqual(len(safeplist.loads(binary({'Files': [entry] * 3}))['Files']), 3)

    def test_reused_large_data_is_bounded(self):
        data = b'A' * 840_000
        with self.assertRaises(safeplist.TooComplex):
            safeplist.loads(binary([data] * 199_000))
        self.assertEqual(len(safeplist.loads(binary([data] * 3))), 3)

    def test_depth_and_cycles(self):
        depth = 200_000
        xml = ('<?xml version="1.0"?><plist version="1.0">' + '<array>' * depth
               + '</array>' * depth + '</plist>').encode()
        with self.assertRaises((safeplist.TooComplex, RecursionError)):
            safeplist.loads(xml)
        cycle = []
        cycle.append(cycle)
        with self.assertRaises(safeplist.TooComplex):
            safeplist.loads(binary(cycle))
        nested = []
        for _ in range(safeplist.MAX_DEPTH - 1):
            nested = [nested]
        safeplist.loads(binary(nested))
        with self.assertRaises(safeplist.TooComplex):
            safeplist.loads(binary([nested]))

    def test_many_ids_are_bounded_while_decoding(self):
        bomb = many_ids()
        self.assertLess(len(bomb), 1 << 20)
        started = time.monotonic()
        with self.assertRaises(safeplist.TooComplex):
            safeplist.loads(bomb)
        self.assertLess(time.monotonic() - started, 3)
        self.assertEqual(len(safeplist.loads(many_ids(members=1000, ids=2))), 2)

    def test_impossible_counts(self):
        body = bytearray(b'bplist00')
        body += b'\xaf\x13' + struct.pack('>Q', 1 << 40)
        table = len(body)
        body += bytes([8]) + bytes(6) + bytes([1, 1]) + struct.pack('>QQQ', 1, 0, table)
        with self.assertRaises(ValueError):
            safeplist.loads(bytes(body))
        body = bytearray(b'bplist00\x08')
        body += bytes(6) + bytes([1, 1]) + struct.pack('>QQQ', 1 << 40, 0, 9)
        with self.assertRaises(plistlib.InvalidFileException):
            safeplist.loads(bytes(body))

    def test_overlapping_references_bounded_memory(self):
        for members in (10_000, 100_000):
            tracemalloc.start()
            try:
                with self.assertRaises(safeplist.TooComplex):
                    safeplist.loads(nested_overlapping(members))
                peak = tracemalloc.get_traced_memory()[1]
            finally:
                tracemalloc.stop()
            self.assertLess(peak, 12 << 20, members)


if __name__ == '__main__':
    unittest.main()
