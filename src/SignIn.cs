using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace GoveeLights {
    internal static class SignInOptions {
        internal const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run", Name="Govee Lights (local controller)";
        private const string Options=@"Software\Govee Lights";
        internal static string Command {get{return "\""+Assembly.GetExecutingAssembly().Location+"\" --startup";}}
        internal static bool Enabled {
            get {using(var k=Registry.CurrentUser.OpenSubKey(RunKey))return k!=null && String.Equals(k.GetValue(Name) as string,Command,StringComparison.OrdinalIgnoreCase);}
        }
        internal static bool AutoSync {
            get {using(var k=Registry.CurrentUser.OpenSubKey(Options))return k!=null && Object.Equals(k.GetValue("ResumeSelectedAtSignIn"),1);}
            set {using(var k=Registry.CurrentUser.CreateSubKey(Options))k.SetValue("ResumeSelectedAtSignIn",value?1:0);}
        }
        internal static void SetEnabled(bool enable) {
            using(var k=Registry.CurrentUser.CreateSubKey(RunKey)) {
                string old=k.GetValue(Name) as string;
                if(old!=null && !String.Equals(old,Command,StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Another installation owns this startup entry. Disable startup in that installation first.");
                if(enable) {
                    if(Command.Length>260)throw new IOException("The app’s path is too long for a Windows sign-in entry.");
                    k.SetValue(Name,Command,RegistryValueKind.String);
                } else k.DeleteValue(Name,false);
            }
        }
    }
    internal sealed partial class Controller {
        private Task initialization;
        private CancellationTokenSource pendingResume;
        private Task InitializeOnce() {return initialization??(initialization=Startup());}
        private void SetupSignIn() {
            Click("NavSettings",()=>OpenExtra("Settings"));
            Click("SetupLights",async()=>{OpenLights();await ScanLights();});
            Get<CheckBox>("StartWindows").Click+=(s,e)=>{
                try { SignInOptions.SetEnabled(Get<CheckBox>("StartWindows").IsChecked==true);
                    if(!SignInOptions.Enabled)CancelResume();
                } catch(Exception ex){Text("Notice",ex.Message);}
                ShowSignInSettings();
            };
            Get<CheckBox>("ResumeWindows").Click+=(s,e)=>{
                try {SignInOptions.AutoSync=Get<CheckBox>("ResumeWindows").IsChecked==true;
                    if(!SignInOptions.AutoSync)CancelResume();
                }catch(Exception ex){Text("Notice",ex.Message);}
                ShowSignInSettings();
            };
            ShowSignInSettings();
        }
        private void ShowSignInSettings() {
            bool enabled=SignInOptions.Enabled;
            Get<CheckBox>("StartWindows").IsChecked=enabled;
            Get<CheckBox>("ResumeWindows").IsEnabled=enabled;
            Get<CheckBox>("ResumeWindows").IsChecked=enabled && SignInOptions.AutoSync;
        }
        internal async Task StartAtSignIn() {
            trayHidden=true;tray.Visible=true;Window.ShowInTaskbar=false;
            bool resume=SignInOptions.AutoSync && SignInOptions.Enabled;
            var cts=new CancellationTokenSource();
            if(resume) pendingResume=cts;
            try {
                await InitializeOnce();
                if(resume)await ResumeWhenReady(cts);
            }catch(OperationCanceledException){}catch(Exception ex){Text("Notice",ex.Message);}
            finally {if(pendingResume==cts)pendingResume=null;cts.Dispose();if(!quitRequested)RefreshTray();}
        }
        private async Task ResumeWhenReady(CancellationTokenSource cts) {
            int delay=5;
            while(!cts.IsCancellationRequested && !quitRequested && !closing && SignInOptions.Enabled && SignInOptions.AutoSync) {
                var members=library.Items.Where(x=>x.Included && LightModel.Find(x.Sku).Streaming).ToArray();
                if(members.Length==0)return;
                if(!busy && session==null && members.All(m=>lights.Any(d=>d.Id==m.Id && d.Sku==m.Sku))) {
                    if(await RunSession(false))return;
                }
                if(cts.IsCancellationRequested)return;
                RefreshTray();Text("Notice","Waiting for selected lights. Check power, Wi-Fi and LAN Control in each light’s Govee Home settings. Cancel from the tray if needed.");
                await Task.Delay(TimeSpan.FromSeconds(delay),cts.Token);
                delay=Math.Min(60,delay*2);
                if(!busy && session==null)await ScanLights();
            }
        }
        private void CancelResume() {var pending=pendingResume;if(pending!=null){pendingResume=null;pending.Cancel();RefreshTray();}}
    }
}
