# V2 · M0 当前运行行为

更新：2026-09-29。本文保留 M0 基线；当前 M1 行为见[M1 运行说明](network-traffic-m1-runtime.md)；验收结果见[当日实施记录](../archive/2026-09-29-v2-m0-implementation.md)，操作见[开发快速开始](../quick-start/network-traffic-m0.md)。

## 1. 已实现范围

插件提供回环显式代理、HTTP/HTTPS 解密转发、普通 HTTP/2、流式响应、有限正文记录、受控目标映射，以及共用一个运行所有者的 Document、状态 Tool 和 Standalone。M0 页面提供端口、启停、实际协议策略、公开根证书、活动数、字节数和最近八条元信息。完整抓包列表、内容查看器、保存回看属于 M1。

默认不启动；监听 127.0.0.1，端口 0 由实际监听分配。不修改系统代理、hosts 或系统信任。每次运行创建新的内存 CA，停止后私钥随实例释放，公开 PEM 可从页面复制给指定测试客户端。再次启动需要重新配置该客户端的信任。

## 2. SOLID 与组合入口

| 文件/对象 | 职责与所有权 |
| --- | --- |
| [ProxyContracts](../../src/NetWorkTrafficPlugin.Plugin/Application/Proxy/ProxyContracts.cs) | 不可变选项、状态、观察结果及窄接口 IProxyRuntime |
| [ProxySessionCoordinator](../../src/NetWorkTrafficPlugin.Plugin/Application/Proxy/ProxySessionCoordinator.cs) | 每容器一个运行所有者，协调起停、代次、等待取消和退出 |
| [RouteTargetResolver](../../src/NetWorkTrafficPlugin.Plugin/Application/Proxy/RouteTargetResolver.cs) | 纯 URI 决策，不执行网络或访问 UI |
| [TitaniumProxyRuntime](../../src/NetWorkTrafficPlugin.Plugin/Infrastructure/Proxy/TitaniumProxyRuntime.cs) | 第三方库、证书、真实请求回调和空闲任务的适配边界 |
| [BoundedBodyRecorder](../../src/NetWorkTrafficPlugin.Plugin/Infrastructure/Capture/BoundedBodyRecorder.cs) | 有界复制、单写入者、连续前缀和记录完整性 |
| [ProxyPanelModel](../../src/NetWorkTrafficPlugin.Plugin/Features/ProxyStatus/ProxyPanelModel.cs) | 页面状态投影和自己的等待令牌 |
| [服务入口](../../src/NetWorkTrafficPlugin.Plugin/Plugin/NetWorkTrafficPluginServices.cs) | 构造注入生产实现；不向应用层泄露 Titanium 类型 |

只在真实 I/O 替换处使用 IProxyRuntime、IBodyRecordSink。纯决策、状态模型和界面使用普通类，没有通用消息总线、规则框架或空转接口。详细中文注释解释并发、借用缓冲区、失败与释放原因。

AddNetWorkTrafficPluginServices 被 Module、Standalone 和独立测量进程复用。Module 使用 SDK 3.4.1 的 UseLifecycle、AddDocument、AddTool；状态 Tool 使用 Hide 关闭行为。保留原示例命令身份及四个回归测试，暂不提升代理按钮为全局快捷键。

## 3. 生命周期与失败

同配置并发启动共享一次运行；运行中改配置、停止中启动、释放后启动均拒绝。页面取消只取消自己的等待；最后一个 Document 关闭仍继续运行。可见 View 每秒在 UI 线程读取快照，离开视觉树停止计时，不持有网络回调订阅，因此没有订阅者异常反向破坏网络的问题。

停止使旧代次失效、停止接纳、取消在途、等待库最多五秒排空并收口记录器，随后等待上游延迟关闭。协调器给整条退出路径十秒截止。超时或释放失败显示 Faulted，保留资源身份，不能声称停止成功或重新占用资源。第三方完全不响应取消时只可报告故障，无法承诺强制释放所有托管任务。

SDK 生命周期 ShutdownAsync 等待协调器异步释放；同步容器 Dispose 是可测试的兜底。Standalone 关闭事件先取消窗口关闭，异步等待同一路径成功后释放容器并再次关闭；失败显示窗口标题错误。真实 Host 是否按其协议调用这些入口仍需 H03 实调。

页面/状态错误只展示中文阶段说明与异常类别，不透传原始异常消息或凭据。RequestObservation 保存原始/有效 URI、方法、两侧实际 HTTP 版本、连接身份、时间、状态码和记录状态。所有经此代理观察的请求当前标记 Captured；合成探针身份在独立证据文件中标识。更丰富的 Stage/Kind 产品诊断留到 M1。

## 4. 已核定的协议与目标映射

| 路径 | 当前范围 |
| --- | --- |
| HTTP/1.1、HTTPS CONNECT 解密 | 受控 GET/POST、路径/重复 Query、正文摘要、多值头、无正文、压缩、3xx、keep-alive |
| 普通 HTTPS HTTP/2 | exact h2、两侧实际 2.0、同客户端连接多流及单流取消 |
| 配置目标映射的运行 | 整次运行只协商 HTTP/1.1；允许回退的 h2 客户端得到 1.1，exact h2 明确失败 |
| HTTP/3、WebSocket、任意客户端 | 未列为已验能力 |

固定版本 7.0.13 原生 h2 目标跨 authority 改写实测产生 PROTOCOL_ERROR。当前明确收窄映射运行范围，不 fork 上游库，不把回退标成 h2 支持。

映射按精确源主机名（忽略大小写）及可选合成 X-M0-Route 条件决定，目标只接受同协议的源地址（scheme/host/port），保留路径原始转义、重复 Query、方法和正文。无路径重写、跨协议转换或热更新规则。运行期间快照不变，改配置需先停止再启动。拒绝 userinfo、fragment、非法 scheme、直接回环自代理以及超过 8,192 字符的 URI。对解析到回环的任意 DNS 别名尚无通用循环识别；M0 只验证受控映射。

库没有本探针所需的普通上游自定义解析入口，因此 A 使用 127.0.0.1，B 使用 localhost，分别签发不同 SAN 证书，回执验证 B 的 Host、SNI、端口、正文摘要与 A 未命中行为。不修改机器 hosts、不使用公网 DNS。B 拒绝、响应头超时或证书错误时不回退 A、不自动重放 POST。

## 5. 流式和记录边界

网络回调不读取完整 Body，不等待 UI 或磁盘；对库借用块只复制允许记录的部分。单写入者消费有界队列；首次额度不足后永久停止该请求的记录，保存连续前缀，真实转发继续。写入失败标 Failed，不能把文件当完整响应。

| 项目 | 当前边界 |
| --- | --- |
| 默认模式 | MetadataOnly；另支持 None、BoundedBody |
| 活动请求 | 最多 20；超额返回 503，不无限排队 |
| 单响应记录 | 最多 1 MiB，达到限额标 Partial |
| 正文队列 | 最多 4 MiB，另限 256 个排队块；正在写入块仍计入字节预算 |
| 总记录磁盘 | 每运行最多 64 MiB；达到后不再记录正文 |
| 元信息 | 最近 1,000 个已完成请求，另含最多 20 个活动请求；淘汰累计计数 |
| 页面显示 | 最近 8 条，目标截到 180 字符且不展示 Query |
| SSE 解析器 | 严格增量 UTF-8、CRLF/LF、多 data 行、心跳、半事件，默认 65,536 个 UTF-16 字符的当前事件上限 |

SSE 解析器目前是独立受测基础组件；生产页面只显示字节/元信息，不维护 256 条事件历史，也未实现正文预览。PreviewBytes 是预留约束，当前不分配预览缓存。原设计的 32 MiB 正文内存上限由更紧的 4 MiB 队列满足，第三方缓冲另外用独立进程峰值测量。

TransportState 与 CaptureState 分开：完整转发仍可只有元信息或部分正文；未知长度保持 ExpectedLength=null；短读、取消或失败不能报完整。停止与最后响应回调竞争时保守标取消。保存字节仅累计成功写入，失败文件即使存在也不能用于完整性推断。

固定库的 IdleReadTimeoutSeconds 在 h1 响应复制期间是一次性截止，会截断活跃长流。适配器禁用它，使用每 250 ms 检查、默认 30 秒无响应数据的空闲判断；每个 Body 块更新单调时钟。测试可缩到一秒但不可关闭，已测试活跃流跨多窗口和沉默响应结束。空闲终止调用库会话取消；任意 h2 空闲与其他流的组合仍应在 M1 扩展验证。连接预算五秒、响应头预算十秒，总响应时长不设短截止。

h1 上游短读时固定库可能留下等待声明长度的客户端连接；适配器在完整性失败后调用公开会话终止 API，使客户端看到失败。未对 h2 整连接采取该短读补偿。

## 6. 证据与适用范围

[本地开发入口](../../tools/verify-development.ps1)运行 locked restore、Debug 零警告构建、普通测试、独立资源测试、TRX/场景/证据校验和文档检查。没有 CI、部署或发布步骤。门禁检查自身有失败注入。

资源测试每种模式先记录启动就绪快照，再用 20 请求各 16 MiB 预热，128 MiB 与 1 GiB 各三次，固定 64 KiB 生成/哈希缓冲。代理由单独控制台进程承载，端点和客户端在测试进程；另有直连 128 MiB 对照、未知长度、20 请求混合负载、十条流持续十分钟。每 250 ms 采样私有内存、工作集、托管堆、活动数、队列、保存量和句柄；记录退出码、停止时间、端口重绑、文件独占打开结果。正式对照曲线使用并发预热基线，另保存启动就绪快照；未把这一个快照冒充冷启动过程峰值。

阈值被冻结在 [resource profile](../../tools/m0-resource-profile.json)：私有内存增量 ≤256 MiB，大/小文件三次峰值中位数差 ≤64 MiB。它们属于本机 Debug 探针门槛，不是产品吞吐或平台容量承诺。

原始证据留在忽略目录 TestResults/NetworkTrafficM0，按 runId 分目录；每轮唯一身份与源码 SHA-256 包括未跟踪行为文件，前后必须一致。D/A 是设计审查，H 是人工验证，不计入自动测试数量。当前没有实际 Host、真实客户端或发布通过结论。
