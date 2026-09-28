# V6.1 插件图标与公共资源

V6.1 同时提供插件专属图标和 `MyAvaloniaManagement.Icons` 公共矢量包。图标仍通过描述符的
`IconPath` 字符串引用；新版目录树与功能中心显示声明图标，旧版目录继续显示默认四宫格。

## 版本与职责

| 组件 | 版本 | 职责 |
| --- | --- | --- |
| Core / UI SDK | 3.4.1 | 保持同版本；图标契约兼容 3.4.0 |
| MyAvaloniaManagement.Icons | 3.4.1 | 独立 .NET 10 资源包，无 SDK/Avalonia/DI 依赖 |
| Plugin.Build | 3.4.1 | 允许图标资源 DLL 作为私有依赖部署及打包 |
| Plugin.Templates | 3.4.1 | 使用 Avalonia 12.1.2 的统一版本模板 |

`AddIcon` 最初在 SDK 3.4.0 提供。本模板按 SDK 3.4.1 编译，生成的 manifest 最低版本为 `3.4.1`；既有 3.4.0 插件无需因图标契约改变清单。
仅在自己的 View 使用资源包的旧 SDK 插件，不因资源包本身被强制升级 SDK。
资源包升级后要重新构建并交付消费者，不会自动替换已安装程序中的资源。

## 三种使用方式

**直接引用公共名称**：向 `DocumentDescriptor` 的 `iconPath` 传入 `"builtin:table"`。
图形取自 Host 所引用的公共资源版本；Host 不认识的新名称会显示默认图标。
八个稳定名称为 `module`、`folder`、`table`、`chart`、`text-check`、`image`、`video`、`download`，
完整名称均以 `builtin:` 开头。

**专属图标**：在唯一模块的 `Configure` 中注册单色矢量。Host 自动加上真实插件身份，插件只提供本地名称。

```csharp
using MyAvaloniaManagement.PluginSdk.UI;

var icon = registration.AddIcon("business-envelope", new VectorIconDefinition(
    "M1,2 H23 V14 H1 Z M3,4 L12,10 L21,4 L20,3 L12,8 L4,3 Z",
    viewBoxWidth: 24, viewBoxHeight: 16));

registration.AddDocument<MainDocument, MainView>(new DocumentDescriptor(
    PluginIds.MainDocument, "文本检测", "检测业务文档", "闲才/文本检测", iconPath: icon));
```

返回引用形如 `plugin:myavalonia.plugin.example/business-envelope`。本地名称以小写字母开头，
允许小写字母、数字和单个连字符分段。不要手写其他插件的引用；声明的 Owner 由 Host 校验。
同一插件重复名称会拒绝整个候选，即使图形相同；不同插件使用相同本地名称互不影响。
模块返回后注册窗口封闭，不能在 View 创建或按钮点击时追加图标。

**公共图形注册为专属引用**：插件自己选择公共资源版本，然后复制基础字段。

```csharp
using MyAvaloniaManagement.Icons;
using MyAvaloniaManagement.PluginSdk.UI;

var asset = CommonIcons.TextCheck;
var icon = registration.AddIcon("text-review", new VectorIconDefinition(
    asset.PathData, asset.ViewBoxWidth, asset.ViewBoxHeight));
```

此时图形跟随插件交付，即使 Host 使用旧资源包也能显示。`CommonIconAsset` 保留在插件自己的加载上下文中，
跨边界只传递共享 SDK 的不可变描述，不传资源对象、类型、回调、View 或 Provider。

## 安装和打包

中央版本管理项目添加：

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="MyAvaloniaManagement.Icons" Version="[3.4.1]" />

<!-- 插件 csproj：引用包，并声明它是插件拥有的私有运行时资产。 -->
<PackageReference Include="MyAvaloniaManagement.Icons" />
<ManagedPluginPrivatePackage Include="MyAvaloniaManagement.Icons" />
```

本模板使用 Build `3.4.1`（此能力最初由 `1.1.3` 提供）；ZIP 中应包含 `MyAvaloniaManagement.Icons.dll`，不能包含共享 Core/UI SDK DLL。
本仓 MyPlugTest 使用 ProjectReference，通过既有 `ManagedPluginAsset` 扩展点部署图标 DLL；
外部模板使用上述 NuGet 私有包声明。两条路径均需验收真实 ZIP，不能只检查普通 bin 目录。

## 页面、主题和异常

公共资源使用 20×20 逻辑画布、EvenOdd 填充。专属图标可以声明其他有限正数尺寸及
`IconFillRule.NonZero`；Host 以原点 `(0,0)` 等比居中绘制，保留画布留白并裁掉越界区域。
当前位置的主题画刷决定前景色，每个位置拥有独立控件，仅复用本 Runtime 内的几何数据。

自己的 View / Standalone 可直接使用 `asset.PathData`：用 Viewbox 包住指定宽高的 Canvas，
在 Canvas 中放置填充 Path。模板已有实际示例；Standalone 直接创建同一个 View，不依赖 Host 注册表。
页面使用的资源与 Host 功能入口注册是两个使用位置，拥有相同数据即可，无需建立全局单例。

空引用、未知名称、不支持的文件/网址引用、跨插件引用、不可用插件或坏路径都显示默认图标。
坏几何仅降级显示并去重记录 `ICON_GEOMETRY_INVALID`，不会删除业务功能；结构性注册错误仍按候选隔离处理。
创建意图省略图标时继续继承 Document 图标；填写未知引用时按该引用降级，不改变原有创建意图规则。

更多说明见 Host 仓库 docs/quick-start/plugin-icons.md；本模板 MainView 可独立预览。
