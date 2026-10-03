using Xunit;

namespace NetWorkTrafficPlugin.Tests.Fixtures;

/// <summary>
/// 固定库的缺口探针故意保留停滞上传，普通协议探针包含TLS失败后异步清理；二者按集合串行隔离，
/// 避免与整个测试程序集的无关负载竞争退出期限。用例内部12路并发和独立资源测量仍完整执行。
/// </summary>
[CollectionDefinition("ProxyProtocol", DisableParallelization = true)]
public sealed class NetworkProbeCollection;
