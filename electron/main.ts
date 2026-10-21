import { app, BrowserWindow, ipcMain, dialog, shell, Menu, screen } from 'electron'
import path from 'node:path'
import os from 'node:os'
import { mkdirSync } from 'node:fs'
import { mkdir, readdir, readFile, writeFile, appendFile, stat } from 'node:fs/promises'
import { pathToFileURL } from 'node:url'
import { randomUUID } from 'node:crypto'
import { z } from 'zod'
import { Store } from './storage'
import { canonical, findRoots, scanSources, assertSafeVersionDirectory, instanceId, versionDependents } from './instances'
import { Accounts } from './accounts'
import { Tasks } from './tasks'
import { GameService } from './game'
import { PluginHost } from './plugins'
import { instanceOptionsSchema, settingsSchema, workspaceSchema, type Instance, type LogEntry, type Snapshot } from '../shared/model'
import { repairWorkspace } from '../shared/workspace'
import { assertInstanceUnprotected } from '../shared/instance-list'
import { Resources, resourceSelection } from './resources'
import { listMods, importMods, toggleMod, modPath } from './mods'
import { installSelection } from '../shared/loaders'
import { loaderQuery } from './loaders'

const base = process.env.HAKF_DATA_DIR ? path.resolve(process.env.HAKF_DATA_DIR) : path.join(process.env.PORTABLE_EXECUTABLE_DIR || (app.isPackaged ? path.dirname(app.getPath('exe')) : process.cwd()), 'data')
mkdirSync(base, { recursive: true })
app.setPath('userData',path.join(base,'chromium'))
app.setPath('sessionData',path.join(base,'chromium'))
app.setPath('logs',path.join(base,'logs'))
const store = new Store(base)
let win: BrowserWindow | undefined, instances: Instance[] = [], warnings: string[] = [], closing = false
const emit = (type: string, data: unknown) => { if(win && !win.isDestroyed()) win.webContents.send('hakf:event',{type,data}) }
const logs: LogEntry[] = []
let logWrites: Promise<void> = Promise.resolve()
const logFile = path.join(base,'logs',`launcher-${new Date().toISOString().replace(/[:.]/g,'-')}.log`)
const log = (text: string, level: 'info' | 'error' = 'info', instanceId?: string) => {
  // Never put credentials from launch arguments or game output into persisted logs.
  const redacted = text.replace(/(Bearer\s+|--accessToken\s+|"?access_token"?\s*[:=]\s*"?)([^\s",]+)/gi,'$1[REDACTED]').slice(0,20000)
  const entry: LogEntry = { time: new Date().toISOString(), level, text:redacted, instanceId }
  logs.push(entry); if(logs.length>1500) logs.splice(0,logs.length-1500)
  emit('log',entry)
  logWrites = logWrites.then(()=>appendFile(logFile,`${entry.time} [${level}] ${instanceId || 'launcher'} ${redacted}\n`)).catch(e=>console.error('日志写入失败',e.message))
}
const tasks = new Tasks(emit,log)
// Optional per-installation override: <data>/client-id.txt, read once at startup.
let fileClientId = ''
const accounts = new Accounts(store,emit,()=>fileClientId)
const resources=new Resources(base,tasks)
const refresh = async () => { const result = await scanSources(store.state); instances = result.instances; warnings = result.warnings; emitSnapshot(); return snapshot() }
const game = new GameService(store,accounts,tasks,log,()=>{emitSnapshot()})
const plugins = new PluginHost()
function snapshot(): Snapshot { const client=accounts.clientIdInfo(); return { state:store.state, instances:instances.map(i=>({...i,running:game.running.has(i.id)||game.launching.has(i.id),lastPlayed:store.state.lastPlayed[i.id]})), tasks:tasks.entries,logs,dataPath:base,appVersion:app.getVersion(),memoryMb:Math.floor(os.totalmem()/1048576),scanWarnings:warnings,authConfigured:accounts.configured(),authClientId:client.id,authSource:client.source } }
function emitSnapshot(){emit('snapshot',snapshot())}
const idSchema = z.string().min(1).max(200)
const getInstance = (id: unknown) => { const key=idSchema.parse(id), instance=instances.find(i=>i.id===key); if(!instance) throw new Error('此实例已不存在，请重新扫描'); return instance }
const idle = (i: Instance) => { if(game.running.has(i.id)||game.launching.has(i.id)||game.installs.has(i.root.toLowerCase())||resources.locks.has(path.resolve(i.gameDir).toLowerCase())) throw new Error('实例正在运行或安装中，请等待结束') }
const resourceIdle=(i:Instance)=>{idle(i);assertInstanceUnprotected(i);if(instances.some(other=>other.gameDir.toLowerCase()===i.gameDir.toLowerCase()&&(game.running.has(other.id)||game.launching.has(other.id))))throw new Error('另一个使用相同游戏目录的实例正在运行')}
async function mutateResources<T>(i:Instance,work:()=>Promise<T>){resourceIdle(i);const key=path.resolve(i.gameDir).toLowerCase();resources.locks.add(key);try{return await work()}finally{resources.locks.delete(key)}}
const openDirectory = async (directory: string) => { const error = await shell.openPath(directory); if(error) throw new Error(error) }

async function handle(method: string, payload: any): Promise<unknown> {
  switch(method) {
    case 'snapshot': return snapshot()
    case 'workspace.save': store.state.workspace=repairWorkspace(workspaceSchema.parse(payload)); await store.save(); return true
    case 'preset.save': { const {name,workspace}=z.object({name:z.string().trim().min(1).max(80),workspace:workspaceSchema}).parse(payload); if(store.state.presets.length>=30)throw new Error('最多保存 30 个预设'); store.state.presets.push({id:randomUUID(),name,workspace:repairWorkspace(workspace)}); await store.save();emitSnapshot(); return true }
    case 'preset.delete': store.state.presets=store.state.presets.filter(p=>p.id!==idSchema.parse(payload));await store.save();emitSnapshot();return true
    case 'source.add': {
      const result=await dialog.showOpenDialog(win!,{title:'添加 Minecraft 目录或实例容器文件夹',properties:['openDirectory']})
      if(result.canceled)return null
      const directory=await canonical(result.filePaths[0])
      if(store.state.sources.some(s=>s.path.toLowerCase()===directory.toLowerCase()))throw new Error('此目录已经添加')
      await findRoots(directory)
      const source={id:randomUUID(),path:directory,label:path.basename(directory)||directory}
      store.state.sources.push(source)
      // Re-adding a source makes previously removed records discoverable again.
      const rediscovered=new Set((await scanSources({...store.state,sources:[source],hidden:[]})).instances.map(i=>i.id))
      store.state.hidden=store.state.hidden.filter(id=>!rediscovered.has(id))
      await store.save();return refresh()
    }
    case 'source.remove': store.state.sources=store.state.sources.filter(s=>s.id!==idSchema.parse(payload));await store.save();return refresh()
    case 'scan': return refresh()
    case 'instance.save': {const {id,options}=z.object({id:idSchema,options:instanceOptionsSchema}).parse(payload); const i=getInstance(id);idle(i);store.state.options[id]=options;await store.save();return refresh()}
    case 'instance.remove': {const i=getInstance(payload);idle(i);assertInstanceUnprotected(i);store.state.hidden=[...new Set([...store.state.hidden,i.id])];await store.save();return refresh()}
    case 'instance.delete': {
      const i=getInstance(payload);idle(i)
      assertInstanceUnprotected(i)
      const target=await assertSafeVersionDirectory(i,store.state.sources)
      const references=await versionDependents(i)
      if(references.dependents.length)throw new Error(`以下版本依赖此实例，包含已隐藏的记录：${references.dependents.join('、')}。请先处理依赖版本。`)
      const unknown=references.unreadable.length?`\n\n另有 ${references.unreadable.length} 个版本描述损坏，无法确认其依赖关系：${references.unreadable.join('、')}`:''
      const result=await dialog.showMessageBox(win!,{type:'warning',title:'删除实例文件',message:`将“${i.name}”移至回收站？`,detail:`仅处理此目录：\n${target}\n\n该目录中的独立存档、模组和配置也会移入回收站。共享 assets、libraries 和根目录中的存档保留。${unknown}`,buttons:['取消','移至回收站'],defaultId:0,cancelId:0,noLink:true})
      if(result.response!==1)return false
      idle(i)
      const checked=await assertSafeVersionDirectory(i,store.state.sources)
      if(checked!==target)throw new Error('实例目录发生变化，请重新扫描')
      await shell.trashItem(checked);delete store.state.options[i.id];await store.save();await refresh();return true
    }
    case 'instance.open': return openDirectory(getInstance(payload).gameDir)
    case 'instance.launch': {const i=getInstance(payload);if(resources.locks.has(path.resolve(i.gameDir).toLowerCase()))throw new Error('请等待资源安装完成');await plugins.beforeLaunch(i);await game.start(i);return true}
    case 'mods.list': return listMods(getInstance(payload))
    case 'mods.import': {const i=getInstance(payload);resourceIdle(i);const result=await dialog.showOpenDialog(win!,{title:'导入本地模组',properties:['openFile','multiSelections'],filters:[{name:'Minecraft 模组',extensions:['jar']}]});if(result.canceled)return null;await mutateResources(i,()=>importMods(i,result.filePaths));return listMods(i)}
    case 'mods.toggle': {const p=z.object({id:idSchema,file:z.string().max(200)}).parse(payload),i=getInstance(p.id);await mutateResources(i,()=>toggleMod(i,p.file));return listMods(i)}
    case 'mods.delete': {const p=z.object({id:idSchema,file:z.string().max(200)}).parse(payload),i=getInstance(p.id);resourceIdle(i);const target=await modPath(i,p.file);const result=await dialog.showMessageBox(win!,{type:'question',message:`将 ${p.file} 移至回收站？`,detail:target,buttons:['取消','移至回收站'],defaultId:0,cancelId:0});if(result.response!==1)return null;await mutateResources(i,async()=>shell.trashItem(await modPath(i,p.file)));return listMods(i)}
    case 'resource.search': return resources.search(payload)
    case 'resource.versions': {const p=z.object({selection:resourceSelection,instanceId:idSchema.optional()}).parse(payload);return resources.versions(p.selection,p.instanceId?getInstance(p.instanceId):undefined)}
    case 'resource.plan': {const p=z.object({selection:resourceSelection,instanceId:idSchema}).parse(payload);return resources.plan(p.selection,getInstance(p.instanceId))}
    case 'resource.install': {const p=z.object({selection:resourceSelection,instanceId:idSchema}).parse(payload),i=getInstance(p.instanceId);resourceIdle(i);return resources.install(p.selection,i)}
    case 'resource.configure': if(payload===undefined)return resources.configured();return resources.configure(payload)
    case 'resource.open': return shell.openExternal(await resources.projectUrl(payload))
    case 'instance.stop': {const i=getInstance(payload),process=game.running.get(i.id);if(!process)throw new Error('游戏尚未运行');const r=await dialog.showMessageBox(win!,{type:'warning',message:`结束“${i.name}”的游戏进程？`,detail:'尚未保存的游戏进度可能丢失，建议优先在游戏中退出。',buttons:['取消','结束进程'],defaultId:0,cancelId:0});if(r.response===1)process.kill();return r.response===1}
    case 'settings.save': store.state.settings=settingsSchema.parse(payload);await store.save();emitSnapshot();return true
    case 'appearance.save': Object.assign(store.state.settings,settingsSchema.pick({theme:true,accent:true,reduceMotion:true,fontScale:true}).parse(payload));await store.save();emitSnapshot();return true
    case 'java.detect': return game.findJava()
    case 'java.addFolder': {const result=await dialog.showOpenDialog(win!,{title:'选择 Java 所在目录（扫描最多 5 层）',properties:['openDirectory']});if(result.canceled)return null;const directory=await canonical(result.filePaths[0]);if(!store.state.settings.javaRoots.some(p=>p.toLowerCase()===directory.toLowerCase())){if(store.state.settings.javaRoots.length>=30)throw new Error('最多添加 30 个 Java 搜索目录');store.state.settings.javaRoots.push(directory);await store.save();emitSnapshot()}return game.findJava()}
    case 'java.removeFolder': store.state.settings.javaRoots=store.state.settings.javaRoots.filter(p=>p!==z.string().parse(payload));await store.save();emitSnapshot();return game.findJava()
    case 'java.choose': {const result=await dialog.showOpenDialog(win!,{title:'选择 Java 可执行文件',properties:['openFile'],filters:[{name:'Java 可执行文件',extensions:['exe']}]});return result.canceled?null:result.filePaths[0]}
    case 'versions': return game.releases()
    case 'loaders.list': {const p=loaderQuery.extend({refresh:z.boolean().default(false)}).parse(payload);return game.loaders.list(p,p.refresh)}
    case 'install': {
      // Keep old saved wizard callers and r3 test clients compatible.
      const legacy=payload?.loader===undefined&&typeof payload?.fabric==='boolean'?{...payload,loader:payload.fabric?'fabric':'vanilla'}:payload
      const input=installSelection.parse(legacy)
      let root: string
      if(input.sourceId){const source=store.state.sources.find(s=>s.id===input.sourceId);if(!source)throw new Error('请选择已登记目录');const roots=await findRoots(source.path);if(roots.length!==1)throw new Error('此目录包含多个游戏根目录，请单独添加目标 .minecraft 目录');root=roots[0]}
      else {root=path.join(base,'minecraft');await mkdir(path.join(root,'versions'),{recursive:true});if(!store.state.sources.some(s=>s.path===root)){store.state.sources.push({id:randomUUID(),path:root,label:'便携游戏目录'});await store.save()}}
      for(const existing of instances)if(existing.root.toLowerCase()===root.toLowerCase()&&(game.running.has(existing.id)||game.launching.has(existing.id)))throw new Error('请先退出此目录中正在运行的游戏再安装')
      for(const existing of instances)if(existing.root.toLowerCase()===root.toLowerCase()&&existing.protected&&(existing.version===input.version||(existing.minecraft===input.version&&existing.loader.toLowerCase()===input.loader)))assertInstanceUnprotected(existing)
      return game.install(root,input,async(version,signal)=>{
        const id=instanceId(root,version),options={name:version,group:'',favorite:false,memory:store.state.settings.memory,java:['forge','neoforge','optifine'].includes(input.loader)?input.java:'',isolated:true,protected:false,memoryMode:'inherit' as const}
        if(input.fabricApi){
          const instance:Instance={...options,id,version,sourceId:'',root,gameDir:path.join(root,'versions',version),minecraft:input.version,loader:'Fabric'}
          const choices=await resources.versions({source:'modrinth',kind:'mod',projectId:'fabric-api'},instance,signal)
          if(!choices.length)throw new Error('未找到与此游戏版本兼容的 Fabric API')
          const selected=choices[0],task=resources.install({source:'modrinth',kind:'mod',projectId:selected.projectId,versionId:selected.id},instance)
          await tasks.wait(task,signal)
        }
        signal.throwIfAborted();store.state.options[id]??=options;store.state.hidden=store.state.hidden.filter(h=>h!==id);await store.save();await refresh()
      })
    }
    case 'account.login': {const method=z.enum(['browser','device']).parse(payload??'browser');const a=await accounts.login(method);emitSnapshot();return a}
    case 'account.reopen': await accounts.reopen();return true
    case 'account.copyCode': accounts.copyCode();return true
    case 'account.offline': {const a=await accounts.addOffline(payload);emitSnapshot();return a}
    case 'account.cancel': accounts.cancel();return true
    case 'account.select': {const id=idSchema.parse(payload);if(!store.state.accounts.some(a=>a.id===id))throw new Error('账户不存在');store.state.selectedAccount=id;await store.save();emitSnapshot();return true}
    case 'account.remove': await accounts.remove(idSchema.parse(payload));emitSnapshot();return true
    case 'account.reset': {const result=await dialog.showMessageBox(win!,{type:'warning',title:'清理登录缓存',message:'移除全部账户及本机登录凭据？',detail:'游戏文件保留。下次启动游戏前需要重新登录。',buttons:['取消','清理'],defaultId:0,cancelId:0,noLink:true});if(result.response!==1)return false;await accounts.reset();emitSnapshot();return true}
    case 'task.cancel': tasks.cancel(idSchema.parse(payload));return true
    case 'plugins.list': return plugins.list()
    case 'plugins.command': {const command=plugins.commands.get(idSchema.parse(payload));if(!command)throw new Error('插件命令不存在');return command.action()}
    case 'logs.export': {const result=await dialog.showSaveDialog(win!,{title:'导出启动器日志',defaultPath:'Half-launcher.log',filters:[{name:'日志',extensions:['log']}]});if(!result.canceled&&result.filePath)await writeFile(result.filePath,logs.map(l=>`${l.time} [${l.level}] ${l.text}`).join('\n'));return !result.canceled}
    case 'open': {
      const kind=z.enum(['data','logs','website','redeem','microsoft','registration']).parse(payload)
      if(kind==='data'||kind==='logs')return openDirectory(kind==='data'?base:path.join(base,'logs'))
      return shell.openExternal({website:'https://www.minecraft.net/',redeem:'https://www.minecraft.net/redeem',microsoft:'https://microsoft.com/devicelogin',registration:'https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade'}[kind])
    }
    case 'window.minimize': win?.minimize();return true
    case 'window.maximize': if(win?.isMaximized())win.unmaximize();else win?.maximize();return true
    case 'window.close': win?.close();return true
    case 'window.closeReady': accounts.cancel();if(win)store.state.windowBounds={...win.getNormalBounds(),maximized:win.isMaximized()};await store.save();await logWrites;closing=true;tasks.shutdown();win?.close();return true
    default: throw new Error('不支持的操作')
  }
}

async function createWindow() {
  const remembered=store.state.windowBounds
  const area=remembered.x!==undefined&&remembered.y!==undefined?screen.getDisplayMatching({x:remembered.x,y:remembered.y,width:remembered.width,height:remembered.height}).workArea:screen.getPrimaryDisplay().workArea
  const width=Math.min(area.width,Math.max(1050,remembered.width)),height=Math.min(area.height,Math.max(680,remembered.height))
  const x=remembered.x===undefined?undefined:Math.max(area.x,Math.min(remembered.x,area.x+area.width-width)),y=remembered.y===undefined?undefined:Math.max(area.y,Math.min(remembered.y,area.y+area.height-height))
  win=new BrowserWindow({width,height,x,y,minWidth:Math.min(area.width,1050),minHeight:Math.min(area.height,680),frame:false,show:false,backgroundColor:'#0e1110',title:'Half Mc Launcher',icon:path.join(__dirname,'icon.ico'),webPreferences:{preload:path.join(__dirname,'preload.cjs'),contextIsolation:true,nodeIntegration:false,sandbox:true,webSecurity:true}})
  // Use the same outer-bounds API for restoring and saving; constructor sizing
  // can include different invisible Windows frame metrics at fractional DPI.
  win.setBounds({width,height,...(x===undefined?{}:{x}),...(y===undefined?{}:{y})})
  Menu.setApplicationMenu(null)
  win.webContents.setWindowOpenHandler(()=>({action:'deny'}))
  win.webContents.on('will-navigate',event=>event.preventDefault())
  win.webContents.session.setPermissionRequestHandler((_wc,_permission,callback)=>callback(false))
  win.on('close',event=>{if(!closing){event.preventDefault();emit('closeRequested',null)}})
  if(remembered.maximized)win.maximize()
  win.once('ready-to-show',()=>win?.show())
  if(process.env.HAKF_DEV_URL==='http://127.0.0.1:5173')await win.loadURL(process.env.HAKF_DEV_URL)
  else await win.loadFile(path.join(__dirname,'../dist/index.html'))
}
if(!app.requestSingleInstanceLock())app.quit()
else {
  app.on('second-instance',()=>{if(win?.isMinimized())win.restore();win?.focus()})
  app.whenReady().then(async()=>{
    await mkdir(path.join(base,'logs'),{recursive:true})
    try { fileClientId=(await readFile(path.join(base,'client-id.txt'),'utf8')).split(/\r?\n/)[0].trim() } catch(error:any) { if(error.code!=='ENOENT') log('读取 client-id.txt 失败：'+error.message,'error') }
    await store.load();await accounts.load();await resources.load().catch(e=>log(e.message,'error'))
    plugins.load({id:'hakf.example',name:'工作区助手',version:'0.1.0',description:'内置示例扩展，演示命令注册与启动前事件。',activate(ctx){ctx.registerCommand('diagnose','检查启动环境',async()=>{const java=await game.findJava();ctx.log(`发现 ${java.length} 个可用 Java，登记 ${store.state.sources.length} 个目录`);return {javaCount:java.length}});ctx.onBeforeLaunch(async i=>{ctx.log(`准备启动 ${i.name}`)})}},message=>log(`[扩展] ${message}`))
    ipcMain.handle('hakf:invoke',async(event,method,payload)=>{
      try {if(event.sender!==win?.webContents||event.senderFrame!==win.webContents.mainFrame)throw new Error('拒绝非主页面请求');if(typeof method!=='string')throw new Error('操作名称无效');return {ok:true,value:await handle(method,payload)}} catch(error:any){const message=error instanceof z.ZodError?'输入无效：'+error.issues.map(i=>i.message).join('；'):String(error?.message||error);return {ok:false,error:message}}
    })
    await refresh();await createWindow();log('Half Mc Launcher 0.1.0 已就绪');if(store.warning)log(store.warning,'error')
  }).catch(error=>{dialog.showErrorBox('Half 启动失败',String(error?.stack||error));app.exit(1)})
  app.on('window-all-closed',()=>{tasks.shutdown();app.quit()})
}
