import { _electron as electron,expect } from '@playwright/test'
import { mkdir,readdir,writeFile } from 'node:fs/promises'
import path from 'node:path'
import assert from 'node:assert/strict'
import { Version,MinecraftFolder,generateArguments,LaunchPrecheck } from '@xmcl/core'
const directory=path.resolve('.test-install'),runtimeRoot=path.resolve('.test-java/runtime21')
const java=path.join(runtimeRoot,(await readdir(runtimeRoot))[0],'bin','java.exe')
const env={...process.env,HAKF_DATA_DIR:directory};delete env.ELECTRON_RUN_AS_NODE
const app=await electron.launch({args:['.'],env,timeout:30000}),errors=[]
try{
  const page=await app.firstWindow();page.on('pageerror',e=>errors.push(e.message));await page.waitForSelector('.page-header');await page.keyboard.press('Control+0')
  await page.locator('.sidebar nav button').filter({hasText:'下载'}).click()
  await page.locator('.download-tabs').getByRole('button',{name:'游戏',exact:true}).click()
  await expect(page.getByRole('button',{name:'选择 Forge',exact:true})).toBeVisible({timeout:30000})
  await page.getByLabel('搜索游戏版本',{exact:true}).fill('1.21.1');await page.getByRole('button',{name:/^1\.21\.1\s/}).click()
  await page.getByRole('button',{name:'选择 Forge',exact:true}).click()
  await expect(page.getByLabel('加载器版本')).toBeEnabled({timeout:45000})
  await expect.poll(async()=>await page.getByLabel('加载器版本').inputValue(),{timeout:45000}).not.toBe('')
  await page.screenshot({path:'artifacts/r4-loaders.png',animations:'disabled'})
  await page.getByRole('button',{name:'添加账户',exact:true}).click()
  await expect(page.getByRole('radio',{name:'浏览器授权（推荐）'})).toBeChecked()
  await page.screenshot({path:'artifacts/r4-accounts.png',animations:'disabled'})
  await page.getByRole('button',{name:'关闭账户管理'}).click()
  const reports=[]
  for(const loader of ['fabric','quilt','forge','neoforge','optifine','liteloader']){
    const minecraft=loader==='liteloader'?'1.12.2':'1.21.1'
    const source=loader==='optifine'||loader==='liteloader'?'mirror':'auto'
    const list=await page.evaluate(q=>window.launcher.invoke('loaders.list',q),{minecraft,loader,source})
    assert.ok(list.length,loader+' catalog empty')
    const pinned=loader==='forge'?'52.0.16':loader==='neoforge'?'21.1.1':undefined
    const selected=list.find(v=>v.version===pinned)||list.find(v=>v.recommended)||list.find(v=>v.stable)||list[0]
    const options={version:minecraft,loader,loaderVersion:selected.version,loaderSource:source,java,fabricApi:loader==='fabric',sourceId:''}
    const existing=(await page.evaluate(()=>window.launcher.invoke('snapshot'))).instances.find(i=>i.version===`${minecraft}-${loader}-${selected.version}`)
    let instance=existing
    if(!existing){
      const id=await page.evaluate(p=>window.launcher.invoke('install',p),options)
      await expect.poll(async()=>{const snapshot=await page.evaluate(()=>window.launcher.invoke('snapshot')),task=snapshot.tasks.find(t=>t.id===id);if(task?.status==='error')throw new Error(task.detail);return task?.status},{timeout:600000,intervals:[1000,2000,4000]}).toBe('success')
      instance=(await page.evaluate(()=>window.launcher.invoke('snapshot'))).instances.find(i=>i.version===`${minecraft}-${loader}-${selected.version}`)
    }
    assert.ok(instance,loader+' instance missing');assert.equal(instance.loader.toLowerCase(),loader)
    const resolved=await Version.parse(instance.root,instance.version),launch={version:resolved,gamePath:instance.gameDir,resourcePath:instance.root,javaPath:java,gameProfile:{name:'R4Test',id:'00000000000000000000000000000000'},accessToken:'test-only',maxMemory:2048,nativeRoot:path.join(directory,'r4-natives',loader)}
    const args=await generateArguments(launch);assert.ok(args.includes('--gameDir'))
    for(const check of LaunchPrecheck.Default)await check(MinecraftFolder.from(instance.root),resolved,launch)
    if(loader==='fabric')assert.ok((await page.evaluate(id=>window.launcher.invoke('mods.list',id),instance.id)).some(m=>/Fabric API/i.test(m.name)))
    reports.push({loader,minecraft,selected,version:instance.version,precheck:true});console.log('R4 installed and prechecked:',loader,selected.version,selected.source)
    await writeFile('artifacts/r4-install-results.json',JSON.stringify(reports,null,2))
  }
  assert.deepEqual(errors,[])
  console.log('R4 loader and desktop tests passed')
}finally{await app.close()}
