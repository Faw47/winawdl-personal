# SPDX-License-Identifier: MIT
# Adapted from bin/airdrop-serve.py in omdrop, Copyright (c) 2026 Brent Kearney.
"""Log records as single bounded lines. Whoever is in radio range chooses the
names, file names and headers these logs repeat, so none of it may start a
line of its own or make one as long as it likes."""
import logging
import re

# Longest message, in UTF-8 bytes, as in omdrop.
MAX_MESSAGE_BYTES = 32 * 1024
# C0 and C1 controls (line breaks among them) as \xNN; line and paragraph
# separators, zero-width characters, bidi controls and the BOM as \uNNNN. Lone
# surrogates, which only a Python string can hold, go as \uNNNN too rather
# than failing the write.
ESCAPED = re.compile('[\x00-\x1f\x7f-\x9f]|'
                     '[  ​-‏‪-‮⁦-⁩﻿\ud800-\udfff]')


def escape(message):
    """message as one line of at most MAX_MESSAGE_BYTES."""
    out = ESCAPED.sub(lambda m: f'\\x{ord(m[0]):02x}' if ord(m[0]) < 0xa0
                      else f'\\u{ord(m[0]):04x}', message)
    data = out.encode('utf-8')
    if len(data) <= MAX_MESSAGE_BYTES:
        return out
    cut = MAX_MESSAGE_BYTES
    while (data[cut] & 0xc0) == 0x80:     # back to a character boundary
        cut -= 1
    return f'{data[:cut].decode("utf-8")} [... {len(data) - cut} more bytes]'


class Formatter(logging.Formatter):
    """Every record, its traceback included, as one escaped, bounded line."""

    def format(self, record):
        return escape(super().format(record))


def configure(level, stream):
    """logging.basicConfig with this formatter, in the format the programs use."""
    handler = logging.StreamHandler(stream)
    handler.setFormatter(Formatter('%(asctime)s %(levelname)s %(message)s'))
    logging.basicConfig(level=level, handlers=[handler])
