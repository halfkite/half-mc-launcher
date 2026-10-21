import { expect,it } from 'vitest'
import { createServer } from 'node:http'
import { mkdtemp,rm,readFile } from 'node:fs/promises'
import { createRequire } from 'node:module'
import path from 'node:path'
import os from 'node:os'
import { Agent } from 'undici'
import { createDefaultNodeInstallRuntime,executeInstallManifest } from '@xmcl/installer'
import { withDownloadRedirects } from '../electron/network'
it('follows an installer redirect when a custom network dispatcher is supplied',async()=>{
  const {ZipFile}=createRequire(import.meta.url)('yazl'),zip=new ZipFile();zip.addBuffer(Buffer.from('test content'),'installer.txt');zip.end();const chunks:Buffer[]=[];for await(const chunk of zip.outputStream)chunks.push(chunk);const bytes=Buffer.concat(chunks)
  const seen:(string|undefined)[]=[]
  const dir=await mkdtemp(path.join(os.tmpdir(),'hakf-redirect-')),agent=new Agent(),server=createServer((req,res)=>{seen.push(req.headers['user-agent']);if(req.url==='/installer'){res.writeHead(302,{Location:'/installer.jar'});res.end('Found. Redirecting')}else{res.writeHead(200,{'Content-Type':'application/java-archive','Content-Length':bytes.length});res.end(bytes)}})
  try{
    await new Promise<void>(resolve=>server.listen(0,'127.0.0.1',resolve));const address=server.address() as {port:number},file=path.join(dir,'installer.jar')
    const runtime=createDefaultNodeInstallRuntime({dispatcher:withDownloadRedirects(agent)})
    await executeInstallManifest({schemaVersion:1,tasks:[{id:'installer',type:'files',files:[{path:file,urls:[`http://127.0.0.1:${address.port}/installer`],validator:'zip'}]}]},runtime,{attempts:1})
    expect(await readFile(file)).toEqual(bytes)
    expect(seen).toEqual(['HalfMcLauncher/0.1.0','HalfMcLauncher/0.1.0'])
  }finally{await agent.close();server.close();server.closeAllConnections();await rm(dir,{recursive:true,force:true})}
})
