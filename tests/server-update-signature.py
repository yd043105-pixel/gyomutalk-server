"""The SYSTEM update task checks Ed25519 signatures in Windows PowerShell; this holds it to RFC 8032 and to the
signatures the release workflow makes (scripts/Create-UpdateFeed.py uses the same library as here)."""
import base64, os, pathlib, subprocess, tempfile, time
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from cryptography.hazmat.primitives import serialization

script = pathlib.Path(__file__).resolve().parent.parent / 'scripts' / 'Install-ServerUpdate.ps1'
powershell = os.path.join(os.environ.get('SystemRoot', r'C:\Windows'), 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe')

def check(path, signature, key):
    return subprocess.run([powershell, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', str(script),
                           '-Check', str(path), '-Signature', base64.b64encode(signature).decode(), '-PublicKey', base64.b64encode(key).decode()],
                          capture_output=True, text=True, timeout=300).returncode

def raw(key):
    return key.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)

with tempfile.TemporaryDirectory() as folder:
    folder = pathlib.Path(folder)
    # RFC 8032 section 7.1, test 1 (empty message) and test 2 (one byte).
    for secret, public, message, signature in [
        ('9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60', 'd75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a', '',
         'e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b'),
        ('4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb', '3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c', '72',
         '92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00')]:
        file = folder / 'rfc.bin'; file.write_bytes(bytes.fromhex(message))
        assert raw(Ed25519PrivateKey.from_private_bytes(bytes.fromhex(secret))) == bytes.fromhex(public)
        assert check(file, bytes.fromhex(signature), bytes.fromhex(public)) == 0, 'RFC 8032 vector rejected'
        bad = bytearray(bytes.fromhex(signature)); bad[0] ^= 1
        assert check(file, bytes(bad), bytes.fromhex(public)) == 11, 'RFC 8032 vector with a changed R accepted'
    print('PASS RFC 8032 vectors are accepted and a changed signature is refused')

    key = Ed25519PrivateKey.generate(); other = Ed25519PrivateKey.generate()
    payload = folder / 'setup.exe'; payload.write_bytes(os.urandom(3 * 1024 * 1024 + 17))
    data = payload.read_bytes(); signature = key.sign(data)
    started = time.monotonic()
    assert check(payload, signature, raw(key)) == 0, 'release-style signature rejected'
    print(f'PASS a 3 MB installer signed like a release is accepted ({time.monotonic() - started:.1f}s)')
    assert check(payload, signature, raw(other)) == 11, 'signature from another key accepted'
    print('PASS the signature of another product key is refused')
    tampered = bytearray(data); tampered[len(tampered) // 2] ^= 0x40; payload.write_bytes(bytes(tampered))
    assert check(payload, signature, raw(key)) == 11, 'changed installer accepted'
    print('PASS one changed byte in the installer is refused')
    payload.write_bytes(data)
    s = int.from_bytes(signature[32:], 'little') + 7237005577332262213973186563042994240857116359379907606001950938285454250989
    if s < 2 ** 256:
        assert check(payload, signature[:32] + s.to_bytes(32, 'little'), raw(key)) == 11, 'non-canonical S accepted'
    assert check(payload, signature[:63], raw(key)) == 11, 'short signature accepted'
    print('PASS a malleated or short signature is refused')
