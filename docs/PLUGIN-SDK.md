# 0.1.0 扩展接口

首版扩展是随程序一起编译的受信任 TypeScript 模块。外部 JS 插件、插件商店、动态安装和隔离进程属于后续版本；不能把此接口视为第三方代码沙箱。

接口定义位于 `electron/plugins.ts`：

```ts
interface LauncherPlugin {
  id: string
  name: string
  version: string
  description: string
  activate(context: PluginContext): void
}

interface PluginContext {
  registerCommand(id: string, title: string, action: () => Promise<unknown>): void
  onBeforeLaunch(callback: (instance: Readonly<Instance>) => Promise<void>): void
  log(message: string): void
}
```

使用 `PluginHost.load` 注册。命令 ID 自动添加插件 ID 前缀；重复插件或命令 ID 会报错。启动前事件按注册顺序执行，收到实例信息的冻结副本。事件失败会阻止启动并将错误返回界面。

内置 `hakf.example` 注册了“检查启动环境”命令，并监听启动前事件。可以从扩展页面执行该命令，结果记录在任务日志中。

账户访问令牌不在实例结构或插件上下文中。页面不能直接访问 Node.js、文件系统或任意 IPC，所有桌面功能都经过 preload 白名单和主进程参数校验。

后续接口计划：页面注册、受控任务提交、下载提供器、显式权限声明与独立进程插件。暂不承诺这些接口的稳定实现。
