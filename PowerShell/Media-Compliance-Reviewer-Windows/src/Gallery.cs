using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
namespace MediaReviewer {
    public sealed class CardItem:INotifyPropertyChanged {
        public MediaRow Record { get; private set; }
        bool selected,pending; BitmapSource image; string imageError;
        public bool ImageQueued;
        public CardItem(MediaRow record) { Record=record; }
        public string Name { get { return Record.Name; } }
        public string PathLabel { get { return Record.RelativePath; } }
        public string FullPath { get { return Record.SourcePath; } }
        public string Info { get { return Record.Status=="EVIDENCE" ? "Password-protected original" : Record.OriginalRetained && Record.LocationCount==0 ? "Stored original - no source location" : (Record.OriginalRetained ? "Original retained | " : "")+Record.LocationCount+" location"+(Record.LocationCount==1 ? "" : "s")+" · "+Record.Extension.ToUpperInvariant()+" · "+(Record.Size/1024.0).ToString("0.#",CultureInfo.CurrentCulture)+" KiB"+(Record.SourceIsHistorical ? " · Historical location" : ""); } }
        public string FingerprintLabel { get { return Record.Algorithm=="sha256" ? "Full SHA-256" : "Quick candidate match"; } }
        public string PreviewRetryLabel {get{return String.IsNullOrEmpty(Record.PreviewRetryState) ? "" : "Preview retry: "+Record.PreviewRetryState;}}
        public string Placeholder { get { return imageError ?? (Record.PreviewState=="unavailable" ? (String.IsNullOrEmpty(Record.PreviewError) ? "Preview unavailable" : Record.PreviewError) : Record.PreviewBytes==null && Record.PreviewState!="available" ? "Preview pending" : "Loading preview"); } }
        public BitmapSource Image { get { return image; } }
        public Visibility PlaceholderVisibility { get { return image==null ? Visibility.Visible : Visibility.Collapsed; } }
        public bool IsSelected { get { return selected; } set { if(selected!=value){selected=value;Changed("IsSelected");} } }
        public bool IsPending { get { return pending; } set { if(pending!=value){pending=value;Changed("IsPending");Changed("CanSelect");} } }
        public bool CanSelect { get { return !pending; } }
        public bool CanLoadImage { get { return image==null && imageError==null && !ImageQueued && Record.PreviewBytes!=null; } }
        public void SetImage(BitmapSource source,string error) { image=source; imageError=error; ImageQueued=false; Changed("Image");Changed("Placeholder");Changed("PlaceholderVisibility"); }
        public void Update(MediaRow record) { bool changed=Record.ContentId!=record.ContentId || Record.PreviewRevision!=record.PreviewRevision;Record=record;
            if(changed){image=null;imageError=null;ImageQueued=false;Changed("Image");Changed("PlaceholderVisibility");}
            if(image!=null) Record.PreviewBytes=null; Changed("Name");Changed("PathLabel");Changed("FullPath");Changed("Info");Changed("FingerprintLabel");Changed("Placeholder");Changed("PreviewRetryLabel"); }
        public event PropertyChangedEventHandler PropertyChanged;
        void Changed(string name) { var handler=PropertyChanged; if(handler!=null) handler(this,new PropertyChangedEventArgs(name)); }
    }
    public sealed class MediaLoadResult {
        public string Kind,Error; public long GroupId,ContentId,Generation,PreviewRevision; public BitmapSource Image;public int SourceWidth,SourceHeight,RequestedSide;
    }
    internal sealed class ThumbnailWork { internal long Id,Content,Generation,PreviewRevision; internal byte[] Bytes; }
    internal sealed class InspectWork { internal MediaRow Row; internal long Generation;internal int Side; }
    public sealed class MediaLoader:IDisposable {
        readonly BoundedQueue<ThumbnailWork> thumbnails=new BoundedQueue<ThumbnailWork>(20,1024*1024);
        readonly BoundedQueue<MediaLoadResult> output=new BoundedQueue<MediaLoadResult>(20,16*1024*1024);
        readonly object inspectGate=new object();InspectWork pendingInspect;MediaLoadResult inspectResult;CancellationTokenSource activeInspect;
        readonly CancellationTokenSource stop=new CancellationTokenSource(); readonly Thread thumbnailThread,inspectThread;
        long latestInspect; volatile bool closing,closed;
        public bool Closed { get { return closed; } }
        public MediaLoader() { thumbnailThread=new Thread(Thumbnails) { IsBackground=true,Name="MediaReviewer saved thumbnail loader" }; inspectThread=new Thread(Inspect) { IsBackground=true,Name="MediaReviewer source inspector" }; thumbnailThread.Start(); inspectThread.Start(); }
        public bool QueueThumbnail(long id,long generation,byte[] bytes){return QueueThumbnail(id,0,generation,bytes);}
        public bool QueueThumbnail(long id,long content,long generation,byte[] bytes,long previewRevision=0) { if(closing) return false; if(bytes==null || bytes.Length>32768) throw new InvalidDataException("Saved thumbnail exceeds its bounded encoded size"); return thumbnails.TryAdd(new ThumbnailWork { Id=id,Content=content,Generation=generation,Bytes=bytes,PreviewRevision=previewRevision },bytes.Length+128); }
        public bool QueueInspect(MediaRow row,long generation,int side) {
            if(row==null || row.RootPath==null || row.RelativePath==null || row.RootPath.Length+row.RelativePath.Length>65536 || side<1 || side>InspectionPlan.MaxSide)throw new ArgumentException("Invalid bounded inspection request");
            var snapshot=new MediaRow {GroupId=row.GroupId,ContentId=row.ContentId,RootPath=row.RootPath,RelativePath=row.RelativePath,Size=row.Size,ModifiedTicks=row.ModifiedTicks,FileIdentity=row.FileIdentity};
            lock(inspectGate){if(closing)return false;latestInspect=generation;pendingInspect=new InspectWork {Row=snapshot,Generation=generation,Side=side};inspectResult=null;if(activeInspect!=null)activeInspect.Cancel();Monitor.PulseAll(inspectGate);return true;}
        }
        public void CancelInspect(long generation){lock(inspectGate){latestInspect=generation;pendingInspect=null;inspectResult=null;if(activeInspect!=null)activeInspect.Cancel();Monitor.PulseAll(inspectGate);}}
        public bool TryTakeResult(out MediaLoadResult result) {lock(inspectGate){result=inspectResult;inspectResult=null;if(result!=null && result.Generation==latestInspect)return true;}result=output.Take(CancellationToken.None,0);return result!=null; }
        internal static BitmapSource Decode(byte[] bytes,int side) {
            int width,height; Preview.Dimensions(bytes,bytes.Length,out width,out height);
            if(width>side || height>side || (long)width*height>InspectionPlan.MaxPixels) throw new InvalidDataException("Saved preview dimensions exceed the bounded display limit");
            using(var stream=new MemoryStream(bytes,false)) {
                var bitmap=new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption=BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth=Math.Min(side,width); bitmap.StreamSource=stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
            }
        }
        void Post(MediaLoadResult result) { long size=512+(result.Image==null ? 0 : (long)result.Image.PixelWidth*result.Image.PixelHeight*4); output.Add(result,size,stop.Token); }
        void Thumbnails() {
            try { while(!stop.IsCancellationRequested) { ThumbnailWork work=thumbnails.Take(stop.Token,100); if(work==null) continue; var result=new MediaLoadResult { Kind="Thumbnail",GroupId=work.Id,ContentId=work.Content,Generation=work.Generation,PreviewRevision=work.PreviewRevision }; try { result.Image=Decode(work.Bytes,256); } catch(Exception error) { result.Error=error.Message; } Post(result); } } catch(OperationCanceledException) {}
        }
        void Inspect() {
            while(!stop.IsCancellationRequested){InspectWork work;CancellationTokenSource request;
                lock(inspectGate){if(pendingInspect==null && !closing)Monitor.Wait(inspectGate,100);if(closing)break;work=pendingInspect;pendingInspect=null;if(work==null)continue;request=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);activeInspect=request;}
                var result=new MediaLoadResult {Kind="Inspect",GroupId=work.Row.GroupId,ContentId=work.Row.ContentId,Generation=work.Generation,RequestedSide=work.Side};
                try{for(int attempt=0;;attempt++){try{var preview=Preview.CreateExpectedScoped(Catalog.PreviewSourcePath(work.Row.RootPath,work.Row.RelativePath),work.Row.Size,work.Row.ModifiedTicks,work.Row.FileIdentity,work.Side,InspectionPlan.EncodedLimit,request.Token,work.Row.RootPath);request.Token.ThrowIfCancellationRequested();result.Image=Decode(preview.Bytes,work.Side);result.SourceWidth=preview.SourceWidth;result.SourceHeight=preview.SourceHeight;break;}catch(Exception error){if(!FileAccessNative.Transient(error) || attempt>=3)throw;if(request.Token.WaitHandle.WaitOne(attempt==0 ? 250 : attempt==1 ? 1000 : 3000))request.Token.ThrowIfCancellationRequested();}}}
                catch(OperationCanceledException){}catch(Exception error){result.Error=error.Message;}
                finally{lock(inspectGate){if(!closing && !request.IsCancellationRequested && work.Generation==latestInspect)inspectResult=result;activeInspect=null;request.Dispose();}}
            }
        }
        public void Dispose() { if(closed) return; closing=true; stop.Cancel();lock(inspectGate){pendingInspect=null;inspectResult=null;if(activeInspect!=null)activeInspect.Cancel();Monitor.PulseAll(inspectGate);}thumbnails.Close();output.Close();thumbnailThread.Join();inspectThread.Join();closed=true; }
        public void CloseAsync() { if(closing) return; closing=true; new Thread(Dispose) { IsBackground=true,Name="MediaReviewer preview shutdown" }.Start(); }
    }
}
