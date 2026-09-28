# 临时部署、正式发布与验收

部署分为开发期临时联调和正式 ZIP 发布。两者都必须使用 Build 包筛选出的干净插件目录，不能直接复制
普通 `bin/Debug` 或 `bin/Release`，因为普通输出可能包含 Host 应当统一提供的共享程序集。

## 新增 NuGet 包后必须同步的三个位置

插件业务代码新增运行时 NuGet 依赖时，以 `Some.Private.Runtime` 为例，同时修改以下位置：

```xml
<!-- 1. 解决方案根 Directory.Packages.props：统一锁定版本 -->
<ItemGroup>
  <PackageVersion Include="Some.Private.Runtime" Version="[1.2.3]" />
</ItemGroup>

<!-- 2、3. src/NetWorkTrafficPlugin.Plugin/NetWorkTrafficPlugin.Plugin.csproj：引用并声明由插件携带 -->
<ItemGroup>
  <PackageReference Include="Some.Private.Runtime" />
</ItemGroup>
<ItemGroup>
  <ManagedPluginPrivatePackage Include="Some.Private.Runtime" />
</ItemGroup>
```

`ManagedPluginPrivatePackage` 是正式交付资产所有权声明，不是重复的 `PackageReference`。Build 只从这里列出的
NuGet 包收集托管 DLL 和当前 `win-x64` RID 资产；漏写时 Standalone 或普通 `bin` 可能正常，正式 ZIP 却会
缺少 DLL，真实 Host 最终报 `FileNotFoundException`、`FileLoadException` 或类型初始化失败。

还要注意：

- `Include` 使用准确的 NuGet 包 ID；若直接包依赖其他提供运行时文件的包，也要逐一列出这些传递包 ID；
- 用 `dotnet list src/NetWorkTrafficPlugin.Plugin/NetWorkTrafficPlugin.Plugin.csproj package --include-transitive` 查看传递依赖；
- SDK、Avalonia、Dock、Semi、Ursa、CommunityToolkit、`Microsoft.Extensions.*` 和 Newtonsoft.Json 是当前
  Host 共享边界，不添加到 `ManagedPluginPrivatePackage`，也不应出现在 ZIP；
- 只给 Standalone 或 Tests 使用的包只添加到对应项目，不添加到 Plugin 项目；
- NuGet 包若通过构建 Target 生成完整原生目录，使用 `ManagedPluginAssetDirectoryRelativePath`；其他额外
  文件使用带 `TargetPath` 的 `ManagedPluginAsset`。

发布前用 `-p:ManagedPluginTraceAssets=true` 输出最终资产映射，并解压 ZIP 确认插件私有 DLL 与原生文件都在
`Controls/NetWorkTrafficPlugin/` 内。

## 临时部署到真实 Host

部署或替换前先完整退出 Host。当前插件发现和加载上下文以进程为边界，不支持热替换。

### 方式一：直接部署

已知 Host 的 `Controls` 目录时，在解决方案根目录执行：

```powershell
dotnet msbuild src/NetWorkTrafficPlugin.Plugin/NetWorkTrafficPlugin.Plugin.csproj `
  -t:DeployManagedPlugin `
  -p:Configuration=Debug `
  -p:ManagedPluginDeployRoot=C:\Path\To\Host\Controls
```

该目标只重建 `Controls/NetWorkTrafficPlugin`，不会清理 `Controls` 根目录或其他插件。需要给开发目录增加醒目标记时，
可覆盖目录名：

```powershell
dotnet msbuild src/NetWorkTrafficPlugin.Plugin/NetWorkTrafficPlugin.Plugin.csproj `
  -t:DeployManagedPlugin `
  -p:Configuration=Debug `
  -p:ManagedPluginDeployRoot=C:\Path\To\Host\Controls `
  -p:ManagedPluginDirectoryName=NetWorkTrafficPlugin-Dev
```

`NetWorkTrafficPlugin-Dev` 只是文件夹名称，插件身份仍是 manifest 中的 `myavalonia.plugin.network.traffic`。

### 方式二：生成暂存目录后手工复制和改名

需要先检查产物或用资源管理器复制时，先部署到一个独立暂存根：

```powershell
dotnet msbuild src/NetWorkTrafficPlugin.Plugin/NetWorkTrafficPlugin.Plugin.csproj `
  -t:DeployManagedPlugin `
  -p:Configuration=Debug `
  -p:ManagedPluginDeployRoot=C:\Temp\NetWorkTrafficPlugin-Deploy\Controls
```

然后把整个 `C:\Temp\NetWorkTrafficPlugin-Deploy\Controls\NetWorkTrafficPlugin` 复制到 Host 的 `Controls` 下。目标叶子目录可以
改为 `NetWorkTrafficPlugin-Dev`，但必须遵守：

- 把插件目录作为一个整体替换，不要把新文件合并覆盖到旧目录，否则删除过的依赖可能残留；
- 同一个 Host 中只保留一份 `myavalonia.plugin.network.traffic`，不能同时留下 `NetWorkTrafficPlugin` 和 `NetWorkTrafficPlugin-Dev`；
- 不修改 `plugin.manifest.json`，不因为临时目录改名而改变 Plugin、Document 或 Tool ID；
- 复制完成后重新启动 Host，再从插件状态和真实 Dock 验证加载结果。

## 正式发布 ZIP

发布前先完成 Release 构建和测试，并按兼容变更更新 `PluginVersion`：

```powershell
dotnet build -c Release -warnaserror
dotnet test -c Release --no-build
dotnet msbuild src/NetWorkTrafficPlugin.Plugin/NetWorkTrafficPlugin.Plugin.csproj `
  -t:BuildManagedPluginPackage `
  -p:Configuration=Release
```

默认输出：

```text
src/NetWorkTrafficPlugin.Plugin/artifacts/managed-plugin-packages/
├─ NetWorkTrafficPlugin.Plugin-1.0.0-win-x64.zip
└─ NetWorkTrafficPlugin.Plugin-1.0.0-win-x64.manifest.json
```

ZIP 内保持 `Controls/NetWorkTrafficPlugin/` 布局；同名外置 `.manifest.json` 记录 ZIP 和文件摘要。正式交付时让二者
保持配对，不要手工重压 ZIP、编辑 ZIP 内 manifest，或把 `bin` 目录自行压缩成发布包。安装时优先使用
Host 提供的导入入口；若由维护者手工解压，也必须保留 ZIP 内的目录层级。

## 真实 Host 最小验收

- 插件状态显示已加载，manifest 的 ID、版本、入口和 SDK 区间正确；
- 每个 Document/Tool 出现在预期菜单或 Dock 区域；
- 同一种 Document 打开两次时状态和 Scope 互不影响；
- Tool 隐藏后可恢复且 singleton 状态保留；
- 保存、恢复、关闭和生命周期行为符合插件声明；
- Host 没有报告共享程序集、私有依赖、入口类型或稳定 ID 错误；
- 替换为正式 ZIP 后完整重启 Host，并再次完成一次关键业务流程。

## 常见注意事项

- Standalone 正常不代表 Host 一定能加载，优先检查正式 manifest、依赖边界和 SDK 区间。
- `plugin.manifest.json` 缺失或错误时重新执行 Build 目标，不要手工补写。
- 私有托管或原生依赖必须通过 Managed Plugin 构建协议声明，不能靠目录扫描碰运气加载。
- 当前只发布 `win-x64`，不要在同一个包里混入其他 RID 的原生资产。
- 调试目录可以改名，但稳定 Plugin ID 不能用目录名代替，也不能用复制副本的方式并行加载同一插件。
