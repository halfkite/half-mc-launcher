import { beforeEach,afterEach,it,expect,vi } from 'vitest'
import { mkdtemp,rm,readFile } from 'node:fs/promises'
import os from 'node:os'
import path from 'node:path'
vi.mock('electron',()=>({safeStorage:{isEncryptionAvailable:()=>true,encryptString:(s:string)=>Buffer.from(s),decryptString:(b:Buffer)=>b.toString()},shell:{openExternal:vi.fn(async()=>{})},clipboard:{writeText:vi.fn()}}))
vi.mock('@azure/msal-node',()=>({PublicClientApplication:class {}}))
vi.mock('../electron/browser-auth',()=>({browserAuthorization:vi.fn()}))
vi.mock('../electron/network',()=>({requestJson:vi.fn()}))
import { browserAuthorization } from '../electron/browser-auth'
import { requestJson } from '../electron/network'
import { Accounts } from '../electron/accounts'
import { Store } from '../electron/storage'
let dir:string,store:Store
beforeEach(async()=>{dir=await mkdtemp(path.join(os.tmpdir(),'hakf-ms-test-'));store=new Store(dir);await store.load();store.state.settings.clientId='00000000-0000-0000-0000-000000000001'})
afterEach(async()=>{vi.resetAllMocks();await rm(dir,{recursive:true,force:true})})
it('turns a browser authorization result into a persisted Minecraft account without exposing tokens in config',async()=>{
  vi.mocked(browserAuthorization).mockResolvedValue({accessToken:'ms-access-token',account:{homeAccountId:'home-id'}} as any)
  vi.mocked(requestJson).mockResolvedValueOnce({Token:'xbox'}).mockResolvedValueOnce({Token:'xsts',DisplayClaims:{xui:[{uhs:'user-hash'}]}}).mockResolvedValueOnce({access_token:'mc-access-token',expires_in:86400}).mockResolvedValueOnce({id:'123456781234123412341234567890ab',name:'MicrosoftPlayer'})
  const accounts=new Accounts(store,()=>{}),account=await accounts.login()
  expect(browserAuthorization).toHaveBeenCalledOnce();expect(account.kind).toBe('microsoft');expect(store.state.selectedAccount).toBe(account.id)
  expect(await accounts.session(account.id)).toMatchObject({accessToken:'mc-access-token',account:{name:'MicrosoftPlayer'}})
  const config=await readFile(path.join(dir,'config.json'),'utf8');expect(config).not.toContain('mc-access-token');expect(config).not.toContain('ms-access-token')
})
it('discards a cancelled browser attempt and permits another attempt',async()=>{
  vi.mocked(browserAuthorization).mockImplementation(async(_client,signal)=>await new Promise((_resolve,reject)=>signal.addEventListener('abort',()=>reject(new Error('登录已取消')),{once:true})))
  const emit=vi.fn(),accounts=new Accounts(store,emit),pending=accounts.login();accounts.cancel();await expect(pending).rejects.toThrow('取消');expect(store.state.accounts).toHaveLength(0)
  vi.mocked(browserAuthorization).mockRejectedValueOnce(new Error('test retry reached authorization'))
  await expect(accounts.login()).rejects.toThrow('retry reached');expect(emit).toHaveBeenCalledWith('loginFinished',null)
})
it('falls back to the bundled public client id so a fresh install needs no setup',async()=>{
  store.state.settings.clientId=''
  expect(new Accounts(store,()=>{}).clientIdInfo()).toMatchObject({id:'c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb',source:'builtin'})
  vi.mocked(browserAuthorization).mockResolvedValue({accessToken:'ms-access-token',account:{homeAccountId:'home-id'}} as any)
  vi.mocked(requestJson).mockResolvedValueOnce({Token:'xbox'}).mockResolvedValueOnce({Token:'xsts',DisplayClaims:{xui:[{uhs:'user-hash'}]}}).mockResolvedValueOnce({access_token:'mc-access-token',expires_in:86400}).mockResolvedValueOnce({id:'123456781234123412341234567890ab',name:'BundledPlayer'})
  const account=await new Accounts(store,()=>{}).login()
  expect(account).toMatchObject({name:'BundledPlayer',kind:'microsoft'})
})
it('prefers an explicit client id from settings or client-id.txt over the bundled one',()=>{
  store.state.settings.clientId='11111111-1111-1111-1111-111111111111'
  expect(new Accounts(store,()=>{},()=>'22222222-2222-2222-2222-222222222222').clientIdInfo().source).toBe('settings')
  store.state.settings.clientId=''
  expect(new Accounts(store,()=>{},()=>'22222222-2222-2222-2222-222222222222\n').clientIdInfo()).toEqual({id:'22222222-2222-2222-2222-222222222222',source:'file'})
  expect(new Accounts(store,()=>{},()=>'not-an-id').clientIdInfo().source).toBe('builtin')
})
