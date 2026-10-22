using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;


namespace AtmosProfile;

public sealed partial class MainWindow : Window
{
    ProfileFile? profile;
    ProfileStatus? status;
    bool updating, busy, loaded;
    Func<Task>? pending;
    string? checkedProfilePath;
    AudioDevice? Selected => Devices.SelectedItem as AudioDevice;
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    public MainWindow()
    {
        InitializeComponent();
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new((int)(560 * scale), (int)(610 * scale)));
        AppWindow.Closing += (_, e) => { if (busy) { e.Cancel = true; Notify("An operation is still running.", true); } };
    }
    void Notify(string text, bool error = false) { pending = null; Allow.Visibility = Visibility.Collapsed; Message.Message = text; Message.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Success; Message.IsOpen = true; }
    void UpdateControls()
    {
        Allow.IsEnabled = EnableAtmos.IsEnabled = !busy;
        Devices.IsEnabled = Browse.IsEnabled = ProfilePath.IsEnabled = !busy;
        Reset.IsEnabled = !busy && Selected != null;
        Personalized.IsEnabled = !busy && Selected != null && (status?.Personalized == true || status?.CanRestore == true || profile != null);
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    async Task Run(Func<Task> action, string failure = "The operation could not be completed.")
    {
        if (busy) return;
        busy = true; FeatureWindow.ActiveOperations++; UpdateControls();
        try { await action(); }
        catch (Exception error) {
            try { await Refresh(); } catch { }
            try { System.IO.Directory.CreateDirectory(ProfileService.DataRoot); System.IO.File.WriteAllText(System.IO.Path.Combine(ProfileService.DataRoot, "last-error.log"), error.ToString()); } catch { }
            Notify(error is ProfileOperationException ? error.Message : error is AggregateException ? "The profile could not be restored. The backup is saved." : failure, true);
        }
        finally { busy = false; FeatureWindow.ActiveOperations--; UpdateControls(); }
    }
    async void Loaded(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (loaded) { await Refresh(); return; }
        var devices = await ProfileService.Devices();
        devices.Insert(0, ProfileService.DefaultDevice);
        Devices.ItemsSource = devices;
        Devices.SelectedItem = ProfileService.DefaultDevice;
        loaded = true;
        await Refresh();
    });
    async Task Refresh()
    {
        status = null; updating = true;
        try
        {

            if (Selected == null) { AtmosStatus.Text = "No playback device available."; return; }
            var device = ProfileService.Resolve(Selected);
            try { status = await Task.Run(() => ProfileService.Inspect(device)); AtmosStatus.Text = status.Message; Personalized.IsOn = status.Personalized; EnableAtmos.Visibility = status.Atmos ? Visibility.Collapsed : Visibility.Visible; }
            catch (Exception error) { Personalized.IsOn = false; AtmosStatus.Text = error is ProfileOperationException ? error.Message : "The device configuration could not be read."; EnableAtmos.Visibility = Visibility.Visible; throw; }
        }
        finally { updating = false; UpdateControls(); }
    }
    async void DeviceChanged(object sender, SelectionChangedEventArgs e) { pending = null; Allow.Visibility = Visibility.Collapsed; if (!busy) await Run(Refresh); }
    async void EnableAtmosClicked(object sender, RoutedEventArgs e) => await Run(async () => {
        if (Selected is AudioDevice selected) {
            var device = ProfileService.Resolve(selected);
            await Task.Run(() => DtsProfileService.SelectProvider(device, AudioPlatform.Dolby, "Dolby Atmos for Headphones"));
            await Refresh(); Notify("Dolby Atmos for Headphones enabled.");
        }
    });
    async void BrowseClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var path = NativeFilePicker.Pick(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (path == null) return;
        ProfilePath.Text = path; await LoadSelection();
    }, "The file picker could not be opened.");
    async Task LoadProfile()
    {
        pending = null; Allow.Visibility = Visibility.Collapsed;
        profile = null; Metadata.Visibility = Visibility.Collapsed;
        string path = ProfilePath.Text.Trim().Trim('"'); checkedProfilePath = path; if (path.Length == 0) return;
        try { profile = await Task.Run(() => ProfileFile.Load(path)); } catch { throw new ProfileOperationException("The profile could not be verified."); }
        ProfilePath.Text = path;
        Metadata.Text = $"{profile.Name}, {profile.Version}, {profile.Room}"; Metadata.Visibility = Visibility.Visible;
        if (profile.Warning != null) { Notify(profile.Warning); Message.Severity = InfoBarSeverity.Warning; }
        else Notify("Profile verified.");
    }
    async void PathChanged(object sender, RoutedEventArgs e) { if (!busy && ProfilePath.Text.Trim().Trim('"') != checkedProfilePath) await Run(LoadSelection, "The profile could not be verified."); }
    async void PathKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e) { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await Run(LoadSelection, "The profile could not be verified."); } }
    async Task LoadSelection()
    {
        bool enabled = status?.Personalized == true;
        var selected = Selected;
        await LoadProfile();
        if (enabled && selected != null && profile != null)
            await Apply(ProfileService.Resolve(selected), profile, true, false);
    }
    void Warn(string text, Func<Task> action)
    {
        Message.Message = text; Message.Severity = InfoBarSeverity.Warning; Message.IsOpen = true;
        pending = action; Allow.Visibility = Visibility.Visible;
    }
    async void AllowClicked(object sender, RoutedEventArgs e)
    {
        var action = pending; pending = null; Allow.Visibility = Visibility.Collapsed;
        if (action != null) await Run(action);
    }
    async void ResetClicked(object sender, RoutedEventArgs e)
    {
        if (busy || Selected == null) return;
        var selected = Selected;
        await Run(() => {
        var device = ProfileService.Resolve(selected);
        Warn($"Reset Dolby configuration for {device.Name}? This restores saved room settings, clears PHRTF and room backups, and saves the current personalized profile as the new Off baseline. DTS is kept.", () => ResetConfiguration(device, false));
        return Task.CompletedTask;
        });
    }
    async Task Apply(AudioDevice device, ProfileFile? selectedProfile, bool enabled, bool force)
    {
        try {
            await Task.Run(() => ProfileService.Switch(device, selectedProfile, enabled, force: force));
            await Refresh(); Notify(enabled ? "Personalized profile on." : "Saved profile restored.");
        } catch (ProfileWarningException e) {
            try { await Refresh(); } catch { }
            Warn(e.Message + " Allow to overwrite it and restart Atmos.", () => Apply(device, selectedProfile, enabled, true));
        } catch { await Refresh(); throw; }
    }
    async Task ResetConfiguration(AudioDevice device, bool force)
    {
        try {
            await Task.Run(() => ProfileService.Reset(device, force));
            profile = null; checkedProfilePath = ""; ProfilePath.Text = ""; Metadata.Visibility = Visibility.Collapsed;
            await Refresh(); Notify("Dolby configuration reset.");
        } catch (ProfileWarningException e) {
            Warn(e.Message, () => ResetConfiguration(device, true));
        }
    }
    async void Toggled(object sender, RoutedEventArgs e)
    {
        if (updating || busy || Selected == null) return;
        bool enabled = Personalized.IsOn; var selected = Selected;
        await Run(async () => { var device = ProfileService.Resolve(selected); if (enabled) await LoadProfile(); await Apply(device, profile, enabled, false); });
    }
}
