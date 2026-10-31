# SPDX-License-Identifier: MIT
# Adapted from bin/airdrop-serve.py in omdrop, Copyright (c) 2026 Brent Kearney.
"""AirDrop receiver for Windows: announces this computer on the AWDL adapter
and serves /Discover, /Ask and /Upload there. Nothing received is opened."""
import argparse
import collections
import contacts
import errno
import json
import logging
import os
import plistlib
import safeplist
import re
import secrets
import select
import socket
import ssl
import struct
import sys
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import identity
import logline
import mdns
import netif
import original_media
import redact
import transfer

SERVICE_TYPE = '_airdrop._tcp.local'
DEFAULT_INTERFACE = 'awdl0'
DEFAULT_PORT = 8771
# macOS's own value. A sender drops receivers that advertise neither
# pipelining (0x04) nor mixed types (0x08).
DEFAULT_FLAGS = 0x3fb
DEFAULT_MODEL = 'MacBookPro18,3'
# What current Apple senders upload, and the gzip archive older ones and
# OpenDrop do. The body says which framing it has.
UPLOAD_TYPES = ('application/x-dvzip', 'application/x-cpio')
# What a sender gets when the owner says no. Not checked against an iPhone.
DECLINED_STATUS = 401
DEFAULT_MAX_RECEIVE_PERCENT = 30
MODEL_RE = re.compile(r'^[A-Za-z]+[0-9]+,[0-9]+$')
MAX_NAME_CHARS = 63
PATH_SEPARATOR = re.compile(r'[\\/]')
INTERFACE_WAIT_SECONDS = 20
BIND_ATTEMPTS = 5
BIND_PAUSE_SECONDS = 2
# Winsock's struct linger: enabled, zero seconds, which closes with a reset.
LINGER_RESET = struct.pack('HH', 1, 0)
# Version 1 with no codecs listed makes the sender convert to legacy formats
# (JPEG, not HEIF). The key has to be present in both answers.
MEDIA_CAPABILITIES = json.dumps({'Version': 1}).encode()


# How many Asks are heard in a window: from one address, and from all of them
# together, since a device can change its address. A sender asks once a
# transfer, so these leave room for a person and none for a flood of requests
# on the owner's screen.
ASK_WINDOW_SECONDS = 30
ASKS_PER_SENDER = 5
ASKS_IN_ALL = 20
TOO_MANY_STATUS = 429


class AskLimiter:
    """Counts recent Asks, by address and in all."""

    def __init__(self, per_sender=ASKS_PER_SENDER, in_all=ASKS_IN_ALL,
                 window=ASK_WINDOW_SECONDS, clock=time.monotonic):
        self.per_sender = per_sender
        self.in_all = in_all
        self.window = window
        self.clock = clock
        self._lock = threading.Lock()
        self._all = collections.deque()
        self._by_sender = {}

    def allow(self, address):
        """Whether an Ask from address is heard, counting it if it is."""
        now = self.clock()
        with self._lock:
            while self._all and self._all[0][0] <= now - self.window:
                _, old = self._all.popleft()
                times = self._by_sender[old]
                times.popleft()
                if not times:
                    del self._by_sender[old]
            mine = self._by_sender.get(address, ())
            if len(self._all) >= self.in_all or len(mine) >= self.per_sender:
                return False
            self._all.append((now, address))
            self._by_sender.setdefault(address, collections.deque()).append(now)
            return True


class HandshakeFailed(Exception):
    """A TLS handshake that was already logged; usually a sender leaving."""


class Watcher:
    """What a program that shows transfers is told, and asked. This one takes
    everything and says nothing."""

    def asked(self, ask, address, left):
        """A sender wants to send what `ask` lists. Returns something true to
        accept, which the upload's calls are then given, or None to decline.
        `left()` turns true once the sender has stopped waiting."""
        return True

    def upload_started(self, accepted, tid, total):
        """An upload begins, on a connection whose ask got `accepted` (None
        without one). Returns what the calls below are given, or None to
        refuse the upload. `total` is the sender's byte count, if it gave one."""
        return True

    def upload_progress(self, token, done):
        pass

    def stored(self, token, names):
        pass

    def upload_failed(self, token, reason):
        pass


class Receiver(ThreadingHTTPServer):
    """Threaded because a sender opens several connections at once; a single
    thread would hold /Discover behind a stalled one."""

    address_family = socket.AF_INET6
    daemon_threads = True
    allow_reuse_address = False

    def __init__(self, port, settings, watcher=None):
        self.settings = settings
        self.watcher = watcher or Watcher()
        self.live = set()
        self.asks = AskLimiter()
        super().__init__(('::', port), Handler)

    @property
    def contacts_only(self):
        """Whether only senders on the known-senders list are accepted."""
        return bool(getattr(self.settings, 'contacts_only', False))

    @property
    def known(self):
        """The known senders' hashed identifiers."""
        return contacts.known_hashes(getattr(self.settings, 'known_senders', ()))

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
        """Reset what senders left open: a phone that has left the link never
        acknowledges a normal close, and the port would stay held for minutes."""
        for sock in list(self.live):
            try:
                sock.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, LINGER_RESET)
                sock.close()
            except OSError:
                pass
            self.live.discard(sock)

    def handle_error(self, request, client_address):
        # A sender ends its keep-alive session with a reset once it is done,
        # and one that stopped waiting is gone before its answer is written.
        if isinstance(sys.exc_info()[1], (ConnectionError, ssl.SSLEOFError, HandshakeFailed)):
            return
        # Through the log rather than printed: an exception's text can carry
        # what the sender sent, and the log escapes it.
        logging.error('error serving [%s]:%s', client_address[0], client_address[1],
                      exc_info=True)


class Handler(BaseHTTPRequestHandler):
    """/Discover and /Ask arrive chunked, /Upload as application/x-dvzip. Ask and
    Upload share one connection, so each body is read once and in place."""

    protocol_version = 'HTTP/1.1'
    # What the watcher returned for the ask on this connection.
    accepted = None
    # Set once an Ask on this connection passed the Contacts Only check: an
    # Upload carries no record of its own, so only its Ask can vouch for it.
    known_sender = False

    def sender_left(self):
        """Whether the sender has closed this connection. Only asked while it
        is owed an answer and so has nothing to send."""
        if not select.select([self.request], [], [], 0)[0]:
            return False
        self.request.settimeout(0)
        try:
            return not self.rfile.peek(1)
        except (ssl.SSLWantReadError, BlockingIOError):
            return False
        except OSError:
            return True
        finally:
            self.request.settimeout(transfer.READ_IDLE_TIMEOUT_SECONDS)

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
        logging.debug('%s %s headers: %s', self.command, self.path, dict(self.headers.items()))
        if self.path == '/Upload':
            self.handle_upload()
            return
        if self.path not in ('/Discover', '/Ask'):
            self.reject(400, 'unknown endpoint')
            return
        try:
            body = transfer.read_small_body(self.rfile, self.headers)
        except TimeoutError:
            self.reject(408, 'request body read timed out')
            return
        except transfer.RequestBodyError as error:
            self.reject(error.status, str(error))
            return
        if self.path == '/Discover':
            self.handle_discover(body)
        else:
            self.handle_ask(body)

    def identity_plist(self):
        settings = self.server.settings
        return plistlib.dumps({
            'ReceiverMediaCapabilities': (
                original_media.CAPABILITIES
                if getattr(settings, 'original_media',
                           os.environ.get('WINAWDL_ORIGINAL_MEDIA') == '1')
                else MEDIA_CAPABILITIES),
            'ReceiverComputerName': settings.name,
            'ReceiverModelName': settings.model,
        }, fmt=plistlib.FMT_BINARY)

    def handle_discover(self, body):
        """Answer who this computer is, unless only known senders may send and
        this one has shown it is not one. Silence then, as far as a status
        can be: an error would tell a stranger that this computer is here."""
        if self.server.contacts_only:
            try:
                record = (safeplist.loads(body) or {}).get('SenderRecordData')
            except Exception:
                record = None
            outcome, detail = contacts.decide_discovery(
                record if isinstance(record, bytes) else None, self.server.known)
            if outcome != contacts.ACCEPT:
                logging.info('discovery withheld (contacts only): %s %s',
                             contacts.WHY[outcome], detail)
                self.reject(DECLINED_STATUS, 'not a known sender')
                return
        self.answer(self.identity_plist())

    def known_sender_check(self, ask):
        """In Contacts Only, whether the sender proved to be a known one; it
        is refused here if not. Every other mode lets it through."""
        if not self.server.contacts_only:
            return True
        try:
            peer_cert = self.connection.getpeercert(binary_form=True)
        except (AttributeError, ValueError):
            peer_cert = None
        record = ask.get('SenderRecordData')
        outcome, detail = contacts.decide(record if isinstance(record, bytes) else None,
                                          peer_cert, self.server.known)
        if outcome != contacts.ACCEPT:
            # No identifier is logged: a hash gives back the phone number.
            self.reject(403, f'contacts only: {contacts.WHY[outcome]} {detail}')
            return False
        logging.info('ask from a known sender')
        self.known_sender = True
        return True

    def handle_ask(self, body):
        # Counted before anything is done with it, malformed or not.
        if not self.server.asks.allow(self.client_address[0]):
            self.reject(TOO_MANY_STATUS, 'too many requests from this sender; try again soon')
            return
        try:
            ask = safeplist.loads(body)
        except Exception as error:
            logging.warning('ask: undecodable %d-byte body: %s',
                            len(body), type(error).__name__)
            self.reject(400, 'malformed or oversized Ask property list')
            return
        if not isinstance(ask, dict):
            self.reject(400, 'Ask must contain a dictionary')
            return
        files = ask.get('Files') or []
        items = ask.get('Items') or []
        if not isinstance(files, list) or not isinstance(items, list):
            self.reject(400, 'Ask contains invalid Files or Items')
            return
        # Before anything is built from them: the lines below grow with the
        # count, and no upload can hold more members than this.
        if max(len(files), len(items)) > transfer.MAX_ARCHIVE_MEMBERS:
            self.reject(413, f'Ask exceeds {transfer.MAX_ARCHIVE_MEMBERS}-item limit')
            return
        if any(not isinstance(f, dict) or not isinstance(f.get('FileName'), str)
               or not f['FileName'] for f in files):
            self.reject(400, 'Ask contains invalid Files metadata')
            return
        logging.debug('ask: %s', transfer.outline(ask))
        kinds = ask.get('TransferType') or {}
        if not isinstance(kinds, (dict, list)):
            kinds = {}
        kinds = [kind for kind in kinds if isinstance(kind, str)]
        # The watcher reads the Ask too.
        ask['TransferType'] = kinds
        sender = ask.get('SenderComputerName')
        names = [str(f.get('FileName')) for f in files]
        if self.server.settings.redact:
            # A sender may give a whole path as a file's name.
            sender = 'a sender'
            names = [PATH_SEPARATOR.split(name)[-1] for name in names]
        logging.info('ask from %s (%s): type %s, %d item(s): %s',
                     sender, ask.get('SenderModelName'),
                     ','.join(kinds) or '?', len(files),
                     '; '.join(f"{name} {f.get('FileType')} {f.get('FileSize')}B"
                               for name, f in zip(names, files, strict=True)) or '-')
        # Before the owner is asked or anything is stored.
        if not self.known_sender_check(ask):
            return
        watcher = self.server.watcher
        self.accepted = watcher.asked(ask, self.client_address[0], self.sender_left)
        if self.accepted is None:
            self.reject(DECLINED_STATUS, 'declined here')
            return
        # Bind consent to the precise filenames announced on this connection.
        # Unsolicited uploads in explicit take-all mode remain unrestricted.
        self.approved_names = [f['FileName'] for f in files]
        # A link transfer has no /Upload: the URL is in Items, so accepting
        # the Ask is the transfer. It is stored and never opened.
        if 'links' in kinds:
            urls = []
            for item in items:
                if not transfer.is_web_link(item):
                    logging.warning('ask: ignoring item that is not a storable web link %.80r',
                                    item)
                    continue
                urls.append(item)
            links = []
            if urls:
                settings = self.server.settings
                try:
                    links = transfer.store_links(urls, settings.dest,
                                                 settings.max_receive_percent)
                except transfer.UploadLimitError as error:
                    self.refuse_links(f'links: {error}')
                    return
                except OSError as error:
                    self.refuse_links(f'links: storage failed: {error}')
                    return
            for stored in links:
                logging.info('link stored as %s', stored)
            watcher.stored(self.accepted, links)
        self.answer(self.identity_plist())

    def refuse_links(self, reason):
        self.server.watcher.upload_failed(self.accepted, reason)
        self.accepted = None
        self.approved_names = None
        self.reject(507, reason)

    def receive_archive(self, raw, budget, plan, deadline, tid, on_progress,
                        approved_names=None):
        """Store the upload's files; returns their names and whether the body
        ended. An iPhone never ends the body of a video or a Live Photo."""
        dest = self.server.settings.dest

        def expect_little_more():
            deadline.idle_seconds = transfer.END_MARKER_WAIT_SECONDS
            self.request.settimeout(deadline.idle_seconds)

        try:
            wire_bytes = transfer.read_request_body(
                self.rfile, self.headers,
                transfer.EndWatchingFile(raw, expect_little_more, on_progress),
                budget.byte_limit, disk=dest, plan=plan, deadline=deadline)
        except TimeoutError as stall:
            if not deadline.between_chunks:
                raise
            try:
                written = transfer.store_upload(raw, dest, tid, budget,
                                                approved=approved_names)
            except transfer.UploadLimitError:
                raise
            except ValueError:
                raise stall from None
            logging.info('upload %s: %d bytes received, a complete archive '
                         'with no end marker', tid, deadline.transferred)
            return written, False
        logging.info('upload %s: %d bytes received', tid, wire_bytes)
        return transfer.store_upload(raw, dest, tid, budget,
                                     approved=approved_names), True

    def handle_upload(self):
        settings = self.server.settings
        if self.server.contacts_only and not self.known_sender:
            self.reject(403, 'contacts only: an upload no accepted Ask vouches for')
            return
        ctype = self.headers.get('Content-Type', '').split(';')[0].strip().lower()
        if ctype not in UPLOAD_TYPES:
            self.reject(415, f'unsupported Content-Type {ctype!r}')
            return
        if not transfer.UPLOAD_SLOTS.acquire(blocking=False):
            self.reject(503, 'too many uploads in progress')
            return
        tid = transfer.safe_name(self.headers.get('TransferID', ''), 'upload')
        watcher = self.server.watcher
        approved_names = getattr(self, 'approved_names', None)
        self.approved_names = None
        token = watcher.upload_started(self.accepted, tid, announced_bytes(self.headers))
        self.accepted = None
        if token is None:
            transfer.UPLOAD_SLOTS.release()
            self.reject(DECLINED_STATUS, f'upload {tid} follows no accepted ask')
            return
        deadline = transfer.ReadDeadline()
        plan = None
        refused = None
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
                written, ended = self.receive_archive(
                    raw, budget, plan, deadline, tid,
                    lambda done: watcher.upload_progress(token, done),
                    approved_names=approved_names)
            logging.info('upload %s: stored %s', tid, ', '.join(written))
        except TimeoutError as error:
            expected = plan[1] if plan else None
            refused = 408, f'upload {tid}: {deadline.stall_report(error, expected)}'
        except (transfer.BodyTooLarge, transfer.UploadLimitError) as error:
            refused = 413, str(error)
        except (ConnectionError, ssl.SSLError, transfer.BodyEnded) as error:
            # Socket errors are OSErrors too, but the disk is fine: the sender
            # went away, and there is nobody to answer.
            reason = (f'upload {tid}: the sender left after {deadline.transferred} '
                      f'bytes: {error}')
            logging.info('%s', reason)
            watcher.upload_failed(token, reason)
            self.close_connection = True
            return
        except transfer.RequestBodyError as error:
            refused = error.status, str(error)
        except OSError as error:
            refused = 507, f'storage failed: {error}'
        except Exception as error:
            refused = 422, f'upload is not a valid bounded DVZip archive: {error}'
        finally:
            transfer.UPLOAD_SLOTS.release()
            self.request.settimeout(transfer.READ_IDLE_TIMEOUT_SECONDS)
        if refused:
            watcher.upload_failed(token, refused[1])
            self.reject(*refused)
            return
        watcher.stored(token, written)
        self.send_response(200)
        self.send_header('Content-Length', '0')
        # The upload is complete: close promptly so iOS shows "Sent", rather
        # than leaving a reusable HTTP connection idle until timeout.
        # Missing end markers already require close; normal uploads can too.
        self.send_header('Connection', 'close')
        self.close_connection = True
        self.end_headers()

    def log_message(self, fmt, *args):
        logging.info('%s %s', self.client_address[0], fmt % args)


def announced_bytes(headers):
    """The size a sender gives for what it uploads, or None without a usable one."""
    try:
        total = int(headers.get('TotalBytes', ''))
    except ValueError:
        return None
    return total if total > 0 else None


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
    parser = argparse.ArgumentParser(description=__doc__)
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
    parser.add_argument('--trace', action='store_true',
                        help='also print each request\'s headers, what a sender asks '
                             'and the members of its archive')
    parser.add_argument('--contacts-only', action='store_true',
                        help='accept only senders on the known-senders list, as Apple '
                             'proves them; others do not see this computer')
    parser.add_argument('--known-senders', dest='known_senders_file', metavar='FILE',
                        help='the Apple ID emails and phone numbers accepted from, '
                             'one a line')
    parser.add_argument('--redact', action='store_true',
                        help='print no names, addresses or account folders, for a '
                             'recording or a shared screen')
    args = parser.parse_args(argv)
    try:
        args.dest = valid_dir(args.dest)
    except argparse.ArgumentTypeError as error:
        parser.error(f'--outdir: {error}')
    args.known_senders = []
    if args.known_senders_file:
        try:
            args.known_senders = contacts.read_known_senders(args.known_senders_file)
        except OSError as error:
            parser.error(f'--known-senders: {error}')
    if args.contacts_only and not args.known_senders:
        parser.error('--contacts-only needs --known-senders with someone on it')
    return args


def wait_for_interface(alias):
    """Index and address of the AWDL adapter, which exists only while awdld
    runs and gets its address a moment after it appears."""
    found = netif.wait_for_interface(alias, INTERFACE_WAIT_SECONDS)
    if not found:
        raise SystemExit(f'interface {alias} is not up: start scripts\\awdl-run.ps1 first')
    return found


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


def tls_context(keys_dir, contacts_only=False):
    """The receiver's TLS side. Visible to everyone, no certificate is asked
    for: nothing would read it, and one that did not chain to Apple would
    fail the handshake. With Contacts Only one is asked for, as Apple's
    client certificate is half of what proves who a sender is. Optional
    means it may be absent, not that a bad one is let through, so Apple's
    intermediates are trusted beside its root: a sender may send its leaf
    alone."""
    certificate, key = identity.ensure_self_signed(keys_dir)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(certificate, key)
    context.verify_mode = ssl.CERT_NONE
    if contacts_only:
        context.load_verify_locations(cafile=contacts.APPLE_ROOT_CA)
        for intermediate in contacts.apple_intermediates():
            context.load_verify_locations(cafile=intermediate)
        context.verify_mode = ssl.CERT_OPTIONAL
    return context


def main(argv=None):
    settings = parse_args(argv)
    redactor = redact.Redactor(redact.local_words())
    if settings.redact:
        # Both streams, so that an error report is covered as well as the log.
        sys.stdout = redact.Stream(sys.stdout, redactor.hide)
        sys.stderr = redact.Stream(sys.stderr, redactor.hide)
    logline.configure(logging.DEBUG if settings.trace else logging.INFO, sys.stdout)
    try:
        context = tls_context(settings.keys, settings.contacts_only)
    except identity.IdentityError as error:
        raise SystemExit(str(error)) from None
    settings.interface_index, address = wait_for_interface(settings.iface)
    redactor.name(address, 'own-address')

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
