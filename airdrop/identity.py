# SPDX-License-Identifier: MIT
"""The TLS identity this receiver presents: a self-signed certificate, which
is all a sender needs when the receiver is visible to everyone."""
import ctypes
from ctypes import wintypes
import logging
import os
import shutil
import ssl
import subprocess
import sys
import tempfile

CERTIFICATE = 'certificate.self-signed.pem'
KEY = 'key.self-signed.pem'
SECRET = 'key.self-signed.secret'
VALID_DAYS = '365'
# Passed explicitly because some Windows builds of OpenSSL point at a default
# configuration file that does not exist. Senders do not look at the name.
REQUEST_CONFIG = '''[req]
distinguished_name = subject
prompt = no
[subject]
CN = AirDrop receiver
'''
# Git for Windows ships OpenSSL without putting it on PATH.
GIT_OPENSSL = (('mingw64', 'bin', 'openssl.exe'), ('usr', 'bin', 'openssl.exe'))


class IdentityError(Exception):
    pass


def default_keys_dir():
    base = os.environ.get('LOCALAPPDATA') or os.path.expanduser('~')
    return os.path.join(base, 'winawdl', 'keys')


def find_openssl():
    found = shutil.which('openssl')
    if found:
        return found
    git = shutil.which('git')
    if git:
        root = os.path.dirname(os.path.dirname(os.path.realpath(git)))
        for parts in GIT_OPENSSL:
            candidate = os.path.join(root, *parts)
            if os.path.isfile(candidate):
                return candidate
    raise IdentityError('OpenSSL not found: put openssl on PATH or install Git for Windows')


def _dpapi(data, protecting):
    class Blob(ctypes.Structure):
        _fields_ = [('size', wintypes.DWORD), ('data', ctypes.POINTER(ctypes.c_ubyte))]

    crypt = ctypes.WinDLL('crypt32', use_last_error=True)
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.LocalFree.argtypes = [ctypes.c_void_p]
    kernel.LocalFree.restype = ctypes.c_void_p
    operation = crypt.CryptProtectData if protecting else crypt.CryptUnprotectData
    operation.argtypes = [ctypes.POINTER(Blob), ctypes.c_void_p, ctypes.c_void_p,
                          ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD,
                          ctypes.POINTER(Blob)]
    operation.restype = wintypes.BOOL
    buffer = (ctypes.c_ubyte * len(data)).from_buffer_copy(data)
    source, result = Blob(len(data), buffer), Blob()
    # UI_FORBIDDEN, without LOCAL_MACHINE: only the current user can decrypt.
    if not operation(ctypes.byref(source), None, None, None, None, 1, ctypes.byref(result)):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        return ctypes.string_at(result.data, result.size)
    finally:
        ctypes.memset(result.data, 0, result.size)
        kernel.LocalFree(result.data)


def protect(data):
    return _dpapi(data, True)


def key_password(keys_dir):
    if sys.platform != 'win32':
        return None
    with open(os.path.join(keys_dir, SECRET), 'rb') as secret:
        password = _dpapi(secret.read(), False)
    if not password:
        raise ValueError('empty identity secret')
    return password


def _restrict(keys_dir):
    """Replace inherited and explicit access with this user's inheritable ACE."""
    advapi = ctypes.WinDLL('advapi32', use_last_error=True)
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.LocalFree.argtypes = [ctypes.c_void_p]
    kernel.LocalFree.restype = ctypes.c_void_p
    advapi.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD,
                                       ctypes.POINTER(wintypes.HANDLE)]
    advapi.GetTokenInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p,
                                          wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
    advapi.ConvertSidToStringSidW.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.LPWSTR)]
    advapi.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes = [
        wintypes.LPCWSTR, wintypes.DWORD, ctypes.POINTER(ctypes.c_void_p), ctypes.c_void_p]
    advapi.SetFileSecurityW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, ctypes.c_void_p]
    token = wintypes.HANDLE()
    if not advapi.OpenProcessToken(kernel.GetCurrentProcess(), 8, ctypes.byref(token)):
        raise ctypes.WinError(ctypes.get_last_error())
    sid = wintypes.LPWSTR()
    try:
        needed = wintypes.DWORD()
        advapi.GetTokenInformation(token, 1, None, 0, ctypes.byref(needed))
        info = ctypes.create_string_buffer(needed.value)
        if not advapi.GetTokenInformation(token, 1, info, needed, ctypes.byref(needed)):
            raise ctypes.WinError(ctypes.get_last_error())
        if not advapi.ConvertSidToStringSidW(ctypes.c_void_p.from_buffer(info), ctypes.byref(sid)):
            raise ctypes.WinError(ctypes.get_last_error())
        descriptor = ctypes.c_void_p()
        sddl = f'D:P(A;OICI;FA;;;{sid.value})'
        if not advapi.ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl, 1, ctypes.byref(descriptor), None):
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            # Protect existing files too, even if they had explicit permissions.
            paths = [keys_dir] + [os.path.join(keys_dir, name)
                                  for name in (CERTIFICATE, KEY, SECRET)
                                  if os.path.isfile(os.path.join(keys_dir, name))]
            for path in paths:
                if not advapi.SetFileSecurityW(path, 0x80000004, descriptor):
                    raise ctypes.WinError(ctypes.get_last_error())
        finally:
            kernel.LocalFree(descriptor)
    finally:
        if sid:
            kernel.LocalFree(sid)
        kernel.CloseHandle(token)


def _validate(certificate, key, password):
    ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER).load_cert_chain(certificate, key, password=password)


def _atomic(path, data):
    descriptor, temporary = tempfile.mkstemp(dir=os.path.dirname(path))
    try:
        with os.fdopen(descriptor, 'wb') as out:
            out.write(data)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def _windows_identity(keys_dir):
    os.makedirs(keys_dir, exist_ok=True)
    _restrict(keys_dir)
    certificate, key = (os.path.join(keys_dir, name) for name in (CERTIFICATE, KEY))
    migrate = False
    if os.path.isfile(certificate) and os.path.isfile(key):
        try:
            with open(key, 'rb') as stored:
                encrypted = b'ENCRYPTED PRIVATE KEY' in stored.read()
            _validate(certificate, key, key_password(keys_dir) if encrypted else b'')
            if encrypted:
                return certificate, key
            migrate = True
        except (OSError, ValueError) as error:
            # Do not log key bytes, passphrases, or a DPAPI blob.
            logging.warning('Stored AirDrop identity cannot be loaded (%s); making a new identity',
                            type(error).__name__)
    elif any(os.path.exists(path) for path in (certificate, key, os.path.join(keys_dir, SECRET))):
        logging.warning('Stored AirDrop identity is incomplete; making a new identity')

    password = os.urandom(32).hex().encode('ascii')
    # The temporary directory inherits the restricted keys directory's ACL.
    with tempfile.TemporaryDirectory(dir=keys_dir) as temporary:
        new_key = os.path.join(temporary, KEY)
        new_certificate = os.path.join(temporary, CERTIFICATE)
        if migrate:
            command = ['pkcs8', '-topk8', '-in', key, '-out', new_key,
                       '-v2', 'aes-256-cbc', '-iter', '100000', '-passout', 'stdin']
        else:
            config = os.path.join(temporary, 'request.cnf')
            with open(config, 'w', encoding='ascii') as out:
                out.write(REQUEST_CONFIG)
            command = ['req', '-config', config, '-newkey', 'rsa:2048', '-x509',
                       '-days', VALID_DAYS, '-keyout', new_key, '-out', new_certificate,
                       '-passout', 'stdin']
        run = subprocess.run([find_openssl(), *command], input=password + b'\n',
                             capture_output=True)
        if run.returncode != 0:
            raise IdentityError('OpenSSL could not encrypt the AirDrop identity')
        _validate(certificate if migrate else new_certificate, new_key, password)
        _atomic(os.path.join(keys_dir, SECRET), protect(password))
        # Replace plaintext only once encryption and DPAPI have both succeeded.
        os.replace(new_key, key)
        if not migrate:
            os.replace(new_certificate, certificate)
    if migrate:
        logging.info('Stored AirDrop identity encrypted for the current Windows user')
    return certificate, key


def ensure_self_signed(keys_dir):
    """Return the certificate and key paths, creating the pair on first use."""
    if sys.platform == 'win32':
        try:
            return _windows_identity(keys_dir)
        except (OSError, ValueError) as error:
            message = f'Could not protect the AirDrop identity ({type(error).__name__})'
            raise IdentityError(message) from error
    certificate = os.path.join(keys_dir, CERTIFICATE)
    key = os.path.join(keys_dir, KEY)
    if os.path.isfile(certificate) and os.path.isfile(key):
        return certificate, key
    os.makedirs(keys_dir, exist_ok=True)
    config = os.path.join(keys_dir, 'request.cnf')
    with open(config, 'w', encoding='ascii') as out:
        out.write(REQUEST_CONFIG)
    try:
        run = subprocess.run(
            [find_openssl(), 'req', '-config', config, '-newkey', 'rsa:2048',
             '-nodes', '-x509', '-days', VALID_DAYS, '-keyout', key,
             '-out', certificate],
            capture_output=True, text=True)
    finally:
        os.unlink(config)
    if run.returncode != 0:
        raise IdentityError(f'openssl could not create a certificate: {run.stderr.strip()}')
    return certificate, key
