using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RGB=System.Drawing.Color;

namespace GoveeLights {
    internal sealed partial class Controller {
        private SceneFrame lastPreview;
        private int shownBands=-1;
        private readonly Grid[] tiles=new Grid[4];
        private void UpdateBands(SceneFrame frame) {
            var record=selected==null?null:library.Items.FirstOrDefault(x=>x.Id==selected.Id);
            bool split=record!=null && record.Placement=="Split bars" && selected.Sku=="H6056" && mapping!=null;
            bool horizontal=record!=null && (record.Placement=="Top half" || record.Placement=="Bottom half");
            int n=split?Math.Min(mapping.Zones,mapping.Physical):horizontal?2:1;
            if(shownBands!=n) {
                shownBands=n;
                string[] names={"HalfL","HalfR","BarL","BarR"};
                for(int k=0;k<4;k++) {
                    var grid=new Grid();tiles[k]=grid;Get<Border>(names[k]).Child=grid;
                    for(int i=0;i<n;i++) {
                        grid.RowDefinitions.Add(new RowDefinition());
                        var tile=new Border {BorderThickness=new Thickness(0,i==0?0:1,0,0),BorderBrush=Brush("#454E5C")};
                        if(k<2 && split) {
                            var marker=new Border{CornerRadius=new CornerRadius(2),Background=Brush("#CC1B2026"),
                                Padding=new Thickness(3,0,3,0),HorizontalAlignment=k==0?HorizontalAlignment.Left:HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center,
                                Margin=new Thickness(3,0,3,0),Child=new TextBlock{Text=(i+1).ToString(),Foreground=Brushes.White,FontSize=9}};
                            tile.Child=marker;
                        }
                        Grid.SetRow(tile,i);grid.Children.Add(tile);
                    }
                }
            }
            bool active=frame!=null && record!=null && (testing || record.Included);
            string name=record==null?"":record.Name+" · ";
            Text("ZoneLegend",name+(split?n+(n==1?" zone per bar":" zones per bar"):record!=null?record.Placement:"Screen layout")+
                (active?" · Live":" · Layout preview"));
            for(int k=0;k<4;k++) {
                bool left=k%2==0;
                if(k>=2) Get<Border>(left?"BarL":"BarR").Visibility=split?Visibility.Visible:Visibility.Collapsed;
                for(int i=0;i<n;i++) {
                    var tile=(Border)tiles[k].Children[i];
                    // Segment numbers describe screen regions from top to bottom. Flips map physical wiring,
                    // so a correctly mounted bar still displays the same top region on the wall.
                    RGB c;
                    if(active) {
                        Pair pair=split?frame.Bands(n)[i]:frame.Whole;
                        c=left?pair.Left:pair.Right;
                        if(!split && record!=null) {
                            c=SceneColors.Source(frame,record.Placement);
                            if((left && record.Placement=="Right half") || (!left && record.Placement=="Left half") ||
                                (i==0 && record.Placement=="Bottom half") || (i==1 && record.Placement=="Top half"))c=RGB.FromArgb(32,36,42);
                        }
                    } else {int shade=41+(i%2)*12;c=left?RGB.FromArgb(shade,shade+12,shade+22):RGB.FromArgb(shade+7,shade+10,shade+22);}
                    var old=tile.Background as SolidColorBrush;
                    if(old==null || old.Color.R!=c.R || old.Color.G!=c.G || old.Color.B!=c.B) tile.Background=Brush(c);
                    if(split) {
                        bool flip=left?mapping.FlipLeft:mapping.FlipRight;
                        int slot=flip?n-1-i:i;
                        int first=1+(slot*mapping.Physical+n-1)/n,last=((slot+1)*mapping.Physical+n-1)/n;
                        int bar=(left!=mapping.Swap)?1:2;
                        tile.ToolTip=(left?"Left":"Right")+" zone "+(i+1)+" · bar "+bar+", "+
                            (first==last?"segment "+first:"segments "+first+"–"+last);
                    }
                }
            }
        }
    }
}
