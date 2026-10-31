# SPDX-License-Identifier: MIT
# Adapted from bin/contacts.py in omdrop, Copyright (c) 2026 Brent Kearney.
"""Whether an AirDrop sender is someone on the known-senders list.

Contacts Only is not a name check. A sender that proposes a transfer presents
two things, as omdrop measured from real transfers (2026-09-18):

- an Apple-issued client certificate, on /Ask and /Upload but never on
  /Discover, whose subject CN is com.apple.idms.appleid.prd.<altDsID>, issued
  by the Apple Application Integration Certification Authority;
- an Apple ID validation record: a CMS blob signed by Apple holding the
  SHA-256 of every phone number and email address verified for that Apple ID,
  and the same altDsID.

Checking them takes only Apple's public certificates, in apple-ca/. Three
checks, and each matters:

1. The record is signed by Apple's record-signing certificate
   (AppleIDValidationRecordSigning...), issued by the Apple Application
   Integration 2 CA. Apple's root issues to many services, and none of the
   others speaks for an Apple ID.
2. The client certificate verifies to Apple's root and its CN ends in the
   record's altDsID. Records are handed to anyone who asks, so without this a
   record caught off the air could be replayed by someone without the key.
3. One of the record's hashes is on the known-senders list.

SenderComputerName is never trusted: a sender chooses it.
"""
import hashlib
import os
import re
import subprocess
import tempfile
from datetime import datetime, timezone

import identity
import safeplist

HERE = os.path.dirname(os.path.abspath(__file__))
APPLE_CA_DIR = os.path.join(HERE, 'apple-ca')
APPLE_ROOT_CA = os.path.join(APPLE_CA_DIR, 'apple-root-ca.pem')
# Issues the client certificates of Apple devices.
APPLE_AAI_CA = os.path.join(APPLE_CA_DIR, 'apple-aai-ca.pem')
# The one trust anchor for validation records: genuine ones are signed by
# AppleIDValidationRecordSigning05, which this intermediate issued.
RECORD_CA = os.path.join(APPLE_CA_DIR, 'apple-aai2-ca.pem')
RECORD_SIGNER_CN_PREFIX = 'AppleIDValidationRecordSigning'
CN_PREFIX = 'com.apple.idms.appleid.prd.'
# Only the certificates named on the command line: openssl otherwise adds the
# system's CA directory and store, and with them every public CA.
NO_SYSTEM_CAS = ['-no-CApath', '-no-CAstore']
# A console program started from the app's windowless engine would otherwise
# open a console window of its own for each check.
NO_WINDOW = getattr(subprocess, 'CREATE_NO_WINDOW', 0)

ACCEPT = 'accept'
REJECT_NO_RECORD = 'no-record'
REJECT_BAD_RECORD = 'bad-record'
REJECT_NO_CERT = 'no-certificate'
REJECT_UNBOUND = 'record-not-bound-to-certificate'
REJECT_UNKNOWN = 'not-a-known-sender'

WHY = {
    ACCEPT: 'sender is a known sender',
    REJECT_NO_RECORD: 'sender sent no Apple ID validation record',
    REJECT_BAD_RECORD: 'the validation record is not signed by Apple, or has expired',
    REJECT_NO_CERT: 'sender presented no client certificate',
    REJECT_UNBOUND: 'the validation record does not belong to the presented certificate',
    REJECT_UNKNOWN: 'sender is not on the known-senders list',
}

PEM_CERT = re.compile(rb'-----BEGIN CERTIFICATE-----\r?\n.*?-----END CERTIFICATE-----\r?\n?',
                      re.S)


def apple_intermediates():
    """The issuing CAs a sender's leaf certificate needs to reach the root.
    An iPhone sends its intermediate, but a sender may send the leaf alone."""
    return [APPLE_AAI_CA, RECORD_CA]


def openssl(*args, data=None):
    return subprocess.run([identity.find_openssl(), *args], input=data,
                          capture_output=True, creationflags=NO_WINDOW)


def _subject_cn(pem):
    """The subject CN of one PEM certificate, '' if it has none. Read one
    attribute a line: the one-line subject is "CN = value" on OpenSSL 3.0
    and "CN=value" on later releases."""
    subject = openssl('x509', '-noout', '-subject', '-nameopt', 'multiline', data=pem)
    cn = ''
    for line in subject.stdout.decode(errors='replace').splitlines():
        key, sep, value = line.partition('=')
        if sep and key.strip() == 'commonName':
            cn = value.strip()
    return cn


def normalize(entry):
    """An identifier as Apple hashes it: an email lowercased, a phone number
    as its digits alone. A mistake here is a silent non-match."""
    entry = entry.strip()
    if not entry or entry.startswith('#'):
        return None
    if '@' in entry:
        return entry.lower()
    digits = re.sub(r'\D', '', entry)
    return digits or None


def hash_identifier(entry):
    return hashlib.sha256(entry.encode()).hexdigest().lower()


def known_hashes(entries):
    """The hashed identifiers of everyone accepted from. The list is kept in
    the clear for the owner to read and edit; it is hashed here."""
    out = set()
    for entry in entries:
        normalized = normalize(str(entry))
        if normalized:
            out.add(hash_identifier(normalized))
    return out


def read_known_senders(path):
    """Identifiers from a file, one a line; # starts a comment."""
    with open(path, encoding='utf-8', errors='replace') as listing:
        return [line for line in listing.read().splitlines() if normalize(line)]


def verify_record(record, ca_file=None):
    """What Apple's signature on a validation record vouches for, or None.

    The record CA is the one trust anchor (-partial_chain lets an
    intermediate be one), and the system's CAs are never consulted. The
    record must have exactly one signer, named as Apple's record-signing
    certificates are. -purpose any: the signer is not an S/MIME certificate,
    so openssl's default purpose check refuses genuine records.
    """
    ca = ca_file or RECORD_CA
    if not record or not os.path.isfile(ca):
        return None
    with tempfile.TemporaryDirectory() as tmp:
        out = os.path.join(tmp, 'payload')
        signers = os.path.join(tmp, 'signers.pem')
        verified = openssl('cms', '-verify', '-inform', 'DER', '-CAfile', ca, *NO_SYSTEM_CAS,
                           '-partial_chain', '-purpose', 'any', '-signer', signers,
                           '-out', out, data=record)
        if verified.returncode != 0:
            return None
        with open(signers, 'rb') as signed_by:
            certificates = PEM_CERT.findall(signed_by.read())
        if (len(certificates) != 1
                or not _subject_cn(certificates[0]).startswith(RECORD_SIGNER_CN_PREFIX)):
            return None
        with open(out, 'rb') as payload_file:
            payload = payload_file.read()
    # The signature says who signed it, not that its payload is small.
    try:
        plist = safeplist.loads(payload)
    except Exception:
        return None
    if not isinstance(plist, dict):
        return None
    valid_as_of = plist.get('ValidAsOf')
    duration = plist.get('SuggestValidDuration')
    expires = None
    if isinstance(valid_as_of, datetime) and isinstance(duration, (int, float)):
        base = valid_as_of if valid_as_of.tzinfo else valid_as_of.replace(tzinfo=timezone.utc)
        expires = base.timestamp() + duration
    listed = []
    for key in ('ValidatedEmailHashes', 'ValidatedPhoneHashes'):
        if isinstance(plist.get(key), list):
            listed += plist[key]
    account = plist.get('altDsID')
    return {
        'account': account if isinstance(account, str) else '',
        'hashes': {h.lower() for h in listed if isinstance(h, str)},
        'expired': bool(expires and expires < datetime.now(timezone.utc).timestamp()),
    }


def certificate_account(der, ca_file=None, untrusted=None):
    """The Apple ID a client certificate belongs to, or '' if it belongs to
    none. It must verify to Apple's root and to nothing else; a name in its
    issuer field is only a string its maker wrote. A sender visible to
    everyone presents a self-signed certificate, which yields ''."""
    ca = ca_file or APPLE_ROOT_CA
    if not der or not os.path.isfile(ca):
        return ''
    chain = list(untrusted) if untrusted is not None else apple_intermediates()
    with tempfile.TemporaryDirectory() as tmp:
        leaf = os.path.join(tmp, 'leaf.pem')
        if openssl('x509', '-inform', 'DER', '-out', leaf, data=der).returncode != 0:
            return ''
        args = ['verify', '-CAfile', ca, *NO_SYSTEM_CAS, '-purpose', 'any']
        if chain:
            bundle = os.path.join(tmp, 'chain.pem')
            with open(bundle, 'wb') as out:
                for path in chain:
                    with open(path, 'rb') as part:
                        out.write(part.read())
            args += ['-untrusted', bundle]
        if openssl(*args, leaf).returncode != 0:
            return ''
        with open(leaf, 'rb') as pem:
            cn = _subject_cn(pem.read())
    return cn[len(CN_PREFIX):] if cn.startswith(CN_PREFIX) else ''


def decide(record, peer_cert, known, ca_file=None, untrusted=None, record_ca=None):
    """Whether to accept a transfer from this sender: (outcome, detail).

    detail never carries an identifier: the hashes give a phone number back
    to anyone who tries every number. ca_file and untrusted are for the
    client certificate and record_ca for the record; only a test names them.
    """
    if not record:
        return REJECT_NO_RECORD, {}
    info = verify_record(record, record_ca)
    if info is None or info['expired']:
        return REJECT_BAD_RECORD, {'expired': bool(info and info['expired'])}
    if not peer_cert:
        return REJECT_NO_CERT, {}
    account = certificate_account(peer_cert, ca_file, untrusted)
    if not account or account != info['account']:
        return REJECT_UNBOUND, {'apple_issued_certificate': bool(account)}
    if info['hashes'] & known:
        return ACCEPT, {}
    return REJECT_UNKNOWN, {'known_senders': len(known)}


def decide_discovery(record, known, record_ca=None):
    """Whether this sender may see this computer at all: (outcome, detail).

    Weaker than decide, and only for /Discover. A sender sends its client
    certificate on /Ask, never on /Discover, so nothing binds the record to
    the connection here, and a replayed record of a known sender is answered.
    That decides who sees this computer, never who may send.

    It hides only from a sender positively identified as unknown. A missing,
    malformed or expired record is answered: "could not tell who this is"
    must not hide this computer from a device whose records were never
    measured. /Ask still refuses the transfer.
    """
    if not record:
        return ACCEPT, {'identified': False}
    info = verify_record(record, record_ca)
    if info is None or info['expired']:
        return ACCEPT, {'identified': False}
    if info['hashes'] & known:
        return ACCEPT, {'identified': True}
    return REJECT_UNKNOWN, {'known_senders': len(known)}
