import { createHash, randomUUID } from 'node:crypto'
import { createReadStream, createWriteStream } from 'node:fs'
import { readFile, writeFile, rename, unlink, lstat, copyFile } from 'node:fs/promises'
import { constants } from 'node:fs'
import { pipeline } from 'node:stream/promises'
import { Readable, Transform } from 'node:stream'
import path from 'node:path'
import { safeStorage } from 'electron'
import { z } from 'zod'
import type { Instance } from '../shared/model'
import type { ResourceKind, ResourceProject, ResourceSource, ResourceVersion } from '../shared/resources'
import type { Tasks } from './tasks'
import { resourceDirectory, safeFilename, modMetadata } from './mods'
import { readdir } from 'node:fs/promises'
import { requestJson } from './network'

export const resourceQuery=z.object({source:z.enum(['modrinth','curseforge']),kind:z.enum(['mod','resourcepack','shader']),query:z.string().max(200).default(''),game:z.string().max(100).default(''),loader:z.string().max(30).default(''),offset:z.number().int().min(0).max(10000).default(0)})
export const resourceSelection=z.object({source:z.enum(['modrinth','curseforge']),kind:z.enum(['mod','resourcepack','shader']),projectId:z.string().regex(/^[a-zA-Z0-9_-]{1,100}$/),versionId:z.string().regex(/^[a-zA-Z0-9_-]{1,100}$/).optional()})
type Query=z.infer<typeof resourceQuery>
type Selection=z.infer<typeof resourceSelection>
const cfLoaders:Record<string,number>={forge:1,fabric:4,quilt:5,neoforge:6}
const classes={mod:6,resourcepack:12,shader:6552}
const MAX_FILE=512*1024*1024
export function compatible(version:ResourceVersion,instance:Instance) {
  return version.games.includes(instance.minecraft)&&(version.kind!=='mod'||version.loaders.includes(instance.loader.toLowerCase()))
}
export async function fileHash(file:string,algorithm:string) {const hash=createHash(algorithm);for await(const bytes of createReadStream(file))hash.update(bytes);return hash.digest('hex')}
export class Resources {
  private key=''
  readonly locks=new Set<string>()
  constructor(private base:string,private tasks:Tasks){}
  async load(){try{this.key=safeStorage.decryptString(await readFile(path.join(this.base,'curseforge.bin')))}catch(e:any){if(e.code!=='ENOENT')throw new Error('CurseForge 配置无法解密，请重新设置密钥')}}
  configured(){return !!this.key}
  async projectUrl(input:unknown) {const q=resourceSelection.parse(input);if(q.source==='modrinth')return `https://modrinth.com/${q.kind}/${encodeURIComponent(q.projectId)}`;const result=await this.cf(`mods/${encodeURIComponent(q.projectId)}`);const url=new URL(result.data.links.websiteUrl);if(url.protocol!=='https:'||!['www.curseforge.com','curseforge.com'].includes(url.hostname))throw new Error('项目页面地址无效');return url.href}
  async configure(input:unknown){const key=z.string().trim().max(300).parse(input);if(key&&!safeStorage.isEncryptionAvailable())throw new Error('系统加密服务不可用');const file=path.join(this.base,'curseforge.bin');if(key){await writeFile(file+'.tmp',safeStorage.encryptString(key));await rename(file+'.tmp',file)}else await unlink(file).catch((e:NodeJS.ErrnoException)=>{if(e.code!=='ENOENT')throw e});this.key=key;return !!key}
  private cf<T=any>(route:string,signal?:AbortSignal){if(!this.key)throw new Error('请先在设置中配置 Half 自己的 CurseForge API 密钥，或切换到 Modrinth');return requestJson<T>('https://api.curseforge.com/v1/'+route,{headers:{'x-api-key':this.key},signal})}
  async search(input:unknown):Promise<{projects:ResourceProject[];total:number}> {
    const q=resourceQuery.parse(input)
    if(q.source==='modrinth'){
      const facets=[[`project_type:${q.kind}`]];if(q.game)facets.push([`versions:${q.game}`]);if(q.loader&&q.kind==='mod')facets.push([`categories:${q.loader}`])
      const params=new URLSearchParams({query:q.query,facets:JSON.stringify(facets),limit:'20',offset:String(q.offset),index:q.query?'relevance':'downloads'})
      const result=await requestJson<any>('https://api.modrinth.com/v2/search?'+params)
      return {total:result.total_hits,projects:result.hits.map((p:any)=>({id:p.project_id,source:'modrinth',name:p.title,summary:p.description,downloads:p.downloads,author:p.author,icon:p.icon_url||''}))}
    }
    const params=new URLSearchParams({gameId:'432',classId:String(classes[q.kind]),searchFilter:q.query,pageSize:'20',index:String(q.offset),sortField:'6',sortOrder:'desc'})
    if(q.game)params.set('gameVersion',q.game);if(q.loader&&q.kind==='mod'&&cfLoaders[q.loader])params.set('modLoaderType',String(cfLoaders[q.loader]))
    const result=await this.cf('mods/search?'+params)
    return {total:result.pagination.totalCount,projects:result.data.map((p:any)=>({id:String(p.id),source:'curseforge',name:p.name,summary:p.summary,downloads:p.downloadCount,author:p.authors.map((a:any)=>a.name).join(', '),icon:p.logo?.thumbnailUrl||p.logo?.url||''}))}
  }
  private mrVersion(v:any,kind:ResourceKind):ResourceVersion {
    const f=v.files?.find((f:any)=>f.primary)||v.files?.[0]
    if(!f)throw new Error('此版本没有可下载文件')
    return {id:v.id,projectId:v.project_id,source:'modrinth',name:v.name,filename:f.filename,url:f.url,hash:f.hashes?.sha512||f.hashes?.sha1||'',algorithm:f.hashes?.sha512?'sha512':'sha1',size:f.size,games:v.game_versions||[],loaders:v.loaders||[],kind,dependencies:(v.dependencies||[]).filter((d:any)=>d.dependency_type==='required').map((d:any)=>({projectId:d.project_id||undefined,versionId:d.version_id||undefined}))}
  }
  private cfVersion(v:any,kind:ResourceKind):ResourceVersion {
    const hash=v.hashes?.find((h:any)=>h.algo===1)||v.hashes?.find((h:any)=>h.algo===2)
    return {id:String(v.id),projectId:String(v.modId),source:'curseforge',name:v.displayName,filename:v.fileName,url:v.downloadUrl||'',hash:hash?.value||'',algorithm:hash?.algo===2?'md5':'sha1',size:v.fileLength,games:v.gameVersions||[],loaders:(v.gameVersions||[]).map((s:string)=>s.toLowerCase()).filter((s:string)=>['forge','fabric','neoforge','quilt'].includes(s)),kind,dependencies:(v.dependencies||[]).filter((d:any)=>d.relationType===3).map((d:any)=>({projectId:String(d.modId)}))}
  }
  async versions(input:unknown,instance?:Instance,signal?:AbortSignal):Promise<ResourceVersion[]> {
    const q=resourceSelection.parse(input)
    if(q.source==='modrinth'){
      const params=new URLSearchParams({include_changelog:'false'})
      if(instance){params.set('game_versions',JSON.stringify([instance.minecraft]));if(q.kind==='mod')params.set('loaders',JSON.stringify([instance.loader.toLowerCase()]))}
      const values=await requestJson<any[]>(`https://api.modrinth.com/v2/project/${encodeURIComponent(q.projectId)}/version?${params}`,{signal})
      return values.map(v=>this.mrVersion(v,q.kind)).filter(v=>!instance||compatible(v,instance))
    }
    const params=new URLSearchParams({pageSize:'50'});if(instance){params.set('gameVersion',instance.minecraft);if(q.kind==='mod'&&cfLoaders[instance.loader.toLowerCase()])params.set('modLoaderType',String(cfLoaders[instance.loader.toLowerCase()]))}
    const result=await this.cf(`mods/${encodeURIComponent(q.projectId)}/files?${params}`,signal)
    return result.data.map((v:any)=>this.cfVersion(v,q.kind)).filter((v:ResourceVersion)=>!instance||compatible(v,instance))
  }
  private async version(q:Selection,signal?:AbortSignal){if(!q.versionId)throw new Error('请选择资源版本');if(q.source==='modrinth')return this.mrVersion(await requestJson(`https://api.modrinth.com/v2/version/${encodeURIComponent(q.versionId)}`,{signal}),q.kind);return this.cfVersion((await this.cf(`mods/${encodeURIComponent(q.projectId)}/files/${encodeURIComponent(q.versionId)}`,signal)).data,q.kind)}
  async plan(input:unknown,instance:Instance,signal?:AbortSignal):Promise<ResourceVersion[]> {
    const q=resourceSelection.parse(input),result:ResourceVersion[]=[],visiting=new Set<string>(),resolved=new Map<string,string>()
    const visit=async(v:ResourceVersion,depth:number)=>{
      signal?.throwIfAborted()
      if(depth>16||result.length>=100)throw new Error('资源依赖数量或深度超过限制')
      if(resolved.has(v.projectId)){if(resolved.get(v.projectId)!==v.id)throw new Error('资源依赖要求了同一项目的不同版本');return}
      if(visiting.has(v.projectId))throw new Error('资源依赖出现循环，已停止安装')
      if(!compatible(v,instance))throw new Error(`资源 ${v.name} 与实例的游戏版本或加载器不兼容`)
      safeFilename(v.filename)
      if(!(v.kind==='mod'?/\.jar$/i:/\.zip$/i).test(v.filename))throw new Error('资源文件类型不匹配')
      if(!v.hash||!new RegExp(`^[a-f0-9]{${v.algorithm==='sha512'?128:v.algorithm==='sha1'?40:32}}$`,'i').test(v.hash))throw new Error('资源缺少有效校验信息')
      if(!v.url)throw new Error('作者未开放第三方下载，请前往项目页手动下载')
      const url=new URL(v.url);if(url.protocol!=='https:')throw new Error('资源下载必须使用 HTTPS')
      if(!Number.isSafeInteger(v.size)||v.size<=0||v.size>MAX_FILE)throw new Error('单个资源文件需小于 512 MiB')
      visiting.add(v.projectId)
      for(const dep of v.dependencies){
        if(!dep.projectId&&!dep.versionId)throw new Error('存在需手动安装的外部依赖')
        let next:ResourceVersion
        if(dep.versionId&&q.source==='modrinth')next=await this.version({source:q.source,kind:'mod',projectId:dep.projectId||'unknown',versionId:dep.versionId},signal)
        else {const choices=await this.versions({source:q.source,kind:'mod',projectId:dep.projectId},instance,signal);next=choices[0];if(!next)throw new Error(`依赖 ${dep.projectId} 没有兼容版本`)}
        await visit(next,depth+1)
      }
      visiting.delete(v.projectId);resolved.set(v.projectId,v.id);result.push(v)
    }
    const selected=await this.version(q,signal);if(selected.projectId!==q.projectId)throw new Error('资源项目和版本不匹配')
    await visit(selected,0);return result
  }
  install(input:unknown,instance:Instance) {
    const q=resourceSelection.parse(input),key=path.resolve(instance.gameDir).toLowerCase()
    if(this.locks.has(key))throw new Error('此游戏目录已有资源安装任务')
    this.locks.add(key)
    return this.tasks.start(`安装资源到 ${instance.name}`,async(_task,signal,update)=>{
      const staged:{temp:string;target:string;version:ResourceVersion}[]=[],created:string[]=[]
      try {
        update('解析版本与必要依赖',5)
        const plan=await this.plan(q,instance,signal),targets=new Set<string>()
        for(let i=0;i<plan.length;i++){
          const v=plan[i],dir=await resourceDirectory(instance,v.kind==='mod'?'mods':v.kind==='shader'?'shaderpacks':'resourcepacks'),target=path.join(dir,v.filename)
          if(targets.has(target.toLowerCase()))throw new Error('依赖中有同名文件冲突');targets.add(target.toLowerCase())
          const existing=await lstat(target).catch((e:NodeJS.ErrnoException)=>{if(e.code==='ENOENT')return null;throw e})
          if(existing){if(existing.isFile()&&!existing.isSymbolicLink()&&await fileHash(target,v.algorithm)===v.hash.toLowerCase())continue;throw new Error(`已存在不同的同名文件：${v.filename}。请先在 Mod 管理中处理`)}
          if(v.kind==='mod'&&await lstat(target+'.disabled').catch((e:NodeJS.ErrnoException)=>{if(e.code==='ENOENT')return null;throw e}))throw new Error(`此模组已被禁用：${v.filename}。请先在 Mod 管理中处理`)
          const temp=path.join(dir,`.hakf-${randomUUID()}.part`);staged.push({temp,target,version:v})
          update(`下载 ${v.filename}（${i+1}/${plan.length}）`,10+75*i/plan.length)
          const response=await fetch(v.url,{signal:AbortSignal.any([signal,AbortSignal.timeout(180000)]),headers:{'User-Agent':'HalfMcLauncher/0.1.0'}})
          if(!response.ok||!response.body)throw new Error(`下载失败（HTTP ${response.status}）`)
          let size=0,lastProgress=0;const digest=createHash(v.algorithm)
          const meter=new Transform({transform(chunk,_enc,cb){size+=chunk.length;if(size>v.size||size>MAX_FILE)return cb(new Error('下载文件超出声明大小'));digest.update(chunk);if(Date.now()-lastProgress>=250){lastProgress=Date.now();update(`${v.filename} · ${(size/1048576).toFixed(1)} / ${(v.size/1048576).toFixed(1)} MiB`,10+75*(i+size/v.size)/plan.length)}cb(null,chunk)}})
          await pipeline(Readable.fromWeb(response.body as any),meter,createWriteStream(temp,{flags:'wx'}),{signal})
          if(size!==v.size||digest.digest('hex')!==v.hash.toLowerCase())throw new Error(`文件完整性校验失败：${v.filename}`)
        }
        const ids=new Map<string,string>()
        if(staged.some(s=>s.version.kind==='mod')){
          const dir=await resourceDirectory(instance,'mods')
          for(const file of await readdir(dir))if(/\.jar$/i.test(file)){
            const info=await lstat(path.join(dir,file));if(!info.isFile()||info.isSymbolicLink())continue
            const meta=await modMetadata(path.join(dir,file)).catch(()=>({ids:[]}))
            for(const id of meta.ids||[])ids.set(id,file)
          }
          for(const item of staged.filter(s=>s.version.kind==='mod'))for(const id of (await modMetadata(item.temp)).ids||[]){if(ids.has(id))throw new Error(`模组 ${id} 已由 ${ids.get(id)} 提供，请先处理旧版本`);ids.set(id,item.version.filename)}
        }
        signal.throwIfAborted();update('写入已校验资源',90)
        // Commit only after every dependency has passed verification; never replace user files.
        for(const item of staged){signal.throwIfAborted();await copyFile(item.temp,item.target,constants.COPYFILE_EXCL);created.push(item.target)}
        update(`已安装 ${plan.length} 个资源（含必要依赖）`,100)
      } catch(e){for(const file of created)await unlink(file);throw e}
      finally {try{for(const item of staged)await unlink(item.temp).catch((e:NodeJS.ErrnoException)=>{if(e.code!=='ENOENT')throw e})}finally{this.locks.delete(key)}}
    },instance.id)
  }
}
