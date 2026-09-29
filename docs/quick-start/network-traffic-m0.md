# V2 · M0 开发快速开始

适用：本地 .NET 10 / PowerShell 7 / Windows x64 开发环境。当前行为见[运行说明](../reference/network-traffic-m0-runtime.md)，技术结论见[实施记录](../archive/2026-09-29-v2-m0-implementation.md)。

## 1. 开发检查

在解决方案根目录运行：

~~~powershell
pwsh -NoProfile -File tools/verify-development.ps1 -Mode Docs
pwsh -NoProfile -File tools/verify-development.ps1 -Mode M0
~~~

Docs 只校验链接、锚点和空白，报告明确不代表业务验证。M0 约需十一分钟以上，含真正十分钟持续流；执行门禁失败自测、锁定还原、Debug 构建、普通测试和隔离资源测试。失败返回非零并保留本轮日志，不能复用旧报告。中断后没有通过结论。

结果在 TestResults/NetworkTrafficM0 下本轮目录的 verification.json，同目录包含 TRX、协议/流式/资源 JSON、过程 CSV、profile 和实际场景映射。正文临时文件清理后不归档；证据只含合成输入和公开身份。

只修改局部逻辑时可先运行普通测试：

~~~powershell
dotnet test tests/NetWorkTrafficPlugin.Tests -c Debug --filter 'Category!=ResourceProbe'
pwsh -NoProfile -File tools/test-development-gate.ps1
~~~

正式记录本地开发完成仍需完整 M0 入口。这些命令不执行 Windows CI、Release、ZIP、签名、部署或发布门禁。

## 2. 最小诊断页

~~~powershell
dotnet run --project src/NetWorkTrafficPlugin.Standalone -c Debug
~~~

打开后默认停止。填端口（0 自动分配）并点击“启动代理”；界面显示实际地址、协议策略和公开根证书。用指定客户端显式设置该代理；HTTP 无需 CA，HTTPS 客户端只在本次测试中信任页面 PEM。不要把“允许所有证书”作为测试信任方式。

手工将页面公开 PEM 保存为 m0-public-root.pem 后，在另一终端用已选择的 HTTPS 测试地址：

~~~powershell
# 替换 实际端口 与 测试地址；公开根只属于本次运行。
curl.exe --proxy http://127.0.0.1:实际端口 --noproxy "" --cacert ./m0-public-root.pem https://测试地址/
~~~

页面不安装系统根或设置系统代理。下次启动生成新根，旧 PEM 不能继续用于新运行。默认仅记录元信息，页面不提供映射编辑器或正文文件查看器。

关闭观察页不会停止插件服务；状态 Tool 可以重新查看并显式停止。Standalone 退出会等待收口。真实 Host 中的 Document/Tool 操作需要按 H03 另行验收，不由 Standalone 推定通过。

## 3. 重复受控演示

普通测试提供 HTTPS、首事件握手、A/B 改写、映射 SSE/128 MiB、exact h2、多流取消和失败分支：

~~~powershell
dotnet test tests/NetWorkTrafficPlugin.Tests -c Debug --filter FullyQualifiedName~ProxyProtocolTests
dotnet test tests/NetWorkTrafficPlugin.Tests -c Debug --filter FullyQualifiedName~ProxyFailureTests
~~~

资源夹具启动 TechnicalProbe，与 UI 复用生产服务；不部署插件。控制通道首行为序列化 ProxyOptions，后续接受 snapshot 和 stop；标准输出为 JSON，stdin 结束也触发停止。该宿主不是正式用户 CLI。

## 4. 排查与清理

端口被占用时换端口或用 0，不要结束不属于本项目的进程。映射运行的 exact h2 失败属于当前限制；改为允许 HTTP/1.1 协商或使用无映射运行验证普通 h2。失败或短读看 TransportState，记录额度/落盘错误看 CaptureState，两者分开判断。

自动夹具只清理自己的 GUID 临时目录；门禁超时只终止其拥有的进程树。无需清理系统信任、系统代理或 hosts，因为自动入口没有写这些设置。手工保存的公开 PEM 可在测试后删除；原始证据按需要保留。
