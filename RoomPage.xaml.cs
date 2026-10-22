using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace AtmosProfile;

public sealed partial class RoomPage : UserControl
{
    bool busy, loaded;
    Func<Task>? pending;
    public RoomPage() { InitializeComponent(); }
    async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true; Message.IsOpen = false; FeatureWindow.ActiveOperations++; Controls();
        try { await action(); }
        catch (Exception e) { Show(e is ProfileOperationException ? e.Message : "The room profile could not be verified.", InfoBarSeverity.Error); FeatureDiagnostics.Save("room", e); }
        finally { busy = false; FeatureWindow.ActiveOperations--; Controls(); }
    }
    void Controls() { Devices.IsEnabled = Setting.IsEnabled = RoomLevel.IsEnabled = RoomFeedback.IsEnabled = GainCorrection.IsEnabled = Enable.IsEnabled = Allow.IsEnabled = !busy; Apply.IsEnabled = Restore.IsEnabled = !busy && Devices.SelectedItem != null; Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; }
    void Show(string text, InfoBarSeverity severity = InfoBarSeverity.Success) { pending = null; Allow.Visibility = Visibility.Collapsed; Message.Message = text; Message.Severity = severity; Message.IsOpen = true; }
    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (loaded) { await Run(Refresh); return; } loaded = true;
        await Run(async () => { var devices = await ProfileService.Devices(); devices.Insert(0, ProfileService.DefaultDevice); Devices.ItemsSource = devices; Devices.SelectedIndex = 0; await Refresh(); });
    }
    async Task Refresh()
    {
        if (Devices.SelectedItem is not AudioDevice selected) return;
        var device = ProfileService.Resolve(selected);
        bool active = await Task.Run(() => { var s = AudioPlatform.Spatial(device); return s.ActiveSpatialAudioFormat.Equals(AudioPlatform.Dolby, StringComparison.OrdinalIgnoreCase) && s.DefaultSpatialAudioFormat.Equals(AudioPlatform.Dolby, StringComparison.OrdinalIgnoreCase); });
        Enable.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        if (active) {
            var settings = await Task.Run(() => DolbyRoomService.Settings(device));
            var item = Setting.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == settings.Mode);
            Setting.SelectedItem = item ?? Setting.Items[0]; RoomLevel.Value = settings.Level; RoomFeedback.Value = settings.Feedback;GainCorrection.IsChecked=settings.GainCorrection;
        }
        Status.Text = active ? await Task.Run(() => DolbyRoomService.Inspect(device)) : "Dolby Atmos for Headphones is not active.";
    }
    async void DeviceChanged(object sender, SelectionChangedEventArgs e) { pending = null; Allow.Visibility = Visibility.Collapsed; if (!busy) await Run(Refresh); }
    async void EnableClicked(object sender, RoutedEventArgs e) => await Run(async () => { if (Devices.SelectedItem is AudioDevice selected) { var d = ProfileService.Resolve(selected); await Task.Run(() => DtsProfileService.SelectProvider(d, AudioPlatform.Dolby, "Dolby Atmos for Headphones")); await Refresh(); Show("Dolby Atmos for Headphones enabled."); } });
    async Task Change(AudioDevice device, string mode, bool force, double level = 100, double feedback = 100,bool gainCorrection=false)
    {
        try { Status.Text = await Task.Run(() => DolbyRoomService.Change(device, mode, force, level, feedback,gainCorrection)); Show(Status.Text); }
        catch (ProfileWarningException e) { Message.Message = e.Message; Message.Severity = InfoBarSeverity.Warning; Message.IsOpen = true; Allow.Visibility = Visibility.Visible; pending = () => Change(device, mode, true, level, feedback,gainCorrection); }
    }
    async void ApplyClicked(object sender, RoutedEventArgs e)
    {
        if (Devices.SelectedItem is AudioDevice selected && Setting.SelectedItem is ComboBoxItem item) { string mode = (string)item.Tag; double level = RoomLevel.Value, feedback = RoomFeedback.Value; await Run(() => {
            var device = ProfileService.Resolve(selected);
            if (mode is "DoubleRoom" or "QuadRoom") {
                string ratio = mode == "QuadRoom" ? "400%" : "200%", attenuation = mode == "QuadRoom" ? "12.04" : "6.02";
                bool correct=GainCorrection.IsChecked==true;string makeup=correct?$"Overall gain is raised by {(mode=="QuadRoom"?12:6)} dB. This can clip; lower playback level before testing.":"Overall gain is unchanged.";
                Message.Message = $"Test {ratio} room/direct? Directional fields are reduced by {attenuation} dB; room taps and feedback retain their original gain. {makeup} On or Restore returns the original gain.";
                Message.Severity = InfoBarSeverity.Warning; Message.IsOpen = true; Allow.Visibility = Visibility.Visible;
                pending = () => Change(device, mode, false, level, feedback,correct);
                return Task.CompletedTask;
            }
            return Change(device, mode, false, level, feedback);
        }); }
    }
    void SettingChanged(object sender, SelectionChangedEventArgs e) { pending = null; if (Allow != null) Allow.Visibility = Visibility.Collapsed; if(GainCorrection!=null)GainCorrection.Visibility=Setting.SelectedItem is ComboBoxItem experimental && (string)experimental.Tag is "DoubleRoom" or "QuadRoom"?Visibility.Visible:Visibility.Collapsed; if (CustomControls != null) CustomControls.Visibility = Setting.SelectedItem is ComboBoxItem item && (string)item.Tag == "Custom" ? Visibility.Visible : Visibility.Collapsed; }
    void GainCorrectionChanged(object sender,RoutedEventArgs e){pending=null;if(Allow!=null)Allow.Visibility=Visibility.Collapsed;}
    void CustomChanged(NumberBox sender, NumberBoxValueChangedEventArgs e) { pending = null; if (Allow != null) Allow.Visibility = Visibility.Collapsed; }
    async void RestoreClicked(object sender, RoutedEventArgs e) { if (Devices.SelectedItem is AudioDevice selected) await Run(() => Change(ProfileService.Resolve(selected), "Restore", false)); }
    async void AllowClicked(object sender, RoutedEventArgs e) { var action = pending; pending = null; Allow.Visibility = Visibility.Collapsed; if (action != null) await Run(action); }
}
