# SPDX-License-Identifier: MIT
# Adapted from Omdrop's bin/safeplist.py, Copyright (c) 2026 Brent Kearney.
"""Bounded property-list decoding for untrusted AirDrop peers.

A binary plist may reference one object many times, or give one object many
IDs. plistlib shares an object referenced by one ID, so walking the result
(a log line or an XML dump) visits every reference, and it builds an object
again for each ID. Both expansions are bounded here while binary decoding.
"""
import io
import plistlib

MAX_EVENTS = 200_000
MAX_DEPTH = 32
VALUE_OVERHEAD = 16
MIN_BYTE_BUDGET = 4 << 20


class TooComplex(ValueError):
    def __init__(self):
        super().__init__(
            f'property list deeper than {MAX_DEPTH}, larger than {MAX_EVENTS} values, '
            'or larger than four times its own size decoded')


def byte_budget(input_len):
    return max(input_len * 4, MIN_BYTE_BUDGET)


def _size(value):
    if isinstance(value, (str, bytes, bytearray)):
        return len(value)
    return VALUE_OVERHEAD


def _measure(root, budget):
    events, decoded = 1, 0
    stack = [(root, 0)]
    while stack:
        value, depth = stack.pop()
        decoded += _size(value)
        if isinstance(value, (dict, list)):
            if depth >= MAX_DEPTH:
                raise TooComplex()
            events += 1 + len(value) * (2 if isinstance(value, dict) else 1)
            decoded += VALUE_OVERHEAD
            if events > MAX_EVENTS:
                raise TooComplex()
            if isinstance(value, dict):
                for key, member in value.items():
                    decoded += _size(key)
                    stack.append((member, depth + 1))
            else:
                stack.extend((member, depth + 1) for member in value)
        if decoded > budget:
            raise TooComplex()


class _BoundedBinaryParser(plistlib._BinaryPlistParser):
    """Reject malicious references before plistlib expands their allocation."""

    def __init__(self, input_len):
        super().__init__(dict_type=dict)
        self._input_len = input_len
        self._budget = byte_budget(input_len)
        self._events = 1
        self._decoded = self._depth = 0
        self.too_complex = False

    def _refuse(self):
        self.too_complex = True
        raise TooComplex()

    def _read_ints(self, n, size):
        if n * size > self._input_len:
            raise plistlib.InvalidFileException()
        return super()._read_ints(n, size)

    def _read_refs(self, n):
        self._events += n
        if self._events > MAX_EVENTS:
            self._refuse()
        return super()._read_refs(n)

    def _read_object(self, ref):
        if self._depth > MAX_DEPTH:
            self._refuse()
        self._depth += 1
        try:
            value = super()._read_object(ref)
        finally:
            self._depth -= 1
        self._decoded += _size(value)
        if self._decoded > self._budget:
            self._refuse()
        return value


def loads(data):
    """Decode a peer plist while bounding object counts, depth and bytes."""
    if data[:8] == b'bplist00':
        parser = _BoundedBinaryParser(len(data))
        try:
            value = parser.parse(io.BytesIO(data))
        except plistlib.InvalidFileException:
            if parser.too_complex:
                raise TooComplex() from None
            raise
    else:
        value = plistlib.loads(data)
    _measure(value, byte_budget(len(data)))
    return value
