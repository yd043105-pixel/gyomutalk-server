"""Build a product-specific Sparkle/WinSparkle feed; never print signing material."""
import argparse,base64,hashlib,json,os,re,sys,xml.etree.ElementTree as ET
from pathlib import Path
from urllib.parse import quote
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from cryptography.hazmat.primitives import serialization
SPARKLE='http://www.andymatuschak.org/xml-namespaces/sparkle'
ET.register_namespace('sparkle',SPARKLE)
def create_feed(config,version,tag,asset,seed,architecture=None):
    if not re.fullmatch(r'\d+\.\d+\.\d+',version) or tag not in [version,'v'+version]: raise ValueError('Release tag/version mismatch')
    product=config['product']
    if product not in ['server','windows','mac'] or config['repository']!='yd043105-pixel/gyomutalk-'+product: raise ValueError('Unexpected repository')
    name=f'gyomutalk-{product}-{version}-setup-win-x64.exe'
    build=version
    if product=='mac':
        if architecture not in ['arm64','x64']: raise ValueError('Mac architecture required')
        name=f'gyomutalk-mac-{version}-macos-{architecture}.zip'
        major,minor,patch=map(int,version.split('.'))
        if minor>=1000 or patch>=1000: raise ValueError('Build number out of range')
        build=str(major*1000000+minor*1000+patch)
    if asset.name!=name or asset.is_symlink() or not asset.is_file() or asset.stat().st_size==0: raise ValueError('Unexpected or missing release asset')
    key=Ed25519PrivateKey.from_private_bytes(seed)
    public=key.public_key().public_bytes(serialization.Encoding.Raw,serialization.PublicFormat.Raw)
    if base64.b64encode(public).decode()!=config['publicKey']: raise ValueError('Signing key does not match embedded product key')
    data=asset.read_bytes();signature=base64.b64encode(key.sign(data)).decode()
    root=ET.Element('rss',{'version':'2.0'});channel=ET.SubElement(root,'channel')
    ET.SubElement(channel,'title').text='교무톡 '+product
    ET.SubElement(channel,'link').text='https://github.com/'+config['repository']+'/releases'
    item=ET.SubElement(channel,'item');ET.SubElement(item,'title').text='교무톡 '+version
    ET.SubElement(item,'description').text='새 버전의 변경 내용은 GitHub 릴리즈 페이지에서 확인하세요.'
    ET.SubElement(item,'{'+SPARKLE+'}minimumSystemVersion').text='15.0' if product=='mac' else '10.0'
    ET.SubElement(item,'enclosure',{'url':'https://github.com/'+config['repository']+'/releases/download/'+quote(tag,safe='')+'/'+name,'length':str(len(data)),'type':'application/octet-stream','{'+SPARKLE+'}version':build,'{'+SPARKLE+'}shortVersionString':version,'{'+SPARKLE+'}edSignature':signature,'{'+SPARKLE+'}os':'macos' if product=='mac' else 'windows'})
    ET.indent(root)
    return ET.tostring(root,encoding='utf-8',xml_declaration=True),hashlib.sha256(data).hexdigest()
def main():
    args=argparse.ArgumentParser();args.add_argument('--asset',type=Path,required=True);args.add_argument('--tag',required=True);args.add_argument('--architecture',choices=['arm64','x64']);args.add_argument('--output',type=Path,required=True);options=args.parse_args()
    config=json.loads(Path('config/update-publisher.json').read_text(encoding='utf-8-sig'))
    version=ET.parse('Directory.Build.props').findtext('./PropertyGroup/Version')
    notes=json.loads(Path('src/SchoolMessenger.Shared/updates.json').read_text(encoding='utf-8-sig'))
    if version!=notes['version']: raise ValueError('Bundled release notes/version mismatch')
    seed=base64.b64decode(os.environ['UPDATER_ED25519_SEED'],validate=True)
    content,digest=create_feed(config,version,options.tag,options.asset,seed,options.architecture)
    options.output.parent.mkdir(parents=True,exist_ok=True);options.output.write_bytes(content)
    options.asset.with_suffix(options.asset.suffix+'.sha256').write_text(digest+'  '+options.asset.name+'\n',encoding='ascii')
    print('Signed update feed created for '+config['product']+' '+version)
if __name__=='__main__':
    try: main()
    except Exception as error: print('Update feed refused: '+str(error),file=sys.stderr);sys.exit(1)
