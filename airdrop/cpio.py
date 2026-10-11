# SPDX-License-Identifier: MIT
"""Reads the cpio archives AirDrop senders upload: the portable ASCII format
(magic 070707) and the two "new ASCII" formats (070701, 070702)."""
import stat

TRAILER = 'TRAILER!!!'
MAX_NAME_BYTES = 4096
BLOCK_BYTES = 64 * 1024
MAGIC_BYTES = 6
# Archives are padded with zeros to a multiple of this after the trailer.
RECORD_BYTES = 512


class CpioError(ValueError):
    pass


def _number(field, base):
    try:
        return int(field, base)
    except ValueError:
        raise CpioError(f'invalid header field {field!r}') from None


def _portable_fields(rest):
    """mode, name size and file size from the 70 bytes after a 070707 magic."""
    return _number(rest[12:18], 8), _number(rest[53:59], 8), _number(rest[59:70], 8)


def _new_ascii_fields(rest):
    fields = [_number(rest[i:i + 8], 16) for i in range(0, 104, 8)]
    return fields[1], fields[11], fields[6]


class _Format:
    def __init__(self, rest_bytes, fields, alignment):
        self.rest_bytes = rest_bytes
        self.header_bytes = MAGIC_BYTES + rest_bytes
        self.fields = fields
        self.alignment = alignment

    def padding(self, length):
        return -length % self.alignment


FORMATS = {b'070707': _Format(70, _portable_fields, 1),
           b'070701': _Format(104, _new_ascii_fields, 4),
           b'070702': _Format(104, _new_ascii_fields, 4)}


def _format_for(magic):
    if magic not in FORMATS:
        raise CpioError(f'unsupported cpio magic {magic!r}')
    return FORMATS[magic]


def _name_from(raw):
    return raw.rstrip(b'\0').decode('utf-8', 'replace')


def _check_name_bytes(name_bytes):
    if not 0 < name_bytes <= MAX_NAME_BYTES:
        raise CpioError(f'invalid name length {name_bytes}')


def _read_exact(source, size):
    data = source.read(size)
    if len(data) != size:
        raise CpioError(f'truncated archive: wanted {size} bytes, got {len(data)}')
    return data


class Entry:
    """One archive member. Its data must be read before the next entry is
    requested; whatever is left unread is skipped."""

    def __init__(self, source, name, mode, size):
        self.pathname = name
        self.size = size
        self.isfile = stat.S_ISREG(mode)
        self._source = source
        self._left = size

    def blocks(self):
        while self._left:
            data = _read_exact(self._source, min(self._left, BLOCK_BYTES))
            self._left -= len(data)
            yield data


def read_entries(source):
    """Yield every member of the archive in `source`, up to its trailer."""
    while True:
        layout = _format_for(_read_exact(source, MAGIC_BYTES))
        mode, name_bytes, size = layout.fields(_read_exact(source, layout.rest_bytes))
        _check_name_bytes(name_bytes)
        name = _name_from(_read_exact(source, name_bytes))
        _read_exact(source, layout.padding(layout.header_bytes + name_bytes))
        if name == TRAILER:
            return
        entry = Entry(source, name, mode, size)
        yield entry
        for _ in entry.blocks():
            pass
        _read_exact(source, layout.padding(size))


class TrailerScanner:
    """Follows an archive as it arrives, keeping none of it, to tell when it
    is all there: its trailer read and its last record filled."""

    def __init__(self):
        self._consumed = 0
        self._trailer_seen = False
        self._skip = 0
        self._held = bytearray()
        self._want = MAGIC_BYTES
        self._stage = self._on_magic
        self._layout = None
        self._entry = None

    @property
    def complete(self):
        return self._trailer_seen and self._consumed % RECORD_BYTES == 0

    def feed(self, data):
        self._consumed += len(data)
        view = memoryview(data)
        while len(view) and not self._trailer_seen:
            if self._skip:
                step = min(self._skip, len(view))
                self._skip -= step
                view = view[step:]
                continue
            take = min(self._want - len(self._held), len(view))
            self._held += view[:take]
            view = view[take:]
            if len(self._held) == self._want:
                held = bytes(self._held)
                self._held.clear()
                self._stage(held)

    def _expect(self, size, stage):
        self._want = size
        self._stage = stage

    def _on_magic(self, magic):
        self._layout = _format_for(magic)
        self._expect(self._layout.rest_bytes, self._on_header)

    def _on_header(self, rest):
        _, name_bytes, size = self._layout.fields(rest)
        _check_name_bytes(name_bytes)
        self._entry = (name_bytes, size)
        self._expect(name_bytes, self._on_name)

    def _on_name(self, raw):
        name_bytes, size = self._entry
        if _name_from(raw) == TRAILER:
            self._trailer_seen = True
            return
        layout = self._layout
        self._skip = (layout.padding(layout.header_bytes + name_bytes) + size +
                      layout.padding(size))
        self._expect(MAGIC_BYTES, self._on_magic)
