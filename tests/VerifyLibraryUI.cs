using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using GoveeLights;
using Microsoft.Win32;

internal static class VerifyLibraryUI {
    [DllImport("advapi32.dll")] static extern int RegOverridePredefKey(IntPtr pre,IntPtr replacement);
    const BindingFlags All=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    static void Need(bool ok,string m){if(!ok)throw new Exception(m);}
    static T Field<T>(object o,string n){return (T)o.GetType().GetField(n,All).GetValue(o);}
    static object Invoke(object o,string n,params object[] a){return o.GetType().GetMethod(n,All).Invoke(o,a);}
    static void Wait(Func<bool> ready) {
        var timer=Stopwatch.StartNew();
        while(!ready() && timer.ElapsedMilliseconds<6000){Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background,new Action(()=>{}));Thread.Sleep(2);}
        Need(ready(),"UI wait timed out");
    }
    static void Pump(Task t){Wait(()=>t.IsCompleted);t.GetAwaiter().GetResult();}
    static IEnumerable<T> Children<T>(DependencyObject root) where T:DependencyObject {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){
            var c=VisualTreeHelper.GetChild(root,i);if(c is T)yield return (T)c;
            foreach(var child in Children<T>(c))yield return child;
        }
    }
    static void Draw(Window win,string path) {
        var root=(FrameworkElement)win.Content;root.Measure(new Size(832,640));root.Arrange(new Rect(0,0,832,640));root.UpdateLayout();
        if(path==null)return;
        var bmp=new RenderTargetBitmap(832,640,96,96,PixelFormats.Pbgra32);bmp.Render(root);
        var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(bmp));using(var f=File.Create(path))enc.Save(f);
    }
    static void Click(Button b){b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
    static Button Btn(Panel p,string content){return Children<Button>(p).First(x=>Object.Equals(x.Content,content));}
    static void Check() {
        var a=new Device{Id="12340001",Sku="H6056",Address=IPAddress.Parse("127.0.0.2")};
        var b=new Device{Id="12340002",Sku="H619A",Address=IPAddress.Parse("127.0.0.3")};
        var m=new Device{Id="12340003",Sku="H6121",Address=IPAddress.Parse("127.0.0.4")};
        var network=new List<Device>();int scans=0,reads=0;
        var ui=new Controller(()=>{scans++;return network.ToList();},d=>{reads++;return new DeviceData{onOff=1,brightness=80,colorTemInKelvin=4000};});
        var win=ui.Window;var lib=Field<LightLibrary>(ui,"library");Func<string,Panel> panel=n=>(Panel)win.FindName(n);
        Pump((Task)Invoke(ui,"Startup"));Need(panel("LightsPage").Visibility==Visibility.Visible && scans==0,"First run did not show setup");
        Draw(win,"work/onboarding-qa.png");Pump((Task)Invoke(ui,"ScanLights"));Need(scans==1 && lib.Items.Count==0,"Empty discovery");
        network.Add(a);network.Add(b);network.Add(m);Pump((Task)Invoke(ui,"ScanLights"));Draw(win,null);
        for(int i=1;i<=3;i++){Click(Btn(panel("FoundList"),"Add light"));Wait(()=>!Field<bool>(ui,"busy"));Draw(win,null);Need(lib.Items.Count==i,"Add button failed");}
        Need(new LightLibrary().Items.Count==3,"Saved records did not survive reload");
        Need(lib.Items.All(x=>!x.Included && x.Placement=="Whole display"),"New lights should default to independent use");
        var bars=lib.Items.Single(x=>x.Id==a.Id);bars.Included=true;bars.Placement="Split bars";lib.Save(bars);Invoke(ui,"RenderLibrary");Draw(win,null);
        var card=panel("SavedList").Children.OfType<Border>().Select(x=>(StackPanel)x.Child).First(x=>Object.Equals(x.Tag,b.Id));
        var include=Children<CheckBox>(card).Single();include.IsChecked=true;include.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        var targets=(SyncTarget[])Invoke(ui,"BuildTargets",false);Need(targets.Length==2 && targets.All(t=>t.Light.Id!=m.Id),"Group selection mismatch");
        Draw(win,null);card=panel("SavedList").Children.OfType<Border>().Select(x=>(StackPanel)x.Child).First(x=>Object.Equals(x.Tag,b.Id));
        Click(Btn(card,"Edit light"));Draw(win,null);card=panel("SavedList").Children.OfType<Border>().Select(x=>(StackPanel)x.Child).First(x=>Object.Equals(x.Tag,b.Id));
        var fields=Children<TextBox>(card).ToArray();fields[0].Text="Desk strip";fields[1].Text="Studio";Click(Btn(card,"Save"));Need(new LightLibrary().Items.Any(x=>x.Name=="Desk strip" && x.Room=="Studio"),"Name or room not saved");
        b.Address=IPAddress.Parse("127.0.0.9");network.Remove(m);Pump((Task)Invoke(ui,"ScanLights"));Need(lib.Items.Count==3,"Offline device lost");
        Draw(win,"work/library-qa.png");
        Pump((Task)Invoke(ui,"Select",a));Invoke(ui,"Navigate",false);
        var mapping=Field<Mapping>(ui,"mapping");var scene=VerifyScenes.Pattern();
        var fakeSession=new CancellationTokenSource();typeof(Controller).GetField("session",All).SetValue(ui,fakeSession);
        typeof(Controller).GetField("lastPreview",All).SetValue(ui,scene);Invoke(ui,"Controls");
        foreach(int n in new[]{1,2,3,6}) {
            mapping.Zones=n;Invoke(ui,"ShowMapping");
            var g=(Grid)((Border)win.FindName("HalfL")).Child;Need(g.Children.Count==n,"Zone layout");
            var brush=(SolidColorBrush)((Border)g.Children[0]).Background;Need(brush.Color.R==scene.Bands(n)[0].Left.R,"Preview color mismatch");
            Need(win.FindName("GlowL")==null && win.FindName("GlowR")==null,"Glow still present");
            Draw(win,"work/zones-"+n+"-qa.png");
        }
        Invoke(ui,"OpenExtra","Settings");Draw(win,"work/settings-qa.png");
        var startup=(CheckBox)win.FindName("StartWindows");startup.IsChecked=true;startup.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        var auto=(CheckBox)win.FindName("ResumeWindows");auto.IsChecked=true;auto.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Need(SignInOptions.Enabled && SignInOptions.AutoSync,"Sign-in options did not persist");
        using(var rk=Registry.CurrentUser.OpenSubKey(SignInOptions.RunKey))Need((string)rk.GetValue(SignInOptions.Name)==SignInOptions.Command,"Run command readback");
        startup.IsChecked=false;startup.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));Need(!SignInOptions.Enabled && !auto.IsEnabled,"Disabling startup");
        Invoke(ui,"OpenExtra","Presets");Draw(win,"work/presets-empty-qa.png");
        var toggle=(CheckBox)win.FindName("ToTray");toggle.IsChecked=true;toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        var e=new CancelEventArgs();Invoke(ui,"OnClosing",win,e);
        Wait(()=>Field<bool>(ui,"trayHidden"));
        Need(e.Cancel && !fakeSession.IsCancellationRequested && Field<System.Windows.Forms.NotifyIcon>(ui,"tray").Visible,"Close to tray stopped sync");
        Need(new LightLibrary().Items.Count==3 && reads>=3,"persistence/read failure");
        var quit=new CancelEventArgs();typeof(Controller).GetField("quitRequested",All).SetValue(ui,true);Invoke(ui,"OnClosing",win,quit);
        Need(quit.Cancel && fakeSession.IsCancellationRequested,"Tray quit did not request restoration");
        typeof(Controller).GetField("session",All).SetValue(ui,null);fakeSession.Dispose();win.Close();
        Console.WriteLine("First run, empty scan, add three lights, manual-only gating, multi-selection, rename, persistence, offline, both zone views, tray hide and quit passed.");
    }
    [STAThread] public static int Main() {
        string temp=@"Software\GoveeLightsQA-"+Guid.NewGuid().ToString("N");IntPtr hk=new IntPtr(unchecked((int)0x80000001));
        try {
            using(var key=Registry.CurrentUser.CreateSubKey(temp)) {
                Need(RegOverridePredefKey(hk,key.Handle.DangerousGetHandle())==0,"QA registry isolation failed");
                try {SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());Check();VerifyHome.Check();VerifyLiveControls.Check();VerifyBars.Check();VerifyAI.Check();Screenshots.Render();}
                finally {RegOverridePredefKey(hk,IntPtr.Zero);}
            }
            return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally{Registry.CurrentUser.DeleteSubKeyTree(temp,false);}
    }
}
