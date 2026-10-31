# SPDX-License-Identifier: MIT
"""The HTTPS endpoints end to end over loopback, with a real TLS handshake but
no AWDL adapter. Needs OpenSSL to create the receiver's certificate."""
import argparse
import gzip
import http.client
import os
import plistlib
import ssl
import sys
import tempfile
import socket
import struct
import threading
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import apple_fixtures  # noqa: E402
import contacts  # noqa: E402
import receiver  # noqa: E402
import transfer  # noqa: E402
from test_transfer import archive, padded, portable_member  # noqa: E402

# Loopback connections carry no interface scope.
LOOPBACK_SCOPE = 0
OTHER_INTERFACE = 4242


class ReceiverTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.keys = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.keys.cleanup)
        cls.context = receiver.tls_context(cls.keys.name)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.settings = argparse.Namespace(
            name='Test PC', model='MacBookPro18,3', dest=self.temp.name,
            max_receive_percent=30, interface_index=LOOPBACK_SCOPE, redact=False)
        self.server = receiver.Receiver(0, self.settings)
        self.server.socket = self.context.wrap_socket(
            self.server.socket, server_side=True, do_handshake_on_connect=False)
        thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(self.stop_server, thread)

    def stop_server(self, thread):
        self.server.shutdown()
        thread.join()
        self.server.reset_sessions()
        self.server.server_close()

    def connect(self):
        trusting = ssl.create_default_context()
        trusting.check_hostname = False
        trusting.verify_mode = ssl.CERT_NONE
        connection = http.client.HTTPSConnection(
            '::1', self.server.server_address[1], context=trusting, timeout=10)
        self.addCleanup(connection.close)
        return connection

    def post_plist(self, connection, path, value):
        body = plistlib.dumps(value, fmt=plistlib.FMT_BINARY)
        connection.request('POST', path, body=iter([body]), encode_chunked=True,
                           headers={'Transfer-Encoding': 'chunked'})
        response = connection.getresponse()
        return response.status, response.read()

    def test_discover_names_this_receiver(self):
        status, body = self.post_plist(self.connect(), '/Discover', {})
        self.assertEqual(status, 200)
        answer = plistlib.loads(body)
        self.assertEqual(answer['ReceiverComputerName'], 'Test PC')
        self.assertEqual(answer['ReceiverModelName'], 'MacBookPro18,3')
        self.assertIn('ReceiverMediaCapabilities', answer)

    def test_original_media_opt_in_advertises_version_three(self):
        with mock.patch.dict(os.environ, {'WINAWDL_ORIGINAL_MEDIA': '1'}):
            status, body = self.post_plist(self.connect(), '/Discover', {})
        self.assertEqual(status, 200)
        capabilities = plistlib.loads(body)['ReceiverMediaCapabilities']
        self.assertEqual(__import__('json').loads(capabilities)['Version'], 3)
        self.assertIn(b'public.heic', capabilities)

    def test_legacy_media_remains_default(self):
        with mock.patch.dict(os.environ, {'WINAWDL_ORIGINAL_MEDIA': ''}):
            status, body = self.post_plist(self.connect(), '/Discover', {})
        self.assertEqual(status, 200)
        capabilities = plistlib.loads(body)['ReceiverMediaCapabilities']
        self.assertEqual(__import__('json').loads(capabilities), {'Version': 1})

    def test_reference_bomb_is_rejected_before_logging_metadata(self):
        # Under 1 MiB on the wire, but expands to >10 MiB of file names.
        entry = {'FileName': 'A' * 60_000, 'FileSize': 1}
        body = plistlib.dumps({'Files': [entry] * 200}, fmt=plistlib.FMT_BINARY)
        self.assertLess(len(body), 1 << 20)
        connection = self.connect()
        with self.assertLogs(level='WARNING') as logs:
            connection.request('POST', '/Ask', body=body,
                               headers={'Content-Type': 'application/octet-stream'})
            response = connection.getresponse()
            self.assertEqual(response.status, 400)
            response.read()
        self.assertTrue(any('undecodable' in line for line in logs.output))
        self.assertFalse(any('ask from' in line for line in logs.output))

    def test_ask_then_upload_on_one_connection_stores_the_file(self):
        connection = self.connect()
        ask = {'SenderComputerName': 'Phone', 'Files': [{'FileName': 'a.txt'}]}
        self.assertEqual(self.post_plist(connection, '/Ask', ask)[0], 200)

        payload = gzip.compress(archive(portable_member('./a.txt', b'sent over TLS')))
        connection.request('POST', '/Upload', body=payload,
                           headers={'Content-Type': 'application/x-dvzip'})
        response = connection.getresponse()
        response.read()
        self.assertEqual(response.status, 200)
        self.assertEqual(response.getheader('Connection'), 'close')
        with open(os.path.join(self.temp.name, 'a.txt'), 'rb') as stored:
            self.assertEqual(stored.read(), b'sent over TLS')

    def test_approved_photo_cannot_turn_into_an_executable(self):
        connection = self.connect()
        ask = {'SenderComputerName': 'Phone', 'Files': [{'FileName': 'photo.jpg'}]}
        self.assertEqual(self.post_plist(connection, '/Ask', ask)[0], 200)
        payload = gzip.compress(archive(portable_member('./photo.jpg', b'photo'),
                                        portable_member('./surprise.exe', b'MZ')))
        connection.request('POST', '/Upload', body=payload,
                           headers={'Content-Type': 'application/x-dvzip'})
        response = connection.getresponse()
        response.read()
        self.assertEqual(response.status, 422)
        self.assertEqual(os.listdir(self.temp.name), [])

    def upload_without_end_marker(self, payload):
        """Send every chunk of an upload but not the zero-length one that
        ends it, as an iPhone does for a video. Returns the status line."""
        trusting = ssl.create_default_context()
        trusting.check_hostname = False
        trusting.verify_mode = ssl.CERT_NONE
        plain = socket.create_connection(('::1', self.server.server_address[1]),
                                         timeout=10)
        with trusting.wrap_socket(plain) as tls:
            tls.sendall(b'POST /Upload HTTP/1.1\r\nHost: receiver\r\n'
                        b'Content-Type: application/x-dvzip\r\n'
                        b'Transfer-Encoding: chunked\r\n\r\n' +
                        b'%x\r\n' % len(payload) + payload + b'\r\n')
            return tls.recv(4096).split(b'\r\n', 1)[0]

    def test_a_complete_upload_is_answered_without_waiting_for_its_end(self):
        payload = gzip.compress(padded(archive(portable_member('./a.mov', b'live'))))
        with mock.patch.object(transfer, 'END_MARKER_WAIT_SECONDS', 0.2):
            status = self.upload_without_end_marker(payload)
        self.assertEqual(status, b'HTTP/1.1 200 OK')
        with open(os.path.join(self.temp.name, 'a.mov'), 'rb') as stored:
            self.assertEqual(stored.read(), b'live')

    def test_a_complete_upload_is_kept_when_its_end_marker_never_comes(self):
        payload = gzip.compress(archive(portable_member('./clip.mp4', b'video')))
        with mock.patch.object(transfer, 'READ_IDLE_TIMEOUT_SECONDS', 1):
            status = self.upload_without_end_marker(payload)
        self.assertEqual(status, b'HTTP/1.1 200 OK')
        with open(os.path.join(self.temp.name, 'clip.mp4'), 'rb') as stored:
            self.assertEqual(stored.read(), b'video')

    def test_a_cut_off_upload_is_still_refused(self):
        payload = gzip.compress(archive(portable_member('./clip.mp4', b'video')))
        with mock.patch.object(transfer, 'READ_IDLE_TIMEOUT_SECONDS', 1):
            status = self.upload_without_end_marker(payload[:-8])
        self.assertEqual(status, b'HTTP/1.1 408 Request Timeout')
        self.assertEqual(os.listdir(self.temp.name), [])

    def test_a_link_is_stored_by_the_ask_alone(self):
        ask = {'TransferType': {'links': True},
               'Items': ['https://example.org/page', 'file:///C:/Windows/win.ini']}
        self.assertEqual(self.post_plist(self.connect(), '/Ask', ask)[0], 200)
        self.assertEqual(os.listdir(self.temp.name), ['example.org-page.url'])

    def test_an_ask_of_too_many_files_is_refused_before_it_is_listed(self):
        files = [{'FileName': f'{n}.jpg'} for n in range(transfer.MAX_ARCHIVE_MEMBERS + 1)]
        with self.assertLogs(level='WARNING') as logs:
            status, _ = self.post_plist(self.connect(), '/Ask', {'Files': files})
        self.assertEqual(status, 413)
        self.assertFalse(any('ask from' in line for line in logs.output))

    def test_an_ask_whose_parts_have_the_wrong_types_is_answered(self):
        self.assertEqual(self.post_plist(self.connect(), '/Ask', {'Items': 7})[0], 400)
        ask = {'TransferType': 5, 'Files': [{'FileName': 'a.txt'}]}
        self.assertEqual(self.post_plist(self.connect(), '/Ask', ask)[0], 200)

    def test_links_over_the_disk_budget_are_refused_and_none_stored(self):
        ask = {'TransferType': {'links': True},
               'Items': ['https://example.org/a', 'https://example.org/b']}
        low = mock.Mock(free=transfer.MIN_FREE_BYTES + 1024)
        failed = []
        self.server.watcher.upload_failed = lambda token, reason: failed.append(reason)
        with mock.patch.object(transfer.shutil, 'disk_usage', return_value=low):
            status, _ = self.post_plist(self.connect(), '/Ask', ask)
        self.assertEqual(status, 507)
        self.assertEqual(os.listdir(self.temp.name), [])
        self.assertEqual(len(failed), 1)

    def test_a_sender_that_closes_mid_upload_has_left(self):
        # What a reset can look like through TLS: the stream just ends.
        failed = []
        left = threading.Event()

        def upload_failed(token, reason):
            failed.append(reason)
            left.set()
        self.server.watcher.upload_failed = upload_failed
        reading = threading.Event()
        self.server.watcher.upload_progress = lambda token, done: reading.set()
        trusting = ssl.create_default_context()
        trusting.check_hostname = False
        trusting.verify_mode = ssl.CERT_NONE
        plain = socket.create_connection(('::1', self.server.server_address[1]), timeout=10)
        tls = trusting.wrap_socket(plain)
        tls.sendall(b'POST /Upload HTTP/1.1\r\nHost: receiver\r\n'
                    b'Content-Type: application/x-dvzip\r\n'
                    b'Transfer-Encoding: chunked\r\n\r\n1000\r\n' + b'x' * 100)
        self.assertTrue(reading.wait(10))
        tls.close()
        self.assertTrue(left.wait(10))
        self.assertIn('the sender left', failed[0])

    def test_a_sender_that_leaves_mid_upload_is_not_a_storage_failure(self):
        failed = []
        left = threading.Event()

        def upload_failed(token, reason):
            failed.append(reason)
            left.set()
        self.server.watcher.upload_failed = upload_failed
        reading = threading.Event()
        self.server.watcher.upload_progress = lambda token, done: reading.set()
        trusting = ssl.create_default_context()
        trusting.check_hostname = False
        trusting.verify_mode = ssl.CERT_NONE
        plain = socket.create_connection(('::1', self.server.server_address[1]), timeout=10)
        tls = trusting.wrap_socket(plain)
        tls.sendall(b'POST /Upload HTTP/1.1\r\nHost: receiver\r\n'
                    b'Content-Type: application/x-dvzip\r\n'
                    b'Transfer-Encoding: chunked\r\n\r\n1000\r\n' + b'x' * 100)
        self.assertTrue(reading.wait(10))
        # A reset, as a phone that cancels sends.
        tls.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER,
                       struct.pack('HH' if os.name == 'nt' else 'ii', 1, 0))
        tls.close()
        self.assertTrue(left.wait(10))
        self.assertIn('the sender left', failed[0])
        self.assertNotIn('storage failed', failed[0])

    def test_redacting_keeps_the_sender_and_its_folders_out_of_the_log(self):
        self.settings.redact = True
        ask = {'SenderComputerName': 'Ada phone', 'SenderModelName': 'iPhone',
               'Files': [{'FileName': 'C:\\Users\\ada\\trip/a.txt', 'FileSize': 3}]}
        with self.assertLogs(level='INFO') as logs:
            self.assertEqual(self.post_plist(self.connect(), '/Ask', ask)[0], 200)
        asked = next(line for line in logs.output if 'ask from' in line)
        self.assertIn('ask from a sender (iPhone)', asked)
        self.assertIn(' a.txt ', asked)
        self.assertNotIn('Ada', asked)
        self.assertNotIn('trip', asked)

    def test_an_upload_of_another_type_is_refused(self):
        connection = self.connect()
        connection.request('POST', '/Upload', body=b'plain',
                           headers={'Content-Type': 'text/plain'})
        self.assertEqual(connection.getresponse().status, 415)
        self.assertEqual(os.listdir(self.temp.name), [])

    def test_connections_from_other_interfaces_are_dropped(self):
        self.settings.interface_index = OTHER_INTERFACE
        with self.assertRaises(OSError):
            self.post_plist(self.connect(), '/Discover', {})


class ContactsOnlyTest(unittest.TestCase):
    """The receiver over TLS with only known senders accepted, against a
    stand-in for Apple's CAs."""

    ACCOUNT = '001288-10-AAAA-BBBB-CCCC-DDDD'
    OTHER = '001288-10-FFFF-9999-8888-7777'

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.tmp.cleanup)
        cls.fx = apple_fixtures.Fixtures(cls.tmp.name)
        for name, path in (('APPLE_ROOT_CA', cls.fx.root_crt),
                           ('APPLE_AAI_CA', cls.fx.mid_crt), ('RECORD_CA', cls.fx.mid_crt)):
            patcher = mock.patch.object(contacts, name, path)
            patcher.start()
            cls.addClassCleanup(patcher.stop)
        cls.keys = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.keys.cleanup)
        cls.context = receiver.tls_context(cls.keys.name, contacts_only=True)
        cls.friend = cls.fx.client(cls.ACCOUNT)
        cls.impostor = cls.fx.client(cls.OTHER)
        cls.friends_record = cls.fx.record(['friend@example.com'], cls.ACCOUNT)
        cls.strangers_record = cls.fx.record(['stranger@example.com'], cls.ACCOUNT)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.settings = argparse.Namespace(
            name='Test PC', model='MacBookPro18,3', dest=self.temp.name,
            max_receive_percent=30, interface_index=LOOPBACK_SCOPE, redact=False,
            contacts_only=True, known_senders=['Friend@Example.com'])
        self.asked = []
        watcher = receiver.Watcher()
        watcher.asked = lambda ask, address, left: self.asked.append(ask) or True
        self.server = receiver.Receiver(0, self.settings, watcher)
        self.server.socket = self.context.wrap_socket(
            self.server.socket, server_side=True, do_handshake_on_connect=False)
        thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(self.stop_server, thread)

    def stop_server(self, thread):
        self.server.shutdown()
        thread.join()
        self.server.reset_sessions()
        self.server.server_close()

    def connect(self, client=None):
        trusting = ssl.create_default_context()
        trusting.check_hostname = False
        trusting.verify_mode = ssl.CERT_NONE
        if client:
            trusting.load_cert_chain(*client)
        connection = http.client.HTTPSConnection(
            '::1', self.server.server_address[1], context=trusting, timeout=10)
        self.addCleanup(connection.close)
        return connection

    def post(self, connection, path, value):
        body = plistlib.dumps(value, fmt=plistlib.FMT_BINARY)
        connection.request('POST', path, body=iter([body]), encode_chunked=True,
                           headers={'Transfer-Encoding': 'chunked'})
        response = connection.getresponse()
        return response.status, response.read()

    def ask(self, record):
        ask = {'SenderComputerName': 'Phone', 'Files': [{'FileName': 'a.txt'}]}
        if record:
            ask['SenderRecordData'] = record
        return ask

    def upload(self, connection):
        payload = gzip.compress(archive(portable_member('./a.txt', b'from a friend')))
        connection.request('POST', '/Upload', body=payload,
                           headers={'Content-Type': 'application/x-dvzip'})
        response = connection.getresponse()
        response.read()
        return response.status

    def test_a_known_sender_is_asked_about_and_stored(self):
        connection = self.connect(self.friend)
        self.assertEqual(self.post(connection, '/Ask', self.ask(self.friends_record))[0], 200)
        self.assertEqual(len(self.asked), 1)
        self.assertEqual(self.upload(connection), 200)
        self.assertEqual(os.listdir(self.temp.name), ['a.txt'])

    def test_a_stranger_is_refused_before_anyone_is_asked(self):
        connection = self.connect(self.friend)
        self.assertEqual(self.post(connection, '/Ask', self.ask(self.strangers_record))[0], 403)
        self.assertEqual(self.asked, [])

    def test_a_known_senders_record_from_another_key_is_refused(self):
        connection = self.connect(self.impostor)
        self.assertEqual(self.post(connection, '/Ask', self.ask(self.friends_record))[0], 403)
        self.assertEqual(self.asked, [])

    def test_a_sender_with_no_certificate_is_refused(self):
        connection = self.connect()
        self.assertEqual(self.post(connection, '/Ask', self.ask(self.friends_record))[0], 403)

    def test_an_upload_with_no_accepted_ask_is_refused(self):
        self.assertEqual(self.upload(self.connect(self.friend)), 403)
        self.assertEqual(os.listdir(self.temp.name), [])

    def test_a_link_from_a_stranger_is_not_stored(self):
        ask = {'TransferType': {'links': True}, 'Items': ['https://example.org/page'],
               'SenderRecordData': self.strangers_record}
        self.assertEqual(self.post(self.connect(self.friend), '/Ask', ask)[0], 403)
        self.assertEqual(os.listdir(self.temp.name), [])

    def test_an_identified_stranger_does_not_see_this_computer(self):
        status, body = self.post(self.connect(), '/Discover',
                                 {'SenderRecordData': self.strangers_record})
        self.assertEqual(status, receiver.DECLINED_STATUS)
        self.assertNotIn(b'Test PC', body)

    def test_a_known_sender_and_an_unidentified_one_see_it(self):
        for value in ({'SenderRecordData': self.friends_record}, {}):
            status, body = self.post(self.connect(), '/Discover', value)
            self.assertEqual(status, 200)
            self.assertEqual(plistlib.loads(body)['ReceiverComputerName'], 'Test PC')


if __name__ == '__main__':
    unittest.main()
