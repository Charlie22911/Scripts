using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
namespace MediaReviewer {
    public sealed class PreviewRetryItem {
        public long GroupId {get;set;}public string State {get;set;}public string Error {get;set;}
    }
    public sealed class PreviewRetryBatch {
        public string Id {get;set;}public string CreatedAt {get;set;}public string State {get;set;}
        public long Total {get;set;}public long Saved {get;set;}public long Failed {get;set;}
        public string Label {get{return CreatedAt+" | "+State+" | "+Saved+" saved, "+Failed+" failed";}}
        public List<PreviewRetryItem> Items=new List<PreviewRetryItem>();
        internal long Cost {get{return 512+Items.Sum(x=>128L+2*(x.State.Length+x.Error.Length));}}
    }
    public static partial class Catalog {
        internal static void PreviewTransaction(SqliteConnection db,Action body){db.Execute("BEGIN IMMEDIATE");try{body();db.Execute("COMMIT");}catch{Rollback(db);throw;}}
        internal static void TrimPreviewHistory(SqliteConnection db,string keep){db.Execute("DELETE FROM preview_retry_batches WHERE id IN(SELECT id FROM preview_retry_batches WHERE state IN('complete','complete_errors','discarded') AND id<>? ORDER BY created_at DESC,id DESC LIMIT 1 OFFSET 19)",keep);}
        internal static PreviewRetryBatch RetryStatus(SqliteConnection db,string batch,bool items){
            var rows=db.Query("SELECT b.id,b.created_at,b.state,COUNT(i.id),COALESCE(SUM(i.state='saved'),0),COALESCE(SUM(i.state='failed'),0) FROM preview_retry_batches b LEFT JOIN preview_retry_items i ON i.batch_id=b.id WHERE b.id=? GROUP BY b.id",new object[]{batch},1);
            if(rows.Count==0)throw new InvalidOperationException("This preview task is unavailable or was removed from completed history.");var r=rows[0];var result=new PreviewRetryBatch {Id=(string)r[0],CreatedAt=(string)r[1],State=(string)r[2],Total=(long)r[3],Saved=(long)r[4],Failed=(long)r[5]};
            if(items)foreach(var row in db.Query("SELECT group_id,CASE WHEN state IN('pending','running') AND ?='interrupted' THEN 'interrupted' ELSE state END,substr(error,1,2048) FROM preview_retry_items WHERE batch_id=? ORDER BY id LIMIT 200",new object[]{result.State,batch},200))result.Items.Add(new PreviewRetryItem {GroupId=(long)row[0],State=(string)row[1],Error=(string)row[2]});return result;
        }
        internal static List<PreviewRetryBatch> RetryHistory(SqliteConnection db){
            var list=new List<PreviewRetryBatch>();foreach(var row in db.Query("SELECT id FROM preview_retry_batches ORDER BY state IN('queued','running','interrupted') DESC,created_at DESC,id DESC LIMIT 40",new object[0],40))list.Add(RetryStatus(db,(string)row[0],false));return list;
        }
        internal static string PreviewSourcePath(string root,string relative){
            if(String.IsNullOrEmpty(root) || String.IsNullOrEmpty(relative) || root.Length>32767 || relative.Length>32767 || root.IndexOf('\0')>=0 || relative.IndexOfAny(new[]{'\0',':','*','?'})>=0 || Path.IsPathRooted(relative))throw new InvalidDataException("This recorded source is not a supported relative file path.");
            if(!Path.IsPathRooted(root) || Path.GetPathRoot(root).Length<3 || root.StartsWith(@"\\.\",StringComparison.Ordinal))throw new InvalidDataException("This root is a descriptive or relative label. Reconnect its source before retrying.");
            string directory=Paths.Source(root),path=Paths.Source(Path.Combine(directory,relative));if(!path.StartsWith(directory.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The recorded source escapes its selected root.");return path;
        }
    }
    public sealed partial class ReviewerEngine {
        static string PreviewBatchId(string value){Guid id;if(!Guid.TryParseExact(value,"N",out id))throw new ArgumentException("Invalid preview task identifier");return id.ToString("N");}
        public string RequestPreviewRetry(long[] groupIds){
            if(groupIds==null || groupIds.Length<1 || groupIds.Length>200 || groupIds.Any(x=>x<1))throw new ArgumentException("Select1 through200 items to retry");long[] ids=groupIds.Distinct().ToArray();string batch=Guid.NewGuid().ToString("N");
            return EnqueueOperationWithId("PreviewRetry",(db,token,progress)=>PreviewRetryCore(db,batch,ids,token,progress),2048+ids.Length*8,null,batch);
        }
        public string RequestResumePreviewRetry(string batchId){string batch=PreviewBatchId(batchId);return EnqueueOperation("PreviewRetry",(db,token,progress)=>PreviewRetryCore(db,batch,null,token,progress));}
        public string RequestPreviewRetryStatus(string batchId){string batch=PreviewBatchId(batchId);return QueuePreviewRead("PreviewRetryStatus",db=>Catalog.RetryStatus(db,batch,true));}
        public string RequestPreviewRetryHistory(){return QueuePreviewRead("PreviewRetryHistory",db=>Catalog.RetryHistory(db));}
        string QueuePreviewRead(string kind,Func<SqliteConnection,object> body){
            if(!ready || closing || faulted)throw new InvalidOperationException("Database is not ready");string id=Guid.NewGuid().ToString("N");var work=new DbWork {Body=body,Done=value=>Respond(new UiResult {Kind=kind,RequestId=id,Success=true,PreviewRetry=value as PreviewRetryBatch,PreviewRetries=value as List<PreviewRetryBatch>}),Failed=error=>Respond(new UiResult {Kind=kind,RequestId=id,Error=error.Message})};if(!reads.TryAdd(work,2048))throw new InvalidOperationException("Reader queue is busy; refresh shortly.");return id;
        }
        public string RequestDiscardPreviewRetry(string batchId){string batch=PreviewBatchId(batchId);return EnqueueOperation("PreviewRetry",(db,token,progress)=>{WaitWrite(w=>{Catalog.PreviewTransaction(w,()=>{
            object state=w.Scalar("SELECT state FROM preview_retry_batches WHERE id=?",batch);if(state==null)throw new InvalidOperationException("Preview task is unavailable.");if((string)state=="running")throw new InvalidOperationException("Cancel the running preview task before discarding it.");w.Execute("UPDATE preview_retry_items SET state='cancelled' WHERE batch_id=? AND state IN('pending','running')",batch);w.Execute("UPDATE preview_retry_batches SET state='discarded' WHERE id=?",batch);Catalog.TrimPreviewHistory(w,batch);Catalog.Bump(w);
        });return null;});return "Pending preview work discarded; media and reviews are unchanged.";});}
        string PreviewRetryCore(SqliteConnection reader,string batch,long[] ids,CancellationToken token,Action<long,long,string> progress){
            token.ThrowIfCancellationRequested();
            WaitWrite(w=>{Catalog.PreviewTransaction(w,()=>{
                if(ids!=null){if(Convert.ToInt64(w.Scalar("SELECT COUNT(*) FROM preview_retry_batches WHERE state IN('queued','running','interrupted')"))>=20)throw new InvalidOperationException("Resume or discard an earlier preview task before adding another; at most20 unfinished tasks are retained.");
                    w.Execute("DELETE FROM preview_retry_batches WHERE id IN(SELECT id FROM preview_retry_batches WHERE state IN('complete','complete_errors','discarded') ORDER BY created_at DESC,id DESC LIMIT 1 OFFSET 19)");w.Execute("INSERT INTO preview_retry_batches VALUES(?,?,'queued')",batch,Catalog.Now());
                    foreach(long group in ids){object content=w.Scalar("SELECT content_id FROM review_groups WHERE id=? AND retired=0",group);if(content==null)throw new InvalidOperationException("A selected item changed; refresh the page before retrying.");w.Execute("INSERT INTO preview_retry_items(batch_id,group_id,content_id,state) VALUES(?,?,?,'pending')",batch,group,content);}Catalog.Bump(w);
                }
                object state=w.Scalar("SELECT state FROM preview_retry_batches WHERE id=?",batch);if(state==null)throw new InvalidOperationException("Preview task is unavailable.");if((string)state=="complete" || (string)state=="complete_errors" || (string)state=="discarded")return;
                w.Execute("UPDATE preview_retry_items SET state='pending' WHERE batch_id=? AND state='running'",batch);w.Execute("UPDATE preview_retry_batches SET state='running' WHERE id=?",batch);Catalog.Bump(w);
            });return null;});
            var initial=Catalog.RetryStatus(reader,batch,false);if(initial.State!="running")return PreviewSummary(initial);
            long done=initial.Saved+initial.Failed;
            try{while(true){token.ThrowIfCancellationRequested();var jobs=reader.Query("SELECT id,group_id,content_id FROM preview_retry_items WHERE batch_id=? AND state='pending' ORDER BY id LIMIT 1",new object[]{batch},1);if(jobs.Count==0)break;var job=jobs[0];long jobId=(long)job[0],group=(long)job[1],content=(long)job[2];
                WaitWrite(w=>{w.Execute("UPDATE preview_retry_items SET state='running',error='' WHERE id=? AND state='pending'",jobId);Catalog.Bump(w);return null;});
                object[] source=null;string path="",error="";PreviewResult preview=null;
                try{
                    var rows=reader.Query("SELECT r.path,o.path,o.size,o.modified_ticks,o.file_identity,o.id FROM review_groups g JOIN source_roots r ON r.id=g.root_id JOIN group_occurrences o ON o.group_id=g.id WHERE g.id=? AND g.content_id=? AND g.retired=0 AND g.status<>'EVIDENCE' ORDER BY o.superseded,o.modified_ticks DESC,o.occurrence_order LIMIT 1",new object[]{group,content},1);
                    if(rows.Count==0)throw new InvalidDataException("No ordinary source location is available; Evidence originals remain protected.");source=rows[0];path=Catalog.PreviewSourcePath((string)source[0],(string)source[1]);
                    for(int attempt=0;;attempt++)try{token.ThrowIfCancellationRequested();preview=Preview.CreateExpectedScoped(path,(long)source[2],(long)source[3],(string)source[4],256,32768,token,(string)source[0]);break;}catch(Exception failure){if(failure is OperationCanceledException || !FileAccessNative.Transient(failure) || attempt>=3)throw;int retry=attempt+1;WaitWrite(w=>{Catalog.Log(w,"retry","Preview retry",path,"Retry "+retry+"/3: "+failure.Message);return null;},2048+path.Length*2L);if(token.WaitHandle.WaitOne(attempt==0 ? 250 : attempt==1 ? 1000 : 3000))token.ThrowIfCancellationRequested();}
                }catch(OperationCanceledException){throw;}catch(Exception failure){error=failure.GetType().Name+": "+failure.Message;if(error.Length>2048)error=error.Substring(0,2048);}
                token.ThrowIfCancellationRequested();FinishPreviewRetry(jobId,group,content,source,path,preview,error);done++;progress(done,initial.Total,"finished previews");
            }
            WaitWrite(w=>{Catalog.PreviewTransaction(w,()=>{w.Execute("UPDATE preview_retry_batches SET state=CASE WHEN EXISTS(SELECT 1 FROM preview_retry_items WHERE batch_id=? AND state='failed') THEN 'complete_errors' ELSE 'complete' END WHERE id=?",batch,batch);Catalog.TrimPreviewHistory(w,batch);});return null;});return PreviewSummary(Catalog.RetryStatus(reader,batch,false));
            }catch{if(!faulted && !stop.IsCancellationRequested)try{WaitWrite(w=>{Catalog.PreviewTransaction(w,()=>{w.Execute("UPDATE preview_retry_items SET state='pending' WHERE batch_id=? AND state='running'",batch);w.Execute("UPDATE preview_retry_batches SET state='interrupted' WHERE id=? AND state='running'",batch);Catalog.Bump(w);});return null;});}catch{}throw;}
        }
        static string PreviewSummary(PreviewRetryBatch value){return "Preview task: "+value.Saved+" saved, "+value.Failed+" failed, "+value.Total+" selected. Good existing previews and review decisions are preserved.";}
        void FinishPreviewRetry(long job,long group,long content,object[] source,string path,PreviewResult preview,string error){
            WaitWrite(w=>{Catalog.PreviewTransaction(w,()=>{
                if(preview!=null && (source==null || w.Scalar("SELECT 1 FROM review_groups g JOIN source_roots r ON r.id=g.root_id JOIN group_occurrences o ON o.group_id=g.id WHERE g.id=? AND g.content_id=? AND g.retired=0 AND g.status<>'EVIDENCE' AND r.path=? AND o.id=? AND o.size=? AND o.modified_ticks=? AND o.file_identity IS ? LIMIT 1",group,content,source[0],source[5],source[2],source[3],source[4])==null)){preview=null;error="Recorded source or identity changed before preview publication; rescan before retrying.";}
                if(preview!=null)w.Execute("INSERT INTO previews(content_id,state,mime,width,height,bytes,error,decoder,revision) VALUES(?,'available',?,?,?,?,NULL,?,1) ON CONFLICT(content_id) DO UPDATE SET state='available',mime=excluded.mime,width=excluded.width,height=excluded.height,bytes=excluded.bytes,error=NULL,decoder=excluded.decoder,revision=previews.revision+1",content,preview.Mime,preview.Width,preview.Height,preview.Bytes,preview.Decoder);
                else{if(error.Length==0)error="Preview unavailable; the fingerprint and any saved preview are retained.";w.Execute("INSERT INTO previews(content_id,state,error,decoder) VALUES(?,'unavailable',?,'Selected preview retry') ON CONFLICT(content_id) DO UPDATE SET state='unavailable',error=excluded.error WHERE previews.state<>'available'",content,error);Catalog.Log(w,"warning","Preview retry",path,error);}
                w.Execute("UPDATE preview_retry_items SET state=?,error=? WHERE id=? AND state='running'",preview!=null ? "saved" : "failed",error,job);Catalog.Bump(w);
            });return null;},4096+path.Length*2L+(preview==null ? 0 : preview.Bytes.Length));
        }
    }
}
