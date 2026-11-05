using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Forms=System.Windows.Forms;

namespace GoveeLights {
    internal sealed partial class Controller {
        private bool trayEnabled,quitRequested,trayHidden;
        private Forms.NotifyIcon tray;
        private Forms.ToolStripMenuItem traySync;
        private void SetupTray() {
            using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Govee Lights"))
                trayEnabled=key!=null && Object.Equals(key.GetValue("CloseToTray"),1);
            var toggle=Get<CheckBox>("ToTray");toggle.IsChecked=trayEnabled;
            tray=new Forms.NotifyIcon {Text="Govee Lights",Icon=System.Drawing.Icon.ExtractAssociatedIcon(
                System.Reflection.Assembly.GetExecutingAssembly().Location)};
            var menu=new Forms.ContextMenuStrip();
            menu.Items.Add("Open Govee Lights",null,(s,e)=>Window.Dispatcher.BeginInvoke(new Action(OpenWindow)));
            traySync=new Forms.ToolStripMenuItem("Start screen sync");
            traySync.Click+=(s,e)=>Window.Dispatcher.BeginInvoke(new Action(async ()=>{
                if(pendingResume!=null && session==null){CancelResume();Text("Notice","Automatic resume canceled.");return;}
                await ToggleSession(testing && session!=null);
            }));
            menu.Items.Add(traySync);menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Stop light notification",null,(s,e)=>Window.Dispatcher.BeginInvoke(new Action(()=>{if(signalCancel!=null)signalCancel.Cancel();})));
            menu.Items.Add("Release individual bars",null,(s,e)=>Window.Dispatcher.BeginInvoke(new Action(async()=>await Busy("Restoring custom sets…",()=>ReleaseBars(library.Items)))));
            menu.Items.Add("Lighting presets…",null,(s,e)=>Window.Dispatcher.BeginInvoke(new Action(()=>{OpenWindow();OpenExtra("Presets");})));
            menu.Items.Add("Quit Govee Lights",null,(s,e)=>Window.Dispatcher.BeginInvoke(new Action(Quit)));
            menu.Opening+=(s,e)=>RefreshTray();
            tray.ContextMenuStrip=menu;tray.DoubleClick+=(s,e)=>Window.Dispatcher.BeginInvoke(new Action(OpenWindow));
            tray.Visible=trayEnabled;
            toggle.Click+=(s,e)=> {
                trayEnabled=toggle.IsChecked==true;
                try {using(var k=Registry.CurrentUser.CreateSubKey(@"Software\Govee Lights")) k.SetValue("CloseToTray",trayEnabled?1:0);}
                catch(Exception ex) {Text("Notice",ex.Message);}
                tray.Visible=trayEnabled || trayHidden;
            };
            Window.Closed+=(s,e)=> {quitRequested=true;CancelResume();tray.Visible=false;var icon=tray.Icon;tray.Dispose();menu.Dispose();if(icon!=null)icon.Dispose();};
            if(Application.Current!=null) Application.Current.SessionEnding+=(s,e)=>{quitRequested=true;CancelResume();CancelBars();if(session!=null)session.Cancel();};
        }
        private void RefreshTray() {
            traySync.Text=session!=null?(testing?"Stop side test":"Stop screen sync"):pendingResume!=null?"Cancel automatic resume":"Start screen sync";
            traySync.Enabled=!aiOperation && (session!=null || pendingResume!=null || (!busy && !liveUpdating && library.Items.Exists(x=>x.Included)));
            tray.Text=session!=null?"Govee Lights · Sync running":barStreams.Count>0?"Govee Lights · Individual bars active":pendingResume!=null?"Govee Lights · Waiting for selected lights":"Govee Lights";
        }
        internal void OpenWindow() {
            trayHidden=false;
            tray.Visible=trayEnabled;
            Window.ShowInTaskbar=true; Window.Show();
            if(Window.WindowState==WindowState.Minimized)Window.WindowState=WindowState.Normal;
            Window.Activate();
        }
        private void HideToTray() { if(quitRequested || !trayEnabled)return;trayHidden=true;RefreshTray();tray.Visible=true;Window.Hide();Window.ShowInTaskbar=false; }
        internal void Quit() {quitRequested=true;CancelResume();Window.Close();}
    }
}
