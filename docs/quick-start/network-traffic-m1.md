# V3 · M1 可用抓包快速开始

适用环境：Windows x64、.NET 10、PowerShell 7。本次仅运行本地 Debug 开发验证，不使用 AIFLOW、Windows CI、部署或发布门禁。完整边界见[当前运行说明](../reference/network-traffic-m1-runtime.md)，结果见[实施记录](../archive/2026-09-29-v3-m1-implementation.md)。

## 1. 启动并接入客户端

在解决方案根目录执行：

```powershell
dotnet restore --locked-mode
dotnet run --project src/NetWorkTrafficPlugin.Standalone -c Debug
```

展开“代理配置与 HTTPS 接入诊断”，确认记录目录与策略。默认“有限内容”，每方向最多 1 MiB。端口 0 表示自动分配，点击“启动代理”后复制实际 `127.0.0.1:端口`。

客户端需显式使用此代理；插件不修改系统设置。HTTP 不需证书。HTTPS 时展开公开根证书、复制 PEM 到本次测试文件，并让选定客户端仅信任该根。不要使用忽略所有证书错误的选项。每次重启根证书都会变化，需重新配置。

以下命令中的端口、地址和公开证书文件由本次实际运行替换：

```powershell
curl.exe --proxy http://127.0.0.1:实际端口 --noproxy "" --cacert ./m1-public-root.pem https://测试地址/
```

接入面板会提示：HTTP/2 空闲流只提示、不自动终止。可在客户端取消单流，或停止整个代理；需要代理自动空闲超时的请求使用 HTTP/1.1。普通 h2 抓包仍可使用。

## 2. 查看、筛选和保存

1. 在客户端发起请求，选择列表中的记录。宽屏并列显示详情，窄屏进入详情后可“返回列表”，也支持 Esc。
2. 在正文页切换请求/响应；其他标签查看概览、Query、Header 和 Cookie。记录状态 Partial 表示只保存了一部分，不能当作完整 Body。
3. SSE/NDJSON 逐事件显示；“暂停跟随”保持当前阅读位置，接收仍继续。压缩流首版只尝试解析保存前缀，不承诺实时首事件。
4. 展开更多筛选，组合方法、状态、关键字、时间。清空当前视图不清共享数据，不影响另一页面。
5. 输入捕获根之外的**全新目录路径**并点击“保存”。存档包含真实 Header/正文；界面遮罩不会改写文件。长流保存的是当时已提交的前缀。
6. “打开”同一存档目录进入只读视图；“返回实时”回到当前捕获。打开存档不发起网络请求，正在运行的代理仍可继续捕获。
7. 在导出页选择目标路径，导出已有正文前缀。路径必须未存在且在托管捕获根之外；不补下载未记录数据。

关闭页面不等于停止代理。使用主页面/Tool 的“停止代理”结束运行；Standalone 退出也会收口运行。端口与策略改动在下一次启动生效。

## 3. 本地开发门禁

```powershell
# 文档链接、锚点和空白，不代表业务验证。
pwsh -NoProfile -File tools/verify-development.ps1 -Mode Docs

# M1 完整开发验证，包含全部前置 M0、M1 资源和生产 UI 测量。
pwsh -NoProfile -File tools/verify-development.ps1 -Mode M1
```

不传 Mode 时仍是 M0，避免旧入口静默改变范围。M1 含两组实际 600 秒持续测量及大资源传输，完整运行通常超过二十分钟；期间不要修改行为源码或并行运行其他负载测量。任何测试失败/跳过、证据缺失、预算越界或源码摘要变化都导致非零退出。

局部开发可先运行：

```powershell
dotnet test tests/NetWorkTrafficPlugin.Tests -c Debug --filter 'Category!=ResourceProbe&Category!=M1ResourceProbe&Category!=NativeUiProbe'
pwsh -NoProfile -File tools/TestM1DevelopmentGate.ps1
```

结果保留在 `TestResults/NetworkTrafficM1/<runId>`：verification、场景映射、TRX、协议/存档/生命周期 JSON、资源 CSV、生产 UI PNG、桌面原始采样与各命令日志。GateSelfTests 是明确的校验器夹具，不能拿来冒充业务结果。完整门禁通过仍不代表真实 Host、真实客户端、人工或发布通过。

## 4. 排错

| 现象 | 处理 |
| --- | --- |
| 列表无请求 | 核对实际端口、客户端代理排除规则、是否绕开显式代理；HTTPS 再核对本次公开 CA |
| HTTP/2 空闲一直 Receiving | 属于已披露范围；客户端取消目标流或停止代理，不会为了超时关闭整条 h2 多流连接 |
| 观察字节增长，保存字节停止 | 查看 Partial/Failed 原因；可能为单体/会话/根目录配额或队列到顶，不会补回已经遗漏的数据 |
| 图片/JSON 无预览 | 查看格式、完整性、解压或尺寸预算诊断；可导出实际保存的原始前缀自行检查 |
| 保存目标被拒绝 | 换为捕获根之外的新目录，不覆盖已有目录；确认磁盘可写与空间足够 |
| 重开提示 Body 校验失败 | 文件缺失或变化；保留降级诊断，不能通过修改完整性字段“修复” |
| 启动提示目录被占用 | 同一托管根仅一个运行；先停止占用该目录的代理，或选择新的独立根 |

真实 Host 接入沿用公开 SDK，部署与人工操作需按专用 H 清单另行验证，本轮没有自动复制到任何 Host 的 Controls。
