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

        // Platform services
        builder.Services.AddSingleton<ISecureStore, MauiSecureStore>();
        builder.Services.AddSingleton<IServerConfigStore, MauiServerConfigStore>();

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
                // Accept self-signed certs for LAN baihuagu servers
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            return client;
        });

        builder.Services.AddSingleton<PairingServiceImpl>();
        builder.Services.AddSingleton<SyncServiceImpl>();
        builder.Services.AddSingleton<QuotaServiceImpl>();
        builder.Services.AddSingleton<LogServiceImpl>();


        return builder.Build();
    }

    private static string? GetMobileClientSecret()
    {
        // Read from build-time injected env var (set via MSBuild property)
        // In dev: export MOBILE_CLIENT_SECRET="your-secret"
        var secret = Environment.GetEnvironmentVariable("MOBILE_CLIENT_SECRET");
        return string.IsNullOrEmpty(secret) ? null : secret;
    }
}
