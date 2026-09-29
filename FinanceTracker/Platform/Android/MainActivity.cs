using Android.App;
using Android.Content.PM;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using FinanceTracker;

namespace FinanceTracker.Android
{
    [Activity(
        Label = "Finance Tracker",
        Theme = "@style/MyTheme",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTop,
        WindowSoftInputMode = SoftInput.AdjustResize,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
    public class MainActivity : AvaloniaMainActivity<App>
    {
        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
            => base.CustomizeAppBuilder(builder)
                .WithInterFont();
    }
}
