import { createServer } from 'node:http'
import { createHash, randomBytes, timingSafeEqual } from 'node:crypto'
import type { PublicClientApplication } from '@azure/msal-node'

export async function browserAuthorization(client:PublicClientApplication,signal:AbortSignal,openBrowser:(url:string)=>Promise<void>,notify:(url:string)=>void) {
  signal.throwIfAborted()
  const state=randomBytes(32).toString('base64url'),verifier=randomBytes(48).toString('base64url')
  const challenge=createHash('sha256').update(verifier).digest('base64url')
  let resolveCode!:(code:string)=>void,rejectCode!:(error:Error)=>void,finished=false
  const code=new Promise<string>((resolve,reject)=>{resolveCode=resolve;rejectCode=reject})
  // A cancellation during URL preparation must not become an unhandled rejection.
  void code.catch(()=>{})
  const server=createServer((req,res)=>{
    const url=new URL(req.url||'/','http://localhost')
    res.setHeader('Content-Type','text/html; charset=utf-8');res.setHeader('Cache-Control','no-store');res.setHeader('Content-Security-Policy',"default-src 'none'; frame-ancestors 'none'")
    if(req.method!=='GET'||url.pathname!=='/'){res.writeHead(404);res.end();return}
    const received=Buffer.from(url.searchParams.get('state')||''),expected=Buffer.from(state)
    if(finished||received.length!==expected.length||!timingSafeEqual(received,expected)){res.writeHead(400);res.end('授权状态无效，请返回启动器重试。');return}
    const value=url.searchParams.get('code'),error=url.searchParams.get('error')
    if(!value&&!error){res.writeHead(400);res.end('未收到授权结果。');return}
    finished=true
    if(error){res.end('授权未完成，请返回 Half 启动器。');rejectCode(new Error(error==='access_denied'?'你已取消 Microsoft 授权':'Microsoft 授权失败，请重新登录'))}
    else{res.end('<!doctype html><meta charset="utf-8"><title>Half 授权完成</title><h1>授权已收到</h1><p>请返回 Half Mc Launcher，正在验证 Minecraft 账户。</p>');resolveCode(value!)}
  })
  const abort=()=>{finished=true;rejectCode(new Error('登录已取消或超时'));server.close();server.closeAllConnections()}
  signal.addEventListener('abort',abort,{once:true})
  try {
    await new Promise<void>((resolve,reject)=>{server.once('error',reject);server.listen(0,'127.0.0.1',()=>{server.removeListener('error',reject);resolve()})})
    signal.throwIfAborted()
    const address=server.address();if(!address||typeof address==='string')throw new Error('无法创建本机授权回调')
    const redirectUri=`http://localhost:${address.port}`
    const scopes=['XboxLive.signin','offline_access']
    const url=await client.getAuthCodeUrl({scopes,redirectUri,state,codeChallenge:challenge,codeChallengeMethod:'S256',prompt:'select_account'})
    signal.throwIfAborted();notify(url);await openBrowser(url)
    const authorizationCode=await code
    signal.throwIfAborted()
    return await client.acquireTokenByCode({scopes,redirectUri,code:authorizationCode,codeVerifier:verifier})
  }finally{signal.removeEventListener('abort',abort);server.close();server.closeAllConnections()}
}
