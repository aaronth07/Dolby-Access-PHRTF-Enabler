using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
namespace AtmosProfile;
public sealed partial class HeadphoneEqPage : UserControl
{
    bool busy, loaded;
    Func<Task>? pending;
    HeadphoneEqProfile? Selected => Profiles.SelectedItem as HeadphoneEqProfile;
    public HeadphoneEqPage(){InitializeComponent();}
    async Task Run(Func<Task> action)
    {
        if(busy)return;busy=true;FeatureWindow.ActiveOperations++;Controls();
        try{await action();}catch(Exception e){Show(e is ProfileOperationException or InvalidDataException ? e.Message : "The EQ operation failed.",true);FeatureDiagnostics.Save("headphone-eq",e);}
        finally{busy=false;FeatureWindow.ActiveOperations--;Controls();}
    }
    void Controls(){Profiles.IsEnabled=ImportPath.IsEnabled=Devices.IsEnabled=Allow.IsEnabled=Browse.IsEnabled=Import.IsEnabled=!busy;Delete.IsEnabled=!busy&&Selected!=null;Apply.IsEnabled=!busy&&Selected!=null&&Devices.SelectedItem!=null;Off.IsEnabled=!busy&&Devices.SelectedItem!=null;}
    void Show(string text,bool error=false){pending=null;Allow.Visibility=Visibility.Collapsed;Message.Message=text;Message.Severity=error?InfoBarSeverity.Error:InfoBarSeverity.Success;Message.IsOpen=true;}
    void Reload(string? id=null){Profiles.ItemsSource=HeadphoneEq.LoadProfiles(out var invalid);if(invalid.Length>0)Show($"{invalid.Length} saved EQ profiles could not be read. Their files were preserved.",true);Profiles.SelectedItem=Profiles.Items.OfType<HeadphoneEqProfile>().FirstOrDefault(p=>p.Id==id)??Profiles.Items.OfType<HeadphoneEqProfile>().FirstOrDefault();Controls();Draw();}
    async void PageLoaded(object sender,RoutedEventArgs e)=>await Run(async()=>{
        if(!loaded){loaded=true;var devices=await ProfileService.Devices();devices.Insert(0,ProfileService.DefaultDevice);Devices.ItemsSource=devices;Devices.SelectedIndex=0;Reload();}
        await Refresh();
    });
    async Task Refresh(){if(Devices.SelectedItem is AudioDevice selected){var d=ProfileService.Resolve(selected);var spatial=await Task.Run(()=>AudioPlatform.Spatial(d));Status.Text=spatial.ActiveSpatialAudioFormat.Equals(DtsProfileService.Provider,StringComparison.OrdinalIgnoreCase)?(await Task.Run(()=>DtsProfileService.Inspect(d))).Message:spatial.ActiveSpatialAudioFormat.Equals(AudioPlatform.Dolby,StringComparison.OrdinalIgnoreCase)?await Task.Run(()=>AtmosEqService.Inspect(d)):"Enable Dolby Atmos for Headphones or DTS Headphone:X for this device.";}}
    void ProfileChanged(object sender,SelectionChangedEventArgs e){if(Allow==null)return;pending=null;Allow.Visibility=Visibility.Collapsed;Draw();Controls();}
    async void DeviceChanged(object sender,SelectionChangedEventArgs e){if(Allow==null)return;pending=null;Allow.Visibility=Visibility.Collapsed;if(!busy)await Run(Refresh);}
    async void BrowseClicked(object sender,RoutedEventArgs e)=>await Run(()=>{var path=FeatureFilePicker.Pick(WinRT.Interop.WindowNative.GetWindowHandle(FeatureWindow.Owner),".txt","Parametric EQ text files");if(path!=null)ImportPath.Text=path;return Task.CompletedTask;});
    async void ImportClicked(object sender,RoutedEventArgs e)=>await Run(()=>{string path=ImportPath.Text.Trim().Trim('"');if(!File.Exists(path))throw new ProfileOperationException("Select an EQ text file.");if(new FileInfo(path).Length>128000)throw new InvalidDataException("The EQ file exceeds 128 KB.");var profile=HeadphoneEq.Import(File.ReadAllText(path),System.IO.Path.GetFileNameWithoutExtension(path));string name=profile.Name;int suffix=2;var existing=HeadphoneEq.Profiles();while(existing.Any(p=>p.Name.Equals(profile.Name,StringComparison.OrdinalIgnoreCase)))profile=profile with{Name=$"{name} ({suffix++})"};HeadphoneEq.Save(profile);Reload(profile.Id);Show("EQ profile saved.");return Task.CompletedTask;});
    async void DeleteClicked(object sender,RoutedEventArgs e)=>await Run(()=>{if(Selected!=null){HeadphoneEq.Delete(Selected);Reload();Show("Saved EQ profile deleted. Applied device EQ is unchanged.");}return Task.CompletedTask;});
    async Task Change(AudioDevice device,string mode,bool force)
    {
        try{var provider=AudioPlatform.Spatial(device).ActiveSpatialAudioFormat;string message;
            if(provider.Equals(DtsProfileService.Provider,StringComparison.OrdinalIgnoreCase))message=await Task.Run(()=>DtsProfileService.ChangeEq(device,mode,force));
            else if(provider.Equals(AudioPlatform.Dolby,StringComparison.OrdinalIgnoreCase)){var p=mode=="Off"?null:HeadphoneEq.Profiles().SingleOrDefault(x=>x.Id==mode[8..])??throw new ProfileOperationException("The selected EQ profile was not found.");message=await Task.Run(()=>AtmosEqService.Change(device,p,force));}
            else throw new ProfileOperationException("Enable Dolby Atmos for Headphones or DTS Headphone:X for this device.");
            Show(message);await Refresh();}
        catch(ProfileWarningException e){Message.Message=e.Message;Message.Severity=InfoBarSeverity.Warning;Message.IsOpen=true;Allow.Visibility=Visibility.Visible;pending=()=>Change(device,mode,true);}
    }
    async void ApplyClicked(object sender,RoutedEventArgs e){if(Devices.SelectedItem is AudioDevice d&&Selected is HeadphoneEqProfile p)await Run(()=>Change(ProfileService.Resolve(d),"Profile:"+p.Id,false));}
    async void OffClicked(object sender,RoutedEventArgs e){if(Devices.SelectedItem is AudioDevice d)await Run(()=>Change(ProfileService.Resolve(d),"Off",false));}
    async void AllowClicked(object sender,RoutedEventArgs e){var action=pending;pending=null;Allow.Visibility=Visibility.Collapsed;if(action!=null)await Run(action);}
    void GraphSizeChanged(object sender,SizeChangedEventArgs e)=>Draw();
    void Draw()
    {
        if(Graph==null||Summary==null)return;Graph.Children.Clear();if(Selected is not HeadphoneEqProfile p){Summary.Text="";return;}
        double width=Graph.ActualWidth;if(width<100)return;const double left=34,top=8,height=138;
        var filters=p.Bands.Select(b=>Biquad.Create(b,48000)).ToArray();double[] frequencies=Enumerable.Range(0,401).Select(i=>20*Math.Pow(1000,i/400d)).ToArray();
        double[] gains=frequencies.Select(f=>p.PreampDb+filters.Sum(b=>20*Math.Log10(Math.Max(b.Response(f,48000).Magnitude,1e-20)))).ToArray();
        double limit=Math.Max(12,Math.Ceiling(gains.Max(Math.Abs)/6)*6);limit=Math.Min(120,limit);
        double X(double f)=>left+(Math.Log10(f/20)/3)*(width-left-8);double Y(double db)=>top+(limit-Math.Clamp(db,-limit,limit))/(2*limit)*height;
        void Label(string text,double x,double y){var item=new TextBlock{Text=text,FontSize=10};Canvas.SetLeft(item,x);Canvas.SetTop(item,y);Graph.Children.Add(item);}
        foreach(double db in new[]{-limit,0,limit}){Graph.Children.Add(new Line{X1=left,X2=width-8,Y1=Y(db),Y2=Y(db),Stroke=new SolidColorBrush(Windows.UI.Color.FromArgb(70,128,128,128))});Label($"{db:g}",0,Y(db)-7);}
        foreach(var tick in new[]{(20d,"20"),(100d,"100"),(1000d,"1k"),(10000d,"10k"),(20000d,"20k")})Label(tick.Item2,X(tick.Item1)-(tick.Item1==20000?20:5),top+height+5);
        var curve=new Polyline{Stroke=new SolidColorBrush(Windows.UI.Color.FromArgb(255,35,145,195)),StrokeThickness=1.8};for(int i=0;i<gains.Length;i++)curve.Points.Add(new Point(X(frequencies[i]),Y(gains[i])));Graph.Children.Add(curve);
        Summary.Text=$"{p.Bands.Length} filters Â· Preamp {p.PreampDb:+0.##;-0.##;0} dB Â· Peak response {gains.Max():+0.##;-0.##;0} dB";
    }
}
