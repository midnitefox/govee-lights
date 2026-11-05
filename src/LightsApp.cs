using System;
using System.Linq;
using System.Threading;
namespace GoveeLights {
    internal static class Program {
        [STAThread] private static int Main(string[] args) {
            string instance=@"Local\GoveeLights-"+System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
            if(args.Length==1 && args[0]=="--quit") {
                EventWaitHandle stop;if(EventWaitHandle.TryOpenExisting(instance+"-Quit",out stop))
                    using(stop)stop.Set();return 0;
            }
            bool resume=args.Length==1 && args[0]=="--resume-sync";
            bool signIn=args.Length==1 && args[0]=="--startup";
            if(signIn && !SignInOptions.Enabled)return 0;
            if (args.Length != 0 && !resume && !signIn) {
                try {
                    bool query = args.Length == 1 && args[0] == "--status";
                    bool sides = args.Length == 1 && args[0] == "--test-sides";
                    int level = 0;
                    if (!query && !sides && !(args.Length == 2 && args[0] == "--brightness" && Int32.TryParse(args[1], out level) && level >= 1 && level <= 100))
                        throw new ArgumentException("Use --status, --test-sides, or --brightness 1..100.");
                    System.Collections.Generic.List<Device> lights;
                    using (var lan = new Lan()) lights = lan.Discover();
                    if(!query) {
                        var saved=new LightLibrary().Items.Where(x=>!sides || x.Sku=="H6056").ToList();
                        if(saved.Count!=1)throw new InvalidOperationException("Choose the target in the app when there is not exactly one matching saved light.");
                        lights=lights.Where(d=>d.Id==saved[0].Id && d.Sku==saved[0].Sku).ToList();
                    }
                    if (lights.Count == 0) throw new InvalidOperationException("No matching Govee light is online.");
                    foreach (var light in lights) {
                        DeviceData state;
                        if (sides) {
                            var mapping = new Mapping(light);
                            state = ScreenSync.Run(light, true, () => mapping.Swap, () => mapping.Zones,
                                System.Threading.CancellationToken.None, null);
                        } else using (var lan = new Lan()) {
                            state = query ? lan.Read(light) : lan.Change(light, Setting.Brightness(level));
                        }
                        Console.WriteLine("{0} {1} power={2} brightness={3} kelvin={4}", light.Sku,light.Address, state.onOff, state.brightness, state.colorTemInKelvin);
                    }
                    return 0;
                } catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
            }
            bool first;
            using(var wake=new EventWaitHandle(false,EventResetMode.AutoReset,instance+"-Open"))
            using(var exit=new EventWaitHandle(false,EventResetMode.AutoReset,instance+"-Quit"))
            using(var mutex=new Mutex(true,instance,out first)) {
                if(!first){if(!signIn)wake.Set();return 0;}
                try {
                    var app=new System.Windows.Application();
                    var controller=new Controller();controller.ResumeOnStartup=resume;
                    var aiBridge=new AiPipeServer(controller.DispatchAi);
                    var hook=ThreadPool.RegisterWaitForSingleObject(wake,(o,timeout)=>{
                        if(!app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(new Action(controller.OpenWindow));
                    },null,Timeout.Infinite,false);
                    var quitHook=ThreadPool.RegisterWaitForSingleObject(exit,(o,timeout)=>{
                        if(!app.Dispatcher.HasShutdownStarted)app.Dispatcher.BeginInvoke(new Action(controller.Quit));
                    },null,Timeout.Infinite,false);
                    try {
                        if(signIn) {
                            app.MainWindow=controller.Window;app.ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown;
                            controller.Window.Closed+=(s,e)=>app.Dispatcher.BeginInvoke(new Action(app.Shutdown));
                            app.Startup+=async (s,e)=>await controller.StartAtSignIn();
                            app.Run();
                        }else app.Run(controller.Window);
                    } finally {aiBridge.Dispose();hook.Unregister(null);quitHook.Unregister(null);}
                } finally {mutex.ReleaseMutex();}
            }
            return 0;
        }
    }
}
