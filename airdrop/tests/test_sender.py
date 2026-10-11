# SPDX-License-Identifier: MIT
"""The sender's pieces, and a whole transfer to this project's own receiver
over loopback. Needs OpenSSL to create the certificate both ends present."""
import argparse
import io
import os
import socket
import sys
import tempfile
import threading
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import cpio  # noqa: E402
import mdns  # noqa: E402
import receiver  # noqa: E402
import sender  # noqa: E402
import transfer  # noqa: E402

# Loopback connections carry no interface scope.
LOOPBACK_SCOPE = 0
SERVICE_TYPE = '_airdrop._tcp.local'
INSTANCE = f'0a1b2c3d4e5f.{SERVICE_TYPE}'
ADDRESS = 'fe80::5000:5eff:fe00:5301'
SERVICE = mdns.Service(SERVICE_TYPE, '0a1b2c3d4e5f', '0a1b2c3d4e5f.local', ADDRESS, 8770,
                       'flags=1019')


class ArchiveWriterTest(unittest.TestCase):
    def test_what_is_written_reads_back(self):
        members = [('.', cpio.DIRECTORY_MODE, 0, 0, []),
                   ('./a.txt', cpio.FILE_MODE, 5, 1700000000, [b'hel', b'lo']),
                   ('./empty', cpio.FILE_MODE, 0, 0, [])]
        raw = b''.join(cpio.write_archive(members))
        self.assertEqual(len(raw) % cpio.RECORD_BYTES, 0)
        read = [(entry.pathname, entry.isfile, b''.join(entry.blocks()))
                for entry in cpio.read_entries(io.BytesIO(raw))]
        self.assertEqual(read, [('.', False, b''), ('./a.txt', True, b'hello'),
                                ('./empty', True, b'')])
        scanner = cpio.TrailerScanner()
        scanner.feed(raw)
        self.assertTrue(scanner.complete)

    def test_a_file_that_changed_size_is_an_error(self):
        for blocks in ([b'ab'], [b'abcd']):
            with self.assertRaises(cpio.CpioError):
                list(cpio.write_archive([('./a', cpio.FILE_MODE, 3, 0, blocks)]))

    def test_blocks_are_compressed_only_when_that_helps(self):
        block = transfer.DVZIP_BLOCK_BYTES
        noise = os.urandom(block)
        framed = list(transfer.encode_dvzip([b'\0' * block, noise[:100], noise[100:], b'tail']))
        sizes = [int.from_bytes(piece[:4], 'big') for piece in framed]
        self.assertLess(sizes[0], block)
        self.assertEqual(sizes[1], block | transfer.DVZIP_STORED)
        self.assertEqual(framed[1][4:], noise)
        self.assertEqual(len(framed), 3)


class BrowseTest(unittest.TestCase):
    def test_an_announcement_locates_its_instance(self):
        instances = mdns.Instances(SERVICE_TYPE)
        self.assertEqual(instances.located(), {})
        instances.feed(SERVICE.response(), 'fe80::1')
        self.assertEqual(instances.located(), {INSTANCE: (ADDRESS, 8770)})
        instances.feed(SERVICE.response(alive=False), 'fe80::1')
        self.assertEqual(instances.located(), {})

    def test_an_instance_without_a_place_is_asked_about(self):
        instances = mdns.Instances(SERVICE_TYPE)
        self.assertEqual(instances.questions(), [(SERVICE_TYPE, mdns.TYPE_PTR)])
        pointer = mdns._record(SERVICE_TYPE, mdns.TYPE_PTR, mdns.CLASS_IN, 4500,
                               mdns.encode_name(INSTANCE))
        header = b'\0\0' + mdns.RESPONSE_FLAGS.to_bytes(2, 'big') + bytes([0, 0, 0, 1, 0, 0, 0, 0])
        instances.feed(header + pointer, 'fe80::1')
        self.assertEqual(instances.located(), {})
        self.assertEqual(instances.questions(),
                         [(SERVICE_TYPE, mdns.TYPE_PTR), (INSTANCE, mdns.TYPE_SRV)])
        self.assertEqual(mdns.question_names(mdns.query(instances.questions())),
                         {SERVICE_TYPE, INSTANCE})

    def test_a_missing_address_falls_back_to_where_the_answer_came_from(self):
        instances = mdns.Instances(SERVICE_TYPE)
        records = [
            mdns._record(SERVICE_TYPE, mdns.TYPE_PTR, mdns.CLASS_IN, 4500,
                         mdns.encode_name(INSTANCE)),
            mdns._record(INSTANCE, mdns.TYPE_SRV, mdns.CLASS_IN, 120,
                         bytes([0, 0, 0, 0, 0x22, 0x42]) + mdns.encode_name('host.local')),
        ]
        header = b'\0\0' + mdns.RESPONSE_FLAGS.to_bytes(2, 'big') + bytes([0, 0, 0, 2, 0, 0, 0, 0])
        instances.feed(header + b''.join(records), 'fe80::2')
        self.assertEqual(instances.located(), {INSTANCE: ('fe80::2', 8770)})

    def test_queries_and_damaged_packets_say_nothing(self):
        self.assertEqual(mdns.response_records(mdns.query([(SERVICE_TYPE, mdns.TYPE_PTR)])), [])
        self.assertEqual(mdns.response_records(SERVICE.response()[:40]), [])
        self.assertEqual(mdns.response_records(b'\x00'), [])


class AskTest(unittest.TestCase):
    def test_each_file_is_named_with_its_type_and_place_in_the_archive(self):
        files = [sender.Outgoing('x', 'Photo.JPG', 1, 0), sender.Outgoing('y', 'notes.xyz', 1, 0)]
        ask = sender.ask_body('Study PC', files, 'AN-ID')
        self.assertEqual(ask['SenderComputerName'], 'Study PC')
        self.assertEqual(ask['TransferID'], {'id': 'AN-ID'})
        self.assertEqual([f['FileSize'] for f in ask['Files']], [1, 1])
        self.assertEqual([(f['FileName'], f['FileType'], f['FileBomPath']) for f in ask['Files']],
                         [('Photo.JPG', 'public.jpeg', './Photo.JPG'),
                          ('notes.xyz', 'public.data', './notes.xyz')])

    def test_two_files_with_one_name_are_refused(self):
        with tempfile.TemporaryDirectory() as first, tempfile.TemporaryDirectory() as second:
            paths = [os.path.join(folder, 'a.txt') for folder in (first, second)]
            for path in paths:
                with open(path, 'wb') as out:
                    out.write(b'x')
            self.assertEqual(sender.outgoing(paths[:1])[0].name, 'a.txt')
            with self.assertRaises(SystemExit):
                sender.outgoing(paths)


class LoopbackTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.keys = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.keys.cleanup)
        cls.server_context = receiver.tls_context(cls.keys.name)
        cls.client_context = sender.client_context(cls.keys.name)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.received = os.path.join(self.temp.name, 'received')
        os.mkdir(self.received)
        settings = argparse.Namespace(
            name='Test PC', model='MacBookPro18,3', dest=self.received,
            max_receive_percent=30, interface_index=LOOPBACK_SCOPE, redact=False)
        self.server = receiver.Receiver(0, settings)
        self.server.socket = self.server_context.wrap_socket(
            self.server.socket, server_side=True, do_handshake_on_connect=False)
        thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(self.stop_server, thread)
        self.port = self.server.server_address[1]

    def stop_server(self, thread):
        self.server.shutdown()
        thread.join()
        self.server.reset_sessions()
        self.server.server_close()

    def source_file(self, name, data):
        path = os.path.join(self.temp.name, name)
        with open(path, 'wb') as out:
            out.write(data)
        return path

    def test_the_receiver_gives_its_name(self):
        self.assertEqual(
            sender.receiver_name('::1', self.port, LOOPBACK_SCOPE, self.client_context),
            'Test PC')

    def test_nothing_listening_is_unreachable(self):
        with socket.socket(socket.AF_INET6, socket.SOCK_STREAM) as unused:
            unused.bind(('::1', 0))
            port = unused.getsockname()[1]
        with self.assertRaises(sender.Unreachable):
            sender.receiver_name('::1', port, LOOPBACK_SCOPE, self.client_context)

    def test_files_arrive_as_they_were(self):
        large = os.urandom(3 * sender.IO_BLOCK_BYTES + 17)
        files = sender.outgoing([self.source_file('clip.mp4', large),
                                 self.source_file('note.txt', b'hello')])
        self.assertTrue(sender.send('::1', self.port, LOOPBACK_SCOPE, self.client_context,
                                    'Sender PC', files))
        stored = {}
        for name in os.listdir(self.received):
            with open(os.path.join(self.received, name), 'rb') as source:
                stored[name] = source.read()
        self.assertEqual(stored, {'clip.mp4': large, 'note.txt': b'hello'})


if __name__ == '__main__':
    unittest.main()
