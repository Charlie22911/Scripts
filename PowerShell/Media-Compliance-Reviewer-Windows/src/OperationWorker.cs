using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
namespace MediaReviewer {
    public sealed class OperationProgress {
        public string RequestId="",Kind="",Stage="",State="queued",Unit="",Error="";
        public long Completed;public long? Total;public DateTime LastProgressUtc;
        internal OperationProgress Copy(){return (OperationProgress)MemberwiseClone();}
    }
    internal sealed class OperationWork {
        internal OperationProgress Progress;internal CancellationTokenSource Cancel;
        internal Func<SqliteConnection,CancellationToken,Action<long,long,string>,object> Body;
        internal Action Cleanup;internal bool Started;
    }
    public sealed partial class ReviewerEngine {
        readonly object operationGate=new object();
        readonly BoundedQueue<OperationWork> operationQueue=new BoundedQueue<OperationWork>(4,128*1024);
        readonly Dictionary<string,OperationWork> operationStates=new Dictionary<string,OperationWork>();
        Thread operationThread;
        readonly Func<string,SqliteConnection> operationReader;
        bool operationReady;volatile Exception operationFailure;
        public OperationProgress GetOperationProgress(string requestId) {lock(operationGate){OperationWork work;return operationStates.TryGetValue(requestId,out work) ? work.Progress.Copy() : null;}}
        // operationGate also owns the transition from a queued secret to an executing body.
        void CleanOperation(OperationWork work){var cleanup=work.Cleanup;work.Cleanup=null;if(cleanup!=null)cleanup();}
        void CancelWork(OperationWork work){if(work.Cancel==null)return;work.Cancel.Cancel();work.Progress.State="cancelling";if(!work.Started)CleanOperation(work);}
        public void CancelOperation(string requestId) {lock(operationGate){OperationWork work;if(operationStates.TryGetValue(requestId,out work))CancelWork(work);}}
        string EnqueueOperation(string kind,Func<SqliteConnection,CancellationToken,Action<long,long,string>,object> body,long retainedBytes=2048,Action cleanup=null) {return EnqueueOperationWithId(kind,body,retainedBytes,cleanup,null);}
        string EnqueueOperationWithId(string kind,Func<SqliteConnection,CancellationToken,Action<long,long,string>,object> body,long retainedBytes,Action cleanup,string requestId) {
            if(!ready || closing || faulted){if(cleanup!=null)cleanup();throw new InvalidOperationException("Database is not ready");}
            var work=new OperationWork {Progress=new OperationProgress {RequestId=requestId??Guid.NewGuid().ToString("N"),Kind=kind,Stage=kind+" queued"},Cancel=CancellationTokenSource.CreateLinkedTokenSource(stop.Token),Body=body,Cleanup=cleanup};
            lock(operationGate){
                if(closing){work.Cancel.Dispose();if(cleanup!=null)cleanup();throw new InvalidOperationException("Database is closing");}
                if(operationFailure!=null || !operationReady){work.Cancel.Dispose();if(cleanup!=null)cleanup();throw new InvalidOperationException("Background worker is unavailable; reopen the database: "+(operationFailure==null ? "initializing" : operationFailure.Message));}
                foreach(var old in operationStates.Values.Where(x=>x.Cancel==null).OrderBy(x=>x.Progress.LastProgressUtc).Take(Math.Max(0,operationStates.Count-12)).ToArray())operationStates.Remove(old.Progress.RequestId);
                operationStates.Add(work.Progress.RequestId,work);
                try{if(!operationQueue.TryAdd(work,retainedBytes)){operationStates.Remove(work.Progress.RequestId);throw new InvalidOperationException("Background operation queue is full; try again after it finishes");}}
                catch{operationStates.Remove(work.Progress.RequestId);if(work.Cancel!=null){work.Cancel.Dispose();work.Cancel=null;}if(work.Cleanup!=null)work.Cleanup();throw;}
            }
            return work.Progress.RequestId;
        }
        void StoreOperation(OperationWork work) {
            OperationProgress p;lock(operationGate)p=work.Progress.Copy();
            WaitWrite(db=>{db.Execute("INSERT INTO long_operations VALUES(?,?,?,?,?,?,?,?,?) ON CONFLICT(id) DO UPDATE SET state=excluded.state,stage=excluded.stage,completed=excluded.completed,total=excluded.total,unit=excluded.unit,error=excluded.error,updated_at=excluded.updated_at",p.RequestId,p.Kind,p.State,p.Stage,p.Completed,p.Total.HasValue ? (object)p.Total.Value : null,p.Unit,p.Error,Catalog.Now());
                db.Execute("DELETE FROM long_operations WHERE id IN(SELECT id FROM long_operations WHERE state IN('complete','failed','cancelled','interrupted') ORDER BY updated_at DESC LIMIT -1 OFFSET 200)");return null;});
        }
        void StartOperations(SqliteConnection writerDb) {
            vaultExists=writerDb.Scalar("SELECT 1 FROM evidence_vault WHERE id=1")!=null;
            if(writerDb.Scalar("SELECT 1 FROM original_exports WHERE state IN('writing','planned') LIMIT 1")!=null)Catalog.Log(writerDb,"warning","Original export","","An export was interrupted. Its explicitly chosen output may be incomplete; inspect or remove it before retrying. No automatic deletion was attempted.");
            writerDb.Execute("UPDATE long_operations SET state='interrupted',stage='Interrupted; inspect any output before starting again',updated_at=? WHERE state IN('queued','running','cancelling')",Catalog.Now());
            lock(operationGate){
                if(closing || stop.IsCancellationRequested)throw new OperationCanceledException("Database initialization was cancelled");
                operationThread=new Thread(OperationMain) {IsBackground=true,Name="MediaReviewer long operations"};operationThread.Start();
                while(!operationReady && operationFailure==null){stop.Token.ThrowIfCancellationRequested();if(closing)throw new OperationCanceledException("Database initialization was cancelled");Monitor.Wait(operationGate,50);}
                if(operationFailure!=null)throw new IOException("Background database reader could not initialize: "+operationFailure.Message,operationFailure);
            }
        }
        void OperationMain() {
            try {using(var db=operationReader(path)) {
                db.Script("PRAGMA query_only=ON; PRAGMA cache_size=-2048; PRAGMA mmap_size=0;");
                lock(operationGate){operationReady=true;Monitor.PulseAll(operationGate);}
                while(true) {
                    OperationWork work=operationQueue.Take(CancellationToken.None,50);if(work==null){if(closing || stop.IsCancellationRequested || operationFailure!=null)break;continue;}
                    string failure="";object value=null;var timer=Stopwatch.StartNew();long lastReport=0;
                    try {
                        if(!vaultUnlocked)ClearVaultWorker();
                        lock(operationGate){work.Progress.State="running";work.Progress.Stage=work.Progress.Kind+" starting";}StoreOperation(work);work.Cancel.Token.ThrowIfCancellationRequested();
                        lock(operationGate){work.Cancel.Token.ThrowIfCancellationRequested();work.Started=true;}
                        Update(s=>{s.BusyOperation=true;s.Stage=work.Progress.Kind+" starting";});
                        value=work.Body(db,work.Cancel.Token,(done,total,unit)=>{
                            if(timer.ElapsedMilliseconds-lastReport<200 && done!=total)return;lastReport=timer.ElapsedMilliseconds;
                            lock(operationGate){work.Progress.Completed=done;work.Progress.Total=total<0 ? (long?)null : total;work.Progress.Unit=unit;work.Progress.Stage=work.Progress.Kind+": "+done+(total<0 ? "" : " / "+total)+" "+unit;work.Progress.LastProgressUtc=DateTime.UtcNow;}
                            Update(s=>{s.Stage=work.Progress.Stage;s.LastProgressUtc=DateTime.UtcNow;});
                            // Writing progress to this source would restart its live backup.
                            // Persist only operation boundaries; progress stays in a bounded snapshot.
                        });
                        lock(operationGate){work.Progress.State="complete";work.Progress.Stage=work.Progress.Kind+" complete";work.Progress.LastProgressUtc=DateTime.UtcNow;}
                    } catch(Exception error) {
                        bool cancelled=work.Cancel.IsCancellationRequested;failure=cancelled ? work.Progress.Kind+" cancelled; completed database commits were retained" : error.Message;
                        lock(operationGate){work.Progress.State=cancelled ? "cancelled" : "failed";work.Progress.Stage=failure;work.Progress.Error=failure;work.Progress.LastProgressUtc=DateTime.UtcNow;}
                    }
                    try{StoreOperation(work);}catch(Exception error){
                        var published=value as OriginalReceipt;
                        if(failure.Length==0 && (work.Progress.Kind=="Report" && value is string || published!=null && published.OutputPublished)){string warning=" The final database status could not be saved: "+error.Message;string message;if(published!=null){published.Message+=warning;message=published.Message;}else{value=(string)value+warning;message=(string)value;}lock(operationGate){work.Progress.Stage=message;}Update(s=>s.Error=message);}
                        else{failure="Operation outcome could not be committed; inspect its output before retrying: "+error.Message;lock(operationGate){work.Progress.State="failed";work.Progress.Error=failure;}}
                    }
                    Update(s=>{s.BusyOperation=false;s.Stage=s.RunningScan ? "Scanning" : "Ready";});
                    var receipt=value as IdentityResolutionReceipt;
                    var original=value as OriginalReceipt;var secret=value as EvidenceInspection;
                    if(secret!=null && (failure.Length!=0 || !vaultUnlocked || secret.Epoch!=VaultGeneration)){secret.Dispose();secret=null;if(failure.Length==0)failure="Vault locked; protected result discarded.";}
                    Respond(new UiResult {RequestId=work.Progress.RequestId,Kind=original!=null ? original.Kind : work.Progress.Kind=="Capture" || work.Progress.Kind=="EvidenceReclassify" ? "Review" : work.Progress.Kind,Success=failure.Length==0,Error=failure,Value=original!=null ? original.Message : receipt==null ? value as string : receipt.Message,IdentityPreview=value as IdentityResolutionPreview,Evidence=secret,GroupIds=original==null ? null : original.GroupIds,OperationId=original!=null ? original.OperationId : receipt==null ? null : receipt.OperationId,Reconciled=original!=null ? original.Reconciled : receipt!=null && receipt.Reconciled});
                    if(!vaultUnlocked)ClearVaultWorker();
                    lock(operationGate){CleanOperation(work);work.Cancel.Dispose();work.Cancel=null;}
                }
            }} catch(Exception error){FailOperations(error);}finally{InvalidateVault();ClearVaultWorker();}
        }
        void FailOperations(Exception error) {
            OperationWork[] abandoned;string message="Background worker stopped; reopen the database before retrying: "+error.Message;
            lock(operationGate){
                operationReady=false;operationFailure=error;operationQueue.Close();
                abandoned=operationStates.Values.Where(x=>x.Cancel!=null).ToArray();
                foreach(var work in abandoned){work.Cancel.Cancel();work.Progress.State="failed";work.Progress.Stage=message;work.Progress.Error=message;work.Progress.LastProgressUtc=DateTime.UtcNow;}
                while(operationQueue.Take(CancellationToken.None,0)!=null){}
                Monitor.PulseAll(operationGate);
            }
            if(!closing && !stop.IsCancellationRequested)Update(s=>{s.BusyOperation=false;s.Error=message;});
            foreach(var work in abandoned){
                try{if(!closing && !stop.IsCancellationRequested)StoreOperation(work);}catch(Exception commitError){lock(operationGate)work.Progress.Error=message+"; failure status could not be committed: "+commitError.Message;}
                Respond(new UiResult {RequestId=work.Progress.RequestId,Kind=work.Progress.Kind=="Capture" || work.Progress.Kind=="EvidenceReclassify" ? "Review" : work.Progress.Kind,Success=false,Error=work.Progress.Error});
                lock(operationGate){CleanOperation(work);work.Cancel.Dispose();work.Cancel=null;}
            }
        }
        void StopOperations(){operationQueue.Close();lock(operationGate){foreach(var work in operationStates.Values)CancelWork(work);}if(operationThread!=null && Thread.CurrentThread!=operationThread)operationThread.Join();}
    }
}
