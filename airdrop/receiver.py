# SPDX-License-Identifier: MIT
# Adapted from bin/airdrop-serve.py in omdrop, Copyright (c) 2026 Brent Kearney.
"""AirDrop receiver for Windows: announces this computer on the AWDL adapter
and serves AirDrop's HTTPS endpoints (/Discover, /Ask, /Upload) there.

While it runs, any Apple device in range that is set to send to everyone can
put files into the download folder. Nothing received is ever opened.
"""
import argparse
import errno
import json
import logging
import os
import plistlib
import re
import secrets
import socket
import ssl
import struct
import sys
import tempfile
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import identity
import mdns
import netif
import transfer

SERVICE_TYPE = '_airdrop._tcp.local'
DEFAULT_INTERFACE = 'awdl0'
DEFAULT_PORT = 8771
# macOS's own value. A sender drops receivers that advertise neither
# pipelining (0x04) nor mixed types (0x08).
DEFAULT_FLAGS = 0x3fb
DEFAULT_MODEL = 'MacBookPro18,3'
DEFAULT_MAX_RECEIVE_PERCENT = 30
MODEL_RE = re.compile(r'^[A-Za-z]+[0-9]+,[0-9]+$')
MAX_NAME_CHARS = 63
INTERFACE_WAIT_SECONDS = 20
BIND_ATTEMPTS = 5
BIND_PAUSE_SECONDS = 2
# Winsock's struct linger: enabled, zero seconds, which closes with a reset.
LINGER_RESET = struct.pack('HH', 1, 0)
# Version 1 with no codecs listed makes the sender convert to legacy formats
# (JPEG, not HEIF). The key has to be present in both answers.
MEDIA_CAPABILITIES = json.dumps({'Version': 1}).encode()


class HandshakeFailed(Exception):
    """A TLS handshake that was already logged; usually a sender leaving."""


class Receiver(ThreadingHTTPServer):
    """Threaded because a sender opens several connections at once; a single
    thread would hold /Discover behind a stalled one."""

    address_family = socket.AF_INET6
    daemon_threads = True
    allow_reuse_address = False

    def __init__(self, port, settings):
        self.settings = settings
        self.live = set()
        super().__init__(('::', port), Handler)

    def verify_request(self, request, client_address):
        # The socket listens on every interface; AirDrop only arrives on one.
        return client_address[3] == self.settings.interface_index

    def process_request(self, request, client_address):
        logging.info('connection from [%s]:%s', client_address[0], client_address[1])
        self.live.add(request)
        super().process_request(request, client_address)

    def shutdown_request(self, request):
        self.live.discard(request)
        super().shutdown_request(request)

    def reset_sessions(self):
        """Close what senders left open with a reset. A phone that has left
        the link never acknowledges a normal close, which would keep the port
        held for minutes."""
        for sock in list(self.live):
            try:
                sock.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, LINGER_RESET)
                sock.close()
            except OSError:
                pass
            self.live.discard(sock)

    def handle_error(self, request, client_address):
        # A sender ends its keep-alive session with a reset once it is done.
        if isinstance(sys.exc_info()[1], (ConnectionResetError, HandshakeFailed)):
            return
        super().handle_error(request, client_address)


class Handler(BaseHTTPRequestHandler):
    """/Discover and /Ask arrive chunked with no Content-Length, and /Upload
    as application/x-dvzip. Ask and Upload share one TLS connection, so each
    body is read from rfile exactly once and in place."""

    protocol_version = 'HTTP/1.1'

    def setup(self):
        self.request.settimeout(transfer.READ_IDLE_TIMEOUT_SECONDS)
        # The handshake runs on this connection's own thread: done in accept,
        # one sender dropping off mid-handshake would block every later one.
        try:
            self.request.do_handshake()
        except OSError as error:
            logging.info('TLS handshake with [%s]:%s failed: %s',
                         self.client_address[0], self.client_address[1], error)
            raise HandshakeFailed from error
        super().setup()

    def handle_expect_100(self):
        # An upload is acknowledged only once its framing and size are checked.
        if self.path == '/Upload':
            return True
        return super().handle_expect_100()

    def reject(self, status, reason):
        logging.warning('%s rejected: %s', self.path, reason)
        self.send_response(status)
        self.send_header('Content-Length', '0')
        self.send_header('Connection', 'close')
        self.end_headers()
        self.close_connection = True

    def answer(self, body):
        self.send_response(200)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_HEAD(self):
        self.send_response(200)
        self.send_header('Content-Length', '0')
        self.end_headers()

    def do_GET(self):
        self.answer(b'\n')

    def do_POST(self):
        if self.path == '/Upload':
            self.handle_upload()
            return
        if self.path not in ('/Discover', '/Ask'):
            self.reject(400, 'unknown endpoint')
            return
        try:
            body = transfer.read_small_body(self.rfile, self.headers)
        except socket.timeout:
            self.reject(408, 'request body read timed out')
            return
        except transfer.RequestBodyError as error:
            self.reject(error.status, str(error))
            return
        if self.path == '/Discover':
            self.answer(self.identity_plist())
        else:
            self.handle_ask(body)

    def identity_plist(self):
        settings = self.server.settings
        return plistlib.dumps({
            'ReceiverMediaCapabilities': MEDIA_CAPABILITIES,
            'ReceiverComputerName': settings.name,
            'ReceiverModelName': settings.model,
        }, fmt=plistlib.FMT_BINARY)

    def handle_ask(self, body):
        try:
            ask = plistlib.loads(body)
        except Exception as error:
            logging.error('ask: undecodable %d-byte body: %r', len(body), error)
            ask = {}
        files = ask.get('Files') or []
        items = ask.get('Items') or []
        kinds = ask.get('TransferType') or {}
        logging.info('ask from %s (%s): type %s, %d item(s): %s',
                     ask.get('SenderComputerName'), ask.get('SenderModelName'),
                     ','.join(kinds) or '?', len(files),
                     '; '.join(f"{f.get('FileName')} {f.get('FileType')} {f.get('FileSize')}B"
                               for f in files) or '-')
        if len(items) > transfer.MAX_ARCHIVE_MEMBERS:
            self.reject(413, f'Ask exceeds {transfer.MAX_ARCHIVE_MEMBERS}-item limit')
            return
        # A link transfer has no /Upload: the URL is in Items, so accepting
        # the Ask is the transfer. It is stored and never opened.
        if 'links' in kinds:
            for item in items:
                if not transfer.is_web_link(item):
                    logging.warning('ask: ignoring non-web item %.80r', item)
                    continue
                stored = transfer.store_link(item, self.server.settings.dest)
                logging.info('link stored as %s', stored)
        self.answer(self.identity_plist())

    def receive_archive(self, raw, budget, plan, deadline, tid):
        """Read the upload into `raw` and store its files. Returns their
        names and whether the body ended properly.

        An iPhone sending a video or a Live Photo sends every chunk and then
        waits, without the zero-length chunk that ends the body. So once the
        archive is seen to be complete the wait for that chunk is cut short,
        and when the body stalls between two chunks, what arrived is accepted
        if it is a complete archive, down to its trailer."""
        dest = self.server.settings.dest

        def expect_little_more():
            deadline.idle_seconds = transfer.END_MARKER_WAIT_SECONDS
            self.request.settimeout(deadline.idle_seconds)

        try:
            wire_bytes = transfer.read_request_body(
                self.rfile, self.headers,
                transfer.EndWatchingFile(raw, expect_little_more),
                budget.byte_limit, disk=dest, plan=plan, deadline=deadline)
        except socket.timeout as stall:
            if not deadline.between_chunks:
                raise
            try:
                written = transfer.store_upload(raw, dest, tid, budget)
            except transfer.UploadLimitError:
                raise
            except ValueError:
                raise stall from None
            logging.info('upload %s: %d bytes received, a complete archive '
                         'with no end marker', tid, deadline.transferred)
            return written, False
        logging.info('upload %s: %d bytes received', tid, wire_bytes)
        return transfer.store_upload(raw, dest, tid, budget), True

    def handle_upload(self):
        settings = self.server.settings
        ctype = self.headers.get('Content-Type', '').split(';')[0].strip().lower()
        if ctype != 'application/x-dvzip':
            self.reject(415, f'unsupported Content-Type {ctype!r}')
            return
        if not transfer.UPLOAD_SLOTS.acquire(blocking=False):
            self.reject(503, 'too many uploads in progress')
            return
        tid = transfer.safe_name(self.headers.get('TransferID', ''), 'upload')
        deadline = transfer.ReadDeadline()
        plan = None
        try:
            budget = transfer.ReceiveBudget(settings.dest, settings.max_receive_percent)
            plan = transfer.body_plan(self.headers, budget.byte_limit)
            logging.info('upload %s: sender announces %s bytes; %d%% of %d '
                         'free bytes allows %d', tid,
                         self.headers.get('TotalBytes', 'no number of'),
                         budget.percent, budget.free_bytes, budget.byte_limit)
            if self.headers.get('Expect', '').lower() == '100-continue':
                self.send_response_only(100)
                self.end_headers()
            with tempfile.TemporaryFile(dir=settings.dest) as raw:
                written, ended = self.receive_archive(raw, budget, plan,
                                                      deadline, tid)
            logging.info('upload %s: stored %s', tid, ', '.join(written))
        except socket.timeout as error:
            expected = plan[1] if plan else None
            self.reject(408, f'upload {tid}: {deadline.stall_report(error, expected)}')
            return
        except (transfer.BodyTooLarge, transfer.UploadLimitError) as error:
            self.reject(413, str(error))
            return
        except transfer.RequestBodyError as error:
            self.reject(error.status, str(error))
            return
        except OSError as error:
            self.reject(507, f'storage failed: {error}')
            return
        except Exception as error:
            self.reject(422, f'upload is not a valid bounded DVZip archive: {error}')
            return
        finally:
            transfer.UPLOAD_SLOTS.release()
            self.request.settimeout(transfer.READ_IDLE_TIMEOUT_SECONDS)
        self.send_response(200)
        self.send_header('Content-Length', '0')
        if not ended:
            # The rest of this request never came, so the connection cannot
            # carry another one.
            self.send_header('Connection', 'close')
            self.close_connection = True
        self.end_headers()

    def log_message(self, fmt, *args):
        logging.info('%s %s', self.client_address[0], fmt % args)


def valid_name(name):
    if not name or transfer.UNSAFE_CHAR.search(name) or len(name) > MAX_NAME_CHARS:
        raise argparse.ArgumentTypeError(
            f'1-{MAX_NAME_CHARS} characters, no path separators or control characters')
    return name


def valid_model(model):
    if not MODEL_RE.match(model):
        raise argparse.ArgumentTypeError('an Apple model identifier such as MacBookPro18,3')
    return model


def valid_percent(text):
    value = int(text)
    if not 1 <= value <= 90:
        raise argparse.ArgumentTypeError('an integer from 1 to 90')
    return value


def valid_dir(path):
    dest = os.path.realpath(os.path.expanduser(path))
    if not os.path.isdir(dest):
        raise argparse.ArgumentTypeError(f'{dest} is not an existing directory')
    return dest


def parse_args(argv):
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    parser.add_argument('--name', type=valid_name,
                        default=socket.gethostname().split('.')[0],
                        help='the name senders see (default: the computer name)')
    parser.add_argument('--model', type=valid_model, default=DEFAULT_MODEL,
                        help='Apple model identifier; picks the icon senders draw')
    parser.add_argument('--outdir', dest='dest',
                        default=os.path.join('~', 'Downloads'),
                        help='where received files go (default: Downloads)')
    parser.add_argument('--max-receive-percent', type=valid_percent,
                        default=DEFAULT_MAX_RECEIVE_PERCENT,
                        help='largest transfer, as a percentage of free disk space')
    parser.add_argument('--iface', default=DEFAULT_INTERFACE)
    parser.add_argument('--port', type=int, default=DEFAULT_PORT)
    parser.add_argument('--flags', type=lambda text: int(text, 0), default=DEFAULT_FLAGS,
                        help='receiver capability bits to advertise')
    parser.add_argument('--keys', default=identity.default_keys_dir(),
                        help='directory holding the TLS certificate and key')
    args = parser.parse_args(argv)
    try:
        args.dest = valid_dir(args.dest)
    except argparse.ArgumentTypeError as error:
        parser.error(f'--outdir: {error}')
    return args


def wait_for_interface(alias):
    """Index and address of the AWDL adapter, which exists only while awdld
    runs and gets its address a moment after it appears."""
    deadline = time.monotonic() + INTERFACE_WAIT_SECONDS
    while True:
        index = netif.interface_index(alias)
        address = netif.link_local_address(index) if index else None
        if address:
            return index, address
        if time.monotonic() >= deadline:
            raise SystemExit(f'interface {alias} is not up: start scripts\\awdl-run.ps1 first')
        time.sleep(0.5)


def bind_receiver(port, settings):
    """A port still held by the previous run is waited out, never side-stepped:
    the announcement names this port only."""
    for attempt in range(1, BIND_ATTEMPTS + 1):
        try:
            return Receiver(port, settings)
        except OSError as error:
            if error.errno != errno.EADDRINUSE:
                raise
        logging.warning('port %d still held; retry %d of %d', port, attempt, BIND_ATTEMPTS)
        time.sleep(BIND_PAUSE_SECONDS)
    raise SystemExit(f'port {port} is held by another program')


def tls_context(keys_dir):
    certificate, key = identity.ensure_self_signed(keys_dir)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(certificate, key)
    # Senders present self-signed certificates, so none is asked for.
    context.verify_mode = ssl.CERT_NONE
    return context


def main(argv=None):
    settings = parse_args(argv)
    logging.basicConfig(level=logging.INFO, stream=sys.stdout,
                        format='%(asctime)s %(levelname)s %(message)s')
    try:
        context = tls_context(settings.keys)
    except identity.IdentityError as error:
        raise SystemExit(str(error))
    settings.interface_index, address = wait_for_interface(settings.iface)

    server = bind_receiver(settings.port, settings)
    server.socket = context.wrap_socket(server.socket, server_side=True,
                                        do_handshake_on_connect=False)
    service_id = secrets.token_hex(6)
    announcer = mdns.Announcer(
        mdns.Service(SERVICE_TYPE, service_id, f'{service_id}.local', address,
                     settings.port, f'flags={settings.flags}'),
        settings.interface_index)
    logging.info('receiving as %s (%s) on [%s%%%d]:%d into %s', settings.name,
                 settings.model, address, settings.interface_index, settings.port,
                 settings.dest)
    announcer.start()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        announcer.stop()
        server.reset_sessions()
        server.server_close()


if __name__ == '__main__':
    main()
