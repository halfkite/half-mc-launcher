# Hakf Mc Launcher 0.1.0 r4

本轮根据用户确认，扩展加载器安装及默认浏览器账户授权。继续使用 Electron / Vue / TypeScript、XMCL 与 MSAL，没有更换技术栈。

## 加载器与组件

下载 → 游戏提供原版、Fabric、Forge、NeoForge、Quilt、独立 OptiFine、旧版 LiteLoader。

选择游戏版本后查询对应组件列表，显示版本、推荐、测试版和实际目录来源；可手动指定组件版本、刷新目录、显示测试版。来源可选自动（官方优先）、仅官方、BMCLAPI。OptiFine 与 LiteLoader 目前使用 BMCLAPI 元数据和下载，界面明确说明；不把它们伪装为官方直连。

Fabric 可勾选同时安装兼容 Fabric API，下载与必要依赖复用 Modrinth 服务；资源子任务失败或取消会传递给安装父任务。安装子实例已经存在时阻止覆盖，保留现有存档与模组。失败时只清理本次新建的目标子实例，不删除共享已校验依赖或原版。

Forge / NeoForge / OptiFine 安装前检查 Java 主版本范围；启动这些实例时也拒绝过新的 Java。Java 来源仍可使用已识别列表、指定文件或搜索目录；不自动下载 Java。LiteLoader 安装是版本描述和文件安装，启动旧游戏需要 Java 8。

## 浏览器授权

- 账户类型入口：微软账户、离线账户。
- 微软默认使用系统浏览器授权，保留设备代码登录备用方式。
- 授权码流程使用随机 state、PKCE S256、本机 IPv4 回调监听；回调地址为随机端口 http://localhost。拒绝无效 state、错误路径和重复消费；取消、超时、打开浏览器失败或关闭程序时清理监听。
- 浏览器授权后共用 Xbox / XSTS / Minecraft 档案验证、加密缓存和刷新逻辑；界面显示授权与档案验证阶段。
- 支持重新打开授权网页；设备代码方式自动打开网页，可复制代码和取消。
- 项目可在构建时提供 HAKF_MICROSOFT_CLIENT_ID；已配置的发行版无需每位用户手填。设置中的自有应用 ID 可覆盖构建配置。

重要边界：本次没有获准访问 Minecraft 的 Hakf 应用 ID，也没有用户真人授权。因此没有验证真实微软账号登录、令牌刷新或进入游戏。浏览器流程已使用真实 MSAL 生成授权 URL，通过本机拒绝回调与取消实测；成功授权后添加 Minecraft 账户使用模拟服务响应测试。不存在借用 PCL/HMCL 的应用 ID、凭据或私人接口。

自有应用需要支持个人账户与公共客户端，配置桌面回调 http://localhost，并获准访问所需 Xbox/Minecraft 服务。仅创建普通 Entra 应用不等于已获得 Minecraft 服务权限。

## 实测范围

54 项自动测试通过，包含已有功能以及 PKCE/state、回调取消/拒绝、重新登录、账户令牌不进入普通配置、加载器版本排序/过滤/回退、Java 范围、依赖子任务失败/取消、代理重定向和请求标识。

| 组件 | 实际测试的游戏与组件版本 | 结果 |
| --- | --- | --- |
| 原版 | Minecraft 1.21.1 | 安装、参数生成、依赖/native 校验通过 |
| Fabric | 1.21.1 / 0.19.5，含 Fabric API | 安装及启动前校验通过 |
| Quilt | 1.21.1 / 0.30.1 | 安装及启动前校验通过 |
| Forge | 1.21.1 / 52.0.16 | Java 21 安装处理器及启动前校验通过 |
| NeoForge | 1.21.1 / 21.1.1 | Java 21 安装处理器及启动前校验通过 |
| OptiFine | 1.21.1 / HD_U_J1 | 补丁处理、独立实例及启动前校验通过 |
| LiteLoader | 1.12.2 / 1.12.2-SNAPSHOT | 本体 MD5、依赖、版本描述及启动前校验通过 |

没有自动打开完整游戏窗口，不能将启动前校验表述为实际进入游戏。Java 21 测试运行环境从 Azul 官方下载并通过官方 SHA-256 校验，放在 .test-java，不进入发行包。所有游戏安装使用 .test-install 或临时目录，没有修改用户既有游戏目录。

桌面检查还覆盖账户类型切换、默认浏览器方式、系统浏览器调用、授权网页重开、拒绝/取消后的状态恢复，以及原有布局、独立标签、会话保存、实例保护和资源管理。多个 UI 测试窗口并发曾导致悬停步骤超时；改为单独运行桌面回归后通过，产品未为此添加强制点击或关闭交互检查。

## 根因修复

【BUG表象】：NeoForge 官方站在系统工具中可访问，但启动器请求失败；OptiFine 下载后 ZIP 校验失败；旧版下载返回 403；OptiFine 补丁报告目标文件不存在。

【真实根因】：Node 请求没有默认使用环境中的代理；为底层安装器替换 dispatcher 时必须保留重定向处理，底层 dispatch 与 fetch 的行为不同；安装下载缺少应用请求标识，造成镜像响应不同；OptiFine 补丁计划声明了输出文件，但补丁程序不会自行创建父目录。

【修复方案】：网络模块共享环境代理；为安装下载显式组合请求标识和重定向策略，代理与直连都使用一致策略；LiteLoader 使用文档下载入口并保留 Maven 备用路径、继续校验 MD5；按补丁计划声明的输出创建父目录后再执行 Java。任务错误展开 AggregateError 和底层原因，显示不含查询参数的失败地址，不以异常吞掉替代修复。

【修改代码】：相关改动位于 electron/network.ts、electron/game.ts、electron/tasks.ts。核心片段：

```ts
const withDownloadRedirects = (dispatcher: Dispatcher) => dispatcher.compose(
  dispatch => (options, handler) => dispatch({
    ...options,
    headers: Array.isArray(options.headers)
      ? [...options.headers, 'user-agent', 'HakfMcLauncher/0.1.0']
      : {...options.headers, 'user-agent': 'HakfMcLauncher/0.1.0'}
  }, handler),
  interceptors.redirect({maxRedirections: 5})
)

for (const task of result.plan.tasks)
  if (task.type === 'java')
    for (const output of task.outputs)
      await mkdir(path.dirname(output.path), {recursive: true})
```

【回归校验】：本地 HTTP 测试服务器返回 302 → ZIP，检查重定向前后应用标识及最终文件内容；真实重新安装 OptiFine、LiteLoader；再次执行六类组件的版本解析、启动参数生成和 XMCL 默认启动前检查。

## 已知范围

- 本轮 OptiFine 为独立实例，不提供未经验证的 Forge/OptiFine、OptiFabric 或其他混合组合。
- Forge/NeoForge/Fabric/Quilt 并非可以任意叠加的组件；当前选择一个主要加载器。
- LiteLoader 是旧版兼容功能，上游与镜像可用性会变化；不支持的游戏版本不显示虚假版本列表。
- 未覆盖全部历史 Forge 格式、所有快照/愚人节版本、全部 Java 发行版或全部整合包。
- Java 版本范围采取保守策略；没有合适的 Java 时明确提示更换。
- 保留 r3 的 Mod/资源功能边界；CurseForge 仍需独立 API 密钥。

## 开发与验证

```text
npm test
npm run build
npm run test:desktop
npm run test:auth
npm run test:features
npm run test:install
npm run test:loaders
node scripts/package-smoke.mjs
```

test:loaders 需要 .test-java/runtime21 中的 Java 21，并会安装游戏资源；其他普通 UI 测试使用独立临时目录。正式构建可设置 HAKF_MICROSOFT_CLIENT_ID，值为项目自己的公共客户端 ID。


## r4 最终发行包核验

r4 便携 EXE：94220879 字节（89.86 MiB），比 r3 增加 5299 字节；应用归档 2564818 字节。正式包检查通过：默认单栏、便携数据目录、凭据加密、设置保存、Java 下拉、离线账户、默认浏览器授权与设备代码选项，以及全部七种游戏组件入口。

SHA-256：

`a62ed7b425c31f89c51425a83d20d8eeeb16edb1502f4732799565126d4875c8`
