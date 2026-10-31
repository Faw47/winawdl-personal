# SPDX-License-Identifier: MIT
# Adapted from tests/test_contacts_policy.py in omdrop, Copyright (c) 2026 Brent Kearney.
"""A stand-in for Apple: a root, an issuing CA under it, a record-signing
certificate, and records and client certificates signed with them. Apple's
signatures cannot be made here, and the checks really verify chains, so the
stand-ins really chain."""
import os
import plistlib
import subprocess
import sys
from datetime import datetime, timedelta, timezone

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import contacts  # noqa: E402
import identity  # noqa: E402

CA_EXTENSIONS = 'basicConstraints=critical,CA:TRUE\nkeyUsage=critical,keyCertSign\n'
LEAF_EXTENSIONS = 'basicConstraints=critical,CA:FALSE\n'


def openssl(*args, data=None):
    return subprocess.run([identity.find_openssl(), *args], input=data,
                          capture_output=True, check=True)


class Fixtures:
    def __init__(self, folder):
        self.folder = folder
        self.root_key, self.root_crt = self.path('root.key'), self.path('root.pem')
        openssl('req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', self.root_key,
                '-out', self.root_crt, '-days', '2', '-subj', '/O=Test/CN=Test Root CA')
        # Leaves are issued below the root, as Apple's are.
        self.mid_crt, self.mid_key = self.issue('mid', 'Test Issuing CA',
                                                (self.root_crt, self.root_key), ca=True)
        # Records are signed by a record-signing certificate under it, as
        # Apple's are by AppleIDValidationRecordSigning05 under AAI2.
        self.signer = self.issue('signer', 'AppleIDValidationRecordSigningTest',
                                 (self.mid_crt, self.mid_key))
        self._count = 0

    def path(self, name):
        return os.path.join(self.folder, name)

    def issue(self, name, cn, issuer, ca=False, subject_o='Test'):
        """A certificate for cn issued by issuer, a (certificate, key) pair."""
        key, crt, csr, ext = (self.path(f'{name}.{e}') for e in ('key', 'pem', 'csr', 'ext'))
        openssl('req', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', csr,
                '-subj', f'/O={subject_o}/CN={cn}')
        with open(ext, 'w', encoding='ascii') as out:
            out.write(CA_EXTENSIONS if ca else LEAF_EXTENSIONS)
        openssl('x509', '-req', '-in', csr, '-CA', issuer[0], '-CAkey', issuer[1],
                '-CAcreateserial', '-days', '2', '-out', crt, '-extfile', ext)
        return crt, key

    def payload(self, identifiers, account, age_days=0, duration=30 * 86400, **extra):
        # Apple stores hashes of normalized identifiers; so must a stand-in.
        hashes = [contacts.hash_identifier(contacts.normalize(i)) for i in identifiers]
        return plistlib.dumps({
            'Version': 2,
            'altDsID': account,
            'ValidAsOf': datetime.now(timezone.utc) - timedelta(days=age_days),
            'SuggestValidDuration': duration,
            'ValidatedEmailHashes': [h for i, h in zip(identifiers, hashes, strict=True)
                                     if '@' in i],
            'ValidatedPhoneHashes': [h for i, h in zip(identifiers, hashes, strict=True)
                                     if '@' not in i],
            **extra,
        })

    def sign(self, payload, signers, chain):
        """A DER CMS record over payload from each (certificate, key) in
        signers, carrying the certificates in chain."""
        source = self.path('payload.bin')
        with open(source, 'wb') as out:
            out.write(payload)
        args = []
        for crt, key in signers:
            args += ['-signer', crt, '-inkey', key]
        if chain:
            bundle = self.path('chain.pem')
            with open(bundle, 'wb') as out:
                for part in chain:
                    with open(part, 'rb') as pem:
                        out.write(pem.read())
            args += ['-certfile', bundle]
        return openssl('cms', '-sign', '-in', source, *args, '-outform', 'DER',
                       '-binary', '-nodetach').stdout

    def record(self, identifiers, account, age_days=0, duration=30 * 86400):
        return self.sign(self.payload(identifiers, account, age_days, duration),
                         [self.signer], [self.mid_crt])

    def client(self, account, issued_by_ca=True):
        """A client certificate and its key, as paths: issued under the
        stand-in root with the CN Apple gives, or self-signed. Self-signed
        stands for both a sender visible to everyone and an impostor who
        writes Apple's name into their own certificate."""
        self._count += 1
        name = f'client{self._count}'
        cn = f'{contacts.CN_PREFIX}{account}' if account else 'airdrop'
        if issued_by_ca:
            return self.issue(name, cn, (self.mid_crt, self.mid_key), subject_o='Apple Inc.')
        key, crt = self.path(f'{name}.key'), self.path(f'{name}.pem')
        openssl('req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', crt,
                '-days', '2', '-subj', f'/O=Apple Inc./CN={cn}')
        return crt, key

    def certificate(self, account, issued_by_ca=True):
        """A client certificate in DER, as a TLS connection gives it."""
        crt, _ = self.client(account, issued_by_ca)
        return openssl('x509', '-in', crt, '-outform', 'DER').stdout
