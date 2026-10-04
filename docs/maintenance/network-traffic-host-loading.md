# 网络插件真实 Host 加载检查

> 核对日期：2026-10-04。适用插件：`myavalonia.plugin.network.traffic` 1.0.0.0，SDK 声明区间 `[3.4.1.0, 4.0.0.0)`。本页记录实际加载故障、修复与后续部署检查，不替代完整业务验收。

## 本次根因

看板显示 `PLUGIN_ASSEMBLY_LOAD_FAILED`、类型预检失败及已隔离。对原安装版 EXE 使用隔离数据重现后，异常明确缺少：

```text
Microsoft.Extensions.Logging.Abstractions, Version=10.0.0.0,
Culture=neutral, PublicKeyToken=adb9793829ddae60
```

Titanium 适配器需要 `ILoggerFactory`，插件当前打包协议又禁止私带 `Microsoft.Extensions` 日志 DLL。原 Host 未显式携带此程序集，所以普通插件构建、Standalone 与测试运行成功仍不能证明真实 Host 可以加载。

修复放在 Host：直接引用日志抽象 NuGet `10.0.10`，将其登记为精确共享根，沿用现有版本、公钥和默认加载上下文检查。保持原有插件打包边界，不复制共享 DLL 到 Controls，不放宽整族禁带规则，不修改插件业务代码。

## 2026-10-04 实际结果

- Host 定向 Debug 回归：21 通过，0 失败，0 跳过，覆盖直接依赖、共享身份、高版本和错误签名拒绝、私有依赖隔离。
- 原安装版源码基线 `66f3f75e84f87d51a2fe68017108189a743aca16` 加最小修复，构建 Debug 自包含单 EXE，替换 `D:\data\avalonia\MyAvaloniaManagement.exe`。
- 修复 EXE SHA-256：`3B8F5ECB4F5063C0C6FC6E47D69B34EA57E741A7A92F238C0B6E0616B717EF59`；原版备份在 `D:\data\avalonia\.backups\host-logging-20261004\MyAvaloniaManagement.exe`。
- Controls 与 HelpWeb 的 261 个文件摘要未变；网络插件仍是原 1.0.0.0 产物。
- 隔离启动没有加载错误；实际用户会话看板显示 **14 个发现、14 个可用、0 个异常**，网络插件生命周期初始化成功。
- “检查安装产物”已核对当前已加载程序集；功能中心可正常创建 HTTP 工作台。未启动代理，未改系统代理和证书信任。

Host 仓库的 `docs/archive/records/host/logging-dependency-fix-20261004.md` 保存交付记录，`docs/maintenance/plugin-logging-dependency.md` 保存测试命令和依赖设计。未使用 AIFLOW、Windows CI 或发布门禁；本次仅关闭加载与窗口创建故障，不代表完整真实客户端、M3/M4/M5 或人工验收完成。

## 后续部署检查

1. 核对 Host EXE 与插件目录身份，使用本次产物启动检查；SDK 区间声明本身不能证明 Host 带齐所有共享依赖。
2. 用隔离 Host 数据根和解包缓存运行实际 EXE，检查启动完成、正常关闭以及本轮诊断；被隔离插件不会必然使进程退出码非零。
3. 正常启动安装版，在插件看板确认网络插件可用、生命周期初始化成功，再检查安装产物。
4. 打开 HTTP 工作台验证贡献激活、Document Scope 与窗口创建；抓包、代理和请求调试另按各阶段矩阵执行。

“尚无匹配的验收报告”表示缺少离线报告，不等于本次会话加载失败。完整退出再启动后会重新加载插件；单纯点击刷新状态不会重新加载旧会话。
