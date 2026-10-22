using Microsoft.UI.Xaml;
namespace AtmosProfile;
public partial class App : Application
{
    Window? window;
    public App()
    {
#if UI_QA
        UiQa.Initialize();
#endif
        ProfileService.PersonalizationTransform = DolbyRoomService.PreserveSetting;
        UnhandledException += (_, e) => WriteError(e.Exception);
        try { InitializeComponent(); } catch (Exception error) { WriteError(error); throw; }
    }
    static void WriteError(Exception error) { try { Directory.CreateDirectory(ProfileService.DataRoot); File.WriteAllText(Path.Combine(ProfileService.DataRoot, "startup-error.txt"), error.ToString()); } catch { System.Diagnostics.Debug.WriteLine(error); } }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { window = new MainWindow(); FeatureWindow.Attach(window);
#if UI_QA
            window.AppWindow.Show(false); _ = UiQa.Run(window);
#else
            window.Activate();
#endif
        }
        catch (Exception error) { WriteError(error); throw; }
    }
}
