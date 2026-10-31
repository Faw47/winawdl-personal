# SPDX-License-Identifier: MIT
# Adapted from tests/test_contacts_policy.py in omdrop, Copyright (c) 2026 Brent Kearney.
"""Contacts Only accepts the senders on the list and nobody else. The cases
that matter are the refusals, above all a genuine record replayed by someone
without the matching key: records are handed to anyone who asks."""
import os
import plistlib
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from apple_fixtures import Fixtures, openssl  # noqa: E402
import contacts  # noqa: E402

# A real altDsID is 46 characters, but a CN made with `openssl req` holds 64
# and the prefix takes 27.
ACCOUNT = '001288-10-AAAA-BBBB-CCCC-DDDD'
OTHER = '001288-10-FFFF-9999-8888-7777'
FRIEND = {contacts.hash_identifier('friend@example.com')}


class StandIn:
    @classmethod
    def setUpClass(cls):
        cls._tmp = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls._tmp.cleanup)
        cls.fx = Fixtures(cls._tmp.name)


class DecideTest(StandIn, unittest.TestCase):
    def decide(self, record, cert, known):
        return contacts.decide(record, cert, known, ca_file=self.fx.root_crt,
                               untrusted=[self.fx.mid_crt], record_ca=self.fx.mid_crt)[0]

    def test_a_phone_number_matches_however_it_was_typed(self):
        record = self.fx.record(['+1 (555) 010-9999'], ACCOUNT)
        cert = self.fx.certificate(ACCOUNT)
        for written in ('+1 555 010 9999', '15550109999', '+1-555-010-9999'):
            known = contacts.known_hashes([written])
            self.assertEqual(self.decide(record, cert, known), contacts.ACCEPT, written)

    def test_an_email_matches_whatever_its_case(self):
        record = self.fx.record(['Friend@Example.com'], ACCOUNT)
        self.assertEqual(self.decide(record, self.fx.certificate(ACCOUNT), FRIEND),
                         contacts.ACCEPT)

    def test_one_matching_identifier_is_enough(self):
        record = self.fx.record(['a@example.com', 'friend@example.com', '5550001111'], ACCOUNT)
        self.assertEqual(self.decide(record, self.fx.certificate(ACCOUNT), FRIEND),
                         contacts.ACCEPT)

    def test_a_stranger_is_refused(self):
        record = self.fx.record(['stranger@example.com'], ACCOUNT)
        self.assertEqual(self.decide(record, self.fx.certificate(ACCOUNT), FRIEND),
                         contacts.REJECT_UNKNOWN)

    def test_a_record_replayed_without_its_key_is_refused(self):
        record = self.fx.record(['friend@example.com'], ACCOUNT)
        self.assertEqual(self.decide(record, self.fx.certificate(OTHER), FRIEND),
                         contacts.REJECT_UNBOUND)

    def test_a_self_signed_certificate_carries_no_account(self):
        record = self.fx.record(['friend@example.com'], ACCOUNT)
        self.assertEqual(self.decide(record, self.fx.certificate(ACCOUNT, issued_by_ca=False),
                                     FRIEND),
                         contacts.REJECT_UNBOUND)

    def test_a_record_nobody_signed_is_refused(self):
        forged = plistlib.dumps({'altDsID': ACCOUNT, 'ValidatedEmailHashes': list(FRIEND)})
        self.assertEqual(self.decide(forged, self.fx.certificate(ACCOUNT), FRIEND),
                         contacts.REJECT_BAD_RECORD)

    def test_a_changed_record_is_refused(self):
        record = bytearray(self.fx.record(['friend@example.com'], ACCOUNT))
        record[record.index(ACCOUNT.encode())] ^= 0x01
        self.assertEqual(self.decide(bytes(record), self.fx.certificate(ACCOUNT), FRIEND),
                         contacts.REJECT_BAD_RECORD)

    def test_an_expired_record_is_refused(self):
        record = self.fx.record(['friend@example.com'], ACCOUNT, age_days=60)
        self.assertEqual(self.decide(record, self.fx.certificate(ACCOUNT), FRIEND),
                         contacts.REJECT_BAD_RECORD)

    def test_no_certificate_is_refused_whatever_the_record(self):
        record = self.fx.record(['friend@example.com'], ACCOUNT)
        self.assertEqual(self.decide(record, None, FRIEND), contacts.REJECT_NO_CERT)

    def test_no_record_is_refused(self):
        self.assertEqual(self.decide(None, self.fx.certificate(ACCOUNT), FRIEND),
                         contacts.REJECT_NO_RECORD)

    def test_an_empty_list_refuses_everyone(self):
        record = self.fx.record(['friend@example.com'], ACCOUNT)
        self.assertEqual(self.decide(record, self.fx.certificate(ACCOUNT), set()),
                         contacts.REJECT_UNKNOWN)

    def test_the_account_is_read_whatever_subject_format_openssl_prints(self):
        # OpenSSL 3.0 prints a one-line subject as "CN = value", later ones
        # as "CN=value"; force the first wherever no format is named.
        real_run = subprocess.run

        def as_openssl_3_0(args, *more, **options):
            args = list(args)
            if 'x509' in args and '-subject' in args and '-nameopt' not in args:
                args += ['-nameopt', 'oneline']
            return real_run(args, *more, **options)

        cert = self.fx.certificate(ACCOUNT)
        with mock.patch.object(contacts.subprocess, 'run', as_openssl_3_0):
            account = contacts.certificate_account(cert, self.fx.root_crt, [self.fx.mid_crt])
        self.assertEqual(account, ACCOUNT)


class RecordSignatureTest(StandIn, unittest.TestCase):
    """Only the record-signing certificate under the pinned intermediate
    speaks for an Apple ID: the root issues to many services."""

    @classmethod
    def setUpClass(cls):
        super().setUpClass()
        fx = cls.fx
        cls.other_ca = fx.issue('other', 'Test Other Service CA',
                                (fx.root_crt, fx.root_key), ca=True)
        cls.other_signer = fx.issue('other-signer', 'AppleIDValidationRecordSigningTest',
                                    cls.other_ca)
        cls.mallory = fx.issue('mallory', 'Mallory', (fx.mid_crt, fx.mid_key))

    def setUp(self):
        for name, path in (('APPLE_ROOT_CA', self.fx.root_crt), ('RECORD_CA', self.fx.mid_crt)):
            patcher = mock.patch.object(contacts, name, path)
            patcher.start()
            self.addCleanup(patcher.stop)

    def payload(self, **extra):
        return self.fx.payload(['friend@example.com'], ACCOUNT, **extra)

    def test_a_record_from_the_record_signing_certificate_verifies(self):
        info = contacts.verify_record(self.fx.record(['friend@example.com'], ACCOUNT))
        self.assertEqual(info['account'], ACCOUNT)
        self.assertEqual(info['hashes'], FRIEND)
        self.assertFalse(info['expired'])

    def test_a_record_under_another_ca_of_the_same_root_is_refused(self):
        record = self.fx.sign(self.payload(), [self.other_signer], [self.other_ca[0]])
        self.assertIsNone(contacts.verify_record(record))
        self.assertEqual(contacts.decide(record, b'unused', FRIEND)[0],
                         contacts.REJECT_BAD_RECORD)

    def test_a_signer_without_the_record_signing_name_is_refused(self):
        record = self.fx.sign(self.payload(), [self.mallory], [self.fx.mid_crt])
        self.assertIsNone(contacts.verify_record(record))

    def test_the_intermediate_itself_cannot_sign_a_record(self):
        record = self.fx.sign(self.payload(), [(self.fx.mid_crt, self.fx.mid_key)], [])
        self.assertIsNone(contacts.verify_record(record))

    def test_a_second_signer_is_refused(self):
        record = self.fx.sign(self.payload(), [self.fx.signer, self.mallory], [self.fx.mid_crt])
        self.assertIsNone(contacts.verify_record(record))

    @unittest.skipIf(os.name == 'nt', 'openssl rehash builds a CA directory on Unix only')
    def test_the_systems_certificate_authorities_are_not_trusted(self):
        record = self.fx.sign(self.payload(), [self.other_signer], [self.other_ca[0]])
        with tempfile.TemporaryDirectory() as system:
            shutil.copy(self.fx.root_crt, system)
            openssl('rehash', system)
            environment = {'SSL_CERT_DIR': system, 'SSL_CERT_FILE': self.fx.root_crt}
            with mock.patch.dict(os.environ, environment):
                self.assertIsNone(contacts.verify_record(record, self.fx.mid_crt))

    def test_the_signed_payload_is_decoded_under_safeplists_limits(self):
        nested = []
        for _ in range(40):
            nested = [nested]
        record = self.fx.sign(self.payload(Nested=nested), [self.fx.signer], [self.fx.mid_crt])
        self.assertIsNone(contacts.verify_record(record))

    def test_a_payload_that_is_not_a_dictionary_is_refused(self):
        record = self.fx.sign(plistlib.dumps(['not', 'a', 'record']),
                              [self.fx.signer], [self.fx.mid_crt])
        self.assertIsNone(contacts.verify_record(record))

    def test_discovery_answers_a_sender_it_could_not_identify(self):
        record = self.fx.sign(self.payload(), [self.other_signer], [self.other_ca[0]])
        self.assertEqual(contacts.decide_discovery(record, FRIEND),
                         (contacts.ACCEPT, {'identified': False}))
        self.assertEqual(contacts.decide_discovery(None, FRIEND)[0], contacts.ACCEPT)

    def test_discovery_hides_from_an_identified_stranger(self):
        record = self.fx.record(['stranger@example.com'], ACCOUNT)
        self.assertEqual(contacts.decide_discovery(record, FRIEND)[0],
                         contacts.REJECT_UNKNOWN)


class ListTest(unittest.TestCase):
    def test_the_file_skips_comments_and_blank_lines(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, 'known-senders')
            with open(path, 'w', encoding='utf-8') as out:
                out.write('# my people\n\nFriend@Example.com\n+1 (555) 010-9999\n')
            entries = contacts.read_known_senders(path)
        self.assertEqual(contacts.known_hashes(entries),
                         {contacts.hash_identifier('friend@example.com'),
                          contacts.hash_identifier('15550109999')})


class VendoredAppleCATest(unittest.TestCase):
    """The CAs shipped in apple-ca/ are Apple's and keep verifying senders."""

    # Apple Root CA, as Apple publishes it.
    ROOT_SHA256 = ('B0:B1:73:0E:CB:C7:FF:45:05:14:2C:49:F1:29:5E:6E:'
                   'DA:6B:CA:ED:7E:2C:68:C5:BE:91:B5:A1:10:01:F0:24')
    # The first AAI CA vendored by omdrop expired on 2026-10-21; Apple
    # re-issued it, and only the new copy chains after that day.
    AFTER_THE_OLD_EXPIRY = '1792627200'

    def test_the_root_is_apples(self):
        fingerprint = openssl('x509', '-in', contacts.APPLE_ROOT_CA, '-noout',
                              '-fingerprint', '-sha256').stdout.decode()
        self.assertIn(self.ROOT_SHA256, fingerprint.upper())

    def test_each_intermediate_chains_to_the_root(self):
        for pem in contacts.apple_intermediates():
            with self.subTest(pem=os.path.basename(pem)):
                openssl('verify', '-attime', self.AFTER_THE_OLD_EXPIRY, '-no-CApath',
                        '-no-CAstore', '-CAfile', contacts.APPLE_ROOT_CA, pem)

    def test_records_are_pinned_to_the_aai2_intermediate(self):
        subject = openssl('x509', '-in', contacts.RECORD_CA, '-noout', '-subject',
                          '-nameopt', 'multiline').stdout.decode()
        self.assertRegex(subject, r'commonName\s*=\s*Apple Application Integration 2 '
                                  r'Certification Authority')


if __name__ == '__main__':
    unittest.main()
