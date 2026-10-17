using System;
using System.IO;
using System.Threading;
namespace MediaReviewer {
    public sealed class PackageResult:IDisposable {
        public long Generation;public string Kind,Value,Error,Details;public bool Success,Durable,OutputAttention;public PackageItem[] Items;public EvidenceInspection Inspection;
        public void Dispose(){if(Inspection!=null)Inspection.Dispose();Inspection=null;Items=null;}
    }
    // One owner, one active command, one replaceable pending command, one view result
    // and one durable original-export outcome. Output receipts do not follow view generations.
    // No PowerShell callback runs on this thread and no package is copied into a database.
    public sealed class EvidencePackageSession:IDisposable {
        static readonly object ownersGate=new object();static int owners;
        sealed class Work:IDisposable {
            internal long Generation;internal string Kind,Destination;internal int Index;internal byte[] Password;internal readonly CancellationTokenSource Cancel=new CancellationTokenSource();
            public void Dispose(){if(Password!=null)Array.Clear(Password,0,Password.Length);Cancel.Dispose();}
        }
        readonly object gate=new object();readonly Thread worker;readonly string path;Work pending,active;PackageResult result,outcome;EvidenceInspection view;EvidencePackageReader reader;
        long generation;bool ready,unlocked,busy=true,closing,closed,lockRequested;string outputWarning;OperationProgress status=new OperationProgress {Kind="Open",State="running",Stage="Opening protected package"};
        EvidencePackageSession(string path){this.path=path;pending=new Work {Kind="Open",Generation=0};worker=new Thread(Run) {Name="MediaReviewer protected package",IsBackground=true};worker.Start();}
        public static EvidencePackageSession Open(string path){if(String.IsNullOrEmpty(path) || path.Length>32767)throw new ArgumentException("Choose a protected package");string source=Paths.Source(path);lock(ownersGate){if(owners!=0)throw new InvalidOperationException("A protected package is still open or closing. Wait for its owned worker before opening another");owners++;}try{return new EvidencePackageSession(source);}catch{lock(ownersGate)owners--;throw;}}
        public bool Ready{get{lock(gate)return ready;}}public bool Unlocked{get{lock(gate)return unlocked;}}public bool Busy{get{lock(gate)return busy;}}public bool Closed{get{lock(gate)return closed;}}public OperationProgress Status{get{lock(gate)return status.Copy();}}
        public long Generation{get{lock(gate)return generation;}}public string OutputWarning{get{lock(gate)return outputWarning;}}
        void ClearView(){if(view!=null){view.Dispose();view=null;}if(result!=null){result.Dispose();result=null;}}
        long Submit(Work work){lock(gate){
            if(closing || closed || !ready){work.Dispose();throw new InvalidOperationException("Protected package is not ready");}
            if((work.Kind=="Inspect" || work.Kind=="Export") && !unlocked){work.Dispose();throw new InvalidOperationException("Unlock the protected package first");}
            if(work.Kind=="Export" && outcome!=null){work.Dispose();throw new InvalidOperationException("Read the previous original-export outcome before starting another export");}
            work.Generation=++generation;if(work.Kind!="Export")ClearView();else if(result!=null){result.Dispose();result=null;}
            if(work.Kind=="Unlock" || work.Kind=="Lock")unlocked=false;if(work.Kind=="Lock")lockRequested=true;if(active!=null)active.Cancel.Cancel();if(pending!=null){pending.Cancel.Cancel();pending.Dispose();}pending=work;busy=true;status=new OperationProgress {Kind=work.Kind,State="queued",Stage=work.Kind+" queued"};Monitor.PulseAll(gate);return generation;
        }}
        public long RequestUnlock(byte[] password){if(password==null || password.Length<1 || password.Length>65536){if(password!=null)Array.Clear(password,0,password.Length);throw new ArgumentException("Enter a bounded non-empty password");}return Submit(new Work {Kind="Unlock",Password=password});}
        public long RequestInspect(int index){if(index<0 || index>=200)throw new ArgumentOutOfRangeException("index");return Submit(new Work {Kind="Inspect",Index=index});}
        public long RequestExport(int index,string destination){if(index<0 || index>=200 || String.IsNullOrEmpty(destination) || destination.Length>32767)throw new ArgumentException("Choose an original and a new output file");return Submit(new Work {Kind="Export",Index=index,Destination=destination});}
        public long RequestLock(){return Submit(new Work {Kind="Lock"});}
        public void Cancel(){lock(gate){
            if(closing)return;generation++;if(active!=null)active.Cancel.Cancel();if(pending!=null){if(pending.Kind=="Unlock")unlocked=false;pending.Cancel.Cancel();pending.Dispose();pending=null;}if(result!=null){result.Dispose();result=null;}busy=active!=null || lockRequested;
            // A late Cancel does not turn an already completed output into a cancelled one.
            // Keep Status consistent with the durable receipt across later Dispatcher ticks.
            if(!busy && (outcome!=null || status.Kind=="Export" && (status.State=="complete" || status.State=="failed"))){if(outcome!=null){status.Kind="Export";status.State=outcome.Success ? "complete" : "failed";status.Error=outcome.Error??"";status.Stage=outcome.Success ? outcome.Value : outcome.Error;}return;}
            status.State=busy ? "cancelling" : "cancelled";status.Stage=busy ? "Cancelling; waiting for the owned operation" : "Cancelled";Monitor.PulseAll(gate);
        }}
        public PackageResult TakeResult(){lock(gate){if(outcome!=null){var receipt=outcome;outcome=null;return receipt;}var found=result;result=null;if(found!=null && found.Generation!=generation){found.Dispose();return null;}return found;}}
        void Progress(long done,long total,string unit){lock(gate){status.Completed=done;status.Total=total<0 ? (long?)null : total;status.Unit=unit;status.Stage=done+(total<0 ? "" : " / "+total)+" "+unit;status.LastProgressUtc=DateTime.UtcNow;}}
        static string Describe(EvidenceInspection original){
            var text=new System.Text.StringBuilder("Full SHA-256: "+original.Hash+"\nOriginal size: "+original.Size+" bytes\n");
            try{var metadata=EvidenceFormat.Json().Deserialize<System.Collections.Generic.Dictionary<string,object>>(Catalog.ReviewUtf8.GetString(original.Metadata));object value;if(metadata.TryGetValue("displayNotice",out value))text.AppendLine(Convert.ToString(value));else{
                var details=metadata;object decision;if(metadata.TryGetValue("decision",out decision) && decision is System.Collections.Generic.Dictionary<string,object>)details=(System.Collections.Generic.Dictionary<string,object>)decision;
                foreach(string name in new[]{"name","path","rootPath","reviewer","notes"}){object field;if(!details.TryGetValue(name,out field))metadata.TryGetValue(name,out field);string label=name=="name" ? "File" : name=="path" ? "Recorded path" : name=="rootPath" ? "Source folder" : name=="reviewer" ? "Reviewer" : "Notes";if(field!=null)text.AppendLine(label+": "+Convert.ToString(field));}
                object locations;if(metadata.TryGetValue("occurrences",out locations) && locations is object[]){var list=(object[])locations;for(int i=0;i<Math.Min(8,list.Length);i++){var location=list[i] as System.Collections.Generic.Dictionary<string,object>;object path;if(location!=null && location.TryGetValue("path",out path))text.AppendLine("Recorded location: "+Convert.ToString(path));}if(list.Length>8)text.AppendLine("Additional locations remain preserved in the encrypted package.");}
            }}catch(Exception){text.AppendLine("Saved details are unavailable; the original was verified.");}
            string result=text.ToString();return result.Length<=16384 ? result : result.Substring(0,16384)+"\n[Saved details exceed this display. The encrypted package remains complete.]";
        }
        void Run(){
            try{while(true){Work work;lock(gate){while(pending==null && !lockRequested && !closing)Monitor.Wait(gate,250);if(closing)break;if(lockRequested){if(reader!=null)reader.Lock();lockRequested=false;unlocked=false;if(pending==null){busy=false;status.State="complete";status.Stage="Package locked; protected details cleared.";continue;}}work=pending;pending=null;active=work;status.State="running";status.Stage=work.Kind=="Open" ? "Opening protected package" : work.Kind+" starting";}
                var reply=new PackageResult {Kind=work.Kind,Generation=work.Generation};try{
                    var token=work.Cancel.Token;token.ThrowIfCancellationRequested();
                    if(work.Kind=="Open"){reader=EvidencePackageReader.Open(path,token,Progress);reply.Value="Protected package opened. Enter its originals password.";}
                    else if(work.Kind=="Unlock"){reader.Unlock(work.Password,token,Progress);reply.Items=reader.Items;reply.Value="Package authenticated. Select a stored item.";}
                    else if(work.Kind=="Lock"){reader.Lock();reply.Value="Package locked; protected details cleared.";}
                    else if(work.Kind=="Inspect"){reply.Inspection=reader.Inspect(work.Index,token,Progress);reply.Details=Describe(reply.Inspection);if(reply.Inspection.Preview!=null)try{reply.Inspection.PreviewImage=MediaLoader.Decode(reply.Inspection.Preview,256);}catch(Exception error){reply.Inspection.PreviewError="Original verified; saved preview unavailable: "+error.Message;}reply.Value="Stored original authenticated and full SHA-256 verified.";}
                    else if(work.Kind=="Export"){reader.ExportOriginal(work.Index,work.Destination,token,Progress);reply.Value="Verified original exported: "+work.Destination;}
                    if(work.Kind!="Export")token.ThrowIfCancellationRequested();reply.Success=true;
                }catch(Exception error){if(work.Kind=="Unlock" && reader!=null)reader.Lock();SetFailure(reply,error);}
                Complete(work,reply);
                work.Dispose();
            }}catch(Exception error){lock(gate){status.State="failed";status.Error=error.Message;status.Stage="Package worker stopped: "+error.Message;}}
            finally{try{if(reader!=null)reader.Dispose();}finally{lock(ownersGate)owners--;lock(gate){if(active!=null){active.Dispose();active=null;}if(pending!=null){pending.Dispose();pending=null;}ClearView();unlocked=false;busy=false;closed=true;Monitor.PulseAll(gate);}}}
        }
        static void SetFailure(PackageResult reply,Exception error){reply.OutputAttention=error is IncompleteOriginalExportException;reply.Error=error is OperationCanceledException ? "Cancelled; no incomplete output was published." : error.Message;}
        void Complete(Work work,PackageResult reply){lock(gate){
            active=null;if(lockRequested){if(reader!=null)reader.Lock();lockRequested=false;}busy=pending!=null;ready=reader!=null;unlocked=!closing && ready && reader.Unlocked && (pending==null || (pending.Kind!="Lock" && pending.Kind!="Unlock"));
            if(work.Kind=="Export"){
                // An output receipt is independent of replaceable view generations, including
                // cancellation after publication and failures that leave an explicit output.
                reply.Durable=true;if(outcome!=null)outcome.Dispose();outcome=reply;
                if(reply.OutputAttention)outputWarning=reply.Error;
            }else if(!closing && work.Generation==generation){if(result!=null)result.Dispose();result=reply;if(reply.Inspection!=null)view=reply.Inspection;}else reply.Dispose();
            if(!closing && pending==null){status.State=reply.Success ? "complete" : work.Cancel.IsCancellationRequested && reply.Error=="Cancelled; no incomplete output was published." ? "cancelled" : "failed";status.Error=reply.Error??"";status.Stage=reply.Success ? reply.Value : reply.Error;if(work.Kind!="Export" && work.Generation!=generation){status.State="cancelled";status.Stage="Cancelled";}}
        }}
        public void CloseAsync(){lock(gate){if(closing)return;closing=true;unlocked=false;ClearView();if(active!=null)active.Cancel.Cancel();if(pending!=null){pending.Cancel.Cancel();pending.Dispose();pending=null;}status.State="closing";status.Stage="Closing protected package; waiting for its owner";Monitor.PulseAll(gate);}}
        public void Dispose(){CloseAsync();if(Thread.CurrentThread!=worker)worker.Join();}
    }
}
