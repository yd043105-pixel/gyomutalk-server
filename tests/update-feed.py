import base64,importlib.util,pathlib,subprocess,tempfile,xml.etree.ElementTree as ET
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from cryptography.hazmat.primitives import serialization
spec=importlib.util.spec_from_file_location('feeds','scripts/Create-UpdateFeed.py');module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
key=Ed25519PrivateKey.generate();seed=key.private_bytes(serialization.Encoding.Raw,serialization.PrivateFormat.Raw,serialization.NoEncryption());pub=base64.b64encode(key.public_key().public_bytes(serialization.Encoding.Raw,serialization.PublicFormat.Raw)).decode()
with tempfile.TemporaryDirectory() as folder:
    for product in ['server','windows','mac']:
        arch='arm64' if product=='mac' else None
        asset=pathlib.Path(folder)/(f'gyomutalk-mac-1.0.1-macos-arm64.zip' if arch else f'gyomutalk-{product}-1.0.1-setup-win-x64.exe');asset.write_bytes(b'SYNTHETIC NONEXECUTABLE UPDATE PAYLOAD')
        config=dict(product=product,repository='yd043105-pixel/gyomutalk-'+product,publicKey=pub)
        content,digest=module.create_feed(config,'1.0.1','v1.0.1',asset,seed,arch);enclosure=ET.fromstring(content).find('./channel/item/enclosure');signature=enclosure.attrib['{'+module.SPARKLE+'}edSignature'];key.public_key().verify(base64.b64decode(signature),asset.read_bytes())
        assert enclosure.attrib['{'+module.SPARKLE+'}version']==('1000001' if arch else '1.0.1')
        for invalid in ['v2.0.0','v1.0.1;echo injected']:
            try: module.create_feed(config,'1.0.1',invalid,asset,seed,arch)
            except ValueError: pass
            else: raise AssertionError('Unmatched release accepted')
        try: module.create_feed({**config,'publicKey':base64.b64encode(bytes(32)).decode()},'1.0.1','v1.0.1',asset,seed,arch)
        except ValueError: pass
        else: raise AssertionError('Wrong publisher accepted')
        native=pathlib.Path('.tools/update-frameworks/winsparkle/WinSparkle-0.9.4/bin/winsparkle-tool.exe')
        if native.exists():
            command=[str(native.resolve()),'verify','--public-key',pub,'--signature',signature,str(asset)]
            assert subprocess.run(command,capture_output=True).returncode==0,'Native signature interoperability failed'
            asset.write_bytes(asset.read_bytes()+b'TAMPER');assert subprocess.run(command,capture_output=True).returncode!=0,'Tampered payload accepted'
        print('PASS '+product+': valid Ed25519 signature, exact release/version, wrong-key rejection and tamper rejection')
