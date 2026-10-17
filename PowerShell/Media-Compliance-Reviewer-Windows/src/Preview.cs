using System;
using System.IO;
using System.Threading;
namespace MediaReviewer {
    public sealed class PreviewResult {
        public byte[] Bytes;public int Width,Height,SourceWidth,SourceHeight;public string Mime="image/jpeg",Decoder="libvips";
    }
    public static class Preview {
        static readonly object hostGate=new object();static DecoderHost host;
        public static void Shutdown(){lock(hostGate){if(host!=null){host.Dispose();host=null;}}}
        public static void VerifyDecoder(string directory){
            string path=Path.Combine(directory,"decoder-check-"+Guid.NewGuid().ToString("N")+".gif");
            try{File.WriteAllBytes(path,Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"));Create(path);}finally{if(File.Exists(path))File.Delete(path);}
        }
        internal static void Dimensions(byte[] bytes,int count,out int width,out int height){
            using(var source=new MemoryStream(bytes,0,count,false))ImageHeader.Dimensions(source,ImageHeader.Detect(bytes,""),out width,out height);
            if((long)width*height>32000000)throw new InvalidDataException("Embedded preview dimensions exceed the 32 megapixel limit");
        }
        public static PreviewResult Create(string path){return CreateExpected(path,-1,0,null,256,32768);}
        public static PreviewResult CreateExpected(string path,long size,long ticks,string identity){return CreateExpected(path,size,ticks,identity,256,32768);}
        internal static PreviewResult CreateExpected(string path,long size,long ticks,string identity,int side,int limit){return CreateExpected(path,size,ticks,identity,side,limit,CancellationToken.None);}
        internal static PreviewResult CreateExpected(string path,long size,long ticks,string identity,int side,int limit,CancellationToken token){
            return CreateExpectedScoped(path,size,ticks,identity,side,limit,token,null);
        }
        internal static PreviewResult CreateExpectedScoped(string path,long size,long ticks,string identity,int side,int limit,CancellationToken token,string root){
            token.ThrowIfCancellationRequested();DecoderHost current;lock(hostGate){if(host==null)host=new DecoderHost(Native.RuntimeDirectory);current=host;}
            return current.CreateScoped(path,size,ticks,identity,side,limit,token,root);
        }
    }
}
