import { readFile, writeFile, readdir } from 'node:fs/promises'
import path from 'node:path'
const lock=JSON.parse(await readFile('package-lock.json','utf8'))
export async function writeNotices(inputs){
const used=new Set(inputs.filter(p=>p.includes('node_modules/')).map(p=>{const parts=p.split('/');const index=parts.lastIndexOf('node_modules');return parts.slice(0,index+(parts[index+1].startsWith('@')?3:2)).join('/')}))
for(const name of ['vue','lucide-vue-next','zod'])used.add('node_modules/'+name)
const blocks=['Half Mc Launcher 0.1.0 — third-party notices\n\nElectron/Chromium notices are also distributed with the runtime.\n']
for(const [location,entry] of Object.entries(lock.packages)){
  if(!location||!used.has(location))continue
  const manifest=JSON.parse(await readFile(path.join(location,'package.json'),'utf8'))
  const files=await readdir(location)
  const license=files.find(f=>/^(license|licence|copying)(\.md|\.txt)?$/i.test(f))
  blocks.push(`\n${'='.repeat(72)}\n${manifest.name} ${manifest.version}\nLicense: ${manifest.license||entry.license||'See package repository'}\nRepository: ${typeof manifest.repository==='string'?manifest.repository:manifest.repository?.url||''}\n\n`)
  if(license)blocks.push(await readFile(path.join(location,license),'utf8'))
  else if(manifest.name.startsWith('@xmcl/'))blocks.push(await readFile('build/XMCL-LICENSE.txt','utf8'))
}
await writeFile('build/THIRD-PARTY-NOTICES.txt',blocks.join(''))
}
