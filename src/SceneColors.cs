using System;
using System.Drawing;

namespace GoveeLights {
    internal sealed class SceneFrame {
        public Pair Whole;
        public readonly Pair[] One=new Pair[1], Two=new Pair[2], Three=new Pair[3], Six=new Pair[6];
        public Pair[] Bands(int n) {
            switch(n) {case 1:return One;case 2:return Two;case 3:return Three;case 6:return Six;
                default:throw new ArgumentOutOfRangeException("n");}
        }
        public static SceneFrame Solid(Pair p) {
            var s=new SceneFrame {Whole=p};
            s.One[0]=p;for(int i=0;i<2;i++)s.Two[i]=p;
            for(int i=0;i<3;i++)s.Three[i]=p;
            for(int i=0;i<6;i++)s.Six[i]=p;
            return s;
        }
    }
    // One reusable estimator per capture session. Fixed-size work for a 64 x 36 GPU reduction.
    internal sealed class SceneColors {
        private static readonly double[] Linear = MakeLinear();
        private readonly double[,] hue = new double[12, 4];
        private readonly Func<double> clock;
        private int trim=-1, pendingTrim=-1;
        private double pendingSince;
        internal bool HasPendingCrop {get{return pendingTrim>=0;}}
        internal SceneColors(Func<double> time=null) {
            var elapsed=System.Diagnostics.Stopwatch.StartNew();clock=time??(()=>elapsed.Elapsed.TotalMilliseconds);
        }
        private static double[] MakeLinear() {
            var table = new double[256];
            for (int i=0;i<256;i++) { double v=i/255.0; table[i]=v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4); }
            return table;
        }
        private static int Encode(double v) {
            v=Math.Min(1,Math.Max(0,v)); return (int)Math.Round(255*(v<=.0031308?12.92*v:1.055*Math.Pow(v,1/2.4)-.055));
        }
        public static Color Whole(Pair p) {
            return Color.FromArgb(Encode((Linear[p.Left.R]+Linear[p.Right.R])/2),Encode((Linear[p.Left.G]+Linear[p.Right.G])/2),
                Encode((Linear[p.Left.B]+Linear[p.Right.B])/2));
        }
        public static Color Source(SceneFrame frame,string placement) {
            if(placement=="Left half")return frame.Whole.Left;
            if(placement=="Right half")return frame.Whole.Right;
            if(placement!="Top half" && placement!="Bottom half")return Whole(frame.Whole);
            int start=placement=="Top half"?0:3;
            double r=0,g=0,b=0;
            for(int i=start;i<start+3;i++) {
                Pair p=frame.Six[i];
                r+=Linear[p.Left.R]+Linear[p.Right.R];g+=Linear[p.Left.G]+Linear[p.Right.G];b+=Linear[p.Left.B]+Linear[p.Right.B];
            }
            return Color.FromArgb(Encode(r/6),Encode(g/6),Encode(b/6));
        }
        public SceneFrame Analyze(byte[] pixels) {
            int top=0, bottom=0, limit=Sampler.Height/4;
            while(top<limit && BorderRow(pixels,top)) top++;
            while(bottom<limit && BorderRow(pixels,Sampler.Height-1-bottom)) bottom++;
            int proposed=top>=2 && bottom>=2 ? Math.Min(top,bottom) : 0;
            double now=clock();
            if(trim<0)trim=proposed;
            if(proposed==trim)pendingTrim=-1;
            else if(proposed!=pendingTrim){pendingTrim=proposed;pendingSince=now;}
            else if(now-pendingSince>=350){trim=proposed;pendingTrim=-1;}
            int end=Sampler.Height-trim;
            var result=new SceneFrame {Whole=Region(pixels,trim,end)};
            result.One[0]=result.Whole;
            Fill(pixels,trim,end,result.Two);
            Fill(pixels,trim,end,result.Three);
            Fill(pixels,trim,end,result.Six);
            return result;
        }
        private void Fill(byte[] p,int top,int bottom,Pair[] bands) {
            int height=bottom-top;
            for(int i=0;i<bands.Length;i++)
                bands[i]=Region(p,top+i*height/bands.Length,top+(i+1)*height/bands.Length);
        }
        private Pair Region(byte[] p,int top,int bottom) {
            return new Pair(Half(p,0,top,bottom),Half(p,Sampler.Width/2,top,bottom));
        }
        private static bool BorderRow(byte[] p,int y) {
            int lit=0;
            for(int x=0;x<Sampler.Width;x++) {
                int i=4*(y*Sampler.Width+x);
                if(Math.Max(p[i],Math.Max(p[i+1],p[i+2]))>7 && ++lit>1) return false;
            }
            return true;
        }
        private void Vote(int bucket,double amount,double r,double g,double b) {
            hue[bucket,0]+=amount; hue[bucket,1]+=amount*r; hue[bucket,2]+=amount*g; hue[bucket,3]+=amount*b;
        }
        private Color Half(byte[] pixels,int start,int top,int bottom) {
            Array.Clear(hue,0,hue.Length);
            double rSum=0,gSum=0,bSum=0,votes=0; int n=0;
            for(int y=top;y<bottom;y++) for(int x=start;x<start+Sampler.Width/2;x++) {
                int i=4*(y*Sampler.Width+x), b=pixels[i],g=pixels[i+1],r=pixels[i+2];
                double lr=Linear[r],lg=Linear[g],lb=Linear[b]; rSum+=lr;gSum+=lg;bSum+=lb;n++;
                int max=Math.Max(r,Math.Max(g,b)), span=max-Math.Min(r,Math.Min(g,b));
                if(span<12 || max<20) continue;
                double h=max==r?(double)(g-b)/span:max==g?2.0+(double)(b-r)/span:4.0+(double)(r-g)/span;
                double pos=(h<0?h+6:h)*2;
                int k=(int)pos; double f=pos-k;
                double weight=Math.Min(1,span/55.0)*Math.Min(1,span/(.35*max));
                Vote(k%12,weight*(1-f),lr,lg,lb); Vote((k+1)%12,weight*f,lr,lg,lb); votes+=weight;
            }
            double rMean=rSum/n,gMean=gSum/n,bMean=bSum/n;
            if(votes>0) {
                double biggest=0;
                for(int h=0;h<12;h++) {
                    double w=hue[(h+11)%12,0]+hue[h,0]+hue[(h+1)%12,0];
                    if(w>biggest)biggest=w;
                }
                double dominance=Math.Min(1,Math.Max(0,(biggest/votes-.55)/.35));
                double strength=.80*dominance*Math.Min(1,Math.Max(0,(biggest/n-.28)/.55));
                if(strength>0) {
                    // Continuous weights avoid a discontinuity when two hue neighborhoods exchange rank.
                    double sum=0,r=0,g=0,b=0;
                    for(int h=0;h<12;h++) {
                        int a=(h+11)%12,c=(h+1)%12;double w=hue[a,0]+hue[h,0]+hue[c,0];
                        if(w<=0)continue;double weight=Math.Pow(w/biggest,8);sum+=weight;
                        r+=weight*(hue[a,1]+hue[h,1]+hue[c,1])/w;
                        g+=weight*(hue[a,2]+hue[h,2]+hue[c,2])/w;
                        b+=weight*(hue[a,3]+hue[h,3]+hue[c,3])/w;
                    }
                    rMean+=strength*(r/sum-rMean);gMean+=strength*(g/sum-gMean);bMean+=strength*(b/sum-bMean);
                }
            }
            return Color.FromArgb(Encode(rMean),Encode(gMean),Encode(bMean));
        }
        internal struct Lab { public double L,A,B; }
        internal static Lab Perceptual(Color c) {
            double r=Linear[c.R],g=Linear[c.G],b=Linear[c.B];
            double l=Math.Pow(.4122214708*r+.5363325363*g+.0514459929*b,1.0/3);
            double m=Math.Pow(.2119034982*r+.6806995451*g+.1073969566*b,1.0/3);
            double s=Math.Pow(.0883024619*r+.2817188376*g+.6299787005*b,1.0/3);
            return new Lab { L=.2104542553*l+.793617785*m-.0040720468*s,
                A=1.9779984951*l-2.428592205*m+.4505937099*s,B=.0259040371*l+.7827717662*m-.808675766*s };
        }
        // Compare against the last transmitted color, so a slow ramp eventually crosses the threshold.
        public static Color HoldNoise(Color sent,Color now) {
            if(sent.ToArgb()==now.ToArgb() || (now.R==0 && now.G==0 && now.B==0)) return now;
            var a=Perceptual(sent);var b=Perceptual(now);
            double l=a.L-b.L,x=a.A-b.A,y=a.B-b.B;
            double threshold=b.L<.18?.010:Math.Max(a.A*a.A+a.B*a.B,b.A*b.A+b.B*b.B)<.02*.02?.009:.006;
            return l*l+x*x+y*y<threshold*threshold ? sent : now;
        }
    }
}
