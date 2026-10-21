import type { Instance } from '../shared/model'
export interface PluginContext {
  registerCommand(id: string, title: string, action: () => Promise<unknown>): void
  onBeforeLaunch(callback: (instance: Readonly<Instance>) => Promise<void>): void
  log(message: string): void
}
export interface LauncherPlugin { id: string; name: string; version: string; description: string; activate(context: PluginContext): void }
export class PluginHost {
  commands = new Map<string, { title: string; pluginId: string; action: () => Promise<unknown> }>()
  hooks: Array<(instance: Readonly<Instance>) => Promise<void>> = []
  plugins: Array<Omit<LauncherPlugin,'activate'>> = []
  load(plugin: LauncherPlugin, log: (message: string) => void) {
    if(this.plugins.some(p => p.id === plugin.id)) throw new Error('插件 ID 重复')
    plugin.activate({ registerCommand: (id,title,action) => { const key = `${plugin.id}:${id}`; if(this.commands.has(key)) throw new Error('插件命令重复'); this.commands.set(key,{title,pluginId:plugin.id,action}) }, onBeforeLaunch: callback => this.hooks.push(callback), log })
    const {activate,...info} = plugin; this.plugins.push(info)
  }
  list() { return this.plugins.map(p=>({...p,commands:[...this.commands.entries()].filter(([,c])=>c.pluginId===p.id).map(([id,c])=>({id,title:c.title}))})) }
  async beforeLaunch(instance: Instance) { for(const hook of this.hooks) await hook(Object.freeze({...instance})) }
}
