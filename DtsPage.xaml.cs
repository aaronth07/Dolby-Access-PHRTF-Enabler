using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace AtmosProfile;

public sealed partial class DtsPage : UserControl
{
    bool busy, loaded, updating;
    string? candidate, candidateDevice;
    Func<Task>? pending;
    public DtsPage() { InitializeComponent(); }
    void Controls() { Devices.IsEnabled = SofaPath.IsEnabled = Browse.IsEnabled = Enable.IsEnabled = Allow.IsEnabled = Reset.IsEnabled = Room.IsEnabled = Interpolation.IsEnabled = !busy; Convert.IsEnabled = !busy && !string.IsNullOrWhiteSpace(SofaPath.Text); Apply.IsEnabled = !busy && candidate != null; Restore.IsEnabled = !busy && Devices.SelectedItem != null; Custom.IsEnabled = !busy && (candidate != null || Custom.IsOn); Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; }
    void Show(string text, InfoBarSeverity severity = InfoBarSeverity.Success) { pending = null; Allow.Visibility = Visibility.Collapsed; Message.Message = text; Message.Severity = severity; Message.IsOpen = true; }
    async Task Run(Func<Task> action)
    {
        if (busy) return; busy = true; Message.IsOpen = false; FeatureWindow.ActiveOperations++; Controls();
        try { await action(); }
        catch (Exception e) {
            try { await Refresh(); } catch { }
            Show(e is ProfileOperationException ? e.Message : "The DTS operation failed.", InfoBarSeverity.Error);
            Details.Text = e is ProfileToolException tool ? tool.Details : e.GetBaseException().Message;
            DetailsPanel.Header = "Error details"; DetailsPanel.Visibility = Visibility.Visible; DetailsPanel.IsExpanded = true;
            FeatureDiagnostics.Save("dts", e);
        }
        finally { busy = false; FeatureWindow.ActiveOperations--; Controls(); }
    }
    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (loaded) { await Run(Refresh); return; } loaded = true;
        await Run(async () => { var devices = await ProfileService.Devices(); devices.Insert(0, ProfileService.DefaultDevice); Devices.ItemsSource = devices; Devices.SelectedIndex = 0; await Refresh(); });
    }
    Task Refresh() => Refresh(candidate == null);
    async Task Refresh(bool updateRoom)
    {
        if (Devices.SelectedItem is not AudioDevice selected) return;
        updating = true;
        try { var status = await Task.Run(() => DtsProfileService.Inspect(selected)); Status.Text = status.Message; Custom.IsOn = status.Custom; if (updateRoom) Room.SelectedItem = Room.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == status.RoomMode) ?? Room.Items[0]; Enable.Visibility = status.Active ? Visibility.Collapsed : Visibility.Visible;
            if (status.Active && status.EqMode == "Original") Show("DTS headphone EQ filters are active. Applying a SOFA disables them; Restore returns the original EQ.", InfoBarSeverity.Warning);
        }
        catch { Enable.Visibility = Visibility.Collapsed; Custom.IsOn = false; Status.Text = "DTS device status is unavailable."; throw; }
        finally { updating = false; }
    }
    void Invalidate() { candidate = candidateDevice = null; DetailsPanel.Visibility = Visibility.Collapsed; pending = null; Allow.Visibility = Visibility.Collapsed; Controls(); }
    void PathChanged(object sender, TextChangedEventArgs e) { if (Details != null) Invalidate(); }
    void InterpolationChanged(object sender, SelectionChangedEventArgs e) { if (Details != null) Invalidate(); }
    async void DeviceChanged(object sender, SelectionChangedEventArgs e) { Invalidate(); if (!busy) await Run(Refresh); }
    async void BrowseClicked(object sender, RoutedEventArgs e) => await Run(async () => { var window = FeatureWindow.Owner; var path = FeatureFilePicker.Pick(WinRT.Interop.WindowNative.GetWindowHandle(window), ".sofa", "Select SOFA file"); if (path != null) SofaPath.Text = path; await Task.CompletedTask; });
    async void ConvertClicked(object sender, RoutedEventArgs e) => await Run(async () => {
        if (Devices.SelectedItem is not AudioDevice selected) return;
        var d = ProfileService.Resolve(selected); string path = SofaPath.Text.Trim().Trim('"');
        await ConvertProfile(d, path, false, (string)((ComboBoxItem)Interpolation.SelectedItem).Tag);
    });
    async Task ConvertProfile(AudioDevice device, string path, bool force, string interpolationMethod)
    {
        Invalidate();
        try {
            var result = await Task.Run(() => DtsProfileService.Convert(device, path, force, interpolationMethod));
            candidate = result.Path; candidateDevice = device.Id; Details.Text = result.Summary; DetailsPanel.Header = "Conversion details"; DetailsPanel.Visibility = Visibility.Visible; DetailsPanel.IsExpanded = false; Show(result.NativeValidated ? "SOFA converted. Native format validation passed." : "SOFA converted without native validation.", result.NativeValidated ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        } catch (ProfileWarningException e) {
            Message.Message = e.Message; Message.Severity = InfoBarSeverity.Warning; Message.IsOpen = true;
            Allow.Visibility = Visibility.Visible; pending = () => ConvertProfile(device, path, true, interpolationMethod);
        }
    }
    async void EnableClicked(object sender, RoutedEventArgs e) => await Run(async () => { if (Devices.SelectedItem is AudioDevice selected) { var d = ProfileService.Resolve(selected); await Task.Run(() => DtsProfileService.Enable(d)); await Refresh(candidate == null); if (!Message.IsOpen) Show("DTS Headphone:X enabled."); } });
    string RoomMode => Room.SelectedItem is ComboBoxItem item ? (string)item.Tag : "Original";
    bool ConfirmOverdrive(string mode,Func<Task> action)
    {
        if(mode is not("Spacious200" or "Spacious400"))return false;
        Message.Message=$"Test Spacious {(mode=="Spacious400"?400:200)}%? The native Spacious room sends are raised by {(mode=="Spacious400"?"12.04":"6.02")} dB. Personalized FIRs and feedback stay unchanged. Higher output can clip; lower playback level before testing.";
        Message.Severity=InfoBarSeverity.Warning;Message.IsOpen=true;Allow.Visibility=Visibility.Visible;pending=action;return true;
    }
    async Task Change(AudioDevice d, bool enabled, bool force, string? selectedRoom = null,bool experimentalAccepted=false)
    {
        string mode = selectedRoom ?? RoomMode;
        if(enabled&&!experimentalAccepted&&ConfirmOverdrive(mode,()=>Change(d,enabled,force,mode,true)))return;
        try { if (enabled && d.Id != candidateDevice) throw new ProfileOperationException("Verify the SOFA file for this device."); string message = await Task.Run(() => DtsProfileService.Change(d, candidate, enabled, force, "Selected", false, mode)); await Refresh(true); Show(message, message == DtsProfileService.EqSkippedMessage ? InfoBarSeverity.Warning : InfoBarSeverity.Success); }
        catch (ProfileWarningException e) { try { await Refresh(); } catch { } Message.Message = e.Message; Message.Severity = InfoBarSeverity.Warning; Message.IsOpen = true; Allow.Visibility = Visibility.Visible; pending = () => Change(d, enabled, true, mode,experimentalAccepted); }
        catch { try { await Refresh(); } catch { } throw; }
    }
    async void ApplyClicked(object sender, RoutedEventArgs e) { if (Devices.SelectedItem is AudioDevice selected) await Run(() => Change(ProfileService.Resolve(selected), true, false)); }
    async void RestoreClicked(object sender, RoutedEventArgs e) { if (Devices.SelectedItem is AudioDevice selected) await Run(() => Change(ProfileService.Resolve(selected), false, false)); }
    async void Toggled(object sender, RoutedEventArgs e) { if (busy || updating || Devices.SelectedItem is not AudioDevice selected) return; bool enabled = Custom.IsOn; await Run(() => Change(ProfileService.Resolve(selected), enabled, false)); }
    async Task ApplyRoom(AudioDevice device, string mode, bool force,bool experimentalAccepted=false)
    {
        if(!experimentalAccepted&&ConfirmOverdrive(mode,()=>ApplyRoom(device,mode,force,true)))return;
        try { string message = await Task.Run(() => DtsProfileService.ChangeRoom(device, mode, force)); await Refresh(true); Show(message); }
        catch (ProfileWarningException e) { Message.Message = e.Message; Message.Severity = InfoBarSeverity.Warning; Message.IsOpen = true; Allow.Visibility = Visibility.Visible; pending = () => ApplyRoom(device, mode, true,experimentalAccepted); }
    }
    async void RoomChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loaded || updating || busy || Devices.SelectedItem is not AudioDevice selected) return;
        pending = null; Allow.Visibility = Visibility.Collapsed;
        if (candidate != null && !Custom.IsOn) return;
        string mode = RoomMode;
        await Run(() => ApplyRoom(ProfileService.Resolve(selected), mode, false));
    }
    async void ResetClicked(object sender, RoutedEventArgs e)
    {
        await Run(() => {
            if (Devices.SelectedItem is AudioDevice selected) {
                var device = ProfileService.Resolve(selected);
                Message.Message = $"Reset DTS configuration for {device.Name}? This restores the saved DTS profile and clears its conversions and backups. Dolby is kept.";
                Message.Severity = InfoBarSeverity.Warning; Message.IsOpen = true; Allow.Visibility = Visibility.Visible;
                pending = () => ResetConfiguration(device, false);
            }
            return Task.CompletedTask;
        });
    }
    async Task ResetConfiguration(AudioDevice device, bool force)
    {
        try {
            await Task.Run(() => DtsProfileService.Reset(device, force));
            SofaPath.Text = ""; Invalidate(); await Refresh(); Show("DTS configuration reset.");
        } catch (ProfileWarningException e) {
            Message.Message = e.Message; Message.Severity = InfoBarSeverity.Warning; Message.IsOpen = true; Allow.Visibility = Visibility.Visible;
            pending = () => ResetConfiguration(device, true);
        }
    }
    async void AllowClicked(object sender, RoutedEventArgs e) { var action = pending; pending = null; Allow.Visibility = Visibility.Collapsed; if (action != null) await Run(action); }
}
