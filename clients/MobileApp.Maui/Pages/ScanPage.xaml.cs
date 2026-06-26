using ZXing.Net.Maui;

namespace MobileApp.Maui.Pages;

public partial class ScanPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _tcs = new();

    public ScanPage()
    {
        InitializeComponent();
    }

    public Task<string?> WaitForResultAsync() => _tcs.Task;

    private void OnBarcodesDetected(object sender, BarcodeDetectionEventArgs e)
    {
        var first = e.Results?.FirstOrDefault();
        if (first == null || string.IsNullOrEmpty(first.Value))
            return;

        // 只取第一个识别结果
        if (_tcs.TrySetResult(first.Value))
        {
            MainThread.BeginInvokeOnMainThread(async () => await Navigation.PopModalAsync());
        }
    }

    private void OnCancelClicked(object? sender, EventArgs e)
    {
        _tcs.TrySetResult(null);
        Navigation.PopModalAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        _tcs.TrySetResult(null);
        return base.OnBackButtonPressed();
    }
}
