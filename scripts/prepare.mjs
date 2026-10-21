import { readFile, writeFile, access, copyFile } from 'node:fs/promises'
import { transform } from 'esbuild'
import { spawn } from 'node:child_process'
// @xmcl/unzip 2.2.0 ships compiled files but has unpublished-workspace metadata.
// npm overrides correct its dependency edge; correct the entrypoint to the shipped CJS file.
const filename = new URL('../node_modules/@xmcl/unzip/package.json', import.meta.url)
const manifest = JSON.parse(await readFile(filename, 'utf8'))
if (manifest.version === '2.2.0' && manifest.main === './index.ts') {
  await access(new URL('../node_modules/@xmcl/unzip/dist/index.js', import.meta.url))
  manifest.main = './dist/index.js'
  manifest.types = './dist/index.d.ts'
  await writeFile(filename, JSON.stringify(manifest, null, 2) + '\n')
  console.log('Corrected @xmcl/unzip 2.2.0 published entrypoint.')
}
// Installer 6.3.6 imports @xmcl/core/utils, omitted from core 2.16.2's package.
// Rebuild that exact upstream module from the source shipped in its sourcemap.
const core = JSON.parse(await readFile(new URL('../node_modules/@xmcl/core/package.json', import.meta.url),'utf8'))
if (core.version === '2.16.2') {
  const map = JSON.parse(await readFile(new URL('../node_modules/@xmcl/core/dist/index.js.map', import.meta.url),'utf8'))
  const sourceIndex = map.sources.findIndex(source => source === '../utils.ts')
  if (sourceIndex < 0 || !map.sourcesContent[sourceIndex]) throw new Error('XMCL utils source missing; cannot repair the published module')
  const result = await transform(map.sourcesContent[sourceIndex], { loader: 'ts', format: 'cjs', target: 'node24' })
  await writeFile(new URL('../node_modules/@xmcl/core/utils.js', import.meta.url), result.code)
  await copyFile(new URL('../node_modules/@xmcl/core/dist/utils.d.ts', import.meta.url),new URL('../node_modules/@xmcl/core/utils.d.ts', import.meta.url))
  console.log('Rebuilt missing @xmcl/core/utils from shipped upstream source.')
}
if (!process.env.ELECTRON_SKIP_BINARY_DOWNLOAD) {
  await new Promise((resolve, reject) => {
    const child = spawn(process.execPath, ['node_modules/electron/install.js'], { stdio: 'inherit', windowsHide: true })
    child.on('error', reject)
    child.on('exit', code => code === 0 ? resolve() : reject(new Error(`Electron setup exited ${code}`)))
  })
}
