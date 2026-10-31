# SPDX-License-Identifier: MIT
"""Every parser that reads what a stranger sends, fed seeded mutations of
real input: each must give an answer or fail as its callers expect, and do
so quickly. The seed is fixed so a failure repeats."""
import gzip
import io
import os
import plistlib
import random
import sys
import time
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cpio  # noqa: E402
import mdns  # noqa: E402
import safeplist  # noqa: E402
import transfer  # noqa: E402
from test_transfer import archive, portable_member  # noqa: E402

ROUNDS = 400
# Far more than any parser here takes on these sizes.
SLOWEST_SECONDS = 2.0


def mutations(rng, seeds, rounds):
    pool = list(seeds)
    for _ in range(rounds):
        data = bytearray(rng.choice(pool))
        for _ in range(rng.randint(1, 8)):
            choice = rng.random()
            if choice < 0.4 and data:
                data[rng.randrange(len(data))] = rng.randrange(256)
            elif choice < 0.6 and data:
                at = rng.randrange(len(data))
                del data[at:at + rng.randint(1, 16)]
            elif choice < 0.8:
                at = rng.randrange(len(data) + 1)
                data[at:at] = bytes(rng.randrange(256) for _ in range(rng.randint(1, 16)))
            elif data:
                at, start = rng.randrange(len(data)), rng.randrange(len(data))
                data[at:at] = data[start:start + rng.randint(1, 64)]
        data = bytes(data)
        if rng.random() < 0.3:
            pool.append(data)
        yield data


class FuzzTest(unittest.TestCase):
    def survive(self, seeds, parse, allowed=()):
        rng = random.Random(self.id())
        for data in mutations(rng, seeds, ROUNDS):
            started = time.monotonic()
            try:
                parse(data)
            except allowed:
                pass
            except Exception as error:
                self.fail(f'{type(error).__name__}: {error} on {data[:80]!r}')
            self.assertLess(time.monotonic() - started, SLOWEST_SECONDS, data[:80])

    def test_plists_fail_as_valueerror(self):
        seeds = [plistlib.dumps({'Files': [{'FileName': 'a.jpg', 'FileSize': 5}],
                                 'SenderRecordData': b'x' * 40, 'n': 3.5},
                                fmt=plistlib.FMT_BINARY),
                 plistlib.dumps({'a': [1, 2, 'b'], 'c': b'xy', 'd': {'e': True}})]
        self.survive(seeds, safeplist.loads, ValueError)

    def test_archives_fail_as_valueerror(self):
        def read(data):
            for entry in cpio.read_entries(io.BytesIO(data)):
                for _ in entry.blocks():
                    pass
        seeds = [archive(portable_member('./a.txt', b'hello'),
                         portable_member('./b/c.jpg', b'x' * 300))]
        self.survive(seeds, read, ValueError)

    def test_compressed_bodies_fail_as_valueerror(self):
        body = archive(portable_member('./a.txt', b'hello' * 50))
        stored = (len(body) | transfer.DVZIP_STORED).to_bytes(4, 'big') + body
        seeds = [gzip.compress(body), stored]
        self.survive(seeds, lambda data: transfer.decode_dvzip(
            io.BytesIO(data), io.BytesIO(), 1 << 20, None), ValueError)

    def test_the_end_watcher_never_fails(self):
        body = archive(portable_member('./a.txt', b'hello' * 50))
        seeds = [gzip.compress(body)]
        self.survive(seeds, lambda data: transfer.ArchiveEndWatcher().feed(data))

    def test_chunked_bodies_fail_as_request_errors(self):
        seeds = [b'5\r\nhello\r\n6; e=1\r\n world\r\n0\r\n\r\n']
        self.survive(seeds, lambda data: transfer.read_small_body(
            io.BufferedReader(io.BytesIO(data)), {'Transfer-Encoding': 'chunked'}),
            transfer.RequestBodyError)

    def test_multicast_dns_never_fails(self):
        service = mdns.Service('_airdrop._tcp.local', 'abc', 'abc.local', 'fe80::1', 8771,
                               'flags=1')

        def read(data):
            mdns.response_records(data)
            mdns.question_names(data)
            instances = mdns.Instances('_airdrop._tcp.local')
            instances.feed(data, 'fe80::2')
            instances.located()
        self.survive([service.response(), mdns.query([('_airdrop._tcp.local', 12)])], read)


class MalformedPlistTest(unittest.TestCase):
    def test_a_broken_xml_plist_is_a_valueerror(self):
        for body in (b'<?xml version="1.0"?><plist><dict><key>a</ke',
                     b'<?xml version="1.0" encoding="UTX-8"?><plist/>'):
            with self.assertRaises(ValueError):
                safeplist.loads(body)


if __name__ == '__main__':
    unittest.main()
