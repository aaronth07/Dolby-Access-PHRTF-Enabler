using Microsoft.UI.Xaml;
namespace AtmosProfile;
public partial class App : Application
{
    Window? window;
    public App()
    {
        ProfileService.PersonalizationTransform = DolbyRoomService.PreserveSetting;
        UnhandledException += (_, e) => WriteError(e.Exception);
        try { InitializeComponent(); } catch (Exception error) { WriteError(error); throw; }
    }
    static void WriteError(Exception error) { Directory.CreateDirectory(ProfileService.DataRoot); File.WriteAllText(Path.Combine(ProfileService.DataRoot, "startup-error.txt"), error.ToString()); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { window = new MainWindow(); FeatureWindow.Attach(window); window.Activate(); }
        catch (Exception error) { WriteError(error); throw; }
    }
}
