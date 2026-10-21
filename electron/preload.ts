import { contextBridge, ipcRenderer } from 'electron'
const allowed = new Set(['snapshot','workspace.save','preset.save','preset.delete','source.add','source.remove','scan','instance.save','instance.remove','instance.delete','instance.open','instance.launch','instance.stop','settings.save','java.detect','java.choose','versions','install','account.login','account.cancel','account.select','account.remove','task.cancel','open','window.minimize','window.maximize','window.close','window.closeReady','plugins.list','plugins.command','logs.export'])
allowed.add('account.reset')
allowed.add('appearance.save')
for(const method of ['loaders.list','account.reopen','account.copyCode','java.addFolder','java.removeFolder','account.offline','mods.list','mods.import','mods.toggle','mods.delete','resource.search','resource.versions','resource.plan','resource.install','resource.open','resource.configure'])allowed.add(method)
contextBridge.exposeInMainWorld('launcher', {
  invoke: async (method: string, payload?: unknown) => {
    if (!allowed.has(method)) throw new Error('不支持的操作')
    const result = await ipcRenderer.invoke('hakf:invoke', method, payload)
    if (!result.ok) throw new Error(result.error)
    return result.value
  },
  onEvent: (callback: (event: unknown) => void) => { const handler = (_event: unknown, data: unknown) => callback(data); ipcRenderer.on('hakf:event',handler); return () => ipcRenderer.removeListener('hakf:event',handler) }
})
