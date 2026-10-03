# M4 请求调试快速开始

> 当前开放无规则手工请求和可靠旧快照提取；完整M4未验收。能力限制见 [当前实现](../reference/network-traffic-m4-request-debugging.md)。本页只使用本地Debug，不执行Windows CI或发布门禁。

## 手工调试

1. 在HTTP工作台“打开”菜单进入请求调试，无需先启动代理。
2. 输入本地受控服务的绝对URL，选择方法。Query逐行编辑会同步地址；重复键保留。Header使用`Name: Value`；行首`# `可以暂时禁用。
3. GET/HEAD选择None；POST等可选Text、Json、Form或RawFile。Text/Json/Form最多256KiB；单文件最多64MiB。Form每行`name=value`。
4. 在本次变量中填写`name=value`，敏感值用`!token=value`；例如Header写`Authorization: Bearer ${token}`。输入默认遮蔽，可显式显示。
5. 点击“预览最终请求”，核对地址、头和正文。未定义变量、非法输入或不完整来源会阻止准备。预览本身没有网络请求。
6. 点击“发送一次”。需要中止时点“取消本次请求”；派发后取消不代表服务器未执行。再次发送必须重新预览。
7. 在“结果与历史”查看列表与内容；执行摘要页用于重启后追查。收到流式响应时可进入结果页选择在途记录查看内容。历史和模板打开不会发送。

## 两个环境与模板

在“模板与环境”填写环境名称及普通变量，例如`baseUrl=http://127.0.0.1:你的端口`，点击“另存普通环境”。返回编辑页选择环境，地址写`${baseUrl}/api`。需要回到无环境时点击“清除环境选择”，已保存环境仍保留。切换、清除或更新环境后必须重新预览；已经在途的请求保持接纳时字节。

用“另存当前模板”保存草稿，选中模板可载入、更新修订或删除。敏感值下次重新填写，普通变量、未解析结构及禁用Query行保留。另一页面已经提交新修订时，先“加载模板与环境”，核对后再保存。

[合成样例](../examples/m4/README.md)仅使用example.test，单元测试会通过真实库校验和离线预览解析。它不是现成的可访问服务，不要把它当作协议成功证据。

## 从记录提取与对比

在捕获列表选中记录，或在分析时间线选中调用，通过“打开”菜单的提取操作创建调试草稿。当前只支持可证明完整的LegacyObserved事实；旧映射记录、正文前缀、未知编码、缺元信息会拒绝。原始/有效双快照与应用规则需等待M3闭合。

修改、预览并发送后点击“对比来源结果”，查看请求及响应差异。输出会注明部分比较、敏感内容不比较、预算截断等原因。保存结果快照沿用内容页操作，可能包含已保存的原始响应；未显式保存的调试正文不能在重启后恢复。

## 本地开发检查

在仓库根执行：

```powershell
# 文档链接、git diff空白检查
pwsh -NoProfile -File tools/verify-development.ps1 -Mode Docs
# 当前已实现的M4分项：全体普通测试、87条M4测试、端点、资源、18张截图和门禁自测
pwsh -NoProfile -File tools/verify-development.ps1 -Mode M4Foundation
# 独立完整M2兼容回归；包含已有持续资源和桌面探针，耗时较长
pwsh -NoProfile -File tools/verify-development.ps1 -Mode M2
# 完整M4前置检查：当前预期退出1，写明M3和同轮359项缺口
pwsh -NoProfile -File tools/verify-development.ps1 -Mode M4
```

所有报告保存在`TestResults/NetworkTraffic模式/运行身份/`。检查`verification.json`的范围、源码摘要、命令及未验项；不能只看测试总数。M4Foundation通过不等于80项业务场景已全部验收，也不等于359项累计、真实Host或发布通过。
