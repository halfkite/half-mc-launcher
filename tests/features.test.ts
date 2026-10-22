import { afterEach, describe, expect, it, vi } from 'vitest'
import { mkdtemp, mkdir, writeFile, readFile, readdir, rm } from 'node:fs/promises'
import { createRequire } from 'node:module'
import { createHash } from 'node:crypto'
import path from 'node:path'
import os from 'node:os'
vi.mock('electron',()=>({safeStorage:{isEncryptionAvailable:()=>true,encryptString:(s:string)=>Buffer.from(s),decryptString:(b:Buffer)=>b.toString()}}))
vi.mock('../electron/network',()=>({requestJson:vi.fn()}))
import { requestJson } from '../electron/network'
import { Resources } from '../electron/resources'
import { Accounts } from '../electron/accounts'
import { offlineUuid } from '../electron/offline'
import { Store } from '../electron/storage'
import { memoryBudget } from '../shared/memory'
import { Tasks } from '../electron/tasks'
import { instanceOptionsSchema } from '../shared/model'
import type { Instance } from '../shared/model'
import { importMods,listMods,toggleMod,modPath,safeFilename,modMetadata,modIcon } from '../electron/mods'
const require=createRequire(import.meta.url),{ZipFile}=require('yazl')
const dirs:string[]=[]
const temp=async()=>{const d=await mkdtemp(path.join(os.tmpdir(),'hakf-features-'));dirs.push(d);return d}
afterEach(async()=>{vi.restoreAllMocks();vi.unstubAllGlobals();vi.mocked(requestJson).mockReset();for(const d of dirs.splice(0))await rm(d,{recursive:true,force:true})})
async function jar(id='sample',version='1.0'){const zip=new ZipFile();zip.addBuffer(Buffer.from(JSON.stringify({id,name:id,version})),'fabric.mod.json');zip.end();const bytes:Buffer[]=[];for await(const b of zip.outputStream)bytes.push(b);return Buffer.concat(bytes)}
async function archive(entries:[string,Buffer][]){const zip=new ZipFile();for(const [name,data] of entries)zip.addBuffer(data,name);zip.end();const bytes:Buffer[]=[];for await(const b of zip.outputStream)bytes.push(b);return Buffer.concat(bytes)}
async function fixture(){const root=await temp(),gameDir=path.join(root,'versions','fabric');await mkdir(gameDir,{recursive:true});const instance:Instance={...instanceOptionsSchema.parse({name:'测试',group:'',favorite:false,memory:4096,java:'',isolated:true}),id:'fixture',sourceId:'fixture',root,gameDir,version:'fabric',minecraft:'1.21.1',loader:'Fabric'};return instance}
function remote(id:string,bytes:Buffer,deps:any[]=[]){return {id,project_id:id,name:id,game_versions:['1.21.1'],loaders:['fabric'],files:[{filename:`${id}.jar`,url:`https://cdn.modrinth.com/${id}.jar`,size:bytes.length,hashes:{sha512:createHash('sha512').update(bytes).digest('hex')}}],dependencies:deps}}
async function service(i:Instance){const tasks=new Tasks(()=>{},()=>{}),resources=new Resources(i.root,tasks);return {tasks,resources}}
const selected={source:'modrinth',kind:'mod',projectId:'main',versionId:'main'}
describe('launch preferences',()=>{
  it('budgets heaps against free RAM and 32-bit limits',()=>{expect(memoryBudget(16384,12000,'amd64',false)).toBe(2048);expect(memoryBudget(16384,12000,'amd64',true,160)).toBe(7168);expect(memoryBudget(16384,4000,'amd64',true)).toBe(2304);expect(memoryBudget(16384,12000,'x86',true)).toBe(1024);expect(()=>memoryBudget(4096,1000,'amd64',true)).toThrow('不足');expect(()=>memoryBudget(NaN,5000,'amd64',false)).toThrow()})
  it('keeps offline identity stable and sessions independent of online credentials',async()=>{const dir=await temp(),store=new Store(dir);await store.load();const accounts=new Accounts(store,()=>{});const a=await accounts.addOffline('Player_123');expect(a.uuid).toBe(offlineUuid('Player_123'));expect(a.uuid[12]).toBe('3');expect(await accounts.session(a.id)).toEqual({account:a,accessToken:'0'});await expect(accounts.addOffline('player_123')).rejects.toThrow('已添加');await expect(accounts.addOffline('../')).rejects.toThrow();const loaded=new Store(dir);await loaded.load();expect(loaded.state.accounts[0].kind).toBe('offline');await accounts.remove(a.id);expect(store.state.accounts).toHaveLength(0)})
  it('migrates old manual memory without resetting config or accounts',async()=>{const dir=await temp(),store=new Store(dir);const raw:any=store.state;delete raw.settings.memoryMode;delete raw.settings.fontScale;delete raw.settings.javaRoots;raw.options.a={name:'a',group:'',favorite:false,memory:3072,java:'',isolated:true,protected:false};raw.accounts=[{id:'old',name:'MS',uuid:'123',expiresAt:1}];await writeFile(path.join(dir,'config.json'),JSON.stringify(raw));await store.load();expect(store.warning).toBe('');expect(store.state.options.a.memoryMode).toBe('manual');expect(store.state.settings.memoryMode).toBe('manual');expect(store.state.settings.fontScale).toBe(100);expect(store.state.accounts[0].kind).toBe('microsoft')})
})
describe('instance mod files',()=>{
  it('does not mistake Forge dependency IDs for provided mod IDs',async()=>{const i=await fixture(),zip=new ZipFile();zip.addBuffer(Buffer.from('modLoader="javafml"\n[[mods]]\nmodId="own_mod"\nversion="1.0"\ndisplayName="Own Mod"\n[[dependencies.own_mod]]\nmodId="minecraft"\n'), 'META-INF/mods.toml');zip.end();const chunks:Buffer[]=[];for await(const b of zip.outputStream)chunks.push(b);const file=path.join(i.root,'forge.jar');await writeFile(file,Buffer.concat(chunks));expect(await modMetadata(file)).toEqual({name:'Own Mod',version:'1.0',ids:['own_mod']})})
  it('imports and toggles valid mods while preserving filenames and rejecting collisions',async()=>{const i=await fixture(),input=path.join(i.root,'sample.jar');await writeFile(input,await jar());await importMods(i,[input]);expect(await listMods(i)).toMatchObject([{file:'sample.jar',name:'sample',enabled:true,version:'1.0'}]);await toggleMod(i,'sample.jar');expect((await listMods(i))[0].enabled).toBe(false);await toggleMod(i,'sample.jar.disabled');await expect(importMods(i,[input])).rejects.toThrow('同名');await writeFile(path.join(i.gameDir,'mods','sample.jar.disabled'),await jar('other'));await expect(toggleMod(i,'sample.jar')).rejects.toThrow();expect((await listMods(i)).length).toBe(2);await expect(modPath(i,'../sample.jar')).rejects.toThrow();expect(()=>safeFilename('CON.jar')).toThrow()})
  it('shows corrupt file errors without hiding other mods and refuses corrupt imports',async()=>{const i=await fixture();await mkdir(path.join(i.gameDir,'mods'));await writeFile(path.join(i.gameDir,'mods','bad.jar'),'broken');await writeFile(path.join(i.root,'bad.jar'),'broken');expect((await listMods(i))[0].issue).toContain('无法读取');await expect(importMods(i,[path.join(i.root,'bad.jar')])).rejects.toThrow()})
  it('reads mod icons from Fabric metadata, Forge logoFile and the assets fallback',async()=>{
    const i=await fixture(),png=Buffer.from('89504e470d0a1a0a','hex')
    const fabric=path.join(i.root,'fabric-icon.jar')
    await writeFile(fabric,await archive([['fabric.mod.json',Buffer.from(JSON.stringify({id:'iconmod',name:'Icon Mod',version:'1.0',icon:'icon.png'}))],['icon.png',png]]))
    expect(await modMetadata(fabric)).toMatchObject({name:'Icon Mod',iconPath:'icon.png'})
    expect(await modIcon(fabric,'icon.png','iconmod')).toMatch(/^data:image\/png;base64,/)

    const forge=path.join(i.root,'forge-icon.jar')
    await writeFile(forge,await archive([['META-INF/mods.toml',Buffer.from('modLoader="javafml"\n[[mods]]\nmodId="own_mod"\ndisplayName="Own Mod"\nlogoFile="assets/own_mod/icon.png"\n')],['assets/own_mod/icon.png',png]]))
    expect(await modMetadata(forge)).toMatchObject({iconPath:'assets/own_mod/icon.png'})
    expect(await modIcon(forge,'assets/own_mod/icon.png','own_mod')).toMatch(/^data:image\/png;base64,/)

    const fallback=path.join(i.root,'fallback-icon.jar')
    await writeFile(fallback,await archive([['META-INF/mods.toml',Buffer.from('[[mods]]\nmodId="plain_mod"\ndisplayName="Plain"\n')],['assets/plain_mod/icon.png',png]]))
    expect(await modIcon(fallback,undefined,'plain_mod')).toMatch(/^data:image\/png;base64,/)

    await mkdir(path.join(i.gameDir,'mods'),{recursive:true})
    await writeFile(path.join(i.gameDir,'mods','fabric-icon.jar'),await readFile(fabric))
    expect((await listMods(i))[0].icon).toMatch(/^data:image\/png;base64,/)
  })
})
describe('resource transactions',()=>{
  it('resolves required dependencies and installs verified files into the selected instance',async()=>{const i=await fixture(),main=await jar('main'),dep=await jar('dep'),versions={main:remote('main',main,[{dependency_type:'required',project_id:'dep',version_id:'dep'}]),dep:remote('dep',dep)};vi.mocked(requestJson).mockImplementation(async(url:any)=>versions[String(url).split('/').pop() as keyof typeof versions] as any);vi.stubGlobal('fetch',vi.fn(async(url:string)=>new Response(url.includes('main')?main:dep)));const {resources,tasks}=await service(i);expect((await resources.plan(selected,i)).map(v=>v.id)).toEqual(['dep','main']);resources.install(selected,i);await vi.waitFor(()=>expect(tasks.entries[0].status).toBe('success'));expect((await listMods(i)).map(f=>f.name)).toEqual(['dep','main']);expect((await readdir(path.join(i.gameDir,'mods'))).some(f=>f.endsWith('.part'))).toBe(false)})
  it('rejects incompatible versions and circular dependencies before downloading',async()=>{const i=await fixture(),bytes=await jar();let v=remote('main',bytes);v.game_versions=['1.20.1'];vi.mocked(requestJson).mockResolvedValue(v);const {resources}=await service(i);await expect(resources.plan(selected,i)).rejects.toThrow('不兼容');v=remote('main',bytes,[{dependency_type:'required',project_id:'main',version_id:'main'}]);vi.mocked(requestJson).mockResolvedValue(v);await expect(resources.plan(selected,i)).rejects.toThrow('循环')})
  it('cleans staged files after hash failure and keeps user files intact',async()=>{const i=await fixture(),bytes=await jar('main'),v=remote('main',bytes);vi.mocked(requestJson).mockResolvedValue(v);vi.stubGlobal('fetch',vi.fn(async()=>new Response(Buffer.alloc(bytes.length))));const {resources,tasks}=await service(i);resources.install(selected,i);await vi.waitFor(()=>expect(tasks.entries[0].status).toBe('error'));expect(tasks.entries[0].detail).toContain('校验失败');expect(await readdir(path.join(i.gameDir,'mods'))).toEqual([]);expect(resources.locks.size).toBe(0)})
  it('refuses a second version with the same mod ID even when filenames differ',async()=>{const i=await fixture(),bytes=await jar('main');await mkdir(path.join(i.gameDir,'mods'));await writeFile(path.join(i.gameDir,'mods','old.jar'),await jar('main','0.1'));vi.mocked(requestJson).mockResolvedValue(remote('main',bytes));vi.stubGlobal('fetch',vi.fn(async()=>new Response(bytes)));const {resources,tasks}=await service(i);resources.install(selected,i);await vi.waitFor(()=>expect(tasks.entries[0].status).toBe('error'));expect(tasks.entries[0].detail).toContain('old.jar');expect(await readdir(path.join(i.gameDir,'mods'))).toEqual(['old.jar'])})
  it('guards missing CurseForge credentials without issuing requests',async()=>{const i=await fixture(),{resources}=await service(i);await expect(resources.search({source:'curseforge',kind:'mod'})).rejects.toThrow('API 密钥');expect(requestJson).not.toHaveBeenCalled()})
  it('normalizes CurseForge search and versions and respects unavailable third-party downloads',async()=>{const i=await fixture(),{resources}=await service(i);await resources.configure('test-key');vi.mocked(requestJson).mockResolvedValueOnce({pagination:{totalCount:1},data:[{id:42,name:'CF mod',summary:'test',downloadCount:10,authors:[{name:'author'}]}]});expect((await resources.search({source:'curseforge',kind:'mod',game:'1.21.1',loader:'fabric'})).projects[0]).toMatchObject({id:'42',source:'curseforge',name:'CF mod'});expect(vi.mocked(requestJson).mock.calls[0][0]).toContain('modLoaderType=4');const file={id:7,modId:42,displayName:'CF file',fileName:'cf.jar',fileLength:100,gameVersions:['1.21.1','Fabric'],hashes:[{algo:1,value:'a'.repeat(40)}],dependencies:[]};vi.mocked(requestJson).mockResolvedValueOnce({data:[file]});expect((await resources.versions({source:'curseforge',kind:'mod',projectId:'42'},i))[0]).toMatchObject({id:'7',loaders:['fabric'],url:''});vi.mocked(requestJson).mockResolvedValueOnce({data:file});await expect(resources.plan({source:'curseforge',kind:'mod',projectId:'42',versionId:'7'},i)).rejects.toThrow('未开放第三方下载')})
  it('cancels a download without committing partial files and releases the directory lock',async()=>{const i=await fixture(),bytes=await jar('main');vi.mocked(requestJson).mockResolvedValue(remote('main',bytes));vi.stubGlobal('fetch',vi.fn((_url:any,init:any)=>new Promise((_resolve,reject)=>init.signal.addEventListener('abort',()=>reject(new Error('aborted')),{once:true}))));const {resources,tasks}=await service(i),id=resources.install(selected,i);await vi.waitFor(()=>expect(fetch).toHaveBeenCalled());expect(()=>resources.install(selected,i)).toThrow('已有资源安装任务');tasks.cancel(id);await vi.waitFor(()=>expect(tasks.entries[0].status).toBe('cancelled'));expect(await readdir(path.join(i.gameDir,'mods'))).toEqual([]);expect(resources.locks.size).toBe(0)})
})
