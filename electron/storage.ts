import { mkdir, readFile, rename, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { z } from 'zod'
import { defaultWorkspace, repairWorkspace } from '../shared/workspace'
import { settingsSchema, workspaceSchema, instanceOptionsSchema, windowBoundsSchema, type State } from '../shared/model'

const stateSchema = z.object({ schemaVersion: z.literal(1), workspace: workspaceSchema, presets: z.array(z.object({id:z.string(), name:z.string().max(80), workspace: workspaceSchema})).max(30), sources: z.array(z.object({id:z.string(),path:z.string(),label:z.string()})), options: z.record(z.string(),instanceOptionsSchema), hidden: z.array(z.string()), accounts: z.array(z.object({id:z.string(),name:z.string(),uuid:z.string(),expiresAt:z.number(),kind:z.enum(['microsoft','offline']).default('microsoft')})), selectedAccount:z.string(), settings: settingsSchema, lastPlayed:z.record(z.string(),z.string()), windowBounds:windowBoundsSchema.default({width:1440,height:930,maximized:false}) })
export function defaultState(): State { return { schemaVersion: 1, workspace: defaultWorkspace(), presets: [], sources: [], options: {}, hidden: [], accounts: [], selectedAccount: '', settings: { theme: 'light', accent: '#74b5ff', memory: 4096, java: '', clientId: '', reduceMotion: false, fontScale:100, opacity:100, glass:true, memoryMode:'auto', download:{source:'auto',threads:4,retries:2,timeout:60}, javaRoots:[] }, lastPlayed: {},windowBounds:{width:1440,height:930,maximized:false} } }
export class Store {
  state = defaultState()
  warning = ''
  private queue: Promise<void> = Promise.resolve()
  constructor(public readonly directory: string) {}
  async load() {
    await mkdir(this.directory, { recursive: true })
    try {
      const raw = JSON.parse(await readFile(path.join(this.directory, 'config.json'), 'utf8'))
      // Preserve previous manually chosen budgets while new instances inherit the global policy.
      if(raw.settings&&raw.settings.memoryMode===undefined)raw.settings.memoryMode='manual'
      for(const option of Object.values(raw.options||{}) as any[])if(option.memoryMode===undefined)option.memoryMode='manual'
      this.state = stateSchema.parse(raw)
      repairWorkspace(this.state.workspace)
    } catch (error: any) {
      if (error.code !== 'ENOENT') {
        const backup = `config.invalid-${Date.now()}.json`
        await rename(path.join(this.directory, 'config.json'), path.join(this.directory, backup))
        this.warning = `配置无法读取，已保留为 ${backup}，正在使用默认配置。`
      }
    }
    await this.save()
  }
  save() {
    const content = JSON.stringify(this.state, null, 2)
    const operation = this.queue.then(async () => {
      const file = path.join(this.directory, 'config.json')
      await writeFile(`${file}.tmp`, content, 'utf8')
      await rename(`${file}.tmp`, file)
    })
    this.queue = operation.catch(() => {}) // Keep subsequent writes usable; the caller receives the original rejection.
    return operation
  }
  flush() { return this.queue }
}
