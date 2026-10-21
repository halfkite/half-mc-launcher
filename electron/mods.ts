import path from 'node:path'
import { mkdir, lstat, readdir, realpath, copyFile, unlink } from 'node:fs/promises'
import { constants } from 'node:fs'
import { open, filterEntries, readEntry } from '@xmcl/unzip'
import type { Instance } from '../shared/model'
import type { ModFile } from '../shared/resources'
import { inside } from './instances'

export async function resourceDirectory(instance:Instance,folder:'mods'|'resourcepacks'|'shaderpacks') {
  const root=await realpath(instance.root),gameDir=await realpath(instance.gameDir)
  if(root.toLowerCase()!==gameDir.toLowerCase()&&!inside(root,gameDir))throw new Error('游戏目录已越过登记根目录，请重新扫描')
  const dir=path.join(gameDir,folder)
  await mkdir(dir,{recursive:true})
  if((await lstat(dir)).isSymbolicLink()||!inside(gameDir,await realpath(dir)))throw new Error('资源目录是外部链接，已阻止写入')
  return dir
}
export function safeFilename(file:string) {
  if(!file||file.length>200||file!==path.basename(file)||/[<>:"/\\|?*\x00-\x1f]/.test(file)||/[. ]$/.test(file)||/^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(file))throw new Error('资源文件名无效')
  return file
}
export async function modMetadata(file:string):Promise<{name?:string;version?:string;ids?:string[]}> {
  const zip=await open(file,{lazyEntries:true,autoClose:false})
  try {
    const names=['fabric.mod.json','quilt.mod.json','META-INF/neoforge.mods.toml','META-INF/mods.toml','mcmod.info']
    const entries=await filterEntries(zip,names)
    const index=entries.findIndex(Boolean)
    if(index<0)return {}
    const entry=entries[index]!
    if(entry.uncompressedSize>1048576)throw new Error('模组描述过大')
    const text=(await readEntry(zip,entry)).toString('utf8')
    if(index===2||index===3){
      // Only [[mods]] declares provided IDs; dependency tables also contain modId.
      const mods=text.split(/^\s*\[\[mods\]\]\s*$/m).slice(1).map(block=>block.split(/^\s*\[/m)[0])
      return {name:/^\s*displayName\s*=\s*["']([^"']+)/m.exec(mods[0]||'')?.[1],version:/^\s*version\s*=\s*["']([^"']+)/m.exec(mods[0]||'')?.[1],ids:mods.map(block=>/^\s*modId\s*=\s*["']([^"']+)/m.exec(block)?.[1]).filter((id):id is string=>!!id)}
    }
    const json=JSON.parse(text),meta=index===1?json.quilt_loader:index===4?json[0]||json:json
    return {name:String(meta?.metadata?.name||meta?.name||meta?.id||'').slice(0,200),version:String(meta?.version||'').slice(0,100),ids:[meta?.id||meta?.modid].filter(Boolean)}
  } finally {zip.close()}
}
export async function listMods(instance:Instance):Promise<ModFile[]> {
  const dir=path.join(instance.gameDir,'mods')
  let entries
  try {entries=await readdir(dir,{withFileTypes:true})}catch(e:any){if(e.code==='ENOENT')return [];throw e}
  const files:ModFile[]=[]
  for(const entry of entries.filter(e=>e.isFile()&&/\.jar(?:\.disabled)?$/i.test(e.name))){
    const file=path.join(dir,entry.name),info=await lstat(file)
    if(info.isSymbolicLink())continue
    let meta:{name?:string;version?:string}={},issue:string|undefined
    try {meta=await modMetadata(file)}catch(e:any){issue=`无法读取模组信息：${e.message}`}
    files.push({file:entry.name,name:meta.name||entry.name.replace(/\.jar(?:\.disabled)?$/i,''),version:meta.version||'',enabled:!entry.name.toLowerCase().endsWith('.disabled'),size:info.size,issue})
  }
  return files.sort((a,b)=>a.name.localeCompare(b.name,'zh-CN',{numeric:true}))
}
export async function modPath(instance:Instance,file:string) {
  safeFilename(file)
  if(!/\.jar(?:\.disabled)?$/i.test(file))throw new Error('请选择 JAR 模组文件')
  const dir=await resourceDirectory(instance,'mods'),target=path.join(dir,file),info=await lstat(target)
  if(!info.isFile()||info.isSymbolicLink())throw new Error('模组文件已发生变化，请刷新列表')
  return target
}
export async function importMods(instance:Instance,files:string[]) {
  const dir=await resourceDirectory(instance,'mods')
  // Validate the entire selection before adding any files.
  for(const file of files){safeFilename(path.basename(file));if(!/\.jar$/i.test(file))throw new Error('只能导入 JAR 模组');await modMetadata(file);try{await lstat(path.join(dir,path.basename(file)));throw new Error(`已存在同名文件：${path.basename(file)}`)}catch(e:any){if(e.code!=='ENOENT')throw e}}
  for(const file of files)await copyFile(file,path.join(dir,path.basename(file)),constants.COPYFILE_EXCL)
}
export async function toggleMod(instance:Instance,file:string) {
  const target=await modPath(instance,file),next=file.toLowerCase().endsWith('.disabled')?target.slice(0,-9):target+'.disabled'
  // Link-free exclusive copy prevents rename from replacing an existing disabled/enabled file.
  await copyFile(target,next,constants.COPYFILE_EXCL)
  try {await unlink(target)}catch(e){await unlink(next);throw e}
}
