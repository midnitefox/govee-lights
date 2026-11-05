using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GoveeLights {
    internal sealed class BarLook {
        internal readonly Color[] Colors={Color.White,Color.White};
        internal readonly int[] Levels={100,100};
        internal BarLook Copy(){var b=new BarLook();Array.Copy(Colors,b.Colors,2);Array.Copy(Levels,b.Levels,2);return b;}
        internal Pair Output(){return new Pair(Scaled(0),Scaled(1));}
        private Color Scaled(int i){var c=Colors[i];double s=Levels[i]/100.0;return Color.FromArgb((int)Math.Round(c.R*s),(int)Math.Round(c.G*s),(int)Math.Round(c.B*s));}
    }
    // App-owned RGB overrides. Native power, brightness and white temperature remain set-wide.
    internal sealed class BarStream {
        internal readonly SavedLight Saved;
        internal readonly int Physical;
        internal volatile bool ModeRefresh;
        private readonly Func<IGroupNetwork> open;
        private readonly object gate=new object();
        private readonly CancellationTokenSource cancel=new CancellationTokenSource();
        private readonly TaskCompletionSource<bool> ready=new TaskCompletionSource<bool>();
        private readonly bool[] colorEdited=new bool[2],levelEdited=new bool[2];
        private BarLook current;
        private readonly bool resuming;
        internal Task Ready{get{return ready.Task;}}
        internal Task<DeviceData> Completion{get;private set;}
        internal BarStream(SavedLight saved,int physical,Func<IGroupNetwork> factory,BarLook look=null) {
            if(saved.Sku!="H6056" || (physical!=3 && physical!=6))throw new IOException("Individual bar control is supported for H6056 sets.");
            Saved=saved;Physical=physical;open=factory;current=look==null?new BarLook():look.Copy();resuming=look!=null;
            ModeRefresh=new Mapping(new Device{Id=saved.Id,Sku=saved.Sku}).PeriodicModeRefresh;
        }
        internal BarLook Snapshot(){lock(gate)return current.Copy();}
        internal void Update(int index,Color? color,int? level) {
            if(index<0 || index>1 || (level.HasValue && (level<0 || level>100)))throw new ArgumentOutOfRangeException("index");
            lock(gate){if(color.HasValue){current.Colors[index]=color.Value;colorEdited[index]=true;}if(level.HasValue){current.Levels[index]=level.Value;levelEdited[index]=true;}}
        }
        internal void Start(){if(Completion!=null)throw new InvalidOperationException();Completion=Task.Run((Func<DeviceData>)Run);}
        internal void Cancel(){try{cancel.Cancel();}catch(ObjectDisposedException){}}
        private DeviceData Run() {
            IGroupNetwork lan=null;SyncTarget target=null;Exception error=null;DeviceData after=null;
            try {
                lan=open();var d=lan.Discover(Saved.Id).FirstOrDefault(x=>x.Id==Saved.Id && x.Sku==Saved.Sku);
                if(d==null)throw new IOException(Saved.Name+" is offline.");
                var state=lan.Read(d);
                if(state.onOff!=1)throw new IOException("Turn the set on before controlling an individual bar.");
                if(state.color==null)throw new IOException("The set did not report RGB support.");
                lock(gate)if(!resuming)for(int i=0;i<2;i++) {
                    if(!colorEdited[i])current.Colors[i]=state.colorTemInKelvin==0?Color.FromArgb(state.color.r,state.color.g,state.color.b):Color.White;
                    if(!levelEdited[i])current.Levels[i]=100;
                }
                target=new SyncTarget(d,Saved.Name,"Split bars",()=>false,()=>1){Physical=()=>Physical,Before=state,After=state,PeriodicModeRefresh=()=>ModeRefresh};
                if(cancel.IsCancellationRequested){ready.TrySetCanceled();return state;}
                target.Armed=true;target.RefreshStream(lan);
                var elapsed=Stopwatch.StartNew();double refresh=25000;
                while(!cancel.IsCancellationRequested) {
                    if(!lan.PollGroupHealth(new[]{d},elapsed.ElapsedMilliseconds))break;
                    if(elapsed.Elapsed.TotalMilliseconds>=refresh){target.MaintainMode(lan);refresh=elapsed.Elapsed.TotalMilliseconds+25000;}
                    target.Send(lan,SceneFrame.Solid(Snapshot().Output()),elapsed.Elapsed.TotalMilliseconds);
                    ready.TrySetResult(true);cancel.Token.WaitHandle.WaitOne(16);
                }
            }catch(Exception ex){error=ex;ready.TrySetException(ex);}
            finally {
                try{if(target!=null && target.Armed){target.Restore(lan);after=target.After;}}
                catch(Exception ex){error=new IOException("Could not restore "+Saved.Name+". "+ex.Message,error??ex);}
                var disposable=lan as IDisposable;if(disposable!=null)disposable.Dispose();
                cancel.Dispose();
                // Ensure startup cannot hang when the light was switched off between preflight and first frame.
                if(!ready.Task.IsCompleted)ready.TrySetException(error??new IOException("Individual bar control stopped before its first frame."));
            }
            if(error!=null)throw error;
            return after;
        }
    }
}
