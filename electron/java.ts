import { execFile } from 'node:child_process'
import { readdir, stat } from 'node:fs/promises'
import path from 'node:path'
import type { JavaInfo } from '../shared/model'

export async function probeJava(file: string): Promise<JavaInfo> {
  return new Promise((resolve, reject) => {
    execFile(file, ['-XshowSettings:properties', '-version'], { windowsHide: true, timeout: 10000, maxBuffer: 128 * 1024 }, (error, stdout, stderr) => {
      if (error) return reject(new Error(`Java 无法运行：${file}。${error.message}`))
      const text = `${stdout}\n${stderr}`
      const version = /(?:java\.version\s*=\s*|version\s+")([^\s"]+)/.exec(text)?.[1]
      if (!version) return reject(new Error(`无法识别 Java 版本：${file}`))
      const parts = version.split(/[._+-]/).map(Number)
      resolve({ path: file, version, major: parts[0] === 1 ? parts[1] : parts[0], arch: /os\.arch\s*=\s*(\S+)/.exec(text)?.[1] || 'unknown' })
    })
  })
}
export async function discoverJava(extra: string[] = [], folders:string[] = []): Promise<JavaInfo[]> {
  const files = new Set(extra.filter(Boolean))
  const pathEntries = (process.env.PATH || '').split(path.delimiter)
  for (const dir of pathEntries) if (dir) files.add(path.join(dir.replace(/^"|"$/g, ''), 'java.exe'))
  if (process.env.JAVA_HOME) files.add(path.join(process.env.JAVA_HOME, 'bin', 'java.exe'))
  const programFiles = process.env.ProgramFiles || 'C:\\Program Files'
  const roots = [path.join(programFiles, 'Java'), path.join(programFiles, 'Eclipse Adoptium'), path.join(programFiles, 'Microsoft'), path.join(programFiles, 'Zulu'), path.join(process.env.APPDATA || '', '.minecraft', 'runtime'), ...folders]
  const visited=new Set<string>()
  let directories=0
  const walk = async (dir: string, depth: number) => {
    const key=path.resolve(dir).toLowerCase()
    if(visited.has(key)||directories>=10000)return
    visited.add(key);directories++
    files.add(path.join(dir,'bin','java.exe'));files.add(path.join(dir,'java.exe'))
    const entries = await readdir(dir, { withFileTypes: true }).catch(() => [])
    for (const entry of entries) if (entry.isDirectory() && !entry.isSymbolicLink()) {
      const child = path.join(dir, entry.name)
      files.add(path.join(child, 'bin', 'java.exe'))
      if (depth > 0) await walk(child, depth - 1)
    }
  }
  for (const root of roots) await walk(root, folders.includes(root)?5:2)
  const candidates = [...files].filter(Boolean)
  const results: JavaInfo[] = []
  // Keep process creation bounded and hide probe consoles on Windows.
  for (let i = 0; i < candidates.length; i += 6) {
    const batch = await Promise.all(candidates.slice(i, i + 6).map(async file => {
      if (!(await stat(file).catch(() => null))?.isFile()) return null
      try { return await probeJava(file) } catch { return null } // Non-Java executables and broken runtimes are not candidates.
    }))
    results.push(...batch.filter((j): j is JavaInfo => j !== null))
  }
  return results.filter((j, i) => results.findIndex(other => other.path.toLowerCase() === j.path.toLowerCase()) === i).sort((a,b) => b.major - a.major)
}
