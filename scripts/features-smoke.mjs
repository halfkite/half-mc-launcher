import { _electron as electron, expect } from '@playwright/test'
import { mkdtemp,mkdir,writeFile,readFile,readdir } from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'
import assert from 'node:assert/strict'
const directory=await mkdtemp(path.join(os.tmpdir(),'hakf-r3-')),root=path.join(directory,'中文 实例'),name='fabric-test'
await mkdir(path.join(root,'versions',name,'mods'),{recursive:true})
await writeFile(path.join(root,'versions',name,name+'.json'),JSON.stringify({id:name,inheritsFrom:'1.21.1',mainClass:'net.fabricmc.loader.impl.launch.knot.KnotClient',libraries:[{name:'net.fabricmc:fabric-loader:0.16.0'}]}))
const env={...process.env,HAKF_DATA_DIR:directory};delete env.ELECTRON_RUN_AS_NODE
let desktop
const errors=[]
try{
  desktop=await electron.launch({args:['.'],env,timeout:30000});let page=await desktop.firstWindow();page.on('pageerror',e=>errors.push(e.message));await page.waitForSelector('.welcome-card')
  await desktop.evaluate(({dialog},root)=>{dialog.showOpenDialog=async()=>({canceled:false,filePaths:[root]})},root)
  await page.getByRole('button',{name:'添加目录',exact:true}).click();await page.waitForSelector('.instance-card')
  const instance=(await page.evaluate(()=>window.launcher.invoke('snapshot'))).instances[0]
  assert.equal(instance.memoryMode,'inherit')
  await page.getByRole('button',{name:'添加账户',exact:true}).click()
  await page.getByRole('button',{name:'离线账户',exact:true}).click()
  await page.getByLabel('离线账户名称').fill('Half_Tester')
  await page.locator('.offline-form').getByRole('button',{name:'添加',exact:true}).click()
  await expect(page.locator('.account-card h3')).toHaveText('Half_Tester')
  await page.getByRole('button',{name:'关闭账户管理'}).click()
  assert.equal((await page.evaluate(()=>window.launcher.invoke('snapshot'))).state.accounts[0].kind,'offline')
  await page.locator('.sidebar nav button').filter({hasText:'设置'}).click()
  await page.getByLabel('字体大小',{exact:true}).fill('150')
  await expect.poll(async()=>(await page.evaluate(()=>window.launcher.invoke('snapshot'))).state.settings.fontScale).toBe(150)
  assert.equal(await page.evaluate(()=>getComputedStyle(document.documentElement).getPropertyValue('--font-scale').trim()),'1.5')
  await page.getByLabel('全局内存分配方式').selectOption('auto')
  await page.getByRole('button',{name:'保存设置',exact:true}).click()
  await expect.poll(async()=>(await page.evaluate(()=>window.launcher.invoke('snapshot'))).state.settings.memoryMode).toBe('auto')
  const runtimes=await page.evaluate(()=>window.launcher.invoke('java.detect'));assert.ok(runtimes.length)
  await page.getByLabel('选择 Java',{exact:true}).selectOption(runtimes[0].path)
  await page.getByRole('button',{name:'保存设置',exact:true}).click()
  await expect.poll(async()=>(await page.evaluate(()=>window.launcher.invoke('snapshot'))).state.settings.java).toBe(runtimes[0].path)
  await desktop.evaluate(({dialog},dir)=>{dialog.showOpenDialog=async()=>({canceled:false,filePaths:[dir]})},path.dirname(path.dirname(runtimes[0].path)))
  await page.getByRole('button',{name:'添加扫描文件夹',exact:true}).click()
  await expect.poll(async()=>(await page.evaluate(()=>window.launcher.invoke('snapshot'))).state.settings.javaRoots.length).toBe(1)
  await expect(page.getByRole('button',{name:'添加扫描文件夹',exact:true})).toBeEnabled()
  await page.locator('.page-scroll').evaluate(e=>e.scrollTop=0)
  await page.screenshot({path:'artifacts/r3-settings-150.png',animations:'disabled'})
  await page.getByLabel('字体大小',{exact:true}).fill('110')
  await page.locator('.sidebar nav button').filter({hasText:'实例'}).click()
  await page.locator('.instance-card').getByRole('button',{name:'Mod',exact:true}).click()
  await expect(page.locator('.mods-modal')).toBeVisible()
  await page.getByRole('button',{name:'下载 Mod',exact:true}).click()
  await expect(page.getByLabel('资源目标实例')).toHaveValue(instance.id)
  await page.getByLabel('搜索资源',{exact:true}).fill('Fabric API')
  await page.locator('.resource-search').getByRole('button',{name:'搜索',exact:true}).click()
  await expect(page.locator('.resource-result').filter({hasText:'Fabric API'}).first()).toBeVisible({timeout:45000})
  await page.locator('.resource-result').filter({hasText:'Fabric API'}).first().click()
  await expect(page.locator('.dependency-plan')).toBeVisible({timeout:45000})
  assert.match(await page.getByLabel('资源版本').inputValue(),/^[A-Za-z0-9]+$/)
  await page.screenshot({path:'artifacts/r3-resource-download.png',animations:'disabled'})
  await page.getByRole('button',{name:'安装到此实例',exact:true}).click()
  await expect.poll(async()=>{const s=await page.evaluate(()=>window.launcher.invoke('snapshot'));return s.tasks.find(t=>t.title.includes('安装资源'))?.status},{timeout:120000}).toBe('success')
  const mods=await page.evaluate(id=>window.launcher.invoke('mods.list',id),instance.id);assert.ok(mods.some(m=>/fabric api/i.test(m.name)))
  await page.evaluate(p=>window.launcher.invoke('mods.toggle',p),{id:instance.id,file:mods[0].file})
  assert.equal((await page.evaluate(id=>window.launcher.invoke('mods.list',id),instance.id))[0].enabled,false)
  await page.locator('.sidebar nav button').filter({hasText:'实例'}).click()
  await page.locator('.instance-card').getByRole('button',{name:'Mod',exact:true}).click()
  await expect(page.locator('.mod-row')).toHaveCount(mods.length)
  await page.screenshot({path:'artifacts/r3-mod-manager.png',animations:'disabled'})
  await page.getByRole('button',{name:'关闭 Mod 管理',exact:true}).click()
  await page.locator('.instance-card').getByRole('button',{name:'设置',exact:true}).click()
  await page.getByRole('checkbox',{name:'保护此实例'}).check();await page.getByRole('button',{name:'保存设置',exact:true}).click();await page.waitForSelector('.modal-backdrop',{state:'hidden'})
  await assert.rejects(()=>page.evaluate(p=>window.launcher.invoke('mods.toggle',p),{id:instance.id,file:mods[0].file+'.disabled'}),/实例保护/)
  await page.locator('.sidebar nav button').filter({hasText:'下载'}).click()
  await page.getByLabel('搜索资源',{exact:true}).fill('')
  for(const [label,kind] of [['资源包','resourcepack'],['光影','shader']]){await page.locator('.download-tabs').getByRole('button',{name:label,exact:true}).click();await expect(page.locator('.resource-result').first()).toBeVisible({timeout:45000});console.log('Live catalog:',kind,await page.locator('.resource-result').count())}
  const unlocked=await page.evaluate(id=>window.launcher.invoke('snapshot').then(s=>s.instances.find(i=>i.id===id)),instance.id)
  await page.evaluate(i=>window.launcher.invoke('instance.save',{id:i.id,options:{...i,protected:false}}),unlocked)
  for(const kind of ['resourcepack','shader']){
    const result=await page.evaluate(kind=>window.launcher.invoke('resource.search',{source:'modrinth',kind,game:'1.21.1'}),kind)
    const project=result.projects[0],selection={source:'modrinth',kind,projectId:project.id}
    const versions=await page.evaluate(p=>window.launcher.invoke('resource.versions',p),{selection,instanceId:instance.id});assert.ok(versions.length)
    selection.versionId=versions[0].id
    const id=await page.evaluate(p=>window.launcher.invoke('resource.install',p),{selection,instanceId:instance.id})
    await expect.poll(async()=>(await page.evaluate(()=>window.launcher.invoke('snapshot'))).tasks.find(t=>t.id===id)?.status,{timeout:120000}).toBe('success')
    const folder=kind==='shader'?'shaderpacks':'resourcepacks';assert.ok((await readdir(path.join(instance.gameDir,folder))).includes(versions[0].filename))
    console.log('Live resource download:',kind,project.name,versions[0].filename)
  }
  await page.getByLabel('资源来源').selectOption('curseforge');await expect(page.getByRole('alert')).toContainText('API 密钥',{timeout:15000})
  assert.deepEqual(errors,[])
  const child=desktop.process();const stopped=new Promise(r=>child.once('exit',r));await page.getByRole('button',{name:'关闭启动器',exact:true}).click();await stopped
  // Reading the saved state after a clean restart verifies schema defaults and offline persistence.
  await new Promise(r=>setTimeout(r,700))
  desktop=await electron.launch({args:['.'],env,timeout:30000});page=await desktop.firstWindow();await page.waitForSelector('.page-header');const saved=await page.evaluate(()=>window.launcher.invoke('snapshot'))
  assert.equal(saved.state.settings.fontScale,110);assert.equal(saved.state.accounts[0].name,'Half_Tester');assert.equal(saved.state.settings.javaRoots.length,1)
  console.log('R3 desktop features passed. Actual Modrinth download:',mods.map(m=>m.file).join(', '),'data:',directory)
}finally{await desktop?.close().catch(()=>{})}
