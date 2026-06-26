using BaihuaguSdk.Push;
using BaihuaguSdk.Services;
using BaihuaguSdk.Signing;
using BaihuaguSdk.Storage;
using MobileApp.Maui.Services;
using MobileContract.Services;
using ZXing.Net.Maui.Controls;

namespace MobileApp.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseBarcodeReader()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        // 先初始化设备 ID，避免后续在 UI 线程反复阻塞 SecureStorage
        DeviceInfoHelper.InitializeAsync().GetAwaiter().GetResult();

        // Platform services
        builder.Services.AddSingleton<ISecureStore, MauiSecureStore>();
        builder.Services.AddSingleton<IServerConfigStore, MauiServerConfigStore>();
        builder.Services.AddSingleton<IVaultStorageFactory>(sp =>
            new VaultStorageFactory(Path.Combine(FileSystem.AppDataDirectory, "vaults")));
        builder.Services.AddSingleton<IQrScanner, MauiQrScanner>();

        // SDK services
        builder.Services.AddSingleton(sp =>
        {
            var deviceId = DeviceInfoHelper.GetDeviceId();
            var deviceName = DeviceInfoHelper.GetDeviceName();
            return new RequestSigner(deviceId, deviceName,
                websiteBaseUrl: "https://www.shzhengji.com",
                mobileClientSecret: GetMobileClientSecret());
        });
        builder.Services.AddSingleton<IRequestSigner>(sp => sp.GetRequiredService<RequestSigner>());

        builder.Services.AddSingleton(sp =>
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            return client;
        });

        // SDK service implementations (factory registration because constructors need string params)
        builder.Services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<HttpClient>();
            var signer = sp.GetRequiredService<IRequestSigner>();
            return new PairingServiceImpl(client, signer,
                DeviceInfoHelper.GetDeviceId(), DeviceInfoHelper.GetDeviceName());
        });
        builder.Services.AddSingleton<IPairingService>(sp => sp.GetRequiredService<PairingServiceImpl>());
        builder.Services.AddSingleton<IDeviceRegistrationService>(sp => sp.GetRequiredService<PairingServiceImpl>());
        builder.Services.AddSingleton<SyncServiceImpl>();
        builder.Services.AddSingleton<ISyncService>(sp => sp.GetRequiredService<SyncServiceImpl>());
        builder.Services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<HttpClient>();
            var signer = sp.GetRequiredService<IRequestSigner>();
            return new LogServiceImpl(client, signer,
                DeviceInfoHelper.GetDeviceId(), DeviceInfoHelper.GetDeviceName());
        });
        builder.Services.AddSingleton<IRemoteLogService>(sp => sp.GetRequiredService<LogServiceImpl>());

        builder.Services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<HttpClient>();
            return new PushWebSocketService(client);
        });
        builder.Services.AddTransient<AuthorizationWatcher>(sp =>
        {
            var registration = sp.GetRequiredService<IDeviceRegistrationService>();
            var pushService = sp.GetRequiredService<PushWebSocketService>();
            return new AuthorizationWatcher(registration, pushService);
        });
        builder.Services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<HttpClient>();
            var signer = sp.GetRequiredService<IRequestSigner>();
            return new PushPollingServiceImpl(client, signer);
        });
        builder.Services.AddSingleton<IPushPollingService>(sp => sp.GetRequiredService<PushPollingServiceImpl>());

        return builder.Build();
    }

    private static string? GetMobileClientSecret()
    {
        var secret = Environment.GetEnvironmentVariable("MOBILE_CLIENT_SECRET");
        return string.IsNullOrEmpty(secret) ? null : secret;
    }
}
