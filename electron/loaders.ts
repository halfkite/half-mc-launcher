import { getForgeVersionList, resolveForgeArtifactVersion, type ForgeVersion } from '@xmcl/installer'
import { z } from 'zod'
import { loaderKinds, neoMinecraft, type LoaderKind, type LoaderRelease } from '../shared/loaders'
import { requestJson } from './network'
export const loaderQuery=z.object({minecraft:z.string().regex(/^[a-zA-Z0-9_.+-]{1,120}$/),loader:z.enum(loaderKinds),source:z.enum(['auto','official','mirror']).default('auto')})
type Query=z.infer<typeof loaderQuery>
export type CatalogEntry=LoaderRelease&{forge?:ForgeVersion;artifact?:string;sha1?:string;url?:string;opti?:{type:string;patch:string};lite?:any;project?:'forge'|'neoforge'}
export const mirror='https://bmclapi2.bangbang93.com'
export const timedFetch=(url:string,init:RequestInit={})=>fetch(url,{...init,signal:init.signal?AbortSignal.any([init.signal,AbortSignal.timeout(12000)]):AbortSignal.timeout(12000),headers:{'User-Agent':'HalfMcLauncher/0.1.0',...init.headers}})
export async function mavenSha1(urls:string[],signal:AbortSignal){for(const url of urls){try{const r=await timedFetch(url+'.sha1',{signal});if(!r.ok)continue;const value=(await r.text()).trim().split(/\s+/)[0];if(/^[a-f0-9]{40}$/i.test(value))return value.toLowerCase()}catch(e){if(signal.aborted)throw e}}throw new Error('无法取得安装器 SHA-1 校验值，请检查下载源或稍后重试')}
export class LoaderCatalog {
  private cache=new Map<string,{time:number;entries:CatalogEntry[]}>()
  async entries(input:unknown,refresh=false):Promise<CatalogEntry[]> {
    const q=loaderQuery.parse(input),key=JSON.stringify(q),cached=this.cache.get(key)
    if(!refresh&&cached&&Date.now()-cached.time<300000)return cached.entries
    let entries:CatalogEntry[]
    if(q.loader==='vanilla')return []
    if(q.loader==='optifine'||q.loader==='liteloader'){
      if(q.source==='official')throw new Error('此组件当前通过 BMCLAPI 提供版本目录，请选择自动或 BMCLAPI')
      entries=await this.mirrored(q)
    }else if(q.source==='mirror')entries=await this.mirrored(q)
    else {
      try{entries=await this.official(q)}catch(e:any){if(q.source==='official')throw new Error(`官方版本目录获取失败：${e.message}`);try{entries=await this.mirrored(q)}catch(f:any){throw new Error(`官方和 BMCLAPI 目录均不可用：${f.message}`)}}
    }
    entries.sort((a,b)=>Number(b.recommended)-Number(a.recommended)||Number(b.stable)-Number(a.stable)||b.version.localeCompare(a.version,undefined,{numeric:true}))
    if(entries.length&&!entries.some(e=>e.recommended))entries[0].detail=[entries[0].detail,'最新可用'].filter(Boolean).join(' · ')
    if(this.cache.size>=100)this.cache.delete(this.cache.keys().next().value!)
    this.cache.set(key,{time:Date.now(),entries});return entries
  }
  async list(input:unknown,refresh=false):Promise<LoaderRelease[]>{return (await this.entries(input,refresh)).map(({version,stable,recommended,detail,source})=>({version,stable,recommended,detail,source}))}
  async select(q:Query,version:string){const entries=await this.entries(q);const result=version?entries.find(e=>e.version===version):entries.find(e=>e.recommended)||entries.find(e=>e.stable)||entries[0];if(!result)throw new Error(`此游戏版本没有所选 ${q.loader} 版本，请刷新列表`);return result}
  private async official(q:Query):Promise<CatalogEntry[]> {
    const mc=encodeURIComponent(q.minecraft)
    if(q.loader==='fabric'||q.loader==='quilt'){
      const base=q.loader==='fabric'?'https://meta.fabricmc.net/v2':'https://meta.quiltmc.org/v3'
      const r=await requestJson<any[]>(`${base}/versions/loader/${mc}`,{signal:AbortSignal.timeout(12000)})
      return r.map(x=>({version:x.loader.version,stable:x.loader.stable??!/alpha|beta|pre|rc/i.test(x.loader.version),recommended:false,detail:'',source:'官方'}))
    }
    if(q.loader==='forge'){
      const r=await getForgeVersionList({minecraft:q.minecraft,fetch:timedFetch})
      return r.versions.filter(v=>!!v.installer?.path).map(v=>({version:v.version,stable:v.type!=='buggy',recommended:v.type==='recommended',detail:v.type==='latest'?'最新':v.type==='buggy'?'存在已知问题':'',source:'官方',forge:v,artifact:resolveForgeArtifactVersion(q.minecraft,v.version),sha1:v.installer.sha1}))
    }
    const project=q.minecraft==='1.20.1'?'forge':'neoforge'
    const r=await requestJson<{versions:string[]}>(`https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/${project}`,{signal:AbortSignal.timeout(12000)})
    return r.versions.filter(v=>neoMinecraft(v,project)===q.minecraft).map(version=>({version,stable:!/alpha|beta|pre|rc/i.test(version),recommended:false,detail:'',source:'官方',project}))
  }
  private async mirrored(q:Query):Promise<CatalogEntry[]> {
    const mc=encodeURIComponent(q.minecraft),json=<T=any>(route:string)=>requestJson<T>(mirror+route,{signal:AbortSignal.timeout(15000)})
    if(q.loader==='fabric'||q.loader==='quilt'){
      const r=await json<any[]>(`/${q.loader}-meta/${q.loader==='quilt'?'v3':'v2'}/versions/loader/${mc}`)
      return r.map(x=>({version:x.loader.version,stable:x.loader.stable??!/alpha|beta|pre|rc/i.test(x.loader.version),recommended:false,detail:'',source:'BMCLAPI'}))
    }
    if(q.loader==='forge'){
      const r=await json<any[]>(`/forge/minecraft/${mc}`)
      return r.filter(v=>v.files?.some((f:any)=>f.category==='installer'&&f.format==='jar')).map(v=>({version:v.version,stable:true,recommended:false,detail:'',source:'BMCLAPI',artifact:resolveForgeArtifactVersion(q.minecraft,v.version),sha1:v.files.find((f:any)=>f.category==='installer'&&f.format==='jar').hash}))
    }
    if(q.loader==='neoforge'){
      if(/^1\.(?:[0-9]|1[0-9])(?:\.|$)/.test(q.minecraft))return []
      const r=await json<any[]>(`/neoforge/list/${mc}`)
      return r.filter(v=>v.mcversion===q.minecraft).map(v=>({version:v.version,stable:!/alpha|beta|pre|rc/i.test(v.version),recommended:false,detail:'',source:'BMCLAPI',project:v.installerPath?.includes('/forge/')?'forge':'neoforge'}))
    }
    if(q.loader==='optifine'){
      const r=await json<any[]>(`/optifine/${mc}`)
      return r.map(v=>({version:`${v.type}_${v.patch}`,stable:!v.patch.toLowerCase().includes('pre'),recommended:false,detail:v.forge||'独立安装',source:'BMCLAPI',opti:{type:v.type,patch:v.patch},url:`${mirror}/optifine/${mc}/${encodeURIComponent(v.type)}/${encodeURIComponent(v.patch)}`}))
    }
    if(!/^1\.(?:[5-9]|1[0-2])(?:\.|$)/.test(q.minecraft))return []
    const v=await json(`/liteloader/list?mcversion=${mc}`)
    if(!v?.build?.version)return []
    return [{version:v.build.version,stable:v.type!=='SNAPSHOT',recommended:false,detail:'旧版组件 · '+v.type,source:'BMCLAPI',lite:v.build,url:`${mirror}/maven/com/mumfrey/liteloader/${encodeURIComponent(v.build.version)}/${encodeURIComponent(v.build.file)}`}]
  }
}
