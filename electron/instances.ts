import { readdir, readFile, realpath, stat } from 'node:fs/promises'
import path from 'node:path'
import { createHash } from 'node:crypto'
import type { Instance, Source, State } from '../shared/model'

export const instanceId = (root: string, version: string) => createHash('sha256').update(`${path.resolve(root).toLowerCase()}\0${version}`).digest('hex').slice(0, 24)
const exists = async (file: string) => !!(await stat(file).catch(() => null))
export async function canonical(file: string) { const resolved=await realpath(file);return resolved.length>path.parse(resolved).root.length?resolved.replace(/[\\/]+$/, ''):resolved }
export function inside(parent: string, child: string) { const r = path.relative(parent, child); return !!r && !r.startsWith(`..${path.sep}`) && r !== '..' && !path.isAbsolute(r) }
export async function findRoots(directory: string): Promise<string[]> {
  const base = await canonical(directory)
  const roots = new Set<string>()
  const consider = async (dir: string) => { if (await exists(path.join(dir, 'versions'))) roots.add(await canonical(dir)) }
  await consider(base)
  await consider(path.join(base, '.minecraft'))
  // A versions directory itself is also accepted.
  if (path.basename(base).toLowerCase() === 'versions') await consider(path.dirname(base))
  // Bounded discovery: containers of independent instances, never a whole-drive recursive walk.
  if (!roots.size) {
    const children = await readdir(base, { withFileTypes: true })
    for (const child of children.slice(0, 2000)) if (child.isDirectory() && !child.isSymbolicLink()) {
      await consider(path.join(base, child.name))
      await consider(path.join(base, child.name, '.minecraft'))
    }
  }
  return [...roots]
}
export async function scanSources(state: State): Promise<{ instances: Instance[]; warnings: string[] }> {
  const instances: Instance[] = [], warnings: string[] = [], seen = new Set<string>()
  for (const source of state.sources) {
    let roots: string[]
    try { roots = await findRoots(source.path) } catch (e: any) { warnings.push(`${source.label}：${e.message}`); continue }
    if (!roots.length) warnings.push(`${source.label}：尚未发现含 versions 的 Minecraft 目录。`)
    for (const root of roots) {
      let entries
      try { entries = await readdir(path.join(root, 'versions'), { withFileTypes: true }) } catch (e: any) { warnings.push(`${root}：${e.message}`); continue }
      for (const entry of entries) {
        if (!entry.isDirectory() || entry.isSymbolicLink()) continue
        const id = instanceId(root, entry.name)
        if (seen.has(id) || state.hidden.includes(id)) continue
        seen.add(id)
        const versionDir = path.join(root, 'versions', entry.name)
        const jsonFile = path.join(versionDir, `${entry.name}.json`)
        if (!await exists(jsonFile)) continue // Ignore natives/cache directories, not real versions.
        let meta: any = {}, issue: string | undefined
        try { meta = JSON.parse(await readFile(jsonFile, 'utf8')); if (!meta.id || (!meta.mainClass && !meta.inheritsFrom)) issue = '版本描述缺少 id、mainClass 或父版本' } catch { issue = '版本 JSON 已损坏，无法启动' }
        const libs: string[] = Array.isArray(meta.libraries) ? meta.libraries.map((l: any) => l?.name || '') : []
        const loader = libs.some(n => n.startsWith('net.fabricmc:fabric-loader:')) ? 'Fabric' : libs.some(n => /net\.neoforged:/.test(n)) ? 'NeoForge' : libs.some(n => /net\.minecraftforge:/.test(n)) ? 'Forge' : libs.some(n => /org\.quiltmc:/.test(n)) ? 'Quilt' : libs.some(n=>n.startsWith('com.mumfrey:liteloader:'))?'LiteLoader':libs.some(n=>/^optifine:/i.test(n))?'OptiFine':'Vanilla'
        const options = state.options[id] ?? { name: entry.name, group: '', favorite: false, memory: state.settings.memory, java: '', protected:false, memoryMode:'inherit' as const, isolated: await exists(path.join(versionDir, 'mods')) || await exists(path.join(versionDir, 'saves')) }
        instances.push({ ...options, id, sourceId: source.id, root, version: entry.name, minecraft: meta.inheritsFrom || meta.id || entry.name, loader, gameDir: options.isolated ? versionDir : root, issue, lastPlayed: state.lastPlayed[id] })
      }
    }
  }
  instances.sort((a, b) => Number(b.favorite) - Number(a.favorite) || a.name.localeCompare(b.name, 'zh-CN', { numeric: true }))
  return { instances, warnings }
}
export async function assertSafeVersionDirectory(instance: Instance, sources: Source[]) {
  const versionRoot = await canonical(path.join(instance.root, 'versions'))
  const target = await canonical(path.join(instance.root, 'versions', instance.version))
  if (!inside(versionRoot, target) || path.dirname(target).toLowerCase() !== versionRoot.toLowerCase()) throw new Error('实例目录越界，已阻止删除')
  for (const source of sources) { const sourcePath = await canonical(source.path); if (sourcePath.toLowerCase() === target.toLowerCase() || inside(target, sourcePath)) throw new Error('此实例目录同时包含已登记的目录，请先移除目录登记') }
  return target
}
export async function versionDependents(instance: Instance) {
  const dependents:string[]=[],unreadable:string[]=[]
  const directory=path.join(instance.root,'versions')
  for(const entry of await readdir(directory,{withFileTypes:true})){
    if(!entry.isDirectory()||entry.isSymbolicLink()||entry.name===instance.version)continue
    const file=path.join(directory,entry.name,`${entry.name}.json`)
    if(!await exists(file))continue
    try{const meta=JSON.parse(await readFile(file,'utf8'));if(meta.inheritsFrom===instance.version||meta.jar===instance.version)dependents.push(entry.name)}
    catch{unreadable.push(entry.name)}
  }
  return {dependents,unreadable}
}
