using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;


namespace AtmosProfile;

public sealed partial class MainWindow : Window
{
    ProfileFile? profile;
    ProfileStatus? status;
    bool updating, busy;
    Func<Task>? pending;
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
        Allow.IsEnabled = !busy;
        Devices.IsEnabled = Browse.IsEnabled = Verify.IsEnabled = ProfilePath.IsEnabled = !busy;
        Reset.IsEnabled = !busy && Selected != null;
        ApplyProfile.IsEnabled = !busy && Selected != null && profile != null;
        Backup.IsEnabled = !busy && status != null;
        Personalized.IsEnabled = !busy && status != null && (status.Personalized || profile != null);
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    async Task Run(Func<Task> action, string failure = "The operation could not be completed.")
    {
        if (busy) return;
        busy = true; UpdateControls();
        try { await action(); }
        catch (Exception error) {
            try { System.IO.Directory.CreateDirectory(ProfileService.DataRoot); System.IO.File.WriteAllText(System.IO.Path.Combine(ProfileService.DataRoot, "last-error.log"), error.ToString()); } catch { }
            Notify(error is ProfileOperationException ? error.Message : error is AggregateException ? "The profile could not be restored. The backup is saved." : failure, true);
        }
        finally { busy = false; UpdateControls(); }
    }
    async void Loaded(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var devices = await ProfileService.Devices();
        devices.Insert(0, ProfileService.DefaultDevice);
        Devices.ItemsSource = devices;
        Devices.SelectedItem = ProfileService.DefaultDevice;
        await Refresh();
    });
    async Task Refresh()
    {
        status = null; updating = true;
        try
        {

            if (Selected == null) { AtmosStatus.Text = "No playback device available."; return; }
            var device = ProfileService.Resolve(Selected);
            try { status = await Task.Run(() => ProfileService.Inspect(device)); AtmosStatus.Text = status.Message; Personalized.IsOn = status.Personalized; }
            catch { Personalized.IsOn = false; AtmosStatus.Text = "No supported Dolby profile on this device."; throw; }
        }
        finally { updating = false; UpdateControls(); }
    }
    async void DeviceChanged(object sender, SelectionChangedEventArgs e) { pending = null; Allow.Visibility = Visibility.Collapsed; if (!busy) await Run(Refresh); }
    async void VerifyClicked(object sender, RoutedEventArgs e) => await Run(async () => { await Refresh(); if (status?.Atmos == true) Notify("Atmos verified."); });
    async void BrowseClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var path = NativeFilePicker.Pick(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (path == null) return;
        ProfilePath.Text = path; await LoadProfile();
    }, "The file picker could not be opened.");
    async Task LoadProfile()
    {
        profile = null; Metadata.Visibility = Visibility.Collapsed;
        string path = ProfilePath.Text.Trim().Trim('"'); if (path.Length == 0) return;
        try { profile = await Task.Run(() => ProfileFile.Load(path)); } catch { throw new ProfileOperationException("The profile could not be verified."); }
        ProfilePath.Text = path;
        Metadata.Text = $"{profile.Name} Ãƒâ€šÃ‚Â· {profile.Version} Ãƒâ€šÃ‚Â· {profile.Room}"; Metadata.Visibility = Visibility.Visible;
        if (profile.Warning != null) { Notify(profile.Warning); Message.Severity = InfoBarSeverity.Warning; }
        else Notify("Profile verified.");
    }
    async void PathChanged(object sender, RoutedEventArgs e) { if (!busy) await Run(LoadProfile, "The profile could not be verified."); }
    async void PathKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e) { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; await Run(LoadProfile, "The profile could not be verified."); } }
    async void BackupClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (Selected == null) return;
        var device = ProfileService.Resolve(Selected); var folder = await Task.Run(() => ProfileService.Backup(device));
        await Refresh(); Notify("Backup saved.");
    });
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
    void ResetClicked(object sender, RoutedEventArgs e)
    {
        if (busy || Selected == null) return;
        var device = ProfileService.Resolve(Selected);
        Warn($"Reset {device.Name}? This deletes this device's application backups and configuration, restarts Atmos, and saves the current endpoint profile as the new Off baseline.", async () => {
            await Task.Run(() => ProfileService.Reset(device)); profile = null; ProfilePath.Text = ""; Metadata.Visibility = Visibility.Collapsed; await Refresh(); Notify("Configuration reset.");
        });
    }
    async Task Apply(AudioDevice device, ProfileFile? selectedProfile, bool enabled, bool force)
    {
        try {
            await Task.Run(() => ProfileService.Switch(device, selectedProfile, enabled, force: force));
            await Refresh(); Notify(enabled ? "Personalized profile on." : "Saved profile restored.");
        } catch (ProfileWarningException e) {
            await Refresh();
            Warn(e.Message + " Allow to overwrite it and restart Atmos.", () => Apply(device, selectedProfile, enabled, true));
        } catch { await Refresh(); throw; }
    }
    async void ApplyClicked(object sender, RoutedEventArgs e)
    {
        if (busy || Selected == null) return;
        var device = ProfileService.Resolve(Selected);
        await Run(async () => { await LoadProfile(); await Apply(device, profile, true, false); });
    }
    async void Toggled(object sender, RoutedEventArgs e)
    {
        if (updating || busy || Selected == null) return;
        bool enabled = Personalized.IsOn; var device = ProfileService.Resolve(Selected);
        await Run(async () => { if (enabled) await LoadProfile(); await Apply(device, profile, enabled, false); });
    }
}
