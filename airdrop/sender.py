# SPDX-License-Identifier: MIT
"""AirDrop sender for Windows: wakes nearby receivers over Bluetooth, finds
them on the AWDL adapter and uploads files to one of them. The receiver has
to be visible to everyone."""
import argparse
import http.client
import logging
import os
import plistlib
import secrets
import socket
import ssl
import subprocess
import sys
import time
import uuid
from typing import NamedTuple

import cpio
import identity
import mdns
import netif
import transfer

SERVICE_TYPE = '_airdrop._tcp.local'
DEFAULT_INTERFACE = 'awdl0'
DEFAULT_MODEL = 'MacBookPro18,3'
INTERFACE_WAIT_SECONDS = 20
DEFAULT_SEARCH_SECONDS = 30
# Once one receiver has answered, how long the others get to show up.
SETTLE_SECONDS = 3
CONNECT_SECONDS = 10
CONNECT_ATTEMPTS = 3
# The receiver's owner has this long to accept.
ASK_SECONDS = 90
UPLOAD_IDLE_SECONDS = 30
IO_BLOCK_BYTES = 64 * 1024
PROGRESS_SECONDS = 2
MIB = 1024 * 1024
HEADERS = {'User-Agent': 'AirDrop/1.0', 'Connection': 'keep-alive'}
# A receiver picks the app that takes a file by this type.
UNIFORM_TYPES = {
    '.jpg': 'public.jpeg', '.jpeg': 'public.jpeg', '.png': 'public.png',
    '.heic': 'public.heic', '.gif': 'com.compuserve.gif',
    '.mov': 'com.apple.quicktime-movie', '.mp4': 'public.mpeg-4',
    '.m4v': 'com.apple.m4v-video', '.mp3': 'public.mp3', '.pdf': 'com.adobe.pdf',
    '.txt': 'public.plain-text', '.zip': 'public.zip-archive',
}
DEFAULT_UNIFORM_TYPE = 'public.data'
BEACON_SCRIPT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                             'scripts', 'airdrop-beacon.ps1')
BEACON_READY = 'advertising'


class Unreachable(Exception):
    pass


class Outgoing(NamedTuple):
    path: str
    name: str
    size: int
    mtime: float


class Connection(http.client.HTTPConnection):
    """HTTPS to a link-local address, which is only complete with the
    interface it is on."""

    def __init__(self, address, port, scope, context, timeout):
        super().__init__(address, port, timeout=timeout)
        self.scope = scope
        self.context = context

    def connect(self):
        sock = socket.socket(socket.AF_INET6, socket.SOCK_STREAM)
        try:
            sock.settimeout(self.timeout)
            sock.connect((self.host, self.port, 0, self.scope))
            self.sock = self.context.wrap_socket(sock)
        except OSError:
            sock.close()
            raise


class Beacon:
    """The Bluetooth advertisement that makes Apple devices nearby turn AWDL
    on and announce AirDrop. Windows PowerShell sends it: it reaches the
    Bluetooth API with nothing installed."""

    def __init__(self, script=BEACON_SCRIPT):
        self.script = script
        self.process = None

    def __enter__(self):
        shell = os.path.join(os.environ.get('SYSTEMROOT', r'C:\Windows'), 'System32',
                             'WindowsPowerShell', 'v1.0', 'powershell.exe')
        try:
            self.process = subprocess.Popen(
                [shell, '-NoProfile', '-NonInteractive', '-File', self.script],
                stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT, text=True)
            said = self.process.stdout.readline().strip()
        except OSError as error:
            said = str(error)
        if said == BEACON_READY:
            logging.info('Bluetooth beacon on')
        else:
            logging.warning('no Bluetooth beacon (%s): a receiver shows up only while '
                            'its own share sheet is open', said or 'no answer')
        return self

    def __exit__(self, *_):
        if self.process:
            self.process.terminate()
            self.process.wait()


class Progress:
    def __init__(self, total):
        self.total = total
        self.done = 0
        self.started = time.monotonic()
        self._last = self.started

    def note(self, size):
        self.done += size
        now = time.monotonic()
        if now - self._last >= PROGRESS_SECONDS:
            self._last = now
            logging.info('sent %d%% at %.1f MB/s', self.done * 100 // max(self.total, 1),
                         self.rate())

    def rate(self):
        return self.done / MIB / max(time.monotonic() - self.started, 0.001)


def client_context(keys_dir):
    certificate, key = identity.ensure_self_signed(keys_dir)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    # A receiver visible to everyone presents a certificate nothing vouches
    # for, and takes ours the same way.
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    context.load_cert_chain(certificate, key)
    return context


def outgoing(paths):
    """The files to send, refusing what an archive cannot hold apart."""
    files = []
    for path in paths:
        if not os.path.isfile(path):
            raise SystemExit(f'{path} is not a file')
        status = os.stat(path)
        files.append(Outgoing(path, os.path.basename(path), status.st_size, status.st_mtime))
    names = [file.name.lower() for file in files]
    if len(set(names)) != len(names):
        raise SystemExit('two of the files have the same name')
    return files


def post_plist(connection, path, value):
    connection.request('POST', path, body=plistlib.dumps(value, fmt=plistlib.FMT_BINARY),
                       headers={**HEADERS, 'Content-Type': 'application/octet-stream'})
    response = connection.getresponse()
    return response.status, response.read()


def outline(body):
    """What a plist answer holds, for the trace."""
    try:
        return transfer.outline(plistlib.loads(body))
    except ValueError:
        return f'<{len(body)} bytes that are no plist>'


def receiver_name(address, port, scope, context):
    """What the receiver here calls itself; None from one that keeps its name
    for its contacts."""
    connection = Connection(address, port, scope, context, CONNECT_SECONDS)
    try:
        status, body = post_plist(connection, '/Discover', {})
        return plistlib.loads(body).get('ReceiverComputerName') if status == 200 else None
    except (OSError, http.client.HTTPException) as error:
        raise Unreachable(str(error) or type(error).__name__) from None
    except ValueError:
        return None
    finally:
        connection.close()


def find_receivers(browser, scope, context, wanted, seconds):
    """{name: (address, port)} of the receivers that answer with a name. Stops
    at the first whose name contains `wanted`, if that is given."""
    named = {}
    attempts = {}
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        for instance, (address, port) in browser.poll().items():
            if attempts.get(instance, 0) >= CONNECT_ATTEMPTS:
                continue
            try:
                name = receiver_name(address, port, scope, context)
            except Unreachable as error:
                attempts[instance] = attempts.get(instance, 0) + 1
                logging.info('a receiver at %s does not answer yet: %s', address, error)
                continue
            attempts[instance] = CONNECT_ATTEMPTS
            if name is None:
                logging.info('a receiver at %s is visible to its contacts only', address)
                continue
            logging.info('found %s', name)
            named[name] = (address, port)
            if wanted and wanted.lower() in name.lower():
                return {name: (address, port)}
            deadline = min(deadline, time.monotonic() + SETTLE_SECONDS)
    return named


def choose(named, wanted):
    if wanted:
        named = {name: where for name, where in named.items()
                 if wanted.lower() in name.lower()}
    if not named:
        raise SystemExit('no receiver found: on the iPhone set AirDrop to '
                         '"Everyone for 10 Minutes" and keep its screen on')
    if len(named) > 1:
        raise SystemExit('several receivers: pick one with --to: ' + ', '.join(sorted(named)))
    return next(iter(named.items()))


def ask_body(sender_name, files, transfer_id):
    """The request to send, with the keys an iPhone's own has, less its Apple
    identity and its preview picture."""
    return {
        'TransferID': {'id': transfer_id},
        'TransferType': {'files': {}},
        'SenderID': secrets.token_hex(6),
        'BundleID': 'com.apple.finder',
        'SenderComputerName': sender_name,
        'SenderModelName': DEFAULT_MODEL,
        'Items': [],
        'Files': [{
            'FileName': file.name,
            'FileType': UNIFORM_TYPES.get(os.path.splitext(file.name)[1].lower(),
                                          DEFAULT_UNIFORM_TYPE),
            'FileSize': file.size,
            'FileBomPath': f'./{file.name}',
            'FileIsDirectory': False,
            'ShouldConvertMediaFormats': False,
        } for file in files],
        'ConvertMediaFormats': False,
    }


def _blocks(path):
    with open(path, 'rb') as source:
        while block := source.read(IO_BLOCK_BYTES):
            yield block


def _members(files):
    # An iPhone's archive starts with the folder its files are named in.
    yield '.', cpio.DIRECTORY_MODE, 0, time.time(), ()
    for file in files:
        yield f'./{file.name}', cpio.FILE_MODE, file.size, file.mtime, _blocks(file.path)


def _archive(files, progress):
    for piece in cpio.write_archive(_members(files)):
        progress.note(len(piece))
        yield piece


def archive_body(files, progress):
    """The upload: the cpio archive of the files, in DVZip blocks."""
    return transfer.encode_dvzip(_archive(files, progress))


def send(address, port, scope, context, sender_name, files):
    """Ask the receiver, and upload once its owner accepts. Returns whether
    the files went through."""
    connection = Connection(address, port, scope, context, ASK_SECONDS)
    try:
        transfer_id = str(uuid.uuid4()).upper()
        status, answer = post_plist(connection, '/Ask',
                                    ask_body(sender_name, files, transfer_id))
        logging.debug('answer to the ask: status %d, %s', status, outline(answer))
        if status != 200:
            logging.warning('declined (status %d)', status)
            return False
        total = sum(file.size for file in files)
        logging.info('accepted; sending %d file(s), %.1f MB', len(files), total / MIB)
        connection.sock.settimeout(UPLOAD_IDLE_SECONDS)
        progress = Progress(total)
        # The receiver ties the upload to what it accepted by the transfer's
        # identifier.
        connection.request('POST', '/Upload', body=archive_body(files, progress),
                           headers={**HEADERS, 'TotalBytes': str(total),
                                    'Content-Type': 'application/x-dvzip',
                                    'TransferID': transfer_id},
                           encode_chunked=True)
        response = connection.getresponse()
        response.read()
        if response.status != 200:
            logging.warning('the receiver refused the upload (status %d)', response.status)
            return False
        logging.info('sent, at %.1f MB/s', progress.rate())
        return True
    finally:
        connection.close()


def parse_args(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('files', nargs='+', help='the files to send')
    parser.add_argument('--to', help='part of the receiver\'s name, when several answer')
    parser.add_argument('--name', default=socket.gethostname().split('.')[0],
                        help='the name the receiver sees (default: the computer name)')
    parser.add_argument('--search', type=int, default=DEFAULT_SEARCH_SECONDS,
                        help='how many seconds to look for receivers')
    parser.add_argument('--trace', action='store_true',
                        help='also print what the receiver answers')
    parser.add_argument('--no-beacon', action='store_true',
                        help='do not advertise over Bluetooth')
    parser.add_argument('--iface', default=DEFAULT_INTERFACE)
    parser.add_argument('--keys', default=identity.default_keys_dir(),
                        help='directory holding the TLS certificate and key')
    return parser.parse_args(argv)


def run(settings, files, context, scope):
    browser = mdns.Browser(SERVICE_TYPE, scope)
    try:
        named = find_receivers(browser, scope, context, settings.to, settings.search)
        if not named:
            logging.info('%d mDNS answers came in over AWDL', browser.heard)
    finally:
        browser.close()
    name, (address, port) = choose(named, settings.to)
    logging.info('asking %s; accept on the device', name)
    try:
        return send(address, port, scope, context, settings.name, files)
    except TimeoutError:
        logging.warning('%s did not answer in time', name)
    except (OSError, http.client.HTTPException) as error:
        logging.warning('the connection to %s broke: %s', name, error or type(error).__name__)
    return False


def main(argv=None):
    settings = parse_args(argv)
    logging.basicConfig(level=logging.DEBUG if settings.trace else logging.INFO,
                        stream=sys.stdout,
                        format='%(asctime)s %(levelname)s %(message)s')
    files = outgoing(settings.files)
    try:
        context = client_context(settings.keys)
    except identity.IdentityError as error:
        raise SystemExit(str(error)) from None
    found = netif.wait_for_interface(settings.iface, INTERFACE_WAIT_SECONDS)
    if not found:
        raise SystemExit(f'interface {settings.iface} is not up: start '
                         'scripts\\awdl-run.ps1 first')
    scope, _ = found
    if settings.no_beacon:
        return 0 if run(settings, files, context, scope) else 1
    with Beacon():
        return 0 if run(settings, files, context, scope) else 1


if __name__ == '__main__':
    sys.exit(main())
