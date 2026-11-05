using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GoveeLights;

internal static class Screenshots {
    const BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    static object Call(object c,string name,params object[] a){return typeof(Controller).GetMethod(name,Private).Invoke(c,a);}
    static void Await(Task t){
        while(!t.IsCompleted){Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background,new Action(()=>{}));Thread.Sleep(1);}
        t.GetAwaiter().GetResult();
    }
    static void Draw(Controller c,string name,int height=800) {
        var root=(FrameworkElement)c.Window.Content;
        root.Measure(new Size(1040,height));root.Arrange(new Rect(0,0,1040,height));root.UpdateLayout();
        var image=new RenderTargetBitmap(1040,height,96,96,PixelFormats.Pbgra32);image.Render(root);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));
        using(var f=File.Create("work/"+name+".png"))png.Save(f);
    }
    internal static void Render() {
        var store=new LightLibrary();var lights=new List<Device>();int number=10;
        foreach(var item in store.Items.ToArray()) {
            if(item.Sku=="H6056"){item.Name="Desk bars";item.Room="Office";item.Included=true;item.Placement="Split bars";}
            else if(item.Sku=="H619A"){item.Name="Shelf strip";item.Room="Living room";item.Included=false;}
            else{item.Name="Accent strip";item.Room="Office";item.Included=false;}
            store.Save(item);lights.Add(new Device{Id=item.Id,Sku=item.Sku,Address=IPAddress.Parse("192.0.2."+number++)});
        }
        Func<Device,DeviceData> read=d=>new DeviceData{onOff=1,brightness=65,colorTemInKelvin=4000,color=new Rgb{r=0,g=0,b=0}};
        var presets=new PresetStore();
        foreach(var setting in new[]{new{Name="Evening",Level=30,Kelvin=2700},new{Name="Focus",Level=85,Kelvin=5000}}) {
            presets.Save(new LightingPreset{Name=setting.Name,Lights=store.Items.Where(x=>x.Room=="Office")
                .Select(x=>new LightLook{Id=x.Id,Sku=x.Sku,Name=x.Name,Power=1,Brightness=setting.Level,Temperature=setting.Kelvin}).ToList()});
        }
        SignInOptions.SetEnabled(true);SignInOptions.AutoSync=true;
        AiOptions.Enabled=true;AiOptions.Targets=new HashSet<string>(store.Items.Where(x=>x.Room=="Office").Select(x=>x.Id));
        var ui=new Controller(()=>lights.ToList(),read);
        Await((Task)Call(ui,"Startup"));Call(ui,"OpenLights");Draw(ui,"lights");
        Call(ui,"OpenExtra","Presets");Draw(ui,"presets");
        Call(ui,"OpenExtra","Settings");Draw(ui,"ai-settings");
        ((ScrollViewer)ui.Window.FindName("ContentScroll")).ScrollToEnd();Draw(ui,"settings");
        var bars=lights.Single(x=>x.Sku=="H6056");Await((Task)Call(ui,"Select",bars));Call(ui,"Navigate",false);
        Call(ui,"Navigate",true);Draw(ui,"light-controls",980);
        typeof(Controller).GetField("manualBar",Private).SetValue(ui,1);Call(ui,"RenderBarControls");Call(ui,"ShowBarValues");Call(ui,"Controls");Draw(ui,"individual-bars",980);
        typeof(Controller).GetField("manualBar",Private).SetValue(ui,0);Call(ui,"Navigate",false);
        ((TextBlock)ui.Window.FindName("Monitor")).Text="Display 1 · 2560 × 1440";
        using(var session=new CancellationTokenSource()) {
            typeof(Controller).GetField("session",Private).SetValue(ui,session);
            Call(ui,"BuildTargets",false);Call(ui,"Controls");
            Call(ui,"SetConnection","Syncing","#90D5FF");
            var scene=new ComfortColors().Process(VerifyScenes.Pattern(),SyncOptions.Comfort,SyncOptions.Palette);typeof(Controller).GetField("lastPreview",Private).SetValue(ui,scene);
            Call(ui,"UpdateBands",scene);((TextBlock)ui.Window.FindName("Notice")).Text="Following the main display. You can minimize this window.";
            Draw(ui,"screen-sync",900);
            typeof(Controller).GetField("session",Private).SetValue(ui,null);
        }
        ui.Quit();
        Console.WriteLine("Rendered README screenshots from the real UI with isolated example data.");
    }
}
