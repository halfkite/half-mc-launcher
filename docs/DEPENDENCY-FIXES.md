# XMCL 发布包修正记录

【BUG表象】：正常安装依赖时出现 `Unsupported URL Type "workspace:"`；安装后运行又分别出现 `@xmcl/unzip/index.ts` 和 `@xmcl/core/utils` 无法找到。

【真实根因】：公开发布的 XMCL 包残留内部工作区元数据，并遗漏安装器使用的子模块入口；这是依赖包发布产物不完整，而非启动器业务参数错误。

【修复方案】：锁定受影响版本，对解压包的依赖边使用公开的 `@xmcl/yauzl` 版本，修正其入口到已经随包分发的编译文件；从 core 随包提供的 sourcemap 中取出原始 utils 源码并编译成缺少的子模块。没有替换上游功能、跳过校验或吞掉模块加载错误。原始源码缺失时准备脚本会明确失败。

【修改代码】：

```diff
 // 根 package.json
+ "overrides": { "@xmcl/unzip": { "@xmcl/yauzl": "2.10.0" } }
+ "postinstall": "node scripts/prepare.mjs"

 // @xmcl/unzip 2.2.0 的发布入口，由准备脚本修正
- "main": "./index.ts"
+ "main": "./dist/index.js"
+ "types": "./dist/index.d.ts"

 // @xmcl/core 2.16.2：准备脚本使用包内原始源码重建缺少的子模块
+ const sourceIndex = map.sources.findIndex(source => source === '../utils.ts')
+ if (sourceIndex < 0 || !map.sourcesContent[sourceIndex]) throw new Error('XMCL utils source missing')
+ const result = await transform(map.sourcesContent[sourceIndex], { loader: 'ts', format: 'cjs', target: 'node24' })
+ await writeFile(coreUtilsPath, result.code)
```

完整可执行实现见 `scripts/prepare.mjs`。修正只针对上述固定版本；升级依赖时应重新评估并移除已被上游解决的修正。

【回归校验】：执行 `npm ci`、构建和测试；用 Node 加载 core 与 installer；实际安装原版 1.21.1 和 Fabric，执行启动参数生成及默认启动前检查，确认 JAR 校验、依赖读取和 native 解压可用；再检查打包程序是否包含修正后的依赖。
