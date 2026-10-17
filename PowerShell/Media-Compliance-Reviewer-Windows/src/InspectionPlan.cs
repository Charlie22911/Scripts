using System;
namespace MediaReviewer {
    public sealed class InspectionPlan {
        public const long MaxPixels=8L*1024*1024;public const int MaxSide=8192,EncodedLimit=2*1024*1024;
        public double Scale,DisplayWidth,DisplayHeight;public int DecodeSide;public bool Limited;
        static void Dimensions(int width,int height){if(width<1 || height<1 || (long)width*height>200000000)throw new ArgumentException("Invalid bounded source dimensions");}
        public static long DecodedPixels(int width,int height,int side){Dimensions(width,height);if(side<1 || side>MaxSide)throw new ArgumentException("Invalid preview size");double scale=Math.Min(1,side/(double)Math.Max(width,height));return checked((long)Math.Ceiling(width*scale)*(long)Math.Ceiling(height*scale));}
        public static int BoundSide(int width,int height,int requested){Dimensions(width,height);int longest=Math.Max(width,height),shortest=Math.Min(width,height);int side=Math.Max(1,Math.Min(Math.Min(requested,MaxSide),longest));side=Math.Min(side,(int)Math.Floor(Math.Sqrt(MaxPixels*(double)longest/shortest)));while(DecodedPixels(width,height,side)>MaxPixels)side--;return side;}
        public static InspectionPlan Create(int width,int height,double viewportWidth,double viewportHeight,string mode,double zoom){
            Dimensions(width,height);if(Double.IsNaN(viewportWidth) || Double.IsInfinity(viewportWidth) || viewportWidth<=0 || Double.IsNaN(viewportHeight) || Double.IsInfinity(viewportHeight) || viewportHeight<=0 || Double.IsNaN(zoom) || Double.IsInfinity(zoom) || zoom<=0 || (mode!="Fit" && mode!="Fill" && mode!="Zoom"))throw new ArgumentException("Invalid inspector viewport or zoom");
            double fit=Math.Min(viewportWidth/width,viewportHeight/height),fill=Math.Max(viewportWidth/width,viewportHeight/height);double scale=mode=="Fit" ? fit : mode=="Fill" ? fill : Math.Max(.000001,Math.Min(8,zoom));
            scale=Math.Min(scale,1000000.0/Math.Max(width,height));double desired=Math.Max(width,height)*scale;int requested=(int)Math.Max(1,Math.Min(MaxSide,Math.Ceiling(desired)));int side=BoundSide(width,height,requested);
            return new InspectionPlan {Scale=scale,DisplayWidth=width*scale,DisplayHeight=height*scale,DecodeSide=side,Limited=side+1<Math.Min(desired,Math.Max(width,height))};
        }
    }
}
