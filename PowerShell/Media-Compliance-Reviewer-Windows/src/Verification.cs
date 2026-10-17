using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
namespace MediaReviewer {
    public sealed class IdentityOutcome {
        public string Hash,Status,Reviewer,Notes,ExamplePath;public long Size,Locations,ExistingGroupId;public bool Anchor,Conflict;
    }
    public sealed class IdentityResolutionPreview {
        public string RunId,Token,SourceStatus,SourceReviewer,SourceNotes,Message;public long GroupId,Revision,Locations,Remaining;
        public bool CanApply,HasConflicts,AcceptConflicts;public List<IdentityOutcome> Outcomes=new List<IdentityOutcome>();
        internal IdentityResolutionPreview RequestCopy(){return new IdentityResolutionPreview {RunId=RunId,Token=Token,Revision=Revision,AcceptConflicts=AcceptConflicts};}
    }
    internal sealed class IdentityResolutionReceipt {internal string OperationId,Message;internal bool Reconciled;}
    internal static class Verification {
        internal static JavaScriptSerializer Json(){return new JavaScriptSerializer {MaxJsonLength=1024*1024};}
        internal static object[] Source(SqliteConnection db,long group,bool protect=false){
            var rows=db.Query("SELECT g.content_id,g.root_id,c.algorithm,g.status,g.retired,r.path,c.algorithm_version,substr(c.digest,1,65),substr(c.sampling,1,257),c.size FROM review_groups g JOIN contents c ON c.id=g.content_id JOIN source_roots r ON r.id=g.root_id WHERE g.id=?",new object[]{group},1);
            if(rows.Count==0 || (long)rows[0][4]!=0)throw new InvalidOperationException("This review group was replaced; refresh the page.");
            if((string)rows[0][3]=="EVIDENCE")throw new InvalidOperationException("Evidence remains protected. Its identity cannot be changed by candidate verification.");
            if((string)rows[0][2]=="sha256")throw new InvalidOperationException("This group already has a full SHA-256 identity.");
            if(protect){
                if(db.Scalar("SELECT 1 FROM imported_fields f JOIN import_extensions e ON e.rowid=f.owner_rowid WHERE f.owner_table='import_extensions' AND e.kind='identity_aliases' AND f.column_name IN('old_key','group_key') LIMIT 1")!=null || db.Scalar("SELECT 1 FROM review_groups WHERE id=? AND length(legacy_key)>4096",group)!=null)throw new InvalidOperationException("Protected identity links contain oversized fields; ordinary verification is unavailable until the protected-record resolver supports them.");
                var guard=db.Query(@"WITH RECURSIVE linked(key) AS (
 SELECT legacy_key FROM review_groups WHERE id=?
 UNION SELECT a.old_key FROM identity_aliases a JOIN linked l ON a.group_key=l.key WHERE length(a.old_key)<=4096 LIMIT 1001)
 SELECT COUNT(*),COALESCE(MAX(EXISTS(SELECT 1 FROM evidence_items e WHERE e.decision_key=l.key) OR EXISTS(SELECT 1 FROM evidence_manifests m WHERE m.decision_key=l.key)),0),COALESCE(MAX(EXISTS(SELECT 1 FROM identity_aliases a WHERE a.group_key=l.key AND length(a.old_key)>4096)),0) FROM linked l",new object[]{group},1)[0];
                if((long)guard[0]>1000 || (long)guard[2]!=0)throw new InvalidOperationException("Protected identity links exceed the bounded resolver limit; no identity was changed.");
                if((long)guard[1]!=0)throw new InvalidOperationException("This candidate links to a protected original. Its identity is retained until the authenticated Evidence workflow can resolve it.");
            }return rows[0];
        }
        internal static FingerprintPolicy CandidatePolicy(object[] source){
            string digest=(string)source[7],sampling=(string)source[8];
            if((string)source[2]!="sampled-sha256-v1" || (long)source[6]!=1 || digest==null || digest.Length!=64 || digest.Any(c=>!Uri.IsHexDigit(c)))throw new InvalidDataException("The saved candidate fingerprint method or version cannot be reproduced. Scan the current contents before verification.");
            var policy=new FingerprintPolicy {ThresholdBytes=0};
            if(!String.Equals(sampling,"html-v4:q2:"+digest,StringComparison.OrdinalIgnoreCase)){
                var match=Regex.Match(sampling??"",@"\Awindows=([0-9]{1,2});maxWindowBytes=([0-9]{1,7})\z",RegexOptions.CultureInvariant);
                if(!match.Success)throw new InvalidDataException("The saved candidate fingerprint parameters cannot be reproduced. Scan the current contents before verification.");
                policy.WindowCount=Int32.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture);policy.MaxWindowBytes=Int32.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture);
                try{policy.Validate();}catch(ArgumentException error){throw new InvalidDataException("The saved candidate fingerprint parameters are unsupported.",error);}
                if(policy.Parameters!=sampling)throw new InvalidDataException("The saved candidate fingerprint parameters are not canonical; automatic promotion is unavailable.");
            }
            long size=(long)source[9];if(size<policy.WindowCount*2 || size>9007199254740991L)throw new InvalidDataException("The saved candidate size is outside the supported sampling range.");return policy;
        }
        internal static void Transaction(SqliteConnection db,Action body){db.Execute("BEGIN IMMEDIATE");try{body();db.Execute("COMMIT");}catch{Catalog.Rollback(db);throw;}}
        internal static void CheckRevision(SqliteConnection db,IdentityResolutionPreview p){
            if(Catalog.Revision(db)!=p.Revision)throw new InvalidOperationException("The database changed after this preview. Verify again before applying; pause the scan if needed.");
            var source=Source(db,p.GroupId);object expected=db.Scalar("SELECT source_content FROM identity_runs WHERE id=?",p.RunId);
            if(expected==null || (long)source[0]!=Convert.ToInt64(expected))throw new InvalidOperationException("The candidate identity changed. Verify again.");
        }
        internal static long ValidChecks(SqliteConnection db,string run,long group){
            return Convert.ToInt64(db.Scalar(@"SELECT COUNT(*) FROM identity_checks c WHERE c.run_id=? AND EXISTS(SELECT 1 FROM group_occurrences o WHERE o.group_id=? AND o.id=c.occurrence_id AND o.superseded=0 AND o.path=c.path AND o.size=c.size AND o.modified_ticks=c.expected_ticks AND o.file_identity IS c.expected_identity)",run,group));
        }
        internal static IdentityResolutionPreview Prepare(SqliteConnection db,string run,long group){
            IdentityResolutionPreview p=null;Transaction(db,()=>{
                Source(db,group,true);long count=Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM group_occurrences WHERE group_id=? AND superseded=0",group));
                long valid=ValidChecks(db,run,group),checks=Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM identity_checks WHERE run_id=?",run));
                if(valid!=checks)throw new InvalidOperationException("A source location changed during verification. Start a fresh verification after scanning its current contents.");
                p=new IdentityResolutionPreview {RunId=run,Token=Guid.NewGuid().ToString("N"),GroupId=group,Revision=Catalog.Revision(db),Locations=count,Remaining=count-valid};
                var source=db.Query("SELECT status,substr(reviewer,1,128),substr(notes,1,2048) FROM review_groups WHERE id=?",new object[]{group},1)[0];
                p.SourceStatus=(string)source[0];p.SourceReviewer=(string)source[1];p.SourceNotes=(string)source[2];
                foreach(var row in db.Query("SELECT digest,size,COUNT(*),MIN(occurrence_order) AS first_location FROM identity_checks WHERE run_id=? GROUP BY digest,size ORDER BY first_location LIMIT 201",new object[]{run},201)){
                    var outcome=new IdentityOutcome {Hash=(string)row[0],Size=(long)row[1],Locations=(long)row[2],Anchor=p.Outcomes.Count==0,Status="TO_REVIEW",Reviewer="",Notes=""};
                    outcome.ExamplePath=(string)db.Scalar("SELECT substr(path,1,512) FROM identity_checks WHERE run_id=? AND digest=? ORDER BY occurrence_order LIMIT 1",run,outcome.Hash);
                    var targets=db.Query(@"SELECT g.id,g.status,CASE WHEN g.status='EVIDENCE' THEN '' ELSE substr(g.reviewer,1,128) END,CASE WHEN g.status='EVIDENCE' THEN '[Protected Evidence]' ELSE substr(g.notes,1,512) END,
 CASE WHEN g.status=s.status AND g.reviewer=s.reviewer AND g.notes=s.notes AND COALESCE((SELECT sha256 FROM imported_fields WHERE owner_table='review_groups' AND owner_rowid=g.id AND column_name='notes'),'')=COALESCE((SELECT sha256 FROM imported_fields WHERE owner_table='review_groups' AND owner_rowid=s.id AND column_name='notes'),'') AND COALESCE((SELECT sha256 FROM imported_fields WHERE owner_table='review_groups' AND owner_rowid=g.id AND column_name='reviewer'),'')=COALESCE((SELECT sha256 FROM imported_fields WHERE owner_table='review_groups' AND owner_rowid=s.id AND column_name='reviewer'),'') THEN 0 ELSE 1 END
 FROM review_groups g JOIN contents c ON c.id=g.content_id JOIN review_groups s ON s.id=? WHERE g.root_id=s.root_id AND g.retired=0 AND c.algorithm='sha256' AND c.algorithm_version=1 AND c.size=? AND c.digest=? AND c.sampling=''",new object[]{group,outcome.Size,outcome.Hash},1);
                    if(targets.Count>0){var t=targets[0];outcome.ExistingGroupId=(long)t[0];outcome.Status=(string)t[1];outcome.Reviewer=(string)t[2];outcome.Notes=(string)t[3];outcome.Conflict=(long)t[4]!=0;p.HasConflicts|=outcome.Conflict;}
                    else if(outcome.Anchor){outcome.Status=p.SourceStatus;outcome.Reviewer=p.SourceReviewer;outcome.Notes=p.SourceNotes;}
                    p.Outcomes.Add(outcome);
                }
                p.CanApply=count>0 && p.Remaining==0 && p.Outcomes.Count>0 && p.Outcomes.Count<=200;
                p.Message=count==0 ? "No current source locations remain. The saved record and preview are retained." : p.Remaining>0 ? "Some locations have not been fully verified. Verify all locations before applying." : p.Outcomes.Count>200 ? "More than 200 distinct full identities were found. This candidate is retained; it needs a larger conflict-review workflow." : "The first location anchors the original card. New splits start To be reviewed. Existing full groups keep their decisions; candidate history is retained.";
                if(p.Outcomes.Count>200)p.Outcomes.Clear();
                db.Execute("UPDATE identity_runs SET state=?,preview_revision=?,token=?,preview_json=? WHERE id=?",p.CanApply ? "prepared" : "checking",p.Revision,p.Token,Json().Serialize(p),run);
            });return p;
        }
        internal static string Payload(IdentityResolutionPreview p){return Identity.Digest(p.RunId+"\n"+p.Token+"\n"+p.Revision.ToString(CultureInfo.InvariantCulture)+"\n"+p.AcceptConflicts);}
        internal static bool Reconciled(SqliteConnection db,string operation,string payload){object old=db.Scalar("SELECT payload_hash FROM operations WHERE id=?",operation);if(old==null)return false;if((string)old!=payload)throw new InvalidOperationException("Operation identifier has a different payload; it cannot be replayed.");return true;}
        internal static IdentityResolutionPreview Load(SqliteConnection db,IdentityResolutionPreview request){
            var rows=db.Query("SELECT token,preview_json,state FROM identity_runs WHERE id=?",new object[]{request.RunId},1);
            if(rows.Count==0 || (string)rows[0][0]!=request.Token || (string)rows[0][2]!="prepared")throw new InvalidOperationException("This verification preview is no longer available. Verify again.");
            var p=Json().Deserialize<IdentityResolutionPreview>((string)rows[0][1]);if(!p.CanApply || p.Revision!=request.Revision)throw new InvalidOperationException("Invalid or incomplete verification preview");
            p.AcceptConflicts=request.AcceptConflicts;if(p.HasConflicts && !p.AcceptConflicts)throw new InvalidOperationException("Review the conflicts and explicitly accept keeping the existing verified decisions before applying.");CheckRevision(db,p);return p;
        }
        internal static void StagePlans(SqliteConnection db,IdentityResolutionPreview p,IEnumerable<IdentityOutcome> outcomes){Transaction(db,()=>{
            CheckRevision(db,p);string root=(string)db.Scalar("SELECT root_id FROM identity_runs WHERE id=?",p.RunId);
            foreach(var outcome in outcomes){
                string now=Catalog.Now();db.Execute("INSERT OR IGNORE INTO contents(algorithm,algorithm_version,size,digest,sampling,legacy_hash,first_seen,last_seen) VALUES('sha256',1,?,?,'',?,?,?)",outcome.Size,outcome.Hash,outcome.Hash,now,now);
                long content=Convert.ToInt64(db.Scalar("SELECT id FROM contents WHERE algorithm='sha256' AND algorithm_version=1 AND size=? AND digest=? AND sampling=''",outcome.Size,outcome.Hash));
                object existing=db.Scalar("SELECT id FROM review_groups WHERE root_id=? AND content_id=? AND retired=0",root,content);
                bool retain=outcome.Anchor && existing==null;object target=existing ?? (retain ? (object)p.GroupId : null);
                db.Execute("INSERT INTO identity_plan VALUES(?,?,?,?,?,?) ON CONFLICT(run_id,digest) DO NOTHING",p.RunId,outcome.Hash,content,target,existing==null && !retain ? 1 : 0,retain ? 1 : 0);
            }
        });}
        internal static void StageLocations(SqliteConnection db,IdentityResolutionPreview p,long after,CancellationToken token){Transaction(db,()=>{
            token.ThrowIfCancellationRequested();CheckRevision(db,p);
            foreach(var row in db.Query("SELECT occurrence_id,digest FROM identity_checks WHERE run_id=? AND occurrence_order>? ORDER BY occurrence_order LIMIT 32",new object[]{p.RunId,after},32))
                db.Execute("INSERT OR IGNORE INTO resolved_locations VALUES(?,?,?,?)",p.RunId,p.GroupId,row[1],row[0]);
        });}
        internal static IdentityResolutionReceipt Publish(SqliteConnection db,IdentityResolutionPreview p,string operation,string payload,CancellationToken token){
            bool reconciled=false;Transaction(db,()=>{
                if(Reconciled(db,operation,payload)){reconciled=true;return;}token.ThrowIfCancellationRequested();CheckRevision(db,p);
                Source(db,p.GroupId,true);
                long count=Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM group_occurrences WHERE group_id=? AND superseded=0",p.GroupId));
                if(count!=p.Locations || ValidChecks(db,p.RunId,p.GroupId)!=count || Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM resolved_locations WHERE run_id=?",p.RunId))!=count)throw new InvalidOperationException("Locations changed or staging is incomplete; nothing was published.");
                if(db.Scalar("SELECT 1 FROM resolved_locations a JOIN resolved_locations b ON b.occurrence_id=a.occurrence_id AND b.run_id<>a.run_id JOIN identity_runs r ON r.id=b.run_id AND r.state='published' WHERE a.run_id=? LIMIT 1",p.RunId)!=null)throw new InvalidOperationException("A location was already resolved by another operation.");
                string root=(string)db.Scalar("SELECT root_id FROM identity_runs WHERE id=?",p.RunId);bool retained=false;
                foreach(var plan in db.Query("SELECT digest,content_id,group_id,is_new,retain_source FROM identity_plan WHERE run_id=? ORDER BY retain_source DESC,digest",new object[]{p.RunId},200)){
                    long content=(long)plan[1],target=plan[2]==null ? 0 : (long)plan[2];
                    if((long)plan[4]!=0){retained=true;db.Execute("UPDATE review_groups SET content_id=? WHERE id=?",content,p.GroupId);}
                    else if(target==0){
                        db.Execute("INSERT INTO review_groups(root_id,content_id,status,legacy_key) VALUES(?,?,'TO_REVIEW',?)",root,content,root+"|verified:"+(string)plan[0]+"|"+content.ToString(CultureInfo.InvariantCulture));target=db.LastId;
                        db.Execute("UPDATE review_groups SET display_sequence=id WHERE id=?",target);db.Execute("UPDATE identity_plan SET group_id=? WHERE run_id=? AND digest=?",target,p.RunId,plan[0]);
                    }
                    db.Execute("INSERT INTO review_events VALUES(?,?,?,?,?,?,?,?,?)",Guid.NewGuid().ToString(),p.GroupId,(string)db.Scalar("SELECT legacy_key FROM review_groups WHERE id=?",p.GroupId),p.SourceStatus,p.SourceStatus,Catalog.Now(),"","Full verification links this candidate to group "+(target==0 ? p.GroupId : target)+"; full SHA-256 "+(string)plan[0],operation);
                }
                if(!retained)db.Execute("UPDATE review_groups SET retired=1 WHERE id=?",p.GroupId);
                db.Execute("UPDATE identity_runs SET state='published' WHERE id=?",p.RunId);long revision=Catalog.Bump(db);
                db.Execute("INSERT INTO operations VALUES(?,?,?,?)",operation,payload,Catalog.Now(),revision);
                Catalog.Log(db,"info","Identity resolution","","Verified group "+p.GroupId+" into "+p.Outcomes.Count+" full identities. Original review history retained. Operation "+operation);
            });return new IdentityResolutionReceipt {OperationId=operation,Reconciled=reconciled,Message="Committed full verification. Original review history retained."};
        }
    }
    public sealed partial class ReviewerEngine {
        static FileStream VerifiedLease(string path,long size,long ticks,string identity){
            var stream=new FileStream(Paths.Io(path),FileMode.Open,FileAccess.Read,FileShare.Read);try{var stamp=FileAccessNative.Stamp(stream);
                if(stamp.Size!=size || stamp.ModifiedTicks!=ticks || stamp.Identity!=identity)throw new InvalidDataException("Source changed after full verification. Scan its current contents before applying: "+path);return stream;
            }catch{stream.Dispose();throw;}
        }
        void VerificationRetry(Action body,string path,CancellationToken token){
            for(int attempt=0;;attempt++){token.ThrowIfCancellationRequested();try{body();return;}catch(Exception error){
                if(error is OperationCanceledException || !FileAccessNative.Transient(error) || attempt>=3)throw;
                WaitWrite(w=>{Catalog.Log(w,"retry","Verification source",path,"Retry "+(attempt+1)+" of 3: "+error.Message);return null;});
                if(token.WaitHandle.WaitOne(attempt==0 ? 250 : attempt==1 ? 1000 : 3000))token.ThrowIfCancellationRequested();
            }}
        }
        static FileStream VerifiedSourceLease(string path,long size,long ticks,string identity,string root){var stream=VerifiedLease(path,size,ticks,identity);try{Paths.OpenedSourceWithinRoot(stream,root);return stream;}catch{stream.Dispose();throw;}}
        void VerificationPreviews(SqliteConnection reader,IdentityResolutionPreview p,CancellationToken token,Action<long,long,string> progress){
            string root=(string)reader.Scalar("SELECT roots.path FROM source_roots roots JOIN identity_runs r ON r.root_id=roots.id WHERE r.id=?",p.RunId);long done=0;
            foreach(var outcome in p.Outcomes){
                token.ThrowIfCancellationRequested();long content=Convert.ToInt64(reader.Scalar("SELECT content_id FROM identity_plan WHERE run_id=? AND digest=?",p.RunId,outcome.Hash));
                if(outcome.Status!="EVIDENCE" && reader.Scalar("SELECT 1 FROM previews WHERE content_id=? AND state='available'",content)==null){
                    var check=reader.Query("SELECT path,size,modified_ticks,file_identity FROM identity_checks WHERE run_id=? AND digest=? ORDER BY occurrence_order LIMIT 1",new object[]{p.RunId,outcome.Hash},1)[0];string path=Path.Combine(root,(string)check[0]);PreviewResult preview=null;Exception previewError=null;
                    VerificationRetry(()=>{using(var lease=VerifiedSourceLease(path,(long)check[1],(long)check[2],(string)check[3],root)){
                        try{preview=Preview.CreateExpectedScoped(path,(long)check[1],(long)check[2],(string)check[3],256,32768,token,root);}catch(OperationCanceledException){throw;}catch(Exception error){if(FileAccessNative.Transient(error))throw;previewError=error;}
                    }},path,token);
                    token.ThrowIfCancellationRequested();WaitWrite(w=>{Verification.Transaction(w,()=>{
                        Verification.CheckRevision(w,p);
                        if(preview!=null)w.Execute("INSERT INTO previews(content_id,state,mime,width,height,bytes,decoder) VALUES(?,'available',?,?,?,?,?) ON CONFLICT(content_id) DO UPDATE SET state='available',mime=excluded.mime,width=excluded.width,height=excluded.height,bytes=excluded.bytes,error=NULL,decoder=excluded.decoder",content,preview.Mime,preview.Width,preview.Height,preview.Bytes,preview.Decoder);
                        else {w.Execute("INSERT INTO previews(content_id,state,error,decoder) VALUES(?,'unavailable',?,'Verified source preview') ON CONFLICT(content_id) DO UPDATE SET state='unavailable',error=excluded.error WHERE previews.state<>'available'",content,previewError==null ? "Preview unavailable" : previewError.Message);Catalog.Log(w,"warning","Verification preview",path,previewError==null ? "Preview unavailable; full fingerprint retained" : previewError.GetType().Name+": "+previewError.Message);}
                    });return null;},65536);
                }done++;progress(done,p.Outcomes.Count,"verified previews checked");
            }
        }
        public string RequestFullVerification(long groupId,string occurrenceId){
            if(String.IsNullOrEmpty(occurrenceId))occurrenceId=null;
            if(groupId<=0 || (occurrenceId!=null && occurrenceId.Length>131072))throw new ArgumentException("Choose a valid group/location");
            return EnqueueOperation("FullVerification",(db,token,progress)=>VerifyFullCore(db,groupId,occurrenceId,token,progress),2048+(occurrenceId==null ? 0 : occurrenceId.Length*2));
        }
        IdentityResolutionPreview VerifyFullCore(SqliteConnection db,long groupId,string occurrenceId,CancellationToken token,Action<long,long,string> progress){
                var source=Verification.Source(db,groupId,true);var candidatePolicy=Verification.CandidatePolicy(source);string run=Guid.NewGuid().ToString("N"),root=(string)source[1],rootPath=(string)source[5];long content=(long)source[0];
                WaitWrite(w=>{w.Execute("INSERT INTO identity_runs(id,group_id,source_content,root_id,state,created_at) VALUES(?,?,?,?,'checking',?)",run,groupId,content,root,Catalog.Now());return null;});
                long after=0,done=0,total=Convert.ToInt64(db.Scalar("SELECT COALESCE(SUM(size+MIN(?,size/?)*?),0) FROM group_occurrences WHERE group_id=? AND superseded=0 AND (? IS NULL OR id=?)",candidatePolicy.MaxWindowBytes,candidatePolicy.WindowCount*2,candidatePolicy.WindowCount,groupId,occurrenceId,occurrenceId));byte[] buffer=new byte[1024*1024];
                while(true){
                    token.ThrowIfCancellationRequested();var rows=db.Query("SELECT o.id,o.rowid,o.path,o.size,o.modified_ticks,o.file_identity FROM bound_occurrences o WHERE o.root_id=? AND o.content_id=? AND o.superseded=0 AND o.rowid>? AND (? IS NULL OR o.id=?) AND NOT EXISTS(SELECT 1 FROM resolved_locations l JOIN identity_runs r ON r.id=l.run_id AND r.state='published' WHERE l.occurrence_id=o.id) ORDER BY o.rowid LIMIT 1",new object[]{root,content,after,occurrenceId,occurrenceId},1);
                    if(rows.Count==0)break;var row=rows[0];after=(long)row[1];string relative=(string)row[2];FileRecord record=null;
                    for(int attempt=0;;attempt++){
                        token.ThrowIfCancellationRequested();try{
                            string path=Catalog.PreviewSourcePath(rootPath,relative);
                            using(var lease=new FileStream(Paths.Io(path),FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan)){
                                Paths.OpenedSourceWithinRoot(lease,rootPath);if(FileAccessNative.Stamp(lease).Size!=(long)source[9])throw new InvalidDataException("Source no longer matches the saved candidate size. Scan its current contents before verification: "+relative);
                                Action<int> readProgress=n=>{done+=n;progress(done,total,"verification bytes read");};
                                record=FileAccessNative.FingerprintOpened(path,lease,new FileRecord(),buffer,token,readProgress,0,FingerprintPolicy.Full());
                                var candidate=FileAccessNative.FingerprintOpened(path,lease,new FileRecord(),buffer,token,readProgress,0,candidatePolicy);
                                if(candidate==null || candidate.Algorithm!="sampled-sha256-v1" || !String.Equals(candidate.Hash,"q2:"+(string)source[7],StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Source no longer matches the saved candidate fingerprint. Scan its current contents before verification: "+relative);
                            }
                            if(record==null || record.Size!=(long)row[3] || (row[5]==null ? record.ModifiedTicks/10000!=(long)row[4]/10000 : record.ModifiedTicks!=(long)row[4] || record.FileIdentity!=(string)row[5]))throw new InvalidDataException("Source no longer matches the recorded location. Scan its current contents before verification: "+relative);break;
                        }catch(Exception error){if(error is OperationCanceledException || !FileAccessNative.Transient(error) || attempt>=3)throw;
                            WaitWrite(w=>{Catalog.Log(w,"retry","Full verification",Path.Combine(rootPath,relative),"Retry "+(attempt+1)+" of 3: "+error.Message);return null;});
                            if(token.WaitHandle.WaitOne(attempt==0 ? 250 : attempt==1 ? 1000 : 3000))token.ThrowIfCancellationRequested();
                        }
                    }
                    token.ThrowIfCancellationRequested();WaitWrite(w=>{Verification.Transaction(w,()=>{
                        var current=Verification.Source(w,groupId);if((long)current[0]!=content)throw new InvalidOperationException("Candidate identity changed while reading");
                        if(w.Scalar("SELECT 1 FROM bound_occurrences WHERE root_id=? AND content_id=? AND id=? AND superseded=0 AND path=? AND size=? AND modified_ticks=? AND file_identity IS ?",root,content,row[0],row[2],row[3],row[4],row[5])==null)throw new InvalidOperationException("Location changed while reading");
                        w.Execute("INSERT INTO identity_checks VALUES(?,?,?,?,?,?,?,?,?,?)",run,row[0],row[1],row[2],record.Size,row[4],row[5],record.ModifiedTicks,record.FileIdentity,record.Hash);
                    });return null;},2048+relative.Length*2);
                }
                token.ThrowIfCancellationRequested();return (IdentityResolutionPreview)WaitWrite(w=>(object)Verification.Prepare(w,run,groupId));
        }
        public string RequestIdentityResolution(IdentityResolutionPreview preview,string operationId){
            if(preview==null || String.IsNullOrEmpty(preview.RunId) || preview.RunId.Length>64 || String.IsNullOrEmpty(preview.Token) || preview.Token.Length>64 || String.IsNullOrEmpty(operationId) || operationId.Length>100)throw new ArgumentException("A valid preview and operation identifier are required");
            var request=preview.RequestCopy();return EnqueueOperation("IdentityResolution",(db,token,progress)=>ResolveIdentityCore(db,request,operationId,token,progress));
        }
        IdentityResolutionReceipt ResolveIdentityCore(SqliteConnection db,IdentityResolutionPreview request,string operationId,CancellationToken token,Action<long,long,string> progress){
                string payload=Verification.Payload(request);
                if((bool)WaitWrite(w=>(object)Verification.Reconciled(w,operationId,payload)))return new IdentityResolutionReceipt {OperationId=operationId,Reconciled=true,Message="Previously committed identity resolution confirmed."};
                var p=(IdentityResolutionPreview)WaitWrite(w=>(object)Verification.Load(w,request));
                for(int index=0;index<p.Outcomes.Count;index+=16){token.ThrowIfCancellationRequested();var batch=p.Outcomes.Skip(index).Take(16).ToArray();WaitWrite(w=>{Verification.StagePlans(w,p,batch);return null;},65536);}
                VerificationPreviews(db,p,token,progress);
                string root=(string)db.Scalar("SELECT roots.path FROM source_roots roots JOIN identity_runs r ON r.root_id=roots.id WHERE r.id=?",p.RunId);
                long after=0,staged=0;while(true){token.ThrowIfCancellationRequested();var rows=db.Query("SELECT occurrence_order,path,size,modified_ticks,file_identity FROM identity_checks WHERE run_id=? AND occurrence_order>? ORDER BY occurrence_order LIMIT 32",new object[]{p.RunId,after},32);if(rows.Count==0)break;
                    foreach(var row in rows){string file=Path.Combine(root,(string)row[1]);VerificationRetry(()=>{using(var lease=VerifiedSourceLease(file,(long)row[2],(long)row[3],(string)row[4],root)){}},file,token);}
                    long start=after;WaitWrite(w=>{Verification.StageLocations(w,p,start,token);return null;},65536);after=(long)rows[rows.Count-1][0];staged+=rows.Count;progress(staged,p.Locations,"locations prepared");}
                return (IdentityResolutionReceipt)WaitWrite(w=>(object)Verification.Publish(w,p,operationId,payload,token),1024*1024);
        }
        public string RequestIdentityHistory(long groupId){return EnqueueOperation("IdentityHistory",(db,token,progress)=>{
            var text=new StringBuilder("Identity history (up to 32 related groups; notes shown up to 2048 characters)\r\n\r\n");
            foreach(var row in db.Query(@"SELECT DISTINCT g.id,g.status,g.retired,CASE WHEN g.status='EVIDENCE' THEN '' ELSE substr(g.reviewer,1,128) END,CASE WHEN g.status='EVIDENCE' THEN '[Protected Evidence]' ELSE substr(g.notes,1,2048) END,g.display_sequence FROM review_groups g WHERE g.id=? OR g.id IN(SELECT r.group_id FROM identity_runs r JOIN identity_plan p ON p.run_id=r.id WHERE p.group_id=? AND r.state='published') OR g.id IN(SELECT p.group_id FROM identity_runs r JOIN identity_plan p ON p.run_id=r.id WHERE r.group_id=? AND r.state='published') ORDER BY CASE WHEN g.id=? THEN 0 WHEN g.retired=1 THEN 1 ELSE 2 END,g.display_sequence LIMIT 32",new object[]{groupId,groupId,groupId,groupId},32)){
                token.ThrowIfCancellationRequested();text.Append("Group ").Append(row[0]).Append(" · ").Append(row[1]).Append((long)row[2]!=0 ? " (archived review)" : "").Append("\r\nReviewer: ").Append(row[3]).Append("\r\nNotes: ").Append(row[4]).Append("\r\n\r\n");}
            return text.ToString();});}
    }
}
