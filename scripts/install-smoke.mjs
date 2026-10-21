import { _electron as electron, expect } from '@playwright/test'
import path from 'node:path'
import { mkdir, writeFile } from 'node:fs/promises'
import assert from 'node:assert/strict'
import { Version, MinecraftFolder, generateArguments, LaunchPrecheck } from '@xmcl/core'
const directory=path.resolve('.test-install')
await mkdir(directory,{recursive:true})
const env={...process.env,HAKF_DATA_DIR:directory}
delete env.ELECTRON_RUN_AS_NODE
const desktop=await electron.launch({args:['.'],env,timeout:30000})
try{
  const page=await desktop.firstWindow()
  await page.waitForSelector('.page-header')
  const versions=await page.evaluate(()=>window.launcher.invoke('versions'))
  assert.ok(versions.some(v=>v.id==='1.21.1'))
  console.log('Official manifest:',versions.length,'versions')
  const runtimes=await page.evaluate(()=>window.launcher.invoke('java.detect'))
  console.log('Java:',runtimes.map(j=>j.version).join(', '))
  for(const fabric of [false,true]){
    const id=await page.evaluate(fabric=>window.launcher.invoke('install',{version:'1.21.1',fabric,sourceId:''}),fabric)
    let report=0
    while(true){
      const snap=await page.evaluate(()=>window.launcher.invoke('snapshot'))
      const task=snap.tasks.find(t=>t.id===id)
      if(Date.now()-report>15000||task.status!=='running'){console.log(task.status,task.title,task.detail);report=Date.now()}
      if(task.status!=='running'){
        await writeFile(`artifacts/install-${fabric?'fabric':'vanilla'}.json`,JSON.stringify({task,instances:snap.instances,java:runtimes.map(j=>({major:j.major,version:j.version}))},null,2))
        if(task.status==='error'&&fabric&&/目标加载器实例已存在/.test(task.detail))console.log('Existing loader protected from overwrite as expected')
        else assert.equal(task.status,'success',task.detail)
        assert.ok(snap.instances.some(i=>i.loader===(fabric?'Fabric':'Vanilla')))
        const instance=snap.instances.find(i=>i.loader===(fabric?'Fabric':'Vanilla'))
        const resolved=await Version.parse(instance.root,instance.version)
        const options={version:resolved,gamePath:instance.gameDir,resourcePath:instance.root,javaPath:runtimes[0]?.path||'java',gameProfile:{name:'BuildVerification',id:'00000000000000000000000000000000'},accessToken:'verification-only',maxMemory:4096,nativeRoot:path.join(directory,'native-check',fabric?'fabric':'vanilla')}
        const args=await generateArguments(options)
        assert.equal(path.resolve(args[args.indexOf('--gameDir')+1]),path.resolve(instance.gameDir))
        assert.ok(args.some(a=>a.includes(fabric?'KnotClient':'net.minecraft.client.main.Main')))
        for(const check of LaunchPrecheck.Default)await check(MinecraftFolder.from(instance.root),resolved,options)
        console.log('Launch arguments and native/library prechecks passed:',instance.loader)
        break
      }
      await page.waitForTimeout(1500)
    }
  }
  const auth=await page.evaluate(()=>window.launcher.invoke('snapshot'))
  assert.equal(auth.authSource,'builtin');assert.match(auth.authClientId,/^[0-9a-f-]{36}$/i)
  const cancelled=await page.evaluate(async()=>{const id=await window.launcher.invoke('install',{version:'1.21.1',fabric:false,sourceId:''});await window.launcher.invoke('task.cancel',id);return id})
  await expect.poll(async()=>(await page.evaluate(()=>window.launcher.invoke('snapshot'))).tasks.find(t=>t.id===cancelled)?.status,{timeout:15000}).toBe('cancelled')
  console.log('Real vanilla + Fabric installation passed. Bundled Microsoft client id guard passed.')
}finally{await desktop.close()}
