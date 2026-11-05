using System;
using System.Drawing;
using Microsoft.Win32;

namespace GoveeLights {
    internal static class SyncOptions {
        internal static volatile int Comfort=35;
        internal static volatile int Palette;
        internal static readonly string[] Palettes={"Full spectrum","Warm range","Cool range"};
        internal static void Load() {
            using(var k=Registry.CurrentUser.OpenSubKey(@"Software\Govee Lights")) {
                int value;Comfort=k!=null && Int32.TryParse(Convert.ToString(k.GetValue("Comfort",35)),out value)?Math.Max(0,Math.Min(100,value)):35;
                Palette=k!=null && Int32.TryParse(Convert.ToString(k.GetValue("ColorRange",0)),out value)?Math.Max(0,Math.Min(2,value)):0;
            }
        }
        internal static void Save(){using(var k=Registry.CurrentUser.CreateSubKey(@"Software\Govee Lights")){k.SetValue("Comfort",Comfort);k.SetValue("ColorRange",Palette);}}
    }
    internal sealed class ComfortColors {
        private SceneFrame previous,result;
        private int last=-1,lastPalette=-1;
        internal SceneFrame Process(SceneFrame frame,int strength,int palette=0) {
            if(strength==0 && palette==0)return frame;
            if(frame==previous && strength==last && palette==lastPalette)return result;
            previous=frame;last=strength;lastPalette=palette;result=new SceneFrame {Whole=Apply(frame.Whole,strength,palette)};
            foreach(int n in new[]{1,2,3,6})for(int i=0;i<n;i++)result.Bands(n)[i]=Apply(frame.Bands(n)[i],strength,palette);
            return result;
        }
        private static Pair Apply(Pair p,int strength,int palette){return new Pair(Apply(p.Left,strength,palette),Apply(p.Right,strength,palette));}
        internal static Color Apply(Color color,int strength,int palette=0) {
            if((strength<=0 && palette==0) || color.ToArgb()==Color.Black.ToArgb())return color;
            double amount=Math.Min(100,strength)/100.0;var lab=SceneColors.Perceptual(color);
            lab.L-=.18*amount*lab.L*lab.L*lab.L;
            lab.A*=1-.30*amount;lab.B*=1-.30*amount;
            if(palette!=0) {
                double chroma=Math.Sqrt(lab.A*lab.A+lab.B*lab.B);
                if(chroma>.0001) {
                    // A continuous periodic projection avoids hard palette boundaries or a hue-wrap seam.
                    double center=(palette==1?50:235)*Math.PI/180,half=(palette==1?50:55)*Math.PI/180;
                    double hue=center+half*Math.Sin(Math.Atan2(lab.B,lab.A)-center);
                    lab.A=chroma*Math.Cos(hue);lab.B=chroma*Math.Sin(hue);
                }
            }
            // Reduce chroma along the same hue when the result leaves the RGB gamut.
            double r,g,b;ToLinear(lab,1,out r,out g,out b);
            if(!Inside(r,g,b)) {
                double low=0,high=1;
                for(int i=0;i<12;i++){double mid=(low+high)/2;ToLinear(lab,mid,out r,out g,out b);if(Inside(r,g,b))low=mid;else high=mid;}
                ToLinear(lab,low,out r,out g,out b);
            }
            return Color.FromArgb(Encode(r),Encode(g),Encode(b));
        }
        private static bool Inside(double r,double g,double b){return r>=0 && g>=0 && b>=0 && r<=1 && g<=1 && b<=1;}
        private static int Encode(double v){v=Math.Max(0,Math.Min(1,v));return (int)Math.Round(255*(v<=.0031308?12.92*v:1.055*Math.Pow(v,1/2.4)-.055));}
        private static void ToLinear(SceneColors.Lab c,double chroma,out double r,out double g,out double b) {
            double a=c.A*chroma,y=c.B*chroma,l=c.L+.3963377774*a+.2158037573*y,m=c.L-.1055613458*a-.0638541728*y,s=c.L-.0894841775*a-1.291485548*y;
            l=l*l*l;m=m*m*m;s=s*s*s;
            r=4.0767416621*l-3.3077115913*m+.2309699292*s;
            g=-1.2684380046*l+2.6097574011*m-.3413193965*s;
            b=-.0041960863*l-.7034186147*m+1.707614701*s;
        }
    }
    // Backend changes can alter estimated colors. Recover briefly, then pin the fallback for this session.
    internal sealed class CaptureContinuity {
        private double failedAt=-1;
        internal bool Fallback {get;private set;}
        internal void Failed(double now){if(failedAt<0)failedAt=now;}
        internal void Recovered(){failedAt=-1;}
        internal bool ReuseOnTimeout(double now){return failedAt<0 || Hold(now,true);}
        internal bool Hold(double now,bool hasFrame) {
            if(failedAt<0)return false;
            if(hasFrame && now-failedAt<500)return true;
            Fallback=true;return false;
        }
    }
}
