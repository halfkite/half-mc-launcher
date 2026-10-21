import { Agent, EnvHttpProxyAgent, setGlobalDispatcher, interceptors, type Dispatcher } from 'undici'
// Node fetch does not automatically honor the proxy variables supported by
// common Windows network tools. Share one dispatcher with installer downloads.
export const networkDispatcher=(process.env.HTTPS_PROXY||process.env.https_proxy||process.env.HTTP_PROXY||process.env.http_proxy)?new EnvHttpProxyAgent():undefined
if(networkDispatcher)setGlobalDispatcher(networkDispatcher)
// The installer uses dispatch(), which unlike fetch() does not follow redirects
// itself. Preserve that policy when replacing its default proxy dispatcher.
export const withDownloadRedirects=(dispatcher:Dispatcher)=>dispatcher.compose(
  dispatch=>(options,handler)=>dispatch({...options,headers:Array.isArray(options.headers)?[...options.headers,'user-agent','HalfMcLauncher/0.1.0']:{...options.headers,'user-agent':'HalfMcLauncher/0.1.0'}},handler),
  interceptors.redirect({maxRedirections:5})
)
export const downloadDispatcher=withDownloadRedirects(networkDispatcher||new Agent({connections:16}))
export async function requestJson<T = any>(url: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(url, { ...init, headers: { 'User-Agent': 'HalfMcLauncher/0.1.0', ...init.headers }, signal: init.signal ?? AbortSignal.timeout(30000) })
  const text = await response.text()
  let value: any
  try { value = JSON.parse(text) } catch { throw new Error(`服务返回了无效数据（HTTP ${response.status}）`) }
  if (!response.ok) {
    const detail = value.error_description || value.errorMessage || value.Message || value.error || ''
    throw new Error(`请求失败（HTTP ${response.status}）${detail ? `：${String(detail).slice(0, 350)}` : ''}`)
  }
  return value as T
}
