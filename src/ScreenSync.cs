using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace GoveeLights {
    internal struct Pair {
        public Color Left, Right;
        public Pair(Color left, Color right) { Left = left; Right = right; }
        public static readonly Pair Test = new Pair(Color.FromArgb(255,0,0), Color.FromArgb(0,0,255));
    }

    internal sealed class Sampler : IDisposable {
        internal const int Width = 64, Height = 36;
        private readonly Bitmap small = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
        private readonly SceneColors analyzer = new SceneColors();
        private readonly byte[] pixels = new byte[Width * Height * 4];
        private IntPtr gpu;
        private bool havePrevious;
        private Pair previous;
        private SceneFrame latest;
        private readonly CaptureContinuity continuity=new CaptureContinuity();
        internal SceneFrame SampleScene() {Primary();return latest;}
        private long retryGpuAt;
        private readonly Stopwatch age = Stopwatch.StartNew();
        internal string Backend { get; private set; }
        internal int FreshGpuFrames { get; private set; }
        [DllImport("GoveeCapture.dll", ExactSpelling=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        private static extern int CaptureOpen(out IntPtr handle);
        [DllImport("GoveeCapture.dll", ExactSpelling=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        private static extern int CaptureRead(IntPtr handle, [Out] byte[] bytes, uint length);
        [DllImport("GoveeCapture.dll", ExactSpelling=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        private static extern void CaptureClose(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
        [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dest, int x, int y, int w, int h,
            IntPtr src, int sx, int sy, int sw, int sh, uint rop);
        public Pair Primary() {
            long now = age.ElapsedMilliseconds;
            if (gpu == IntPtr.Zero && !continuity.Fallback && now >= retryGpuAt) {
                retryGpuAt = now + 100;
                try { CaptureOpen(out gpu); }
                catch (DllNotFoundException) { retryGpuAt = Int64.MaxValue; }
                catch (BadImageFormatException) { retryGpuAt = Int64.MaxValue; }
                catch (EntryPointNotFoundException) { retryGpuAt = Int64.MaxValue; }
                if(gpu==IntPtr.Zero)continuity.Failed(now);
            }
            if (gpu != IntPtr.Zero) {
                int result = CaptureRead(gpu, pixels, (uint)pixels.Length);
                if (result == 0) {
                    FreshGpuFrames++;
                    continuity.Recovered();
                    latest = analyzer.Analyze(pixels); previous=latest.Whole;
                    havePrevious = true; Backend = "GPU"; return previous;
                }
                if (result == 1 && havePrevious) {
                    if(continuity.ReuseOnTimeout(now)) {
                        if(analyzer.HasPendingCrop){latest=analyzer.Analyze(pixels);previous=latest.Whole;}
                        Backend = "GPU"; return previous;
                    }
                    CloseGpu();
                }
                if (result < 0) { CloseGpu(); retryGpuAt = now + 100;continuity.Failed(now); }
            }
            if(continuity.Hold(now,havePrevious)){Backend="GPU · Recovering";return previous;}
            Backend = "GDI";
            Screen screen = Screen.PrimaryScreen;
            if (screen == null) throw new IOException("Windows could not find the main display.");
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) throw new IOException("Windows could not read the main display.");
            try { previous = Sample(dc, screen.Bounds, true); havePrevious = true; return previous; }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }
        internal Pair Sample(IntPtr source, Rectangle bounds, bool desktop) {
            using (var g = Graphics.FromImage(small)) {
                IntPtr dest = g.GetHdc();
                try {
                    SetStretchBltMode(dest, 4); // HALFTONE downsamples the whole scene.
                    SetBrushOrgEx(dest, 0, 0, IntPtr.Zero);
                    if (!StretchBlt(dest,0,0,Width,Height,source,bounds.X,bounds.Y,bounds.Width,bounds.Height,
                        desktop ? 0x40CC0020u : 0x00CC0020u))
                        throw new IOException("Main display capture stopped. Unlock the desktop and start sync again.");
                } finally { g.ReleaseHdc(dest); }
            }
            var data = small.LockBits(new Rectangle(0,0,Width,Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try { Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
            finally { small.UnlockBits(data); }
            latest=analyzer.Analyze(pixels); return latest.Whole;
        }
        private void CloseGpu() { if (gpu != IntPtr.Zero) { CaptureClose(gpu); gpu = IntPtr.Zero; } }
        public void Dispose() { CloseGpu(); small.Dispose(); }
    }

    internal sealed class FramePacer : IDisposable {
        private sealed class TimerHandle : WaitHandle {
            public TimerHandle(IntPtr handle) { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, true); }
        }
        private readonly TimerHandle timer;
        private readonly WaitHandle[] waits;
        private readonly CancellationToken cancel;
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, IntPtr name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern bool SetWaitableTimer(IntPtr handle, ref long due, int period, IntPtr action, IntPtr args, bool resume);
        public FramePacer(CancellationToken cancel) {
            this.cancel = cancel;
            IntPtr handle = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 2, 0x00100002);
            if (handle == IntPtr.Zero) return;
            timer = new TimerHandle(handle);
            waits = new WaitHandle[] {cancel.WaitHandle, timer};
        }
        public void WaitUntil(double deadlineMs, Stopwatch clock) {
            double left = deadlineMs-clock.Elapsed.TotalMilliseconds;
            if (left <= 0 || cancel.IsCancellationRequested) return;
            long due = -Math.Max(1L,(long)(left*10000));
            if (timer != null && SetWaitableTimer(timer.SafeWaitHandle.DangerousGetHandle(),ref due,0,IntPtr.Zero,IntPtr.Zero,false))
                WaitHandle.WaitAny(waits);
            else cancel.WaitHandle.WaitOne((int)Math.Ceiling(left));
        }
        public void Dispose() { if (timer != null) timer.Dispose(); }
    }

    internal static class ScreenSync {
        internal const double SampleIntervalMs = 1000.0 / 60;
        internal static string Frame(Pair input, bool swap, int perBar) {
            if (perBar != 3 && perBar != 6) throw new ArgumentOutOfRangeException("perBar");
            var colors=new Color[2*perBar];
            for(int i=0;i<colors.Length;i++) colors[i]=((i<perBar)!=swap)?input.Left:input.Right;
            return EncodeZones(colors,colors.Length,false);
        }
        internal static string EncodeZones(Color[] colors,int count,bool stretch) {
            byte[] frame=new byte[7+count*3];
            frame[0]=0xBB;frame[2]=(byte)(2+count*3);frame[3]=0xB0;frame[4]=(byte)(stretch?1:0);frame[5]=(byte)count;
            for(int i=0;i<count;i++) {frame[6+i*3]=colors[i].R;frame[7+i*3]=colors[i].G;frame[8+i*3]=colors[i].B;}
            for (int i=0; i<frame.Length-1; i++) frame[frame.Length-1] ^= frame[i];
            return Convert.ToBase64String(frame);
        }
        internal const string Enable = "uwABsQEK", Disable = "uwABsQAL";
        internal static DeviceData Run(Device selected, bool test, Func<bool> swap, Func<int> zones,
            CancellationToken cancel, Action<Pair> preview, Action<double> packetTiming = null) {
            var target = new SyncTarget(selected,selected.ToString(),selected.Sku=="H6056" ? "Split bars":"Whole display",swap,zones);
            return RunGroup(new[]{target},test,cancel,preview==null ? (Action<SceneFrame>)null : f=>preview(f.Whole),packetTiming)[selected.Id];
        }
        internal static System.Collections.Generic.Dictionary<string,DeviceData> RunGroup(SyncTarget[] targets,bool test,CancellationToken cancel,
            Action<SceneFrame> preview,Action<double> packetTiming=null) {
            using(var lan=new Lan()) using(var sampler=new Sampler()) {
                var colors=new ComfortColors();
                return GroupPlayback.Execute(lan,targets,test,cancel,()=>colors.Process(sampler.SampleScene(),SyncOptions.Comfort,SyncOptions.Palette),preview,packetTiming);
            }
        }
    }
}
