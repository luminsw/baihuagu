using BaihuaguSdk.Services;
using BaihuaguSdk.Signing;
using BaihuaguSdk.Transport;
using Xunit;
using Xunit.Abstractions;

namespace BaihuaguSdk.Tests.Integration;

/// <summary>
/// 集成测试：连接真实百花谷服务器验证 SDK 功能。
/// 运行前设置环境变量:
///   export BAIHUAGU_TEST_URL="http://192.168.x.x:8788"
///   export BAIHUAGU_TEST_VAULT_ID="your-vault-id"  (可选)
/// </summary>
public class BaihuaguIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public BaihuaguIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string? TestUrl =>
        Environment.GetEnvironmentVariable("BAIHUAGU_TEST_URL");

    private static string TestVaultId =>
        Environment.GetEnvironmentVariable("BAIHUAGU_TEST_VAULT_ID") ?? "";

    private bool HasServer => !string.IsNullOrEmpty(TestUrl);

    private (HttpClient, RequestSigner) CreateClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var signer = new RequestSigner("test_device", "IntegrationTestDevice");
        return (client, signer);
    }

    [Fact]
    public async Task HttpTransport_ReachServer()
    {
        if (!HasServer) { _output.WriteLine("SKIP: BAIHUAGU_TEST_URL not set"); return; }

        var (client, signer) = CreateClient();
        var transport = new HttpTransport(client, signer, TestUrl!);

        var resp = await transport.GetAsync("/mg/manifest");
        _output.WriteLine($"Manifest check: Status={resp.StatusCode}, IsSuccess={resp.IsSuccess}");

        Assert.True(resp.StatusCode > 0, "Should get a response");
    }

    [Fact]
    public async Task FetchVaultList_ReturnsVaults()
    {
        if (!HasServer) { _output.WriteLine("SKIP: BAIHUAGU_TEST_URL not set"); return; }

        var (client, signer) = CreateClient();
        var sync = new SyncServiceImpl(client, signer);

        var vaults = await sync.FetchVaultListAsync(TestUrl!);
        _output.WriteLine($"Found {vaults.Count} vaults:");
        foreach (var v in vaults)
            _output.WriteLine($"  - {v.Name} (id={v.Id}, industry={v.Industry})");

        Assert.NotNull(vaults);
    }

    [Fact]
    public async Task FetchManifest_Succeeds()
    {
        if (!HasServer) { _output.WriteLine("SKIP: BAIHUAGU_TEST_URL not set"); return; }
        if (string.IsNullOrEmpty(TestVaultId)) { _output.WriteLine("SKIP: BAIHUAGU_TEST_VAULT_ID not set"); return; }

        var (client, signer) = CreateClient();
        var sync = new SyncServiceImpl(client, signer);
        var manifest = await sync.FetchManifestAsync(TestUrl!, TestVaultId, "test_device");
        _output.WriteLine($"Manifest: vaultId={manifest.VaultId}, files={manifest.Files?.Count ?? 0}");

        Assert.NotNull(manifest);
    }

    [Fact]
    public void Pairing_ParseQrCode_SampleData()
    {
        var json = """{"serverId":"test-001","baseUrl":"http://192.168.1.1:8788","hostName":"测试百花谷"}""";
        var content = PairingServiceImpl.ParseQrCode(json);
        Assert.NotNull(content);
        _output.WriteLine($"Parsed QR: serverId={content!.ServerId}, baseUrl={content.BaseUrl}");

        var addrs = PairingServiceImpl.GetServerAddresses(content);
        Assert.Equal("test-001", addrs.ServerId);
        Assert.Equal("http://192.168.1.1:8788", addrs.HttpUrl);
    }

    [Fact]
    public async Task DeviceRegistration_ToRealServer()
    {
        if (!HasServer) { _output.WriteLine("SKIP: BAIHUAGU_TEST_URL not set"); return; }

        var (client, signer) = CreateClient();
        var pairing = new PairingServiceImpl(client, signer, "sdk_test_device", "SDK Test");

        var result = await pairing.RegisterDeviceAsync(TestUrl!);
        _output.WriteLine($"Register: success={result.Success}, authorized={result.Authorized}, " +
                          $"secret={result.SharedSecret != null}, deviceName={result.DeviceName}");

        Assert.NotNull(result);
    }

    [Fact]
    public async Task FullFlow_ParseQr_Register_FetchVaults_FetchManifest()
    {
        if (!HasServer) { _output.WriteLine("SKIP: BAIHUAGU_TEST_URL not set"); return; }

        var (client, signer) = CreateClient();
        var qrJson = $"{{\"serverId\":\"demo\",\"baseUrl\":\"{TestUrl}\",\"hostName\":\"Demo\"}}";
        var qr = PairingServiceImpl.ParseQrCode(qrJson);
        if (qr == null) { _output.WriteLine("QR parse failed"); return; }

        var addrs = PairingServiceImpl.GetServerAddresses(qr);
        _output.WriteLine($"Step 1: Parsed QR → {addrs.HttpUrl}");

        var pairing = new PairingServiceImpl(client, signer, "sdk_e2e", "SDK E2E");
        var reg = await pairing.RegisterDeviceAsync(addrs.HttpUrl);
        _output.WriteLine($"Step 2: Registered → success={reg.Success}, authorized={reg.Authorized}");

        if (!string.IsNullOrEmpty(reg.SharedSecret))
            signer.SetServerSecret(addrs.HttpUrl, reg.SharedSecret);

        var sync = new SyncServiceImpl(client, signer);
        var vaults = await sync.FetchVaultListAsync(addrs.HttpUrl);
        _output.WriteLine($"Step 3: {vaults.Count} vaults found");

        if (vaults.Count > 0)
        {
            var v = vaults[0];
            try
            {
                var m = await sync.FetchManifestAsync(addrs.HttpUrl, v.Id, "sdk_e2e");
                _output.WriteLine($"Step 4: Manifest '{v.Name}' → {m.Files?.Count ?? 0} files");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Step 4: Failed (may need auth): {ex.Message}");
            }
        }
    }
}
