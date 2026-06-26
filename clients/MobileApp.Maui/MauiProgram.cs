using BaihuaguSdk.Push;
using BaihuaguSdk.Services;
using BaihuaguSdk.Signing;
using BaihuaguSdk.Storage;
using MobileApp.Maui.Services;

namespace MobileApp.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

        // Platform services
        builder.Services.AddSingleton<ISecureStore, MauiSecureStore>();
        builder.Services.AddSingleton<IServerConfigStore, MauiServerConfigStore>();
        builder.Services.AddSingleton<MobileContract.Services.IVaultStorageAdapter>(sp =>
            new VaultStorageAdapter(Path.Combine(FileSystem.AppDataDirectory, "vaults")));

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
        builder.Services.AddSingleton<SyncServiceImpl>();
        builder.Services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<HttpClient>();
            var signer = sp.GetRequiredService<IRequestSigner>();
            return new QuotaServiceImpl(client, signer, "");
        });
        builder.Services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<HttpClient>();
            var signer = sp.GetRequiredService<IRequestSigner>();
            return new LogServiceImpl(client, signer,
                DeviceInfoHelper.GetDeviceId(), DeviceInfoHelper.GetDeviceName());
        });

        builder.Services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<HttpClient>();
            return new PushWebSocketService(client);
        });

        return builder.Build();
    }

    private static string? GetMobileClientSecret()
    {
        var secret = Environment.GetEnvironmentVariable("MOBILE_CLIENT_SECRET");
        return string.IsNullOrEmpty(secret) ? null : secret;
    }
}
