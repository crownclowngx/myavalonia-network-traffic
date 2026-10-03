# V6-02 · 发送前正文能力与依赖缺口

> 核对日期：2026-10-03。固定依赖仍为 Titanium.Web.Proxy 7.0.13。**M3 尚未完成，正文读取适配未接入生产代理**。

## 已验证

测试专用 `TitaniumBodyCapabilityReader` 调用公开 `MaxBufferedBodyBytes` 和 `GetRequestBody(token)`。真实回环探针覆盖256 KiB已知长度/分块正文、UTF-8替换、N+1超限输入；读取及候选产生之前的上游请求头入口计数为零。计数发生在受控端点读取正文之前，不能用“尚未读完Body”冒充“尚未收到头”。

该读取器放在 `tests/Fixtures`，不注册进产品容器。`IRewriteBodyReader` 仍是应用窄端口；满足契约的Fake只用于纯规划测试，不能证明第三方满足相同取消契约。

源码中的 BoundedWriteStream 在写入前检查累计字节，因此支持有界缓冲。它按库的传输块发现超限，并非逐字节读到 N+1；最坏读取还含一个有限传输块。实施中应分别约束应用保留字节和传输预读，不能把应用正文上限误写成套接字恰好读取 N+1。

## 已复现的缺口

客户端严格等待 `100 Continue` 后才发送 Body 时，BeforeRequest 内读取 Body 会等待客户端，而库的合成 Continue 位于 BeforeRequest 之后、发送上游头的阶段。开启 `CompatibilityMode100Continue` 也不能打破这个顺序。

`M3BodyCapabilityTests.固定版本的严格Continue与取消期限存在能力缺口` 将客户端自行发送正文的回退期限设为十秒，不使用短回退掩盖问题。读取截止300毫秒，等待至1300毫秒仍未结束；客户端主动取消后才释放。端点头入口为零。此测试通过代表缺口被复现，**不代表 V6-B06 通过**。另外的50毫秒客户端回退探针只证明客户端自行发出Body后可读写，不算严格Continue支持。

截至核对日，官方最新发布为7.0.15；仅核对其RequestHandler对应源码，同样保留这一Continue顺序，未升级项目，也未验证7.0.15的运行时取消行为。

## 独立的取消缺口

强化探针发现，仅向 `GetRequestBody` 传入取消令牌并不足以按时退出。再将令牌注册到公开 `TerminateSession` 也没有解决。固定库正文变换包含清理/排空路径，取消后仍可能等待客户端数据结束。

`无Expect的停滞上传也不能在取消期限内释放` 使用真实TCP客户端：发完合法请求头后保持连接且不发Body，没有Expect头。300毫秒取消后至1300毫秒任务仍在，客户端关闭后才能退出。普通取消、取消加TerminateSession两种参数均验证。最终等待真实任务结束后再清理，不丢弃后台读取。

因此，**此前提出的417选项只覆盖Continue问题，不能解决完整M3**。不能使用 `WaitAsync` 放弃等待并归还仍被使用的缓冲，也不能用每请求停止整个共享代理规避问题。

## 当前实施决定

原[执行计划](../roadmap/network-traffic-v6-m3-dsl-rewrite-plan.md) V6-02 已规定：“若现版本没有满足该契约的公开路径，登记具体缺口，先完成不依赖它的语言/纯规划/文档工作；M3 保持未完成。”本轮据此完成共享前端、v2编译、原始字段源、纯规划、独立修订库与快照协调基础；实际发送、双快照存档、产品UI、完整M3场景门禁保持未完成。

没有引入源码fork、私有反射、依赖升级或隐式降级。继续完整M3需要可验证的正式依赖/公开适配路径，或明确调整范围并重写验收要求。仅拒绝Expect、缩短客户端回退、只验证完整正文成功路径、将任务丢到后台，都不能关闭本阻断。

## 复现与后续关闭条件

插件仓库内运行：

```powershell
dotnet test NetWorkTrafficPlugin.slnx -c Debug --filter "FullyQualifiedName~M3BodyCapabilityTests" --logger "trx;LogFileName=m3-capability.trx" --results-directory TestResults/M3Capability
```

当前固定版本共9个参数用例，包含5个基础读写/超限用例与4个缺口复现用例。命令成功表示观察与当前能力记录一致；不能作为M3能力可用的开关。独立执行生成TRX；带本地门禁运行身份时另生成 `m3-capability.json`，其中 `supported` / `cancellationWithinDeadline` 明确为false。

关闭缺口至少需要：严格客户端在不依赖自行超时发送的情况下完成受限读取；在取消后1000毫秒内真实读取和清理都退出，包含无Expect的停滞上传；候选提交前两个上游端点均收不到请求头；未知长度读取符合既定探测契约。修复后须将相应缺口复现断言改为成功路径验收，而非删除用例，并重跑完整M3协议/资源/停止矩阵。不得仅根据第三方版本号判断已解决。

依据：[固定版本 SessionEventArgs](https://github.com/justcoding121/titanium-web-proxy/blob/571b7ac896df5986fd3a31f7e02d73fe4bd501a5/src/Titanium.Web.Proxy/EventArguments/SessionEventArgs.cs)、[固定版本 RequestHandler](https://github.com/justcoding121/titanium-web-proxy/blob/571b7ac896df5986fd3a31f7e02d73fe4bd501a5/src/Titanium.Web.Proxy/Handlers/RequestHandler.cs)、[7.0.15 对应源码](https://github.com/justcoding121/titanium-web-proxy/blob/v7.0.15/src/Titanium.Web.Proxy/Handlers/RequestHandler.cs)。
