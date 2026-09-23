using Android.App;
using Android.Content.PM;
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
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
    public class MainActivity : AvaloniaMainActivity<App>
    {
        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
            => base.CustomizeAppBuilder(builder)
                .WithInterFont();
    }
}
