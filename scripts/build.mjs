import { build } from 'esbuild'
import { copyFile } from 'node:fs/promises'
await import('./icons.mjs')
const main = await build({ entryPoints: ['electron/main.ts'], outfile: 'dist-electron/main.cjs', bundle: true, minify: true, metafile: true, platform: 'node', format: 'cjs', target: 'node24', external: ['electron'],define:{'process.env.HAKF_MICROSOFT_CLIENT_ID':JSON.stringify(process.env.HAKF_MICROSOFT_CLIENT_ID||'')} })
await (await import('./notices.mjs')).writeNotices(Object.keys(main.metafile.inputs))
await build({ entryPoints: ['electron/preload.ts'], outfile: 'dist-electron/preload.cjs', bundle: true, platform: 'node', format: 'cjs', external: ['electron'], target: 'node24' })
await copyFile('build/icon.ico','dist-electron/icon.ico')
