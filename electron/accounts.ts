import { PublicClientApplication, type ICachePlugin, type DeviceCodeRequest } from '@azure/msal-node'
import { safeStorage, shell, clipboard } from 'electron'
import { readFile, rename, writeFile, readdir, unlink } from 'node:fs/promises'
import path from 'node:path'
import { randomUUID } from 'node:crypto'
import { requestJson } from './network'
import type { Store } from './storage'
import type { Account, DeviceCode } from '../shared/model'
import { z } from 'zod'
import { offlineName, offlineUuid } from './offline'
import { browserAuthorization } from './browser-auth'
import { resolveClientId } from '../shared/auth'

type Secret = { id: string; homeId: string; accessToken: string; expiresAt: number; clientId: string }
export class Accounts {
  private client?: PublicClientApplication
  private configuredId = ''
  private secrets: Secret[] = []
  private pending?: AbortController
  private deviceRequest?: DeviceCodeRequest
  private loginUrl=''
  private userCode=''
  private writeQueue: Promise<void> = Promise.resolve()
  private refreshes = new Map<string, Promise<{account:Account;accessToken:string}>>()
  constructor(private store: Store, private emit: (type: string, data: unknown) => void, private readFileClientId: () => string = () => '') {}
  clientIdInfo() { return resolveClientId(this.store.state.settings.clientId, this.readFileClientId(), process.env.HAKF_MICROSOFT_CLIENT_ID) }
  configured(){return !!this.clientIdInfo().id}
  async reopen(){if(!this.pending||!this.loginUrl)throw new Error('当前没有等待授权的登录');await shell.openExternal(this.loginUrl)}
  copyCode(){if(!this.pending||!this.userCode)throw new Error('当前没有设备代码');clipboard.writeText(this.userCode)}
  async load() {
    try {
      const bytes = await readFile(path.join(this.store.directory, 'accounts.bin'))
      if (!safeStorage.isEncryptionAvailable()) throw new Error('系统加密服务不可用，无法读取账户')
      this.secrets = z.array(z.object({id:z.string(),homeId:z.string(),accessToken:z.string(),expiresAt:z.number(),clientId:z.string()})).parse(JSON.parse(safeStorage.decryptString(bytes)))
    } catch (error: any) {
      if (error.code !== 'ENOENT') this.emit('notice', '账户凭据无法解密，请重新登录；复制便携目录到另一台电脑后需要重新登录。')
    }
  }
  private async writeEncrypted(file:string,content:string) {
    if (!safeStorage.isEncryptionAvailable()) throw new Error('系统加密服务不可用，无法安全保存账户')
    const bytes=safeStorage.encryptString(content)
    const operation=this.writeQueue.then(async()=>{await writeFile(`${file}.tmp`,bytes);await rename(`${file}.tmp`,file)})
    this.writeQueue=operation.catch(()=>{})
    await operation
  }
  private saveSecrets() { return this.writeEncrypted(path.join(this.store.directory,'accounts.bin'),JSON.stringify(this.secrets)) }
  private getClient(clientId = this.clientIdInfo().id) {
    if (!clientId) throw new Error('未找到可用的 Microsoft 应用 Client ID；请在设置中填写自己的应用 ID，或在数据目录放置 client-id.txt。')
    if (!safeStorage.isEncryptionAvailable()) throw new Error('系统凭据加密服务不可用')
    if (this.client && this.configuredId === clientId) return this.client
    this.configuredId = clientId
    const file = path.join(this.store.directory, `msal-${clientId}.bin`)
    const cachePlugin: ICachePlugin = {
      beforeCacheAccess: async ctx => {
        await this.writeQueue
        try { ctx.tokenCache.deserialize(safeStorage.decryptString(await readFile(file))) } catch (e: any) { if (e.code !== 'ENOENT') throw new Error('登录缓存损坏或无法解密，请在工具页清理登录缓存后重试') }
      },
      afterCacheAccess: async ctx => { if (ctx.cacheHasChanged) await this.writeEncrypted(file,ctx.tokenCache.serialize()) }
    }
    this.client = new PublicClientApplication({ auth: { clientId, authority: 'https://login.microsoftonline.com/consumers' }, cache: { cachePlugin } })
    return this.client
  }
  async login(method:'browser'|'device'='browser'): Promise<Account> {
    if (this.pending) throw new Error('已有登录正在进行，请先完成或取消')
    const client = this.getClient()
    const pending = new AbortController(); this.pending = pending
    const timeout=setTimeout(()=>pending.abort(),300000)
    try {
      this.emit('loginStage',{method,stage:method==='browser'?'正在打开默认浏览器':'正在获取设备代码'})
      let result
      if(method==='browser')result=await browserAuthorization(client,pending.signal,url=>shell.openExternal(url),url=>{this.loginUrl=url;this.emit('loginStage',{method,stage:'请在浏览器完成授权，完成后将自动添加账户'})})
      else {
        this.deviceRequest = { scopes: ['XboxLive.signin', 'offline_access'], deviceCodeCallback: code => {
          if(pending.signal.aborted)return
          this.userCode=code.userCode;this.loginUrl=code.verificationUri
          this.emit('deviceCode', { code: code.userCode, url: code.verificationUri, message: code.message } satisfies DeviceCode)
          void shell.openExternal(code.verificationUri).catch(()=>this.emit('notice','浏览器未能自动打开，请点击重新打开网页'))
        }, cancel: false, timeout: 300 }
        result=await client.acquireTokenByDeviceCode(this.deviceRequest)
      }
      if (pending.signal.aborted) throw new Error('登录已取消')
      if (!result?.accessToken || !result.account) throw new Error('Microsoft 未返回有效账户')
      this.emit('loginStage',{method,stage:'授权完成，正在验证 Xbox 与 Minecraft 档案'})
      const profile = await this.minecraftToken(result.accessToken)
      if (pending.signal.aborted) throw new Error('登录已取消')
      const existing = this.store.state.accounts.find(a => a.kind!=='offline'&&a.uuid === profile.uuid)
      const account: Account = { id: existing?.id || randomUUID(), name: profile.name, uuid: profile.uuid, expiresAt: profile.expiresAt,kind:'microsoft' }
      this.secrets = this.secrets.filter(s => s.id !== account.id)
      this.secrets.push({ id: account.id, homeId: result.account.homeAccountId, accessToken: profile.accessToken, expiresAt: profile.expiresAt, clientId:this.configuredId })
      await this.saveSecrets()
      this.store.state.accounts = [...this.store.state.accounts.filter(a => a.id !== account.id), account]
      this.store.state.selectedAccount = account.id
      await this.store.save()
      return account
    } finally { clearTimeout(timeout);this.pending = undefined; this.deviceRequest = undefined;this.loginUrl='';this.userCode='';this.emit('loginFinished', null) }
  }
  cancel() { this.pending?.abort(); if (this.deviceRequest) this.deviceRequest.cancel = true }
  async addOffline(input:unknown) {
    const name=offlineName.parse(input),uuid=offlineUuid(name)
    if(this.store.state.accounts.some(a=>a.kind==='offline'&&a.name.toLowerCase()===name.toLowerCase()))throw new Error('此离线账户已添加')
    const account:Account={id:randomUUID(),name,uuid,expiresAt:0,kind:'offline'}
    this.store.state.accounts.push(account);this.store.state.selectedAccount=account.id
    await this.store.save();return account
  }
  private async minecraftToken(msToken: string) {
    const post = (url: string, body: unknown) => requestJson<any>(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
    const xbox = await post('https://user.auth.xboxlive.com/user/authenticate', { Properties: { AuthMethod: 'RPS', SiteName: 'user.auth.xboxlive.com', RpsTicket: `d=${msToken}` }, RelyingParty: 'http://auth.xboxlive.com', TokenType: 'JWT' })
    const xsts = await post('https://xsts.auth.xboxlive.com/xsts/authorize', { Properties: { SandboxId: 'RETAIL', UserTokens: [xbox.Token] }, RelyingParty: 'rp://api.minecraftservices.com/', TokenType: 'JWT' })
    const uhs = xsts.DisplayClaims?.xui?.[0]?.uhs
    if (!uhs || !xsts.Token) throw new Error('Xbox 身份验证没有返回有效凭据')
    const mc = await post('https://api.minecraftservices.com/authentication/login_with_xbox', { identityToken: `XBL3.0 x=${uhs};${xsts.Token}` })
    if (!mc.access_token) throw new Error('Minecraft 未返回访问令牌')
    const profile = await requestJson<any>('https://api.minecraftservices.com/minecraft/profile', { headers: { Authorization: `Bearer ${mc.access_token}` } })
    if (!profile.id || !profile.name) throw new Error('此账户未拥有可用的 Minecraft Java 档案')
    return { uuid: profile.id, name: profile.name, accessToken: mc.access_token as string, expiresAt: Date.now() + Number(mc.expires_in || 86400) * 1000 }
  }
  async session(id:string) {
    const pending=this.refreshes.get(id)
    if(pending)return pending
    const operation=this.resolveSession(id)
    this.refreshes.set(id,operation)
    try{return await operation}finally{this.refreshes.delete(id)}
  }
  private async resolveSession(id: string) {
    const account = this.store.state.accounts.find(a => a.id === id), secret = this.secrets.find(s => s.id === id)
    if(account?.kind==='offline')return {account,accessToken:'0'}
    if (!account || !secret) throw new Error('请添加账户或重新登录微软账户')
    if (secret.expiresAt - Date.now() < 120000) {
      const client = this.getClient(secret.clientId)
      const cached = await client.getTokenCache().getAccountByHomeId(secret.homeId)
      if (!cached) throw new Error('登录已失效，请重新登录')
      const result = await client.acquireTokenSilent({ account: cached, scopes: ['XboxLive.signin', 'offline_access'] })
      const refreshed = await this.minecraftToken(result.accessToken)
      Object.assign(secret, { accessToken: refreshed.accessToken, expiresAt: refreshed.expiresAt })
      Object.assign(account, { name: refreshed.name, expiresAt: refreshed.expiresAt })
      await this.saveSecrets(); await this.store.save()
    }
    return { account, accessToken: secret.accessToken }
  }
  async remove(id: string) {
    const secret=this.secrets.find(s=>s.id===id)
    if(secret){const cache=this.getClient(secret.clientId).getTokenCache();const cached=await cache.getAccountByHomeId(secret.homeId);if(cached)await cache.removeAccount(cached)}
    this.secrets = this.secrets.filter(s => s.id !== id); if(secret)await this.saveSecrets()
    this.store.state.accounts = this.store.state.accounts.filter(a => a.id !== id)
    if (this.store.state.selectedAccount === id) this.store.state.selectedAccount = this.store.state.accounts[0]?.id || ''
    await this.store.save()
  }
  async reset() {
    if(this.pending||this.refreshes.size)throw new Error('请先取消登录并等待账户验证结束')
    await this.writeQueue
    for(const name of await readdir(this.store.directory))if(name==='accounts.bin'||/^msal-[a-f0-9-]+\.bin$/i.test(name))await unlink(path.join(this.store.directory,name))
    this.secrets=[];this.client=undefined;this.store.state.accounts=[];this.store.state.selectedAccount=''
    await this.store.save()
  }
}
