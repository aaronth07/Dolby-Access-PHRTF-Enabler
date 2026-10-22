#if UI_QA
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.InteropServices;
using System.Text;
namespace AtmosProfile;
internal static class UiQa
{
 static string output="";
 static readonly List<string> passed=[];
 delegate bool WindowCallback(IntPtr window,IntPtr data);
 [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
 [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread,WindowCallback callback,IntPtr data);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder name,int size);
 [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
 static async Task CancelPicker(Button button,string label)
 {
  uint thread=GetCurrentThreadId();bool canceled=false;
  var closer=Task.Run(async()=>{
   for(int i=0;i<60&&!canceled;i++){
    await Task.Delay(100);
    EnumThreadWindows(thread,(window,_)=>{
     var name=new StringBuilder(80);GetClassName(window,name,name.Capacity);
     if(name.ToString()=="#32770"){canceled=PostMessage(window,0x10,IntPtr.Zero,IntPtr.Zero);return false;}return true;
    },IntPtr.Zero);
   }
  });
  Invoke(button);await closer;await Ready();Check(canceled,label+" file picker opens and cancels cleanly");
 }
 internal static void Initialize()
 {
  var args=Environment.GetCommandLineArgs();
  if(args.Length!=3||args[1]!="--ui-qa")throw new ArgumentException("QA build requires --ui-qa <private output directory>");
  output=Path.GetFullPath(args[2]);Directory.CreateDirectory(output);
  ProfileService.TestDataRoot=SpatialFiles.TestRoot=Path.Combine(output,"app-data");
  HeadphoneEq.TestFolder=Path.Combine(output,"app-data","headphone-eq");
 }
 static void Check(bool value,string text){if(!value)throw new InvalidOperationException(text);passed.Add(text);}
 static T Field<T>(object owner,string name) where T:class => (T)(owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public)?.GetValue(owner)??throw new InvalidOperationException("Missing control: "+name));
 static async Task Ready()
 {
  for(int i=0;i<200;i++){await Task.Delay(100);if(FeatureWindow.ActiveOperations==0){await Task.Delay(150);if(FeatureWindow.ActiveOperations==0)return;}}
  throw new TimeoutException("UI operation did not complete");
 }
 static void Invoke(Button button){Check(button.IsEnabled,"Action enabled: "+button.Content);((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();}
 static IEnumerable<DependencyObject> Children(DependencyObject root)
 {
  yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var item in Children(VisualTreeHelper.GetChild(root,i)))yield return item;
 }
 static async Task Image(FrameworkElement root,string name)
 {
  var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(root);var pixels=await bitmap.GetPixelsAsync();
  Check(bitmap.PixelWidth>100&&bitmap.PixelHeight>100,"Rendered "+name);
  var file=await StorageFile.GetFileFromPathAsync(Path.GetFullPath(Path.Combine(output,name+".png")));
  using var stream=await file.OpenAsync(FileAccessMode.ReadWrite);var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
  encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels.ToArray());await encoder.FlushAsync();
 }
 internal static async Task Run(Window window)
 {
  int code=0;
  try {
   await Task.Delay(1200);await Ready();var root=(FrameworkElement)window.Content;
   var tabs=Children(root).OfType<TabView>().First();Check(tabs.TabItems.Count==4,"Beta.1 four-tab workflow retained");
   foreach(var theme in new[]{ElementTheme.Light,ElementTheme.Dark}){
    root.RequestedTheme=theme;
    foreach(var size in new[]{new Windows.Graphics.SizeInt32(560,720),new Windows.Graphics.SizeInt32(480,420)}){
    window.AppWindow.Resize(size);await Task.Delay(150);
    for(int i=0;i<tabs.TabItems.Count;i++){
     tabs.SelectedIndex=i;await Ready();root.UpdateLayout();
     var item=(TabViewItem)tabs.TabItems[i];var content=(FrameworkElement)item.Content;
     Check(content.ActualWidth>300&&content.ActualHeight>150,$"{theme} {size.Width}×{size.Height}: {item.Header} has usable content bounds");
     var invalid=Children(content).OfType<FrameworkElement>().Where(e=>e.Visibility==Visibility.Visible&&(!double.IsFinite(e.ActualWidth)||!double.IsFinite(e.ActualHeight))).ToArray();
     Check(invalid.Length==0,$"{theme}: {item.Header} layout dimensions finite");
     string name=$"{theme}-{size.Width}-{i}";File.WriteAllBytes(Path.Combine(output,name+".png"),[]);await Image(root,name);
    }
    }
   }
   window.AppWindow.Resize(new(560,720));await Task.Delay(150);
   tabs.SelectedIndex=1;await Ready();var room=(RoomPage)((TabViewItem)tabs.TabItems[1]).Content;
   var settings=Field<ComboBox>(room,"Setting");
   foreach(string mode in new[]{"On","Off","HalfFeedback","DoubleRoom","QuadRoom","Custom"}){
    settings.SelectedItem=settings.Items.OfType<ComboBoxItem>().Single(x=>(string)x.Tag==mode);await Task.Delay(100);
    Check(Field<StackPanel>(room,"CustomControls").Visibility==(mode=="Custom"?Visibility.Visible:Visibility.Collapsed),mode+" shows relevant custom controls");
    Check(Field<CheckBox>(room,"GainCorrection").Visibility==(mode is "DoubleRoom" or "QuadRoom"?Visibility.Visible:Visibility.Collapsed),mode+" shows relevant gain correction");
   }
   tabs.SelectedIndex=0;await Ready();await CancelPicker(Field<Button>(window,"Browse"),"PHRTF");
   tabs.SelectedIndex=3;await Ready();var eq=(HeadphoneEqPage)((TabViewItem)tabs.TabItems[3]).Content;
   await CancelPicker(Field<Button>(eq,"Browse"),"EQ");
   Check(!Field<Button>(eq,"Apply").IsEnabled&&!Field<Button>(eq,"Delete").IsEnabled,"Empty EQ collection disables apply/delete");
   var import=Children(eq).OfType<Button>().Single(b=>b.Content as string=="Import profile");
   Field<TextBox>(eq,"ImportPath").Text=Path.Combine(output,"missing.txt");Invoke(import);await Ready();
   Check(Field<InfoBar>(eq,"Message").Severity==InfoBarSeverity.Error,"Missing EQ file gives an error and remains usable");
   string eqPath=Path.Combine(output,"example.txt");File.WriteAllText(eqPath,"Preamp: -3 dB\nFilter 1: ON PK Fc 1000 Hz Gain 2 dB Q 1\n");Field<TextBox>(eq,"ImportPath").Text=eqPath;Invoke(import);await Ready();
   Check(Field<ComboBox>(eq,"Profiles").Items.Count==1,"EQ import saves and selects a profile");
   Check(Field<Canvas>(eq,"Graph").Children.OfType<Microsoft.UI.Xaml.Shapes.Polyline>().Any(),"EQ response graph draws an actual curve");
   Invoke(import);await Ready();Check(Field<ComboBox>(eq,"Profiles").Items.Count==2,"Repeated EQ import creates a distinct saved profile");
   Invoke(Field<Button>(eq,"Delete"));await Ready();Invoke(Field<Button>(eq,"Delete"));await Ready();
   Check(Field<ComboBox>(eq,"Profiles").Items.Count==0&&!Field<Button>(eq,"Apply").IsEnabled,"Deleting last EQ profile restores empty state");
   tabs.SelectedIndex=2;await Ready();var dts=(DtsPage)((TabViewItem)tabs.TabItems[2]).Content;
   await CancelPicker(Field<Button>(dts,"Browse"),"SOFA");
   Field<TextBox>(dts,"SofaPath").Text=Path.Combine(output,"missing.sofa");await Task.Delay(150);
   Check(!Field<Button>(dts,"Apply").IsEnabled,"Missing SOFA cannot be applied");
   Check(Field<ComboBox>(dts,"Interpolation").Items.Count==2,"Both SOFA interpolation methods remain available");
   Check(FeatureWindow.ActiveOperations==0,"UI operations finish without a stuck busy state");
  }catch(Exception error){code=1;File.WriteAllText(Path.Combine(output,"failure.txt"),error.ToString());}
  File.WriteAllText(Path.Combine(output,"ui-results.json"),JsonSerializer.Serialize(new{passed=code==0,checks=passed,scope="Real WinUI controls and rendering; isolated app data, no audio-setting actions"},new JsonSerializerOptions{WriteIndented=true}));
  window.Close();Environment.Exit(code);
 }
}
#endif
