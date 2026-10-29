import {createHash, createHmac, randomBytes} from 'node:crypto';

export function bridgeHeaders(key, url, method='GET', body='', options={}) {
 const parsed=new URL(url), time=String(options.time ?? Math.floor(Date.now()/1000));
 const nonce=options.nonce ?? randomBytes(32).toString('hex').toUpperCase();
 const service=options.service ?? 'office', keyId=options.keyId ?? 'primary';
 const hash=createHash('sha256').update(body ?? '').digest('hex').toUpperCase();
 const canonical=['YY-BRIDGE-1',method.toUpperCase(),parsed.pathname+parsed.search,time,nonce,service,keyId,hash].join('\n');
 return {'X-YY-Service':service,'X-YY-Key':keyId,'X-YY-Time':time,'X-YY-Nonce':nonce,
  'X-YY-Content-SHA256':hash,
  'X-YY-Signature':createHmac('sha256',key).update(canonical).digest('hex').toUpperCase()};
}
export function bridgeFetch(key,url,init={},options={}) {
 return fetch(url,{...init,headers:{...init.headers,...bridgeHeaders(key,url,init.method ?? 'GET',init.body ?? '',options)}});
}
