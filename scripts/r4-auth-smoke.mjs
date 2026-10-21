import { _electron as electron,expect } from '@playwright/test'
import { mkdtemp } from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'
import assert from 'node:assert/strict'
const directory=await mkdtemp(path.join(os.tmpdir(),'hakf-r4-auth-')),env={...process.env,HAKF_DATA_DIR:directory};delete env.ELECTRON_RUN_AS_NODE
const app=await electron.launch({args:['.'],env,timeout:30000})
try{
  const page=await app.firstWindow();await page.waitForSelector('.welcome-card')
  // Capture browser opening; no real user account or authorization is performed.
  await app.evaluate(({shell})=>{globalThis.__authOpened=[];shell.openExternal=async url=>{globalThis.__authOpened.push(url)}})
  await page.getByRole('button',{name:'添加账户',exact:true}).click()
  await expect(page.getByRole('radio',{name:'浏览器授权（推荐）'})).toBeChecked()
  // The bundled public client id must be usable without typing any application id.
  assert.equal((await page.evaluate(()=>window.launcher.invoke('snapshot'))).authSource,'builtin')
  await page.getByRole('button',{name:'打开浏览器并授权',exact:true}).click()
  await expect.poll(async()=>await app.evaluate(()=>globalThis.__authOpened.length),{timeout:45000}).toBe(1)
  const url=await app.evaluate(()=>globalThis.__authOpened[0]),parsed=new URL(url)
  assert.equal(parsed.protocol,'https:');assert.equal(parsed.hostname,'login.microsoftonline.com');assert.equal(parsed.searchParams.get('code_challenge_method'),'S256')
  const redirect=parsed.searchParams.get('redirect_uri'),state=parsed.searchParams.get('state')
  assert.ok(redirect.startsWith('http://localhost:'))
  await page.getByRole('button',{name:'重新打开网页',exact:true}).click();assert.equal(await app.evaluate(()=>globalThis.__authOpened.length),2)
  await fetch(redirect+'/?'+new URLSearchParams({state,error:'access_denied'}))
  await expect(page.getByRole('button',{name:'打开浏览器并授权',exact:true})).toBeVisible({timeout:15000})
  await expect(page.locator('.toast-notification')).toContainText('取消 Microsoft')
  await page.getByRole('button',{name:'打开浏览器并授权',exact:true}).click()
  await expect(page.getByRole('button',{name:'取消',exact:true})).toBeVisible()
  await page.getByRole('button',{name:'取消',exact:true}).click()
  await expect(page.getByRole('button',{name:'打开浏览器并授权',exact:true})).toBeVisible({timeout:15000})
  assert.equal((await page.evaluate(()=>window.launcher.invoke('snapshot'))).state.accounts.length,0)
  await page.getByRole('button',{name:'离线账户',exact:true}).click();await page.getByLabel('离线账户名称').fill('R4_Offline');await page.locator('.offline-form').getByRole('button',{name:'添加',exact:true}).click()
  await expect(page.locator('.account-card h3')).toHaveText('R4_Offline')
  console.log('Browser login lifecycle passed: system browser callback, PKCE, state, reopen, denial/cancel cleanup, offline selection. No real Microsoft login performed.')
}finally{await app.close()}
