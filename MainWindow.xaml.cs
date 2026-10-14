using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Windows.Storage.Pickers;

namespace AtmosProfile;

public sealed partial class MainWindow : Window
{
    ProfileFile? profile;
    ProfileStatus? status;
    bool updating, busy;
    AudioDevice? Selected => Devices.SelectedItem as AudioDevice;
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    public MainWindow()
    {
        InitializeComponent();
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new((int)(560 * scale), (int)(510 * scale)));
        AppWindow.Closing += (_, e) => { if (busy) { e.Cancel = true; Notify("Wait for the current operation to finish.", true); } };
    }
    void Notify(string text, bool error = false) { Message.Message = text; Message.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Success; Message.IsOpen = true; }
    void UpdateControls()
    {
        Devices.IsEnabled = Browse.IsEnabled = Verify.IsEnabled = !busy;
        Backup.IsEnabled = !busy && status != null;
        Personalized.IsEnabled = !busy && status?.Atmos == true && (status.Personalized ? status.CanRestore : profile != null);
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true; UpdateControls();
        try { await action(); }
        catch (Exception error) { Notify(error.Message, true); }
        finally { busy = false; UpdateControls(); }
    }
    async void Loaded(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var devices = await ProfileService.Devices();
        Devices.ItemsSource = devices;
        var defaultId = Windows.Media.Devices.MediaDevice.GetDefaultAudioRenderId(Windows.Media.Devices.AudioDeviceRole.Default);
        Devices.SelectedItem = devices.FirstOrDefault(d => d.WinRtId.Equals(defaultId, StringComparison.OrdinalIgnoreCase)) ?? devices.FirstOrDefault();
        await Refresh();
    });
    async Task Refresh()
    {
        status = null; updating = true;
        try
        {
            Personalized.IsOn = false;
            if (Selected == null) { AtmosStatus.Text = "No playback device available."; return; }
            var device = Selected;
            try { status = await Task.Run(() => ProfileService.Inspect(device)); AtmosStatus.Text = status.Message; Personalized.IsOn = status.Personalized; }
            catch { AtmosStatus.Text = "No supported Dolby profile on this device."; throw; }
        }
        finally { updating = false; UpdateControls(); }
    }
    async void DeviceChanged(object sender, SelectionChangedEventArgs e) { if (!busy) await Run(Refresh); }
    async void VerifyClicked(object sender, RoutedEventArgs e) => await Run(async () => { await Refresh(); if (status?.Atmos == true) Notify("Atmos provider and Dolby profile storage verified."); });
    async void BrowseClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".personalized_headphone"); picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync(); if (file == null) return;
        profile = null; Metadata.Visibility = Visibility.Collapsed; ProfilePath.Text = file.Path;
        profile = await Task.Run(() => ProfileFile.Load(file.Path));
        Metadata.Text = $"{profile.Name} · {profile.Version} · {profile.Room}"; Metadata.Visibility = Visibility.Visible;
        if (profile.Warning != null) { Notify(profile.Warning); Message.Severity = InfoBarSeverity.Warning; }
        else Notify("Profile metadata and coefficient checksum verified.");
    });
    async void BackupClicked(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (Selected == null) return;
        var device = Selected; var folder = await Task.Run(() => ProfileService.Backup(device));
        await Refresh(); Notify("Backup saved: " + folder);
    });
    async void Toggled(object sender, RoutedEventArgs e)
    {
        if (updating || busy || Selected == null) return;
        bool enabled = Personalized.IsOn; var device = Selected; var selectedProfile = profile;
        await Run(async () =>
        {
            try { _ = await Task.Run(() => ProfileService.Switch(device, selectedProfile, enabled)); Notify(enabled ? "Personalized profile on. Atmos verified." : "Saved profile restored. Atmos verified."); }
            finally { await Refresh(); }
        });
    }
}
