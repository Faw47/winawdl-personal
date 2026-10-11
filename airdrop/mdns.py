# SPDX-License-Identifier: MIT
"""Announces one DNS-SD service over multicast DNS on a single interface.

This is the small part of RFC 6762 an AirDrop receiver needs: say that the
service exists, answer whoever asks for it, and take it back when leaving.
The record set is the one OpenDrop and omdrop publish.
"""
import ipaddress
import logging
import socket
import struct
import threading
import time

GROUP = 'ff02::fb'
PORT = 5353
MAX_PACKET = 9000

TYPE_PTR, TYPE_TXT, TYPE_AAAA, TYPE_SRV, TYPE_NSEC = 12, 16, 28, 33, 47
CLASS_IN = 1
CACHE_FLUSH = 0x8000
FLAG_RESPONSE = 0x8000
RESPONSE_FLAGS = 0x8400  # response, authoritative

TTL_SERVICE = 4500
TTL_HOST = 120
# AWDL drops multicast sent outside the right slot, so one announcement is
# not enough for a sender to notice us.
ANNOUNCE_PERIOD_S = 2.0
# Senders repeat a query several times within a few milliseconds.
MIN_REPLY_GAP_S = 0.1
POINTER_MASK = 0xC0
# More labels and pointers than any valid name has.
MAX_NAME_STEPS = 128
# NSEC type bitmap, window 0, with bits 28 (AAAA) and 47 (NSEC) set.
HOST_TYPES = bytes([0, 6, 0, 0, 0, 0x08, 0, 0x01])


def encode_name(name):
    out = b''
    for label in name.rstrip('.').split('.'):
        raw = label.encode('utf-8')
        if not 0 < len(raw) < 64:
            raise ValueError(f'invalid DNS label {label!r}')
        out += bytes([len(raw)]) + raw
    return out + b'\0'


def decode_name(packet, offset):
    """Return the name at `offset` and the offset just past it, following
    compression pointers."""
    labels = []
    end = None
    for _ in range(MAX_NAME_STEPS):
        length = packet[offset]
        if length & POINTER_MASK == POINTER_MASK:
            if end is None:
                end = offset + 2
            offset = (length & ~POINTER_MASK) << 8 | packet[offset + 1]
        elif length == 0:
            return '.'.join(labels), offset + 1 if end is None else end
        else:
            offset += 1
            labels.append(packet[offset:offset + length].decode('utf-8', 'replace'))
            offset += length
    raise ValueError('name does not terminate')


def question_names(packet):
    """Lower-cased names a query asks about; empty for responses and for
    anything malformed."""
    try:
        _, flags, questions = struct.unpack_from('!HHH', packet)
        if flags & FLAG_RESPONSE:
            return set()
        names = set()
        offset = 12
        for _ in range(questions):
            name, offset = decode_name(packet, offset)
            offset += 4
            names.add(name.lower())
        return names
    except (IndexError, struct.error, ValueError):
        return set()


def _record(name, rtype, rclass, ttl, rdata):
    return encode_name(name) + struct.pack('!HHIH', rtype, rclass, ttl, len(rdata)) + rdata


class Service:
    """The records for one service instance at one address."""

    def __init__(self, service_type, instance, host, address, port, text):
        self.type_name = service_type
        self.instance_name = f'{instance}.{service_type}'
        self.host_name = host
        self.address = ipaddress.IPv6Address(address)
        self.port = port
        self.text = text

    def names(self):
        return {name.lower() for name in
                (self.type_name, self.instance_name, self.host_name)}

    def response(self, alive=True):
        """One unsolicited response carrying every record. `alive=False`
        gives the goodbye form, with all lifetimes zero."""
        service_ttl = TTL_SERVICE if alive else 0
        host_ttl = TTL_HOST if alive else 0
        unique = CLASS_IN | CACHE_FLUSH
        text = self.text.encode('utf-8')
        records = [
            _record(self.type_name, TYPE_PTR, CLASS_IN, service_ttl,
                    encode_name(self.instance_name)),
            _record(self.instance_name, TYPE_SRV, unique, host_ttl,
                    struct.pack('!HHH', 0, 0, self.port) + encode_name(self.host_name)),
            _record(self.instance_name, TYPE_TXT, unique, service_ttl,
                    bytes([len(text)]) + text),
            _record(self.host_name, TYPE_AAAA, unique, host_ttl, self.address.packed),
            # Apple devices also ask for an A record. This says there is none,
            # so they stop waiting for it.
            _record(self.host_name, TYPE_NSEC, unique, host_ttl,
                    encode_name(self.host_name) + HOST_TYPES),
        ]
        return struct.pack('!HHHHHH', 0, RESPONSE_FLAGS, 0, len(records), 0, 0) + b''.join(records)


def open_socket(interface_index):
    sock = socket.socket(socket.AF_INET6, socket.SOCK_DGRAM)
    try:
        # The operating system's own responder holds this port as well.
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 1)
        sock.bind(('::', PORT))
        membership = socket.inet_pton(socket.AF_INET6, GROUP) + struct.pack('@I', interface_index)
        sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_JOIN_GROUP, membership)
        sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_MULTICAST_IF, interface_index)
        sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_MULTICAST_HOPS, 255)
        sock.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_MULTICAST_LOOP, 0)
        sock.settimeout(ANNOUNCE_PERIOD_S)
    except OSError:
        sock.close()
        raise
    return sock


class Announcer:
    """Keeps a Service announced on one interface until stopped."""

    def __init__(self, service, interface_index):
        self.service = service
        self.interface_index = interface_index
        self.sock = open_socket(interface_index)
        self.stopping = threading.Event()
        self.thread = threading.Thread(target=self._run, name='mdns', daemon=True)

    def start(self):
        self.thread.start()

    def stop(self):
        self.stopping.set()
        self.thread.join()
        self._send(self.service.response(alive=False))
        self.sock.close()

    def _send(self, packet):
        try:
            self.sock.sendto(packet, (GROUP, PORT, 0, self.interface_index))
        except OSError as error:
            logging.warning('mdns: send failed: %s', error)

    def _asked(self):
        """Wait for the next packet; True if it is a query for our service
        arriving on our interface."""
        try:
            packet, sender = self.sock.recvfrom(MAX_PACKET)
        except socket.timeout:
            return False
        except OSError as error:
            if not self.stopping.is_set():
                logging.warning('mdns: receive failed: %s', error)
                time.sleep(MIN_REPLY_GAP_S)
            return False
        if sender[3] != self.interface_index:
            return False
        asked = question_names(packet) & self.service.names()
        if asked:
            logging.info('mdns: %s asked for %s', sender[0].split('%')[0],
                         ', '.join(sorted(asked)))
        return bool(asked)

    def _run(self):
        announcement = self.service.response()
        last_sent = 0.0
        while not self.stopping.is_set():
            asked = self._asked()
            since = time.monotonic() - last_sent
            if (asked and since >= MIN_REPLY_GAP_S) or since >= ANNOUNCE_PERIOD_S:
                self._send(announcement)
                last_sent = time.monotonic()
