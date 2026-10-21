/**
 * electron-builder afterPack 钩子：裁掉启动器用不到的 Electron 运行时文件。
 *
 * 这些文件不影响界面渲染与游戏启动：
 * - dxcompiler.dll       WebGPU / D3D12 着色器编译器，界面不使用 WebGPU
 * - electron_wer.dll     Windows 错误上报模块
 * - resources/elevate.exe 仅安装/更新流程使用的提权工具
 *
 * 保留 d3dcompiler_47.dll、vk_swiftshader、vulkan-1 与 ffmpeg，避免在没有 GPU
 * 的机器上出现黑屏或无法播放声音。缺失文件会被跳过，不会让打包失败。
 */
const { rm, stat } = require('node:fs/promises')
const path = require('node:path')

const removable = ['dxcompiler.dll', 'electron_wer.dll', path.join('resources', 'elevate.exe')]

module.exports = async function afterPack(context) {
  let freed = 0
  const removed = []
  for (const relative of removable) {
    const file = path.join(context.appOutDir, relative)
    try {
      const size = (await stat(file)).size
      await rm(file, { force: true })
      freed += size
      removed.push(relative)
    } catch {
      // 文件不存在或已被上游裁掉，继续。
    }
  }
  if (freed) console.log(`  • 已裁剪 Electron 运行时 ${(freed / 1048576).toFixed(1)} MB：${removed.join('、')}`)
}
