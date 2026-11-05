using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RGB=System.Drawing.Color;

namespace GoveeLights {
    // A hue/saturation disc at full RGB value. Device brightness stays independent.
    internal sealed class ColorWheel : FrameworkElement {
        private static readonly BitmapSource disc=MakeDisc();
        private double hue,saturation=1;
        private RGB color=RGB.FromArgb(132,191,255);
        public event EventHandler ColorChanged;
        public ColorWheel() {
            Width=180;Height=180;Focusable=true;Cursor=Cursors.Cross;
            AutomationProperties.SetName(this,"Color wheel");
            AutomationProperties.SetHelpText(this,"Left and right change hue. Up and down change saturation. Home selects white.");
            SelectedColor=color;
            IsEnabledChanged+=(s,e)=>InvalidateVisual();
        }
        public RGB SelectedColor {
            get{return color;}
            set {
                color=value;hue=value.GetHue();int max=Math.Max(value.R,Math.Max(value.G,value.B)),min=Math.Min(value.R,Math.Min(value.G,value.B));
                saturation=max==0?0:(double)(max-min)/max;InvalidateVisual();
            }
        }
        internal static RGB Hsv(double h,double s) {
            h=(h%360+360)%360;s=Math.Max(0,Math.Min(1,s));double f=h/60, x=s*(1-Math.Abs(f%2-1)),m=1-s;
            double r=0,g=0,b=0;
            if(f<1){r=s;g=x;}else if(f<2){r=x;g=s;}else if(f<3){g=s;b=x;}
            else if(f<4){g=x;b=s;}else if(f<5){r=x;b=s;}else{r=s;b=x;}
            return RGB.FromArgb((int)Math.Round((r+m)*255),(int)Math.Round((g+m)*255),(int)Math.Round((b+m)*255));
        }
        private static BitmapSource MakeDisc() {
            const int n=360;var data=new byte[n*n*4];double radius=(n-2)/2.0;
            for(int y=0;y<n;y++)for(int x=0;x<n;x++) {
                double dx=x-(n-1)/2.0,dy=(n-1)/2.0-y,d=Math.Sqrt(dx*dx+dy*dy),a=Math.Max(0,Math.Min(1,radius+.5-d));
                RGB rgb=Hsv(Math.Atan2(dy,dx)*180/Math.PI,d/radius);int k=4*(y*n+x);
                data[k]=(byte)Math.Round(rgb.B*a);data[k+1]=(byte)Math.Round(rgb.G*a);data[k+2]=(byte)Math.Round(rgb.R*a);data[k+3]=(byte)Math.Round(255*a);
            }
            var image=BitmapSource.Create(n,n,96,96,PixelFormats.Pbgra32,null,data,n*4);image.Freeze();return image;
        }
        protected override void OnRender(DrawingContext dc) {
            base.OnRender(dc);double radius=Math.Min(ActualWidth,ActualHeight)/2-6;var center=new Point(ActualWidth/2,ActualHeight/2);
            dc.PushOpacity(IsEnabled?1:.4);dc.DrawImage(disc,new Rect(center.X-radius,center.Y-radius,2*radius,2*radius));
            if(IsKeyboardFocused)dc.DrawEllipse(null,new Pen(Brushes.White,1),center,radius+4,radius+4);
            double h=hue*Math.PI/180;var marker=new Point(center.X+Math.Cos(h)*saturation*radius,center.Y-Math.Sin(h)*saturation*radius);
            dc.DrawEllipse(null,new Pen(Brushes.Black,4),marker,6,6);dc.DrawEllipse(null,new Pen(Brushes.White,2),marker,6,6);dc.Pop();
        }
        private void Changed() {
            color=Hsv(hue,saturation);InvalidateVisual();var handler=ColorChanged;if(handler!=null)handler(this,EventArgs.Empty);
        }
        private void PointAt(Point p) {
            double dx=p.X-ActualWidth/2,dy=ActualHeight/2-p.Y,r=Math.Max(1,Math.Min(ActualWidth,ActualHeight)/2-6);
            hue=(Math.Atan2(dy,dx)*180/Math.PI+360)%360;saturation=Math.Min(1,Math.Sqrt(dx*dx+dy*dy)/r);Changed();
        }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e){base.OnMouseLeftButtonDown(e);Focus();CaptureMouse();PointAt(e.GetPosition(this));e.Handled=true;}
        protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(IsMouseCaptured && e.LeftButton==MouseButtonState.Pressed)PointAt(e.GetPosition(this));}
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e){base.OnMouseLeftButtonUp(e);if(IsMouseCaptured){PointAt(e.GetPosition(this));ReleaseMouseCapture();e.Handled=true;}}
        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e){base.OnGotKeyboardFocus(e);InvalidateVisual();}
        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e){base.OnLostKeyboardFocus(e);InvalidateVisual();}
        protected override void OnKeyDown(KeyEventArgs e) {
            base.OnKeyDown(e);double step=(Keyboard.Modifiers&ModifierKeys.Shift)!=0?10:2;
            switch(e.Key) {
                case Key.Left:hue=(hue+360-step)%360;break;case Key.Right:hue=(hue+step)%360;break;
                case Key.Up:saturation=Math.Min(1,saturation+.02);break;case Key.Down:saturation=Math.Max(0,saturation-.02);break;
                case Key.Home:saturation=0;break;case Key.End:saturation=1;break;default:return;
            }
            Changed();e.Handled=true;
        }
        protected override AutomationPeer OnCreateAutomationPeer(){return new WheelPeer(this);}
        private sealed class WheelPeer : FrameworkElementAutomationPeer,IValueProvider {
            private readonly ColorWheel wheel;
            public WheelPeer(ColorWheel wheel):base(wheel){this.wheel=wheel;}
            protected override string GetClassNameCore(){return "ColorWheel";}
            protected override AutomationControlType GetAutomationControlTypeCore(){return AutomationControlType.Custom;}
            public override object GetPattern(PatternInterface type){return type==PatternInterface.Value?this:base.GetPattern(type);}
            public bool IsReadOnly{get{return !wheel.IsEnabled;}}
            public string Value{get{return String.Format("#{0:X2}{1:X2}{2:X2}",wheel.color.R,wheel.color.G,wheel.color.B);}}
            public void SetValue(string value) {
                if(!wheel.IsEnabled)throw new ElementNotEnabledException();int rgb;string hex=(value??"").Trim().TrimStart('#');
                if(hex.Length!=6 || !Int32.TryParse(hex,System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out rgb))throw new ArgumentException("Use a six-digit hex color.");
                wheel.SelectedColor=RGB.FromArgb((rgb>>16)&255,(rgb>>8)&255,rgb&255);
                var handler=wheel.ColorChanged;if(handler!=null)handler(wheel,EventArgs.Empty);
            }
        }
    }
}
