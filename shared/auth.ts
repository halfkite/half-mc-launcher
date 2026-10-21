// 内置的 Microsoft 公共客户端 ID（公共客户端，无密钥）。
//
// 来源：开源启动器 Prism Launcher（GPLv3）在源码中公开的应用 ID，它已注册
// “个人 Microsoft 账户 + 桌面回环回调 http://localhost + 设备代码”并获准访问
// Xbox Live / Minecraft 服务，因此可以直接完成浏览器授权，用户无需填写任何内容。
//
// 这意味着授权页面会显示 Prism Launcher 的应用名称，而不是 Half。它只是
// “开箱即用”的后备值：设置里填写自己的应用 ID，或在数据目录放置
// client-id.txt，都会覆盖它。用于公开分发前建议注册自己的应用。
export const DEFAULT_CLIENT_ID = 'c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb'

export const clientIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export type ClientIdSource = 'settings' | 'file' | 'build' | 'builtin' | 'none'

export type ClientIdResolution = { id: string; source: ClientIdSource }

/**
 * 按优先级挑选实际使用的应用 ID：
 * 设置页填写 > 数据目录 client-id.txt > 构建时 HAKF_MICROSOFT_CLIENT_ID > 内置公共 ID。
 * 格式不合法的来源会被跳过，而不是让登录直接失败。
 */
export function resolveClientId(settingsId?: string, fileId?: string, buildId?: string): ClientIdResolution {
  const candidates: [Exclude<ClientIdSource, 'none'>, string | undefined][] = [
    ['settings', settingsId],
    ['file', fileId],
    ['build', buildId],
    ['builtin', DEFAULT_CLIENT_ID]
  ]
  for (const [source, value] of candidates) {
    const id = (value || '').trim()
    if (clientIdPattern.test(id)) return { id, source }
  }
  return { id: '', source: 'none' }
}
