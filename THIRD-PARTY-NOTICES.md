# V2 · M0 新增代理依赖声明

核对日期：2026-09-29。仅覆盖本轮新增代理依赖及开发宿主 DI；模板既有依赖仍以各包声明为准。本次没有创建发布包。

| 包 | 固定版本 / 本地 nupkg SHA-256 | 许可及来源 |
| --- | --- | --- |
| Titanium.Web.Proxy | 7.0.13 / 88914C4187735CA99879068B3CC9A785DF7739A4265DFD78347075A80E7838A3 | [对应源码 MIT](https://github.com/justcoding121/titanium-web-proxy/blob/571b7ac896df5986fd3a31f7e02d73fe4bd501a5/LICENSE)；主 nupkg 没有 license 元数据，依据 nuspec 提交对应的源码 LICENSE 核对 |
| Titanium.Web.Proxy.Abstractions | 7.0.13 / 964A0AB7CAA008F37F39113921E9173AE62CACA10804A9C4CE09180A042723E0 | nuspec 标 MIT，与主包同一提交 |
| BouncyCastle.Cryptography | 2.6.2 / 623936FB1FD171579C706390390711282898CF2C759A5167852E5AB82208F8CB | 包内 LICENSE.md / nuspec 标 MIT；源码提交 b4f2f6ad76bcd1f11f365ee50cc7447fbce79077 |
| Microsoft.Extensions.DependencyInjection | 10.0.0，仅 Standalone/TechnicalProbe 直接引用 | MIT，Microsoft；不设插件私有包 |
| Microsoft.Extensions.Logging.Abstractions | 10.0.0，代理传递依赖 | MIT，Microsoft；仍按 Host 共享边界处理 |

本地保存的许可原文：[Titanium MIT](docs/reference/licenses/Titanium.Web.Proxy-MIT.txt)、[Bouncy Castle MIT](docs/reference/licenses/BouncyCastle-MIT.txt)。Bouncy Castle 包 README 另说明修改版 Bzip2 采用 Apache 2.0，保留[Apache 官方许可原文](docs/reference/licenses/Apache-2.0.txt)，来源为 [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0.txt)。

Titanium 主包 nuspec 对应 571b7ac896df5986fd3a31f7e02d73fe4bd501a5；只使用代理库及 Abstractions，不引入 Inspector、Plus 或其桌面组件。在线 develop 分支协议矩阵不作为固定 NuGet 版本兼容证明。

四个项目的 lock 文件记录 NuGet contentHash。Plugin 私有包逐项声明 Titanium.Web.Proxy、Titanium.Web.Proxy.Abstractions、BouncyCastle.Cryptography；共享 SDK/Avalonia/Microsoft.Extensions 不通过私有复制绕过 Host 边界。Debug 解析资产、公开 API 编译与受控运行纳入开发检查；真实 Host 加载上下文及正式发布时的许可随包交付另行验证。
