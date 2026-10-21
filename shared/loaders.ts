import { z } from 'zod'
export const loaderKinds=['vanilla','fabric','forge','neoforge','quilt','optifine','liteloader'] as const
export type LoaderKind=typeof loaderKinds[number]
export const loaderNames:Record<LoaderKind,string>={vanilla:'原版',fabric:'Fabric',forge:'Forge',neoforge:'NeoForge',quilt:'Quilt',optifine:'OptiFine',liteloader:'LiteLoader'}
export type LoaderRelease={version:string;stable:boolean;recommended:boolean;detail:string;source:string}
export const installSelection=z.object({version:z.string().regex(/^[a-zA-Z0-9_.+-]{1,120}$/),loader:z.enum(loaderKinds).default('vanilla'),loaderVersion:z.string().regex(/^[a-zA-Z0-9_.+-]{0,150}$/).default(''),loaderSource:z.enum(['auto','official','mirror']).default('auto'),fabricApi:z.boolean().default(false),sourceId:z.string().max(200).default(''),java:z.string().max(2000).default('')}).superRefine((v,ctx)=>{if(v.fabricApi&&v.loader!=='fabric')ctx.addIssue({code:'custom',message:'Fabric API 只能与 Fabric 一起安装'})})
export type InstallSelection=z.infer<typeof installSelection>
export function installationId(input:Pick<InstallSelection,'version'|'loader'|'loaderVersion'>){return input.loader==='vanilla'?input.version:`${input.version}-${input.loader}-${input.loaderVersion}`.replace(/[^a-zA-Z0-9_.+-]/g,'_')}
/** Compatibility is bounded above as well: legacy Forge is not Java 25 compatible. */
export function requiredJava(minecraft:string,loader:LoaderKind) {
  if(/^\d{2}\./.test(minecraft))return {min:25,max:25}
  const match=/^1\.(\d+)(?:\.(\d+))?/.exec(minecraft)
  if(!match)return {min:8,max:25}
  const minor=Number(match[1]),patch=Number(match[2]||0)
  const major=minor>20||(minor===20&&patch>=5)?21:minor>=18?17:minor===17?16:8
  return {min:major,max:loader==='forge'||loader==='neoforge'||loader==='liteloader'||loader==='optifine'?major:Math.max(major,25)}
}
export function neoMinecraft(version:string,project:'forge'|'neoforge'){
  if(project==='forge')return version.startsWith('1.20.1-')?'1.20.1':''
  const parts=version.split(/[.-]/).map(Number)
  if(parts[0]>=26)return `${parts[0]}.${parts[1]}${parts[2]?'.'+parts[2]:''}`
  return `1.${parts[0]}${parts[1]?'.'+parts[1]:''}`
}
