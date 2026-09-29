using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NetWorkTrafficPlugin.Infrastructure.Proxy;

/// <summary>创建仅驻留内存的运行 CA；不调用证书存储安装 API，不持久化私钥。</summary>
public static class ProxyCertificates
{
    public static X509Certificate2 CreateRoot()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=NetworkTraffic-M0-{Guid.NewGuid():N}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
    }

    /// <summary>
    /// 自定义根只替换链信任，不替换 TLS 的名称检查。必须先拒绝名称不符/无证书，
    /// 再以本轮根构造独立证书链；不访问网络取链，不把未知根之外的错误一并忽略。
    /// </summary>
    public static bool ValidateWithRoot(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2 root)
    {
        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
            return false;
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return chain.Build(leaf);
    }
}
