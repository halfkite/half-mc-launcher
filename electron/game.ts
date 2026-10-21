import { launch, Version, MinecraftFolder } from '@xmcl/core'
import { createDefaultNodeInstallRuntime, executeInstallManifest, resolveMinecraftVersionJsonInstallFile, resolveMinecraftJarInstallFile, resolveLibraryInstallFiles, resolveAssetMetadataInstallManifest, resolveAssetObjectInstallFiles, createFabricInstallWorkflow, createQuiltInstallWorkflow, createModernForgeInstallWorkflow, resolveForgeInstallerFile, resolveOptifineInstallManifest, executeInstallWorkflow, ProgressTrackerMultiple, type InstallManifest, type MinecraftVersion } from '@xmcl/installer'
import { mkdir, readdir, writeFile, stat, rm } from 'node:fs/promises'
import path from 'node:path'
import { memoryBudget } from '../shared/memory'
import { spawn, type ChildProcess } from 'node:child_process'
import { StringDecoder } from 'node:string_decoder'
import os from 'node:os'
import type { Instance, JavaInfo, Release } from '../shared/model'
import { probeJava, discoverJava } from './java'
import type { Store } from './storage'
import type { Accounts } from './accounts'
import type { Tasks } from './tasks'
import { requestJson, downloadDispatcher } from './network'
import { LoaderCatalog, mirror, mavenSha1, timedFetch } from './loaders'
import { installationId, requiredJava, type InstallSelection, type LoaderKind } from '../shared/loaders'

export class GameService {
  running = new Map<string, ChildProcess>()
  launching = new Set<string>()
  installs = new Set<string>()
  private versions: MinecraftVersion[] = []
  java: JavaInfo[] = []
  readonly loaders=new LoaderCatalog()
  constructor(private store: Store, private accounts: Accounts, private tasks: Tasks, private log: (text: string, level?: 'info' | 'error', id?: string) => void, private changed: () => void) {}
  async releases(): Promise<Release[]> {
    const manifest = await requestJson<{versions:MinecraftVersion[]}>('https://piston-meta.mojang.com/mc/game/version_manifest_v2.json')
    this.versions = manifest.versions
    return manifest.versions.map(({id,type,releaseTime,url}) => ({id,type,releaseTime,url}))
  }
  async findJava() { this.java = await discoverJava([this.store.state.settings.java, ...Object.values(this.store.state.options).map(o => o.java)],this.store.state.settings.javaRoots); return this.java }
  async installationJava(minecraft:string,loader:LoaderKind,explicit='') {
    const rule=requiredJava(minecraft,loader)
    const chosen=explicit||this.store.state.settings.java
    if(chosen){const java=await probeJava(chosen);if(java.major<rule.min||java.major>rule.max)throw new Error(`此组件需要 Java ${rule.min}${rule.min===rule.max?'':'–'+rule.max}，当前选择 Java ${java.major}。请更换安装 Java`);return java}
    if(!this.java.length)await this.findJava()
    const java=this.java.find(j=>j.major===rule.min)||this.java.find(j=>j.major>=rule.min&&j.major<=rule.max)
    if(!java)throw new Error(`安装此组件需要 Java ${rule.min}，请先在 Java 管理中添加对应版本`)
    return java
  }
  async install(root:string,input:InstallSelection,complete:(version:string,signal:AbortSignal)=>Promise<void>) {
    if(this.installs.has(root.toLowerCase()))throw new Error('此目录已有安装任务，请等待完成后重试')
    this.installs.add(root.toLowerCase())
    return this.tasks.start(`安装 ${input.version} · ${input.loader==='vanilla'?'原版':input.loader}`,async(_task,signal,update)=>{
      let createdVersion=false,target=''
      try {
        if(!this.versions.length)await this.releases()
        const release=this.versions.find(v=>v.id===input.version);if(!release)throw new Error('找不到所选游戏版本，请刷新列表')
        const entry=input.loader==='vanilla'?undefined:await this.loaders.select({minecraft:input.version,loader:input.loader,source:input.loaderSource},input.loaderVersion)
        const selection={...input,loaderVersion:entry?.version||''},installedId=installationId(selection)
        target=path.join(root,'versions',installedId)
        const exists=await stat(target).catch((e:NodeJS.ErrnoException)=>{if(e.code==='ENOENT')return null;throw e})
        if(exists&&input.loader!=='vanilla')throw new Error('目标加载器实例已存在，请使用现有实例或先处理旧版本，避免覆盖存档')
        const java=['forge','neoforge','optifine'].includes(input.loader)?await this.installationJava(input.version,input.loader,input.java):undefined
        signal.throwIfAborted()
        await mkdir(root,{recursive:true})
        const folder=MinecraftFolder.from(root),tracker=new ProgressTrackerMultiple()
        const runtime=createDefaultNodeInstallRuntime({signal,tracker,maxConcurrency:8,dispatcher:downloadDispatcher,runJava:async command=>{
          signal.throwIfAborted()
          await new Promise<void>((resolve,reject)=>{
            const child=spawn(command.executable,command.args,{cwd:command.cwd,env:{...process.env,...command.env},windowsHide:true,signal})
            let errorText='';child.stdout?.on('data',()=>{});child.stderr?.on('data',chunk=>{errorText=(errorText+chunk.toString()).slice(-8000)})
            child.once('error',reject);child.once('close',code=>code===0?resolve():reject(new Error(`Java 安装步骤失败（退出码 ${code}）：${errorText}`)))
          })
        }})
        const libraryOptions={signal,mavenHost:input.loaderSource==='mirror'?[mirror+'/maven/']:undefined}
        if(input.loaderSource==='auto'){
          const download=runtime.download.bind(runtime)
          runtime.download=files=>download(files.map(file=>{
            const relative=path.relative(path.join(root,'libraries'),file.path)
            if(!relative||relative.startsWith('..')||path.isAbsolute(relative))return file
            const fallback=mirror+'/maven/'+relative.split(path.sep).map(encodeURIComponent).join('/')
            return {...file,urls:[...new Set([...file.urls,fallback])]}
          }))
        }
        let step='下载版本描述',base=0
        const timer=setInterval(()=>{update(`${step}${tracker.total>0?' · '+(tracker.progress/1048576).toFixed(1)+' / '+(tracker.total/1048576).toFixed(1)+' MiB':''}`,base)},600)
        const execute=async(manifest:InstallManifest)=>{await executeInstallManifest(manifest,runtime,{signal,attempts:3})}
        try {
          await execute({schemaVersion:1,tasks:[{id:'version-json',type:'files',files:[resolveMinecraftVersionJsonInstallFile(release,root)]}]})
          let resolved=await Version.parse(root,input.version)
          step='下载游戏与依赖';base=10
          const jar=resolveMinecraftJarInstallFile(resolved)
          await execute({schemaVersion:1,tasks:[{id:'base-libraries',type:'files',files:[...(jar?[jar]:[]),...resolveLibraryInstallFiles(resolved.libraries,folder,libraryOptions)]}]})
          step='下载资源索引';base=30;await execute(resolveAssetMetadataInstallManifest(resolved,folder,{abortSignal:signal}))
          step='下载游戏资源';base=40;await execute({schemaVersion:1,tasks:[{id:'assets',type:'files',files:await resolveAssetObjectInstallFiles(resolved,folder,{abortSignal:signal})}]})
          let installed=input.version
          if(entry){
            createdVersion=true;step='安装 '+input.loader+' '+entry.version;base=70;update(step,base)
            const installOptions={...libraryOptions,versionId:installedId,inheritsFrom:input.version,java:java?.path}
            if(input.loader==='fabric'||input.loader==='quilt'){
              const suffix='/versions/loader/'+encodeURIComponent(input.version)+'/'+encodeURIComponent(entry.version)+'/profile/json'
              const official=(input.loader==='fabric'?'https://meta.fabricmc.net/v2':'https://meta.quiltmc.org/v3')+suffix
              const mirrored=mirror+'/'+input.loader+'-meta/'+(input.loader==='fabric'?'v2':'v3')+suffix
              const options={minecraft:root,minecraftVersion:input.version,version:entry.version,versionId:installedId,signal,profileUrls:input.loaderSource==='mirror'?[mirrored]:input.loaderSource==='auto'?[official,mirrored]:[official]}
              installed=await executeInstallWorkflow(input.loader==='fabric'?createFabricInstallWorkflow(options):createQuiltInstallWorkflow(options),runtime,{signal})
            }else if(input.loader==='forge'||input.loader==='neoforge'){
              let installer
              let artifact=entry.artifact||entry.version
              if(input.loader==='forge'){
                installer=resolveForgeInstallerFile(artifact,entry.forge?.installer,folder,installOptions).file
                if(entry.sha1&&/^[a-f0-9]{40}$/i.test(entry.sha1))installer.checksum={algorithm:'sha1',value:entry.sha1}
                if(entry.source==='BMCLAPI')installer.urls=[mirror+'/maven/net/minecraftforge/forge/'+artifact+'/forge-'+artifact+'-installer.jar',...installer.urls]
              }else{
                const project=entry.project||'neoforge',mavenPath=`net/neoforged/${project}/${entry.version}/${project}-${entry.version}-installer.jar`
                const official='https://maven.neoforged.net/releases/'+mavenPath,mirrored=mirror+'/maven/'+mavenPath
                const urls=entry.source==='BMCLAPI'?[mirrored,official]:input.loaderSource==='official'?[official]:[official,mirrored]
                installer={path:folder.getLibraryByPath(mavenPath),urls,checksum:{algorithm:'sha1',value:await mavenSha1(urls,signal)},validator:'zip' as const}
              }
              const result=await executeInstallWorkflow(createModernForgeInstallWorkflow({id:input.loader,minecraft:folder,minecraftVersion:input.version,installer,artifactVersion:artifact,java:java!.path,installOptions}),runtime,{signal})
              installed=result.version
            }else if(input.loader==='optifine'){
              const installer=path.join(root,'libraries','hakf','optifine',input.version+'-'+entry.version+'.jar')
              await execute({schemaVersion:1,tasks:[{id:'optifine-installer',type:'files',files:[{path:installer,urls:[entry.url!],validator:'zip'}]}]})
              const result=await resolveOptifineInstallManifest(installer,folder,installOptions);installed=result.version
              // XMCL describes the patcher output but does not create its parent.
              // OptiFine opens that file directly and cannot create directories.
              for(const task of result.plan.tasks)if(task.type==='java')for(const output of task.outputs)await mkdir(path.dirname(output.path),{recursive:true})
              await execute(result.plan)
            }else if(input.loader==='liteloader'){
              const meta=entry.lite,libName='com.mumfrey:liteloader:'+meta.version
              if(!/^[a-f0-9]{32}$/i.test(meta.md5||''))throw new Error('LiteLoader 目录缺少有效 MD5 校验值')
              await execute({schemaVersion:1,tasks:[{id:'liteloader-jar',type:'files',files:[{path:folder.getLibraryByPath('com/mumfrey/liteloader/'+meta.version+'/'+meta.file),urls:[mirror+'/liteloader/download?version='+encodeURIComponent(meta.version),entry.url!],checksum:{algorithm:'md5',value:meta.md5},validator:'zip'}]}]})
              const libraries=[{name:libName,url:mirror+'/maven/',downloads:{artifact:{url:entry.url!,path:'com/mumfrey/liteloader/'+meta.version+'/'+meta.file,sha1:'',size:-1}}},...meta.libraries]
              await mkdir(target,{recursive:true})
              await writeFile(path.join(target,installedId+'.json'),JSON.stringify({id:installedId,inheritsFrom:input.version,type:'release',mainClass:'net.minecraft.launchwrapper.Launch',minecraftArguments:'--username ${auth_player_name} --version ${version_name} --gameDir ${game_directory} --assetsDir ${assets_root} --assetIndex ${assets_index_name} --uuid ${auth_uuid} --accessToken ${auth_access_token} --userType ${user_type} --versionType ${version_type} --tweakClass '+meta.tweakClass,libraries}))
              installed=installedId
            }
            if(installed!==installedId)throw new Error('安装组件生成的实例标识与目标不一致，已停止')
            resolved=await Version.parse(root,installed)
            await execute({schemaVersion:1,tasks:[{id:'loader-libraries',type:'files',files:resolveLibraryInstallFiles(resolved.libraries,folder,libraryOptions)}]})
          }
          signal.throwIfAborted();step='完成实例登记';base=95;await complete(installed,signal)
        }finally{clearInterval(timer)}
      }catch(e){
        // Only remove this task's newly created version; shared verified libraries remain reusable.
        if(createdVersion&&target){
          const checked=path.resolve(target),parent=path.resolve(root,'versions')
          if(path.dirname(checked).toLowerCase()!==parent.toLowerCase())throw new Error('安装清理目录越界，已停止')
          await rm(checked,{recursive:true,force:true})
        }
        throw e
      }finally{this.installs.delete(root.toLowerCase());this.changed()}
    })
  }
  async start(instance: Instance) {
    if (this.running.has(instance.id) || this.launching.has(instance.id)) throw new Error('此实例已在运行或启动中')
    if (instance.issue) throw new Error(instance.issue)
    if (this.installs.has(instance.root.toLowerCase())) throw new Error('请等待此目录的安装任务结束')
    this.launching.add(instance.id); this.changed()
    this.tasks.start(`启动 ${instance.name}`, async (_task, signal, update) => {
      try {
        update('解析版本与 Java 要求', 10)
        const version = await Version.parse(instance.root, instance.version)
        const required = version.javaVersion?.majorVersion || 8
        let java: JavaInfo
        const explicit = instance.java || this.store.state.settings.java
        if (explicit) java = await probeJava(explicit)
        else { if (!this.java.length) await this.findJava(); const found = this.java.find(j => j.major === required); if (!found) throw new Error(`此版本需要 Java ${required}。请在工具页检测 Java，或在实例设置中选择 java.exe`); java = found }
        if (java.major < required) throw new Error(`当前 Java ${java.major} 低于此版本要求的 Java ${required}`)
        if(['forge','neoforge','optifine','liteloader'].includes(instance.loader.toLowerCase())){
          const range=requiredJava(instance.minecraft,instance.loader.toLowerCase() as LoaderKind)
          if(java.major>Math.max(required,range.max))throw new Error(`此 ${instance.loader} 实例需要 Java ${required}，当前 Java ${java.major} 过新，请在实例设置中选择兼容版本`)
        }
        const availableMb = Math.floor(os.totalmem() / 1048576)
        const mode=instance.memoryMode==='inherit'?this.store.state.settings.memoryMode:instance.memoryMode
        const mods=await readdir(path.join(instance.gameDir,'mods')).catch((e:NodeJS.ErrnoException)=>{if(e.code==='ENOENT')return [];throw e})
        const memory=mode==='auto'?memoryBudget(availableMb,Math.floor(os.freemem()/1048576),java.arch,instance.loader!=='Vanilla',mods.filter(n=>n.endsWith('.jar')).length):instance.memoryMode==='inherit'?this.store.state.settings.memory:instance.memory
        if (memory > availableMb) throw new Error(`分配内存 ${memory} MB 超过系统总内存 ${availableMb} MB`)
        if(/^(x86|i[3-6]86)$/i.test(java.arch)&&memory>1024)throw new Error('32 位 Java 最多分配 1024 MB，请选择 64 位 Java 或减少内存')
        signal.throwIfAborted()
        update('准备所选账户', 35)
        const session = await this.accounts.session(this.store.state.selectedAccount)
        signal.throwIfAborted()
        await mkdir(instance.gameDir, { recursive: true })
        update('检查依赖并启动游戏', 60)
        this.log(`启动 ${instance.name} · Java ${java.version} · ${memory} MB${mode==='auto'?'（自动）':''} · 游戏目录 ${instance.gameDir}`, 'info', instance.id)
        const process = await launch({ version, gamePath: instance.gameDir, resourcePath: instance.root, javaPath: java.path, gameProfile: {id:session.account.uuid,name:session.account.name}, accessToken:session.accessToken, maxMemory:memory, minMemory:Math.min(512,memory), launcherName:'Half Mc Launcher', launcherBrand:'Half', extraExecOption:{windowsHide:true} })
        this.running.set(instance.id, process)
        const attach = (stream: NodeJS.ReadableStream | null, level: 'info' | 'error') => {
          const decoder = new StringDecoder('utf8'); let pending = ''
          stream?.on('data', (chunk: Buffer) => { pending += decoder.write(chunk); const lines = pending.split(/\r?\n/); pending = lines.pop() || ''; for (const line of lines) if (line) this.log(line,level,instance.id); if(pending.length > 16384){this.log(pending,level,instance.id);pending=''} })
          stream?.on('end', () => { pending += decoder.end(); if(pending) this.log(pending,level,instance.id) })
        }
        attach(process.stdout,'info'); attach(process.stderr,'error')
        process.once('error', error => { this.log(`游戏进程错误：${error.message}`,'error',instance.id); this.running.delete(instance.id); this.changed() })
        process.once('close', code => { this.running.delete(instance.id); this.log(`游戏已退出，退出码 ${code}${code ? '。请查看前面的错误日志以定位原因。' : ''}`, code ? 'error':'info',instance.id); this.changed() })
        this.store.state.lastPlayed[instance.id] = new Date().toISOString(); await this.store.save()
        update('游戏进程已启动', 100); this.changed()
      } finally { this.launching.delete(instance.id); this.changed() }
    }, instance.id)
  }
}
