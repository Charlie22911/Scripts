using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
namespace MediaReviewer {
    // Used only inside the private resource-limited decoder process.
    public static class MediaDecode {
        public const string FfmpegVersion="n9.0.2-27-g7e93014c5b-20261014";
        public static void VerifyTools(){foreach(string tool in new[]{"ffmpeg.exe","ffprobe.exe"}){string version=Encoding.UTF8.GetString(Run(tool,new[]{"-version"},16384));if(!version.StartsWith(Path.GetFileNameWithoutExtension(tool)+" version "+FfmpegVersion,StringComparison.Ordinal))throw new InvalidDataException("Unexpected private "+tool+" version");}}
        static void Pixels(int width,int height){if(width<1 || height<1 || (long)width*height>32000000)throw new InvalidDataException("Preview dimensions exceed the 32 megapixel limit");}
        static bool At(byte[] bytes,int at,string value){return at+value.Length<=bytes.Length && Encoding.ASCII.GetString(bytes,at,value.Length)==value;}
        static Exception Error(){string error=Native.Utf8(Vips.vips_error_buffer());Vips.vips_error_clear();return new InvalidDataException("Bundled image decoder: "+error);}
        public static PreviewResult Create(string path,long size,long ticks,string identity,int side,int limit){
            return CreateScoped(path,size,ticks,identity,side,limit,null);
        }
        public static PreviewResult CreateScoped(string path,long size,long ticks,string identity,int side,int limit,string root){
            if(!((side==256 && limit==32768) || (side>=1 && side<=InspectionPlan.MaxSide && limit==InspectionPlan.EncodedLimit)))throw new InvalidDataException("Invalid bounded preview request");
            string ext=Path.GetExtension(path).TrimStart('.').ToLowerInvariant();if(!Formats.Supported(ext))throw new InvalidDataException("Unsupported media extension");
            using(var source=new FileStream(Paths.Io(path),FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.SequentialScan)){
                FileStamp before=FileAccessNative.Stamp(source);
                if(size>=0 && (size!=before.Size || ticks!=before.ModifiedTicks || (!String.IsNullOrEmpty(identity) && identity!=before.Identity)))throw new InvalidDataException("Source changed after fingerprinting; rescan before generating its preview");
                if(!String.IsNullOrEmpty(root))path=Paths.OpenedSourceWithinRoot(source,root);
                byte[] header=new byte[(int)Math.Min(64,source.Length)];ReadFull(source,header);source.Position=0;ext=ImageHeader.Detect(header,ext);
                PreviewResult result;
                if(ext=="jpeg" || ext=="png"){
                    if(source.Length>128L*1024*1024)throw new InvalidDataException(ext+" input exceeds the 128 MiB preview guardrail; fingerprint retained");
                    int width,height;ImageHeader.Dimensions(source,ext,out width,out height);ImageHeader.SourcePixels(ext,width,height);
                    IntPtr thumbnail=IntPtr.Zero;
                    try{if(Vips.vips_thumbnail(Native.Text(Paths.Io(path)),out thumbnail,InspectionPlan.BoundSide(width,height,side),IntPtr.Zero)!=0)throw Error();result=FromImage(thumbnail,side,limit);OriginalSize(result,width,height);}
                    finally{if(thumbnail!=IntPtr.Zero)Vips.g_object_unref(thumbnail);}
                }else if(Formats.IsVideo(ext) || ext=="heic" || ext=="heif"){
                    if(source.Length>64L*1024*1024*1024)throw new InvalidDataException("Video/HEIF preview input exceeds the 64 GiB preview guardrail; fingerprint retained");
                    ValidateContainer(ext,header);result=Video(path,ext,side,limit);
                }else if(Formats.IsRaw(ext)){
                    byte[] jpeg=EmbeddedJpeg(source);result=FromBuffer(jpeg,side,limit,false);result.Decoder="Embedded RAW JPEG / libvips";
                }else{
                    if(source.Length>128L*1024*1024)throw new InvalidDataException("Image preview input exceeds the 128 MiB guardrail; fingerprint retained");
                    if(ext=="bmp" || ext=="ico"){ValidateRaster(ext,header);result=Wpf(source,ext,side,limit);}
                    else if(ext=="svg"){
                        if(source.Length>4*1024*1024)throw new InvalidDataException("SVG exceeds its 4 MiB XML limit");byte[] svg=new byte[(int)source.Length];ReadFull(source,svg);ValidateSvg(svg);result=FromBuffer(svg,side,limit,true);
                    }else{
                        ValidateRaster(ext,header);IntPtr image=IntPtr.Zero;
                        try{int rc=ext=="gif" ? Vips.vips_gifload(Native.Text(Paths.Io(path)),out image,IntPtr.Zero) : ext=="webp" ? Vips.vips_webpload(Native.Text(Paths.Io(path)),out image,IntPtr.Zero) : ext=="tif" || ext=="tiff" ? Vips.vips_tiffload(Native.Text(Paths.Io(path)),out image,IntPtr.Zero) : Vips.vips_heifload(Native.Text(Paths.Io(path)),out image,IntPtr.Zero);if(rc!=0)throw Error();result=FromImage(image,side,limit);}
                        finally{if(image!=IntPtr.Zero)Vips.g_object_unref(image);}
                    }
                }
                if(!before.Same(FileAccessNative.Stamp(source)))throw new InvalidDataException("Source changed during preview generation");result.Decoder+=" ("+ext+")";return result;
            }
        }
        static void ReadFull(Stream source,byte[] bytes){int at=0,n;while(at<bytes.Length && (n=source.Read(bytes,at,bytes.Length-at))>0)at+=n;if(at!=bytes.Length)throw new EndOfStreamException("Incomplete preview input");}
        static void OriginalSize(PreviewResult result,int width,int height){if((width>height && result.Width<result.Height) || (width<height && result.Width>result.Height)){int swap=width;width=height;height=swap;}result.SourceWidth=width;result.SourceHeight=height;}
        static void ValidateRaster(string ext,byte[] b){
            bool valid=ext=="gif" ? At(b,0,"GIF87a") || At(b,0,"GIF89a") : ext=="webp" ? At(b,0,"RIFF") && At(b,8,"WEBP") : ext=="bmp" ? At(b,0,"BM") : ext=="ico" ? b.Length>=6 && b[0]==0 && b[1]==0 && b[2]==1 && b[3]==0 : ext=="tif" || ext=="tiff" ? b.Length>=4 && ((b[0]==73 && b[1]==73 && (b[2]==42 || b[2]==43) && b[3]==0) || (b[0]==77 && b[1]==77 && b[2]==0 && (b[3]==42 || b[3]==43))) : ext=="avif" && At(b,4,"ftyp") && (At(b,8,"avif") || At(b,8,"avis") || Encoding.ASCII.GetString(b).Contains("avif"));
            if(!valid)throw new InvalidDataException("Media signature does not match the selected "+ext+" format");
            if(ext=="gif" && b.Length>=10)Pixels(b[6]|(b[7]<<8),b[8]|(b[9]<<8));
            if(ext=="bmp" && b.Length>=26 && BitConverter.ToInt32(b,14)>=40)Pixels(BitConverter.ToInt32(b,18),Math.Abs(BitConverter.ToInt32(b,22)));
        }
        static PreviewResult FromBuffer(byte[] bytes,int side,int limit,bool svg){
            GCHandle pin=GCHandle.Alloc(bytes,GCHandleType.Pinned);IntPtr image=IntPtr.Zero;
            try{int width=0,height=0;if(!svg){using(var header=new MemoryStream(bytes,false))ImageHeader.Dimensions(header,"jpeg",out width,out height);ImageHeader.SourcePixels("jpeg",width,height);}int requested=svg ? side : InspectionPlan.BoundSide(width,height,side);int rc=svg ? Vips.vips_svgload_buffer(pin.AddrOfPinnedObject(),new UIntPtr((uint)bytes.Length),out image,IntPtr.Zero) : Vips.vips_thumbnail_buffer(pin.AddrOfPinnedObject(),new UIntPtr((uint)bytes.Length),out image,requested,IntPtr.Zero);if(rc!=0)throw Error();var result=FromImage(image,side,limit);if(!svg)OriginalSize(result,width,height);return result;}
            finally{if(image!=IntPtr.Zero)Vips.g_object_unref(image);pin.Free();}
        }
        static PreviewResult FromImage(IntPtr image,int side,int limit){
            Pixels(Vips.vips_image_get_width(image),Vips.vips_image_get_height(image));IntPtr rotated=IntPtr.Zero;
            try{if(Vips.vips_autorot(image,out rotated,IntPtr.Zero)!=0)throw Error();int originalWidth=Vips.vips_image_get_width(rotated),originalHeight=Vips.vips_image_get_height(rotated),longest=InspectionPlan.BoundSide(originalWidth,originalHeight,side);
                for(int attempt=0;attempt<6;attempt++){
                    IntPtr thumb=IntPtr.Zero,buffer=IntPtr.Zero;
                    try{int requested=attempt<3 ? longest : Math.Max(1,longest>>(attempt-2));if(Vips.vips_thumbnail_image(rotated,out thumb,requested,IntPtr.Zero)!=0)throw Error();UIntPtr length;int quality=attempt==0 ? 85 : attempt==1 ? 65 : 45;if(Vips.vips_image_write_to_buffer(thumb,Native.Text(".jpg[Q="+quality+",strip]"),out buffer,out length,IntPtr.Zero)!=0)throw Error();ulong count=length.ToUInt64();if(count==0 || count>(ulong)limit)continue;byte[] bytes=new byte[(int)count];Marshal.Copy(buffer,bytes,0,bytes.Length);return new PreviewResult {Bytes=bytes,Width=Vips.vips_image_get_width(thumb),Height=Vips.vips_image_get_height(thumb),SourceWidth=originalWidth,SourceHeight=originalHeight};}
                    finally{if(buffer!=IntPtr.Zero)Vips.g_free(buffer);if(thumb!=IntPtr.Zero)Vips.g_object_unref(thumb);}
                }throw new InvalidDataException("Preview could not fit its encoded byte limit");
            }finally{if(rotated!=IntPtr.Zero)Vips.g_object_unref(rotated);}
        }
        static PreviewResult Wpf(Stream source,string ext,int side,int limit){
            if(ext=="ico"){
                using(var reader=new BinaryReader(source,Encoding.UTF8,true)){
                    source.Position=4;int count=reader.ReadUInt16();if(count<1 || count>256 || source.Length<6+16*count)throw new InvalidDataException("Invalid bounded ICO directory");
                    for(int i=0;i<count;i++){source.Position=6+16*i+8;uint length=reader.ReadUInt32(),offset=reader.ReadUInt32();if(length<12 || length>8*1024*1024 || (long)offset+length>source.Length)throw new InvalidDataException("ICO entry exceeds its bounds");source.Position=offset;byte[] header=reader.ReadBytes((int)Math.Min(length,262144));if(header.Length>=24 && header[0]==137){int w,h;Preview.Dimensions(header,header.Length,out w,out h);Pixels(w,h);}else if(header.Length>=12 && BitConverter.ToInt32(header,0)>=40)Pixels(BitConverter.ToInt32(header,4),Math.Abs(BitConverter.ToInt32(header,8))/2);else throw new InvalidDataException("Unsupported ICO entry signature");}
                }source.Position=0;
            }
            BitmapDecoder decoder=BitmapDecoder.Create(source,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);BitmapSource image=decoder.Frames.OrderByDescending(f=>(long)f.PixelWidth*f.PixelHeight).First();Pixels(image.PixelWidth,image.PixelHeight);
            int originalWidth=image.PixelWidth,originalHeight=image.PixelHeight;double scale=Math.Min(1,InspectionPlan.BoundSide(originalWidth,originalHeight,side)/(double)Math.Max(originalWidth,originalHeight));if(scale<1)image=new TransformedBitmap(image,new ScaleTransform(scale,scale));
            for(int quality=85;quality>=25;quality-=20){var encoder=new JpegBitmapEncoder {QualityLevel=quality};encoder.Frames.Add(BitmapFrame.Create(image));using(var output=new MemoryStream()){encoder.Save(output);if(output.Length<=limit)return new PreviewResult {Bytes=output.ToArray(),Width=image.PixelWidth,Height=image.PixelHeight,SourceWidth=originalWidth,SourceHeight=originalHeight,Decoder="Windows baseline BMP/ICO"};}}
            throw new InvalidDataException("BMP/ICO preview exceeds its encoded limit");
        }
        static byte[] EmbeddedJpeg(Stream source){
            byte[] block=new byte[65536],best=null;long scanned=0;int previous=-1,phase=0,skip=0,lengthHigh=0;bool entropy=false,afterScan=false;MemoryStream candidate=null;
            try{int n;while(scanned<128L*1024*1024 && (n=source.Read(block,0,(int)Math.Min(block.Length,128L*1024*1024-scanned)))>0){scanned+=n;
                for(int at=0;at<n;at++){int value=block[at];if(candidate==null){if(previous==255 && value==216){candidate=new MemoryStream();candidate.WriteByte(255);candidate.WriteByte(216);phase=0;entropy=false;skip=0;}previous=value;continue;}
                    candidate.WriteByte((byte)value);if(candidate.Length>8*1024*1024){candidate.Dispose();candidate=null;previous=value;continue;}
                    bool end=false;
                    if(skip>0){if(--skip==0){entropy=afterScan;phase=0;}}
                    else if(phase==2){lengthHigh=value;phase=3;}
                    else if(phase==3){skip=(lengthHigh<<8)+value-2;if(skip<0){candidate.Dispose();candidate=null;}else if(skip==0){entropy=afterScan;phase=0;}}
                    else if(phase==1){if(value==217)end=true;else if(value==255){}else if(entropy && (value==0 || (value>=208 && value<=215))){phase=0;}else if(value==1 || (value>=208 && value<=215)){phase=0;}else{afterScan=value==218;entropy=false;phase=2;}}
                    else if(value==255)phase=1;
                    else if(!entropy){candidate.Dispose();candidate=null;}
                    if(end){byte[] jpeg=candidate.ToArray();candidate.Dispose();candidate=null;try{int w,h;Preview.Dimensions(jpeg,jpeg.Length,out w,out h);if(best==null || jpeg.Length>best.Length)best=jpeg;if(Math.Max(w,h)>=256)return jpeg;}catch(InvalidDataException){};}
                    previous=value;
                }
            }}finally{if(candidate!=null)candidate.Dispose();}
            if(best==null)throw new InvalidDataException("RAW embedded JPEG unavailable within the first 128 MiB (8 MiB candidate limit); full RAW decoding is not available");return best;
        }
        static readonly HashSet<string> SvgElements=new HashSet<string>("svg,g,defs,rect,circle,ellipse,line,polyline,polygon,path,text,tspan,textPath,use,image,linearGradient,radialGradient,stop,pattern,clipPath,mask,symbol,marker,filter,feGaussianBlur,feOffset,feBlend,feColorMatrix,feComposite,feFlood,feMerge,feMergeNode,title,desc,style".Split(','));
        static void Resource(string value){
            if(value.StartsWith("#",StringComparison.Ordinal))return;
            foreach(string prefix in new[]{"data:image/png;base64,","data:image/jpeg;base64,"})if(value.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)){if(value.Length>3*1024*1024)break;byte[] bytes=Convert.FromBase64String(value.Substring(prefix.Length));int w,h;Preview.Dimensions(bytes,bytes.Length,out w,out h);return;}
            throw new InvalidDataException("SVG external resource reference is disabled");
        }
        static void Css(string text){
            if(text.IndexOf('\\')>=0 || text.IndexOf('@')>=0 || text.Contains("/*") || text.Contains("*/"))throw new InvalidDataException("SVG CSS escapes/imports are disabled");
            string cleaned=Regex.Replace(text,"url\\s*\\(\\s*['\"]?#[a-zA-Z0-9_.:-]+['\"]?\\s*\\)","",RegexOptions.IgnoreCase);
            if(Regex.IsMatch(cleaned,"url\\s*\\(",RegexOptions.IgnoreCase))throw new InvalidDataException("SVG CSS external resource is disabled");
        }
        static void ValidateSvg(byte[] bytes){
            var document=new XmlDocument {XmlResolver=null};var settings=new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=4*1024*1024,MaxCharactersFromEntities=0};
            using(var stream=new MemoryStream(bytes,false))using(var reader=XmlReader.Create(stream,settings))document.Load(reader);
            foreach(XmlNode node in document.ChildNodes)if(node.NodeType==XmlNodeType.ProcessingInstruction)throw new InvalidDataException("SVG processing instructions and external stylesheets are disabled");
            if(document.DocumentElement==null || document.DocumentElement.LocalName!="svg")throw new InvalidDataException("SVG root element is missing");
            var nodes=new Stack<Tuple<XmlNode,int>>();nodes.Push(Tuple.Create((XmlNode)document.DocumentElement,0));int count=0;
            while(nodes.Count>0){var pair=nodes.Pop();XmlNode node=pair.Item1;if(++count>100000 || pair.Item2>128)throw new InvalidDataException("SVG structure exceeds its bounds");
                if(node.NodeType==XmlNodeType.ProcessingInstruction)throw new InvalidDataException("SVG processing instructions are disabled");
                if(node.NodeType==XmlNodeType.Element){if(!SvgElements.Contains(node.LocalName) || (node.NamespaceURI!="http://www.w3.org/2000/svg" && node.NamespaceURI!=""))throw new InvalidDataException("SVG scripting/foreign elements are disabled");
                    foreach(XmlAttribute attr in node.Attributes){if(attr.Prefix=="xmlns" || attr.Name=="xmlns")continue;if(attr.LocalName.StartsWith("on",StringComparison.OrdinalIgnoreCase) || (attr.Prefix=="xml" && attr.LocalName=="base"))throw new InvalidDataException("SVG event handlers/base resources are disabled");if(attr.LocalName=="href")Resource(attr.Value);else Css(attr.Value);}
                    if(node.LocalName=="style")Css(node.InnerText);
                }foreach(XmlNode child in node.ChildNodes)nodes.Push(Tuple.Create(child,pair.Item2+1));
            }
        }
        static void ValidateContainer(string ext,byte[] b){
            bool mov=new[]{"mp4","m4v","mov","heic","heif"}.Contains(ext);bool valid=mov ? At(b,4,"ftyp") || At(b,4,"moov") || At(b,4,"wide") || At(b,4,"mdat") : ext=="avi" ? At(b,0,"RIFF") && At(b,8,"AVI ") : ext=="wmv" ? b.Length>=4 && b[0]==0x30 && b[1]==0x26 && b[2]==0xb2 && b[3]==0x75 : ext=="mkv" || ext=="webm" ? b.Length>=4 && b[0]==0x1a && b[1]==0x45 && b[2]==0xdf && b[3]==0xa3 : ext=="mts" || ext=="m2ts" || ext=="ts" ? b.Length>4 && (b[0]==0x47 || b[4]==0x47) : b.Length>=4 && b[0]==0 && b[1]==0 && b[2]==1;
            if(!valid)throw new InvalidDataException("Video/HEIF signature does not match the selected container; playlists and external inputs are disabled");
        }
        static string Quote(string value){
            var text=new StringBuilder("\"");int slashes=0;
            foreach(char character in value){if(character=='\\'){slashes++;continue;}if(character=='"')text.Append('\\',slashes*2+1).Append('"');else text.Append('\\',slashes).Append(character);slashes=0;}
            return text.Append('\\',slashes*2).Append('"').ToString();
        }
        static byte[] Run(string executable,IEnumerable<string> arguments,int maxBytes){
            var info=new ProcessStartInfo(Path.Combine(Native.RuntimeDirectory,executable),String.Join(" ",arguments.Select(Quote))) {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Native.RuntimeDirectory,RedirectStandardOutput=true,RedirectStandardError=true};
            Process child;try{child=Process.Start(info);}catch(System.ComponentModel.Win32Exception error){throw new System.ComponentModel.Win32Exception(error.NativeErrorCode,"Cannot start private "+executable+": "+error.Message+" (Win32 "+error.NativeErrorCode+")");}
            using(var process=child){
                Task<string> errors=Task.Run(()=>{var text=new StringBuilder();char[] block=new char[1024];int n;while((n=process.StandardError.Read(block,0,block.Length))>0){if(text.Length<8192)text.Append(block,0,Math.Min(n,8192-text.Length));}return text.ToString();});
                byte[] result;
                try{using(var output=new MemoryStream()){byte[] block=new byte[8192];int n;while((n=process.StandardOutput.BaseStream.Read(block,0,block.Length))>0){if(output.Length+n>maxBytes)throw new InvalidDataException("Decoder output exceeds its byte limit");output.Write(block,0,n);}result=output.ToArray();}}
                catch{if(!process.HasExited)process.Kill();process.WaitForExit();errors.GetAwaiter().GetResult();throw;}
                process.WaitForExit();string message=errors.GetAwaiter().GetResult();if(process.ExitCode!=0)throw new InvalidDataException(StartupDiagnostics.ProcessExit(executable,process.ExitCode,message));return result;
            }
        }
        static PreviewResult Video(string path,string ext,int side,int limit){
            string format=new[]{"mp4","m4v","mov","heic","heif"}.Contains(ext) ? "mov" : ext=="avi" ? "avi" : ext=="wmv" ? "asf" : ext=="mkv" || ext=="webm" ? "matroska" : ext=="ts" || ext=="mts" || ext=="m2ts" ? "mpegts" : "mpeg";
            var source=new List<string>{"-v","error","-max_alloc","67108864","-protocol_whitelist","file,pipe","-format_whitelist",format=="mov" ? "mov,mp4,m4a,3gp,3g2,mj2" : format,"-probesize","4194304","-analyzeduration","3000000","-f",format};
            if(format=="mov")source.AddRange(new[]{"-enable_drefs","0","-use_absolute_path","0"});
            source.AddRange(new[]{"-i","file:"+Paths.Io(path)});
            var probe=new List<string>(source);probe.AddRange(new[]{"-select_streams","v:0","-show_entries","stream=width,height,codec_type","-of","json"});
            var json=new JavaScriptSerializer {MaxJsonLength=65536};var metadata=json.Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(Run("ffprobe.exe",probe,65536)));object streams;if(!metadata.TryGetValue("streams",out streams) || ((System.Collections.IList)streams).Count==0)throw new InvalidDataException("No video/image stream is available for a poster");var stream=(Dictionary<string,object>)((System.Collections.IList)streams)[0];if(!stream.ContainsKey("width") || !stream.ContainsKey("height"))throw new InvalidDataException("Video dimensions are unavailable");int sourceWidth=Convert.ToInt32(stream["width"]),sourceHeight=Convert.ToInt32(stream["height"]);Pixels(sourceWidth,sourceHeight);side=InspectionPlan.BoundSide(sourceWidth,sourceHeight,side);
            for(int attempt=0;attempt<3;attempt++){
                int requested=Math.Max(1,side>>attempt);var args=new List<string>{"-nostdin","-threads","1","-filter_threads","1","-cpucount","1"};args.AddRange(source);args.AddRange(new[]{"-map","0:v:0","-an","-sn","-dn","-frames:v","1","-vf","scale="+requested+":"+requested+":force_original_aspect_ratio=decrease:reset_sar=1","-threads","1","-c:v","mjpeg","-q:v",attempt==0 ? "5" : "12","-f","image2pipe","pipe:1"});byte[] bytes=Run("ffmpeg.exe",args,2*1024*1024);int width,height;Preview.Dimensions(bytes,bytes.Length,out width,out height);
                // Square-pixel display dimensions can differ from the coded dimensions used above.
                if(width>requested || height>requested || (long)width*height>InspectionPlan.MaxPixels || bytes.Length>limit)continue;
                var result=new PreviewResult {Bytes=bytes,Width=width,Height=height,Decoder="FFmpeg 9.0 poster"};OriginalSize(result,sourceWidth,sourceHeight);double ratio=width/(double)height;if(Math.Abs(result.SourceWidth/(double)result.SourceHeight-ratio)>.01*ratio){int displayWidth=(int)Math.Ceiling(result.SourceHeight*ratio);if(displayWidth>0 && (long)displayWidth*result.SourceHeight<=200000000)result.SourceWidth=displayWidth;else{result.SourceWidth=width;result.SourceHeight=height;}}return result;
            }throw new InvalidDataException("Video poster exceeds its pixel or encoded preview limit");
        }
    }
}
