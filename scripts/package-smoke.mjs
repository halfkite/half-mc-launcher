import { _electron as electron, expect } from '@playwright/test'
import { mkdtemp, readFile, mkdir } from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'
import assert from 'node:assert/strict'
import { listPackage } from '@electron/asar'
const portableDirectory=await mkdtemp(path.join(os.tmpdir(),'hakf-packaged-'))
const executablePath=path.resolve(process.argv[2]||'release/win-unpacked/Half Mc Launcher.exe')
const env={...process.env,PORTABLE_EXECUTABLE_DIR:portableDirectory}
delete env.ELECTRON_RUN_AS_NODE;delete env.HAKF_DATA_DIR;delete env.HAKF_DEV_URL
const desktop=await electron.launch({executablePath,args:[],env,timeout:45000})
const childProcess=desktop.process()
try{
  const page=await desktop.firstWindow(),errors=[]
  page.on('pageerror',e=>errors.push(e.message))
  await page.waitForSelector('.welcome-card',{timeout:20000})
  const snapshot=await page.evaluate(()=>window.launcher.invoke('snapshot'))
  assert.equal(snapshot.state.workspace.layout,'single')
  assert.equal(await page.locator('.pane').count(),1)
  const packagedFiles=listPackage(path.join(path.dirname(executablePath),'resources','app.asar'))
  assert.ok(!packagedFiles.some(file=>/^[\\/]node_modules(?:[\\/]|$)/.test(file)),'Bundled application should not contain full dependency trees')
  assert.equal(snapshot.dataPath,path.join(portableDirectory,'data'))
  assert.equal(snapshot.appVersion,'0.1.0')
  assert.equal(await desktop.evaluate(({app})=>app.isPackaged),true)
  assert.equal(await desktop.evaluate(({safeStorage})=>safeStorage.isEncryptionAvailable()&&safeStorage.decryptString(safeStorage.encryptString('packaging-test'))==='packaging-test'),true)
  assert.ok((await page.evaluate(()=>window.launcher.invoke('versions'))).length>0)
  assert.equal((await page.evaluate(()=>window.launcher.invoke('plugins.list'))).length,1)
  await page.locator('.sidebar nav button').filter({hasText:'设置'}).click()
  await page.getByRole('button',{name:'浅色',exact:true}).click()
  await page.getByRole('button',{name:'鸢尾紫',exact:true}).click()
  await page.waitForFunction(()=>document.documentElement.dataset.theme==='light');await expect.poll(async()=> (await page.evaluate(()=>window.launcher.invoke('snapshot'))).state.settings.accent).toBe('#b8a0ff')
  assert.match(await page.getByRole('button',{name:'浅色',exact:true}).getAttribute('class')||'',/active/)
  await page.getByLabel('全局内存分配方式').selectOption('auto')
  await page.getByRole('button',{name:'保存设置',exact:true}).click()
  await expect.poll(async()=>(await page.evaluate(()=>window.launcher.invoke('snapshot'))).state.settings.memoryMode).toBe('auto')
  assert.equal(await page.getByLabel('字体大小',{exact:true}).count(),1)
  assert.equal(await page.getByLabel('选择 Java',{exact:true}).count(),1)
  await page.evaluate(()=>window.launcher.invoke('account.offline','Packaged_Test'))
  assert.equal((await page.evaluate(()=>window.launcher.invoke('snapshot'))).state.accounts[0].kind,'offline')
  await page.getByRole('button',{name:'添加账户',exact:true}).click()
  await expect(page.getByRole('radio',{name:'浏览器授权（推荐）'})).toBeChecked()
  await expect(page.getByRole('radio',{name:'设备代码登录'})).toBeVisible()
  await page.getByRole('button',{name:'关闭账户管理'}).click()
  await page.locator('.sidebar nav button').filter({hasText:'下载'}).click()
  await page.locator('.download-tabs').getByRole('button',{name:'游戏',exact:true}).click()
  for(const loader of ['原版','Fabric','Forge','NeoForge','Quilt','OptiFine','LiteLoader'])
    await expect(page.getByRole('button',{name:`选择 ${loader}`,exact:true})).toBeVisible({timeout:30000})
  await mkdir('artifacts',{recursive:true})
  await page.screenshot({path:'artifacts/release-preview.png',animations:'disabled'})
  assert.deepEqual(errors,[])
  console.log('Packaged desktop passed: default single pane, no dependency tree, immediate appearance, portable data location, encryption, official manifest, bundled extension.')
  console.log('Executable:',executablePath)
  await desktop.close()
  assert.equal(JSON.parse(await readFile(path.join(portableDirectory,'data','config.json'),'utf8')).schemaVersion,1)
}finally{if(childProcess.exitCode===null)await desktop.close()}
