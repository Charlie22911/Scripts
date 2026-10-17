using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
namespace MediaReviewer {
    public sealed class EvidenceInspection:IDisposable {
        public long GroupId,Epoch,Size; public string Hash; public bool Verified;
        public byte[] Metadata,Preview;
        internal string LegacyKey;
        public System.Windows.Media.Imaging.BitmapSource PreviewImage;public string PreviewError;
        public void Dispose(){var metadata=Interlocked.Exchange(ref Metadata,null);var preview=Interlocked.Exchange(ref Preview,null);if(metadata!=null)Array.Clear(metadata,0,metadata.Length);if(preview!=null)Array.Clear(preview,0,preview.Length);PreviewImage=null;}
    }
    internal sealed class OriginalReceipt {internal string OperationId,Message,Kind="Review";internal long[] GroupIds;internal bool Reconciled,OutputPublished;}
    internal static class EvidenceFormat {
        internal const int ChunkSize=4*1024*1024;
        internal static string Quote(string s){var b=new StringBuilder("\"");foreach(char c in s??""){if(c=='\"' || c=='\\')b.Append('\\').Append(c);else if(c<32){switch(c){case '\b':b.Append("\\b");break;case '\f':b.Append("\\f");break;case '\n':b.Append("\\n");break;case '\r':b.Append("\\r");break;case '\t':b.Append("\\t");break;default:b.Append("\\u").Append(((int)c).ToString("x4"));break;}}else b.Append(c);}return b.Append('"').ToString();}
        internal static byte[] CheckAad(string ws){return Encoding.UTF8.GetBytes("{\"version\":1,\"workspaceId\":"+Quote(ws)+",\"purpose\":\"vault-check\"}");}
        internal static byte[] Aad(string ws,string key,string hash,string purpose,int version,string capture,long size,int count,int index=-1){
            string value="{\"version\":"+version+",\"workspaceId\":"+Quote(ws)+",\"decisionKey\":"+Quote(key)+",\"hash\":"+Quote(hash);
            if(version>=2)value+=",\"purpose\":"+Quote(purpose)+",\"index\":"+(index<0 ? "null" : index.ToString(CultureInfo.InvariantCulture))+",\"count\":"+(index<0 ? "null" : count.ToString(CultureInfo.InvariantCulture));
            if(version==3)value+=",\"captureId\":"+Quote(capture)+",\"originalSize\":"+size.ToString(CultureInfo.InvariantCulture)+",\"chunkSize\":"+ChunkSize+",\"chunkCount\":"+count;
            return Encoding.UTF8.GetBytes(value+"}");
        }
        internal static byte[] Random(int n){var b=new byte[n];using(var random=RandomNumberGenerator.Create())random.GetBytes(b);return b;}
        internal static string Hex(byte[] b){return BitConverter.ToString(b).Replace("-","").ToLowerInvariant();}
        internal static JavaScriptSerializer Json(){return new JavaScriptSerializer {MaxJsonLength=1024*1024,RecursionLimit=64};}
        internal static string Workspace(SqliteConnection db){return (string)db.Scalar("SELECT value FROM app_info WHERE key='workspace_id'");}
        internal static string Payload(ReviewRequest r){return Identity.Digest(r.Status+"\n"+Quote(r.Reviewer)+"\n"+Quote(r.Notes)+"\n"+String.Join(",",r.GroupIds));}
        internal static void Validate(ReviewRequest r,bool reclassify=false){if(r==null || r.GroupIds==null || r.GroupIds.Length<1 || r.GroupIds.Length>200 || r.GroupIds.Any(x=>x<=0) || (r.ExpectedRevisions!=null && r.ExpectedRevisions.Count>200) || String.IsNullOrEmpty(r.OperationId) || r.OperationId.Length>100 || !(reclassify ? new[]{"TO_REVIEW","COMPLIANT","NON_COMPLIANT"} : new[]{"NON_COMPLIANT","EVIDENCE"}).Contains(r.Status) || r.Notes==null || r.Notes.Length>16384 || r.Reviewer==null || r.Reviewer.Length>128)throw new ArgumentException("Invalid bounded original-retention or Evidence action");}
        internal sealed class HashSink:Stream {
            readonly Stream output;readonly SHA256 sha=SHA256.Create();internal long Count;bool finished;
            internal HashSink(Stream output=null){this.output=output;}
            internal string Finish(){if(!finished){sha.TransformFinalBlock(new byte[0],0,0);finished=true;}return Hex(sha.Hash);}
            public override void Write(byte[] bytes,int offset,int count){sha.TransformBlock(bytes,offset,count,bytes,offset);if(output!=null)output.Write(bytes,offset,count);Count+=count;}
            public override bool CanRead{get{return false;}}public override bool CanWrite{get{return true;}}public override bool CanSeek{get{return false;}}public override long Length{get{return Count;}}public override long Position{get{return Count;}set{throw new NotSupportedException();}}
            public override void Flush(){if(output!=null)output.Flush();}public override int Read(byte[] b,int o,int n){throw new NotSupportedException();}public override long Seek(long n,SeekOrigin s){throw new NotSupportedException();}public override void SetLength(long n){throw new NotSupportedException();}protected override void Dispose(bool disposing){sha.Dispose();base.Dispose(disposing);}
        }
        internal sealed class CappedSink:Stream {
            internal readonly MemoryStream Buffer=new MemoryStream();internal long Count;readonly int limit;
            internal CappedSink(int limit=1024*1024){this.limit=limit;}
            internal byte[] Result(){return Count<=limit ? Buffer.ToArray() : Encoding.UTF8.GetBytes("{\"displayNotice\":\"Authenticated metadata exceeds the display limit; its original encrypted bytes are preserved.\"}");}
            public override void Write(byte[] b,int offset,int count){int take=(int)Math.Min(count,Math.Max(0,limit-Count));if(take>0)Buffer.Write(b,offset,take);Count+=count;}
            public override bool CanRead{get{return false;}}public override bool CanWrite{get{return true;}}public override bool CanSeek{get{return false;}}public override long Length{get{return Count;}}public override long Position{get{return Count;}set{throw new NotSupportedException();}}
            public override void Flush(){}public override int Read(byte[] b,int o,int n){throw new NotSupportedException();}public override long Seek(long n,SeekOrigin s){throw new NotSupportedException();}public override void SetLength(long n){throw new NotSupportedException();}protected override void Dispose(bool disposing){Array.Clear(Buffer.GetBuffer(),0,(int)Buffer.Length);Buffer.Dispose();base.Dispose(disposing);}
        }
        internal sealed class LegacySink:Stream {
            readonly byte[] prefix=new byte[4];int filled;long remaining=-1;readonly long total;internal readonly CappedSink Metadata=new CappedSink();internal readonly HashSink Original;
            internal LegacySink(long total,Stream output){this.total=total;Original=new HashSink(output);}
            public override void Write(byte[] b,int at,int count){
                while(filled<4 && count>0){prefix[filled++]=b[at++];count--;}
                if(filled<4)return;if(remaining<0){remaining=((long)prefix[0]<<24)|((long)prefix[1]<<16)|((long)prefix[2]<<8)|prefix[3];if(remaining>total-4)throw new InvalidDataException("Legacy Evidence metadata length is invalid");}
                int take=(int)Math.Min(count,remaining);if(take>0){Metadata.Write(b,at,take);remaining-=take;at+=take;count-=take;}if(count>0)Original.Write(b,at,count);
            }
            internal void Complete(){if(filled!=4 || remaining!=0)throw new InvalidDataException("Truncated legacy Evidence container");}
            public override bool CanRead{get{return false;}}public override bool CanWrite{get{return true;}}public override bool CanSeek{get{return false;}}public override long Length{get{return total;}}public override long Position{get{return Original.Count;}set{throw new NotSupportedException();}}
            public override void Flush(){}public override int Read(byte[] b,int o,int n){throw new NotSupportedException();}public override long Seek(long n,SeekOrigin s){throw new NotSupportedException();}public override void SetLength(long n){throw new NotSupportedException();}protected override void Dispose(bool disposing){Metadata.Dispose();Original.Dispose();Array.Clear(prefix,0,4);base.Dispose(disposing);}
        }
    }
    public sealed partial class ReviewerEngine {
        VaultKey vaultKey;volatile bool vaultUnlocked,vaultExists;long vaultEpoch;
        readonly object vaultStateGate=new object(),vaultResultsGate=new object();readonly List<EvidenceInspection> vaultResults=new List<EvidenceInspection>();
        public bool VaultUnlocked {get{return vaultUnlocked;}}public bool VaultExists{get{return vaultExists;}}public long VaultGeneration{get{return Interlocked.Read(ref vaultEpoch);}}
        void ClearVaultWorker(){if(vaultKey!=null){vaultKey.Dispose();vaultKey=null;}}
        void RequireVault(long epoch,CancellationToken token){token.ThrowIfCancellationRequested();if(!vaultUnlocked || epoch!=VaultGeneration || vaultKey==null)throw new InvalidOperationException("Unlock the originals vault before continuing.");}
        long InvalidateVault(){long epoch;lock(vaultStateGate){vaultUnlocked=false;epoch=Interlocked.Increment(ref vaultEpoch);}lock(vaultResultsGate){foreach(var result in vaultResults)result.Dispose();vaultResults.Clear();}lock(operationGate){foreach(var w in operationStates.Values)if(new[]{"Capture","EvidenceInspect","OriginalExport","VaultUnlock","VaultPasswordChange","EvidenceReclassify","EvidencePackageExport"}.Contains(w.Progress.Kind))CancelWork(w);}return epoch;}
        void TrackProtectedResult(EvidenceInspection result){lock(vaultResultsGate){if(!vaultUnlocked || result.Epoch!=VaultGeneration){result.Dispose();return;}vaultResults.RemoveAll(x=>x.Metadata==null);if(vaultResults.Count>=16){vaultResults[0].Dispose();vaultResults.RemoveAt(0);}vaultResults.Add(result);}}
        public string RequestVaultLock(){long epoch=InvalidateVault();return EnqueueOperation("VaultLock",(db,t,p)=>{lock(vaultStateGate){if(epoch==VaultGeneration){vaultUnlocked=false;ClearVaultWorker();}}return "Originals locked; secret views cleared.";});}
        public string RequestVaultUnlock(byte[] passwordUtf8,bool createIfMissing){
            if(passwordUtf8==null || passwordUtf8.Length<1 || passwordUtf8.Length>65536){if(passwordUtf8!=null)Array.Clear(passwordUtf8,0,passwordUtf8.Length);throw new ArgumentException("Enter a bounded non-empty password");}
            long epoch=InvalidateVault();
            return EnqueueOperation("VaultUnlock",(db,token,progress)=>{
                lock(vaultStateGate){token.ThrowIfCancellationRequested();if(epoch!=VaultGeneration)throw new OperationCanceledException();ClearVaultWorker();}VaultKey derived=null;try{
                    bool creating=db.Scalar("SELECT 1 FROM evidence_vault WHERE id=1")==null;
                    if(creating && !createIfMissing)throw new InvalidOperationException("Create an originals vault first.");
                    byte[] salt=creating ? EvidenceFormat.Random(16) : null;int iterations=600000;
                    progress(0,1,"password key derivation");derived=creating ? Crypto.DeriveKey(passwordUtf8,salt,iterations) : OpenVaultKey(db,passwordUtf8,token);token.ThrowIfCancellationRequested();
                    string ws=EvidenceFormat.Workspace(db);byte[] expected=Encoding.UTF8.GetBytes("ImageComplianceReviewer evidence vault v1");
                    if(creating){byte[] iv=EvidenceFormat.Random(12),cipher=Crypto.Encrypt(derived,iv,EvidenceFormat.CheckAad(ws),expected);
                        WaitWrite(w=>{token.ThrowIfCancellationRequested();Verification.Transaction(w,()=>{w.Execute("INSERT INTO evidence_vault VALUES(1,?,?,?,?,?)",salt,iterations,iv,cipher,Catalog.Now());Catalog.Bump(w);});return null;});vaultExists=true;
                    }
                    lock(vaultStateGate){token.ThrowIfCancellationRequested();if(epoch!=VaultGeneration)throw new OperationCanceledException();vaultKey=derived;derived=null;vaultUnlocked=true;}progress(1,1,"vault unlocked");return "Originals vault unlocked.";
                }finally{if(derived!=null)derived.Dispose();Array.Clear(passwordUtf8,0,passwordUtf8.Length);}
            },1024+passwordUtf8.Length,()=>Array.Clear(passwordUtf8,0,passwordUtf8.Length));
        }
        public string RequestEvidenceLimit(double gigabytes){if(gigabytes!=2.5 && gigabytes!=5)throw new ArgumentException("Choose a 2.5 GB or 5 GB retained-original guardrail");return EnqueueOperation("EvidenceLimit",(db,t,p)=>{WaitWrite(w=>{w.Execute("INSERT INTO settings VALUES('original_limit_bytes',?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",((long)(gigabytes*1000000000)).ToString(CultureInfo.InvariantCulture));return null;});return "Retained-original guardrail: "+gigabytes.ToString(CultureInfo.InvariantCulture)+" GB. Ordinary scanning is unaffected.";});}
        void Capacity(SqliteConnection db,long additional){
            long limit=Convert.ToInt64(db.Scalar("SELECT COALESCE((SELECT value FROM settings WHERE key='original_limit_bytes'),'2500000000')"),CultureInfo.InvariantCulture);
            long allocated=checked(Convert.ToInt64(db.Scalar("PRAGMA page_count"))*Convert.ToInt64(db.Scalar("PRAGMA page_size")));long wal=0;try{var f=new FileInfo(Paths.Io(path+"-wal"));if(f.Exists)wal=f.Length;}catch(IOException){}
            if(additional<0 || additional>limit || allocated>limit-additional || wal>limit-allocated-additional)throw new IOException("Retaining these originals would exceed the selected database guardrail. Choose 5 GB or another database. Scanning has no such limit.");
            var drive=new DriveInfo(Path.GetPathRoot(path));if(drive.AvailableFreeSpace<checked(additional*2+64L*1024*1024))throw new IOException("Insufficient free space for retained originals and SQLite working space; no review was published.");
        }
        internal void AbandonRequest(string operation,CancellationToken token){
            // Only the matching unfinished operation is eligible. Each deletion is bounded to one chunk.
            while(true){token.ThrowIfCancellationRequested();bool deleted=(bool)WaitWrite(w=>{var rows=w.Query("SELECT c.id FROM original_captures c WHERE c.request_id=? AND c.state<>'complete' LIMIT 1",new object[]{operation},1);if(rows.Count==0)return false;string id=(string)rows[0][0];Verification.Transaction(w,()=>{w.Execute("UPDATE original_captures SET state='abandoned' WHERE id=?",id);object index=w.Scalar("SELECT chunk_index FROM original_chunks WHERE capture_id=? LIMIT 1",id);if(index!=null)w.Execute("DELETE FROM original_chunks WHERE capture_id=? AND chunk_index=?",id,index);else{w.Execute("DELETE FROM original_targets WHERE capture_id=?",id);w.Execute("DELETE FROM original_captures WHERE id=?",id);}});return true;});if(!deleted)break;}
            WaitWrite(w=>{w.Execute("DELETE FROM original_targets WHERE request_id=?",operation);return null;});
        }
        public string RequestOriginalCleanup(){return EnqueueOperation("OriginalCleanup",(db,t,p)=>{long done=0;while(true){t.ThrowIfCancellationRequested();string request=(string)db.Scalar("SELECT request_id FROM original_captures WHERE state<>'complete' LIMIT 1");if(request==null)break;AbandonRequest(request,t);p(++done,-1,"unfinished requests discarded");}return "Discarded hidden unfinished captures. Completed originals and reviews were retained.";});}
        public static void ValidateOriginalRetention(ReviewRequest request){EvidenceFormat.Validate(request);}
        public string RequestOriginalRetention(ReviewRequest request){
            EvidenceFormat.Validate(request);var r=request.Copy();r.GroupIds=r.GroupIds.Distinct().OrderBy(x=>x).ToArray();long epoch=VaultGeneration;
            return EnqueueOperation("Capture",(db,token,progress)=>{
                string payload=EvidenceFormat.Payload(r);if((bool)WaitWrite(w=>(object)Verification.Reconciled(w,r.OperationId,payload)))return new OriginalReceipt {OperationId=r.OperationId,GroupIds=r.GroupIds,Reconciled=true,Message="Previously committed original retention confirmed."};
                RequireVault(epoch,token);WaitWrite(w=>{object old=w.Scalar("SELECT payload_hash FROM original_requests WHERE id=?",r.OperationId);if(old!=null && (string)old!=payload)throw new InvalidOperationException("Operation identifier has a different payload");w.Execute("INSERT OR IGNORE INTO original_requests VALUES(?,?,'staging',?)",r.OperationId,payload,Catalog.Now());return null;});
                AbandonRequest(r.OperationId,token);int completed=0;
                foreach(long group in r.GroupIds){RequireVault(epoch,token);
                    if((string)db.Scalar("SELECT status FROM review_groups WHERE id=?",group)=="EVIDENCE" && r.Status!="EVIDENCE")throw new InvalidOperationException("Use the authenticated Evidence reclassification action to leave Evidence.");
                    long expectedRevision=0;bool promoted=false;if(r.ExpectedRevisions!=null && (!r.ExpectedRevisions.TryGetValue(group,out expectedRevision) || Convert.ToInt64(db.Scalar("SELECT review_revision FROM review_groups WHERE id=?",group))!=expectedRevision))throw new InvalidOperationException("A selected review changed after this action was started. Refresh its card before retaining the original.");
                    var legacy=db.Query("SELECT decision_key,hash,size FROM legacy_original_links WHERE group_id=?",new object[]{group},1);
                    string algorithm=(string)db.Scalar("SELECT c.algorithm FROM review_groups g JOIN contents c ON c.id=g.content_id WHERE g.id=? AND g.retired=0",group);
                    if(algorithm==null)throw new InvalidOperationException("A selected card was replaced; refresh the page.");
                    if(algorithm!="sha256" && legacy.Count==0){
                        var preview=VerifyFullCore(db,group,null,token,progress);
                        if(!preview.CanApply || preview.Outcomes.Count!=1 || preview.Outcomes[0].ExistingGroupId!=0)throw new InvalidOperationException("Resolve this candidate with Verify full before retaining originals; differing contents or an existing full group require an explicit choice.");
                        ResolveIdentityCore(db,preview,Identity.Digest(r.OperationId+"\nverify\n"+group),token,progress);promoted=true;
                    }
                    var info=db.Query("SELECT g.content_id,g.legacy_key,c.digest,c.size,g.status,g.reviewed_at,g.review_revision FROM review_groups g JOIN contents c ON c.id=g.content_id WHERE g.id=? AND g.retired=0",new object[]{group},1)[0];
                    if(r.ExpectedRevisions!=null && (long)info[6]!=expectedRevision+(promoted ? 1 : 0))throw new InvalidOperationException("A selected review changed while its identity was being checked; the newer decision was preserved.");
                    string capture=(string)db.Scalar("SELECT l.capture_id FROM original_links l JOIN original_captures c ON c.id=l.capture_id AND c.state='complete' WHERE l.group_id=? AND c.content_id=?",group,info[0]);
                    if(capture!=null || legacy.Count!=0){using(var verified=ReadOriginal(db,group,epoch,token,progress,null)){};}
                    else capture=CaptureOriginal(db,r,group,info,epoch,token,progress);
                    string saved=capture;WaitWrite(w=>{w.Execute("INSERT INTO original_targets VALUES(?,?,?,?,?,?,?,?,?,?)",r.OperationId,group,saved,info[0],info[4],info[5],info[6],legacy.Count==0 ? null : legacy[0][0],legacy.Count==0 ? null : legacy[0][1],legacy.Count==0 ? null : legacy[0][2]);return null;});progress(++completed,r.GroupIds.Length,"originals prepared");
                }
                RequireVault(epoch,token);WaitWrite(w=>{Verification.Transaction(w,()=>{
                    RequireVault(epoch,token);if(Verification.Reconciled(w,r.OperationId,payload))return;
                    var targets=w.Query("SELECT group_id,capture_id,content_id,previous_status,reviewed_at,review_revision,legacy_key,original_hash,original_size FROM original_targets WHERE request_id=?",new object[]{r.OperationId},200);
                    if(targets.Count!=r.GroupIds.Length)throw new InvalidDataException("Original preparation is incomplete");
                    foreach(var row in targets){long group=(long)row[0];if(w.Scalar("SELECT 1 FROM review_groups WHERE id=? AND retired=0 AND content_id=? AND status=? AND reviewed_at IS ? AND review_revision=?",group,row[2],row[3],row[4],row[5])==null)throw new InvalidOperationException("A selected review changed during capture; its newer decision was preserved.");
                        if(row[1]!=null){if(w.Scalar("SELECT 1 FROM original_captures WHERE id=? AND state IN('verified','complete')",row[1])==null)throw new InvalidDataException("A prepared original is not verified");w.Execute("UPDATE original_captures SET state='complete' WHERE id=?",row[1]);w.Execute("INSERT INTO original_links VALUES(?,?) ON CONFLICT(group_id) DO UPDATE SET capture_id=excluded.capture_id",group,row[1]);}
                        else if(w.Scalar("SELECT 1 FROM legacy_original_links WHERE group_id=? AND decision_key=? AND hash=? AND size=?",group,row[6],row[7],row[8])==null)throw new InvalidDataException("The authenticated imported original link changed");
                        w.Execute("UPDATE review_groups SET status=?,reviewer=?,notes=?,reviewed_at=? WHERE id=?",r.Status,r.Reviewer,r.Notes,Catalog.Now(),group);ImportedFields.Delete(w,"review_groups",group,"notes");ImportedFields.Delete(w,"review_groups",group,"reviewer");
                        w.Execute("INSERT INTO review_events VALUES(?,?,?,?,?,?,?,?,?)",Guid.NewGuid().ToString(),group,w.Scalar("SELECT legacy_key FROM review_groups WHERE id=?",group),row[3],r.Status,Catalog.Now(),r.Reviewer,r.Notes,r.OperationId);
                    }
                    w.Execute("UPDATE original_requests SET state='committed' WHERE id=?",r.OperationId);w.Execute("INSERT INTO operations VALUES(?,?,?,?)",r.OperationId,payload,Catalog.Now(),Catalog.Bump(w));Catalog.Log(w,"info","Original retention","","Committed "+targets.Count+" verified encrypted originals; operation "+r.OperationId);
                });return null;});return new OriginalReceipt {OperationId=r.OperationId,GroupIds=r.GroupIds,Message="Committed verified encrypted originals."};
            },4096+(r.Notes.Length+r.Reviewer.Length)*2+r.GroupIds.Length*48);
        }
        string CaptureOriginal(SqliteConnection db,ReviewRequest r,long group,object[] info,long epoch,CancellationToken token,Action<long,long,string> progress){
            long size=(long)info[3];if(size<0 || size>5L*1000000000)throw new IOException("Original exceeds the available capture guardrails; scanning is unaffected.");
            int count=checked((int)((size+EvidenceFormat.ChunkSize-1)/EvidenceFormat.ChunkSize));Capacity(db,checked(size+size/20+count*4096L+1024*1024));
            var sources=db.Query("SELECT roots.path,o.path,o.size,o.modified_ticks,o.file_identity FROM group_occurrences o JOIN source_roots roots ON roots.id=o.root_id WHERE o.group_id=? AND o.superseded=0 ORDER BY o.occurrence_order LIMIT 1",new object[]{group},1);
            if(sources.Count==0)throw new FileNotFoundException("No current source is available for original retention.");var source=sources[0];string sourcePath=Catalog.PreviewSourcePath((string)source[0],(string)source[1]),capture=Guid.NewGuid().ToString("N"),ws=EvidenceFormat.Workspace(db),key=(string)info[1],hash=(string)info[2];
            WaitWrite(w=>{w.Execute("INSERT INTO original_captures(id,request_id,group_id,content_id,decision_key,hash,size,chunk_count,state,created_at) VALUES(?,?,?,?,?,?,?,?,'staging',?)",capture,r.OperationId,group,info[0],key,hash,size,count,Catalog.Now());return null;});
            // A retry reopens the source. Existing chunks are create-once; this attempt replaces only its staging rows.
            VerificationRetry(()=>{
                using(var stream=new FileStream(Paths.Io(sourcePath),FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan))using(var sha=SHA256.Create()){
                    Paths.OpenedSourceWithinRoot(stream,(string)source[0]);var before=FileAccessNative.Stamp(stream);if(before.Size!=size || (source[4]==null ? before.ModifiedTicks/10000!=(long)source[3]/10000 : before.ModifiedTicks!=(long)source[3] || before.Identity!=(string)source[4]))throw new InvalidDataException("Source changed before retention; scan its current contents.");
                    byte[] plain=new byte[EvidenceFormat.ChunkSize];long done=0;
                    try{for(int index=0;index<count;index++){
                        RequireVault(epoch,token);int length=(int)Math.Min(plain.Length,size-done),read=0;while(read<length){int n=stream.ReadAsync(plain,read,length-read,token).GetAwaiter().GetResult();if(n==0)throw new InvalidDataException("Source ended during retention");read+=n;}
                        sha.TransformBlock(plain,0,length,plain,0);byte[] part=plain;if(length!=plain.Length){part=new byte[length];Buffer.BlockCopy(plain,0,part,0,length);}byte[] iv=EvidenceFormat.Random(12),cipher;
                        try{cipher=Crypto.Encrypt(vaultKey,iv,EvidenceFormat.Aad(ws,key,hash,"chunk",3,capture,size,count,index),part);}finally{if(part!=plain)Array.Clear(part,0,part.Length);Array.Clear(plain,0,plain.Length);}
                        int at=index;WaitWrite(w=>{RequireVault(epoch,token);Capacity(w,Math.Max(0,size-done-length)+1024*1024);Verification.Transaction(w,()=>{w.Execute("INSERT INTO original_chunks VALUES(?,?,?,?) ON CONFLICT(capture_id,chunk_index) DO UPDATE SET iv=excluded.iv,ciphertext=excluded.ciphertext",capture,at,iv,cipher);});return null;},cipher.Length+4096);done+=length;progress(done,size,"original bytes encrypted");
                    }
                    sha.TransformFinalBlock(new byte[0],0,0);if(done!=size || !before.Same(FileAccessNative.Stamp(stream)) || EvidenceFormat.Hex(sha.Hash)!=hash)throw new InvalidDataException("Source bytes no longer match the verified full SHA-256; the original and decision were not published.");
                    }finally{Array.Clear(plain,0,plain.Length);}
                }
            },sourcePath,token);
            var metadata=new {format="media-reviewer-original/v3",groupId=group,decisionKey=key,capturedAt=Catalog.Now(),hashes=new {fullSha256=hash},originalSize=size,chunkSize=EvidenceFormat.ChunkSize,chunkCount=count,captureId=capture,name=Path.GetFileName((string)source[1]),path=(string)source[1],rootPath=(string)source[0],reviewer=r.Reviewer,notes=r.Notes};
            byte[] bytes=Encoding.UTF8.GetBytes(EvidenceFormat.Json().Serialize(metadata)),metadataIv=EvidenceFormat.Random(12),metadataCipher;try{metadataCipher=Crypto.Encrypt(vaultKey,metadataIv,EvidenceFormat.Aad(ws,key,hash,"metadata",3,capture,size,count),bytes);}finally{Array.Clear(bytes,0,bytes.Length);}
            byte[] previewIv=null,previewCipher=null,preview=(byte[])db.Scalar("SELECT bytes FROM previews WHERE content_id=? AND state='available' AND length(bytes)<=32768",info[0]);
            if(preview!=null){previewIv=EvidenceFormat.Random(12);previewCipher=Crypto.Encrypt(vaultKey,previewIv,EvidenceFormat.Aad(ws,key,hash,"preview",3,capture,size,count),preview);Array.Clear(preview,0,preview.Length);}
            WaitWrite(w=>{RequireVault(epoch,token);Verification.Transaction(w,()=>{if(Convert.ToInt64(w.Scalar("SELECT COUNT(*) FROM original_chunks WHERE capture_id=?",capture))!=count)throw new InvalidDataException("Incomplete staged original");w.Execute("UPDATE original_captures SET metadata_iv=?,metadata_ciphertext=?,preview_iv=?,preview_ciphertext=?,state='verified' WHERE id=? AND state='staging'",metadataIv,metadataCipher,previewIv,previewCipher,capture);});return null;},metadataCipher.Length+(previewCipher==null ? 0 : previewCipher.Length)+4096);
            // Authenticate stored chunks, size, order and full digest before allowing publication.
            using(var checkedOriginal=ReadNativeOriginal(db,capture,group,epoch,token,progress,null,true)){}return capture;
        }
        public string RequestEvidenceInspect(long groupId){if(groupId<=0)throw new ArgumentException("Choose an original");long epoch=VaultGeneration;return EnqueueOperation("EvidenceInspect",(db,t,p)=>{var result=ReadOriginal(db,groupId,epoch,t,p,null);try{if(result.Preview!=null){try{result.PreviewImage=MediaLoader.Decode(result.Preview,256);}catch(Exception error){result.PreviewError="Original verified; saved thumbnail unavailable: "+error.Message;}}RequireVault(epoch,t);return result;}catch{result.Dispose();throw;}});}
        EvidenceInspection ReadOriginal(SqliteConnection db,long group,long epoch,CancellationToken token,Action<long,long,string> progress,Stream output){
            RequireVault(epoch,token);string capture=(string)db.Scalar("SELECT capture_id FROM original_links WHERE group_id=?",group);if(capture!=null)return ReadNativeOriginal(db,capture,group,epoch,token,progress,output,false);
            var result=ReadLegacyOriginal(db,group,epoch,token,progress,output);try{var links=db.Query("SELECT decision_key,hash,size FROM legacy_original_links WHERE group_id=?",new object[]{group},1);if(links.Count!=0 && ((string)links[0][0]!=result.LegacyKey || (string)links[0][1]!=result.Hash || (long)links[0][2]!=result.Size))throw new InvalidDataException("Authenticated imported original does not match its retained link");return result;}catch{result.Dispose();throw;}
        }
        EvidenceInspection ReadNativeOriginal(SqliteConnection db,string capture,long group,long epoch,CancellationToken token,Action<long,long,string> progress,Stream output,bool staged){
            var rows=db.Query("SELECT decision_key,hash,size,chunk_count,metadata_iv,metadata_ciphertext,preview_iv,preview_ciphertext,state FROM original_captures WHERE id=?",new object[]{capture},1);
            if(rows.Count==0 || ((string)rows[0][8]!="complete" && !(staged && (string)rows[0][8]=="verified")))throw new InvalidDataException("No completed retained original is available");var r=rows[0];string key=(string)r[0],hash=(string)r[1],ws=EvidenceFormat.Workspace(db);long size=(long)r[2];int count=checked((int)(long)r[3]);
            if(db.Scalar("SELECT 1 FROM original_captures c JOIN review_groups g ON g.id=c.group_id JOIN contents k ON k.id=g.content_id WHERE c.id=? AND g.id=? AND g.legacy_key=c.decision_key AND g.content_id=c.content_id AND k.algorithm='sha256' AND k.digest=c.hash",capture,group)==null)throw new InvalidDataException("Retained original is not bound to the selected card and full identity");
            if(size<0 || count!=(size+EvidenceFormat.ChunkSize-1)/EvidenceFormat.ChunkSize)throw new InvalidDataException("Original manifest size or chunk count is invalid");RequireVault(epoch,token);
            var result=new EvidenceInspection {GroupId=group,Epoch=epoch,Hash=hash,Size=size};try{
                result.Metadata=Crypto.Decrypt(vaultKey,(byte[])r[4],EvidenceFormat.Aad(ws,key,hash,"metadata",3,capture,size,count),(byte[])r[5]);
                var meta=EvidenceFormat.Json().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(result.Metadata));if(Convert.ToInt64(meta["originalSize"])!=size || Convert.ToInt32(meta["chunkCount"])!=count || Convert.ToInt32(meta["chunkSize"])!=EvidenceFormat.ChunkSize || (string)meta["captureId"]!=capture || Convert.ToInt64(meta["groupId"])!=group || (string)meta["decisionKey"]!=key || (string)((Dictionary<string,object>)meta["hashes"])["fullSha256"]!=hash)throw new InvalidDataException("Authenticated original metadata disagrees with its manifest or selected card");
                if(r[6]!=null)result.Preview=Crypto.Decrypt(vaultKey,(byte[])r[6],EvidenceFormat.Aad(ws,key,hash,"preview",3,capture,size,count),(byte[])r[7]);
                if(Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM original_chunks WHERE capture_id=?",capture))!=count)throw new InvalidDataException("Missing or extra original chunks");
                using(var sink=new EvidenceFormat.HashSink(output)){for(int index=0;index<count;index++){
                    RequireVault(epoch,token);var chunks=db.Query("SELECT iv,ciphertext FROM original_chunks WHERE capture_id=? AND chunk_index=?",new object[]{capture,index},1);if(chunks.Count!=1)throw new InvalidDataException("Original chunk sequence is incomplete");
                    byte[] plain=Crypto.Decrypt(vaultKey,(byte[])chunks[0][0],EvidenceFormat.Aad(ws,key,hash,"chunk",3,capture,size,count,index),(byte[])chunks[0][1]);try{if(plain.Length!=Math.Min(EvidenceFormat.ChunkSize,size-sink.Count))throw new InvalidDataException("Truncated original chunk");sink.Write(plain,0,plain.Length);}finally{Array.Clear(plain,0,plain.Length);}progress(sink.Count,size,"original bytes authenticated");
                }if(sink.Count!=size || sink.Finish()!=hash)throw new InvalidDataException("Retained original does not match its recorded full SHA-256");}
                RequireVault(epoch,token);result.Verified=true;return result;
            }catch{result.Dispose();throw;}
        }
        byte[] DecryptLegacyField(SqliteConnection db,string table,long row,string column,byte[] iv,byte[] aad,CancellationToken token){
            using(var sink=new EvidenceFormat.CappedSink()){
                // This operation owns the logical immutable payload; supported writers never mutate imported ciphertext.
                Crypto.DecryptVerifiedStream(vaultKey,()=>ImportedFields.Open(db,table,row,column),iv,aad,sink,token);return sink.Result();
            }
        }
        EvidenceInspection ReadLegacyOriginal(SqliteConnection db,long group,long epoch,CancellationToken token,Action<long,long,string> progress,Stream output){
            if(db.Scalar("SELECT 1 FROM imported_fields f JOIN import_extensions e ON e.rowid=f.owner_rowid WHERE f.owner_table='import_extensions' AND e.kind='identity_aliases' AND f.column_name IN('old_key','group_key') LIMIT 1")!=null || db.Scalar("SELECT 1 FROM review_groups WHERE id=? AND length(legacy_key)>4096",group)!=null)throw new InvalidOperationException("Protected identity links contain oversized fields; their original records are preserved but cannot be resolved safely.");
            const string linked=@"WITH RECURSIVE linked(key) AS (SELECT legacy_key FROM review_groups WHERE id=? UNION SELECT a.old_key FROM identity_aliases a JOIN linked l ON a.group_key=l.key WHERE length(a.old_key)<=4096 LIMIT 1001) ";
            var guard=db.Query(linked+@"SELECT COUNT(*),COALESCE(MAX(EXISTS(SELECT 1 FROM identity_aliases a WHERE a.group_key=l.key AND length(a.old_key)>4096)),0) FROM linked l",new object[]{group},1)[0];
            if((long)guard[0]>1000 || (long)guard[1]!=0)throw new InvalidOperationException("Protected identity links exceed the bounded resolver limit; their original records are preserved.");
            var keys=db.Query(linked+@"
SELECT key FROM linked WHERE EXISTS(SELECT 1 FROM evidence_manifests WHERE decision_key=key) OR EXISTS(SELECT 1 FROM evidence_items WHERE decision_key=key) LIMIT 2",new object[]{group},2);
            if(keys.Count!=1 || ((string)keys[0][0]).Length>4096)throw new InvalidOperationException("No unambiguous retained original is available for this card. Its imported protected records are preserved.");
            string key=(string)keys[0][0],ws=EvidenceFormat.Workspace(db);var rows=db.Query("SELECT rowid,hash,metadata_iv,preview_iv,original_size,chunk_size,chunk_count,version FROM evidence_manifests WHERE decision_key=?",new object[]{key},1);
            var result=new EvidenceInspection {GroupId=group,Epoch=epoch,LegacyKey=key};try{
                if(rows.Count>0){var r=rows[0];long row=(long)r[0],size=(long)r[4],width=(long)r[5],count=(long)r[6];string hash=(string)r[1];
                    if((long)r[7]!=2 || size<0 || width<1 || width>EvidenceFormat.ChunkSize || count<0 || count>1000000 || (size==0)!=(count==0))throw new InvalidDataException("Unsupported legacy Evidence manifest");
                    result.Hash=hash;result.Size=size;result.Metadata=DecryptLegacyField(db,"evidence_manifests",row,"metadata_ciphertext",(byte[])r[2],EvidenceFormat.Aad(ws,key,hash,"metadata",2,null,size,(int)count),token);
                    if(r[3]!=null){byte[] preview=DecryptLegacyField(db,"evidence_manifests",row,"preview_ciphertext",(byte[])r[3],EvidenceFormat.Aad(ws,key,hash,"preview",2,null,size,(int)count),token);if(preview.Length<=32768)result.Preview=preview;else Array.Clear(preview,0,preview.Length);}
                    if(Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM evidence_chunks WHERE decision_key=?",key))!=count)throw new InvalidDataException("Missing or extra Evidence chunks");
                    using(var sink=new EvidenceFormat.HashSink(output)){for(int index=0;index<count;index++){
                        RequireVault(epoch,token);var chunks=db.Query("SELECT rowid,iv FROM evidence_chunks WHERE decision_key=? AND chunk_index=?",new object[]{key,index},1);if(chunks.Count!=1)throw new InvalidDataException("Incomplete Evidence chunk sequence");
                        using(var source=ImportedFields.Open(db,"evidence_chunks",(long)chunks[0][0],"ciphertext")){
                            if(source.Length<16 || source.Length>width+16)throw new InvalidDataException("Invalid Evidence chunk size");
                            Crypto.DecryptVerifiedStream(vaultKey,()=>ImportedFields.Open(db,"evidence_chunks",(long)chunks[0][0],"ciphertext"),(byte[])chunks[0][1],EvidenceFormat.Aad(ws,key,hash,"chunk",2,null,size,(int)count,index),sink,token);
                        }progress(sink.Count,size,"imported original bytes authenticated");
                    }if(sink.Count!=size || sink.Finish()!=hash)throw new InvalidDataException("Imported Evidence full SHA-256 or size does not match its original");}
                }else{
                    var r=db.Query("SELECT rowid,hash,iv FROM evidence_items WHERE decision_key=?",new object[]{key},1)[0];long row=(long)r[0];result.Hash=(string)r[1];
                    long length;using(var source=ImportedFields.Open(db,"evidence_items",row,"ciphertext"))length=source.Length-16;if(length<4)throw new InvalidDataException("Truncated legacy Evidence");
                    using(var sink=new EvidenceFormat.LegacySink(length,output)){
                        Crypto.DecryptVerifiedStream(vaultKey,()=>ImportedFields.Open(db,"evidence_items",row,"ciphertext"),(byte[])r[2],EvidenceFormat.Aad(ws,key,result.Hash,null,1,null,0,0),sink,token);sink.Complete();
                        if(sink.Original.Finish()!=result.Hash)throw new InvalidDataException("Imported Evidence full SHA-256 does not match its original");result.Size=sink.Original.Count;result.Metadata=sink.Metadata.Result();progress(result.Size,result.Size,"legacy original bytes authenticated");
                    }
                }
                RequireVault(epoch,token);result.Verified=true;return result;
            }catch{result.Dispose();throw;}
        }
        public string RequestEvidenceOriginalExport(long group,string destination,string operationId){
            if(group<=0 || String.IsNullOrEmpty(destination) || destination.Length>32767 || String.IsNullOrEmpty(operationId) || operationId.Length>100)throw new ArgumentException("Choose an original, a new output file and operation identifier");string target=Paths.Source(destination);long epoch=VaultGeneration;string payload=Identity.Digest("original-export\n"+group+"\n"+target);
            return EnqueueOperation("OriginalExport",(db,t,p)=>{
                object prior=db.Scalar("SELECT payload_hash FROM original_exports WHERE id=?",operationId);if(prior!=null && (string)prior!=payload)throw new InvalidOperationException("Operation identifier has a different export payload");
                RequireVault(epoch,t);using(var verified=ReadOriginal(db,group,epoch,t,p,null)){
                    if((bool)WaitWrite(w=>(object)Verification.Reconciled(w,operationId,payload))){using(var source=File.OpenRead(Paths.Io(target)))VerifyExportFile(source,verified.Hash,verified.Size,t,p);return new OriginalReceipt {Kind="OriginalExport",OperationId=operationId,Reconciled=true,Message="Previously committed original export confirmed."};}
                    WaitWrite(w=>{w.Execute("INSERT INTO original_exports VALUES(?,?,?,'','planned',?) ON CONFLICT(id) DO UPDATE SET state='planned' WHERE original_exports.payload_hash=excluded.payload_hash AND original_exports.state<>'complete'",operationId,payload,target,verified.Hash);return null;});
                    bool created=false,complete=false;string identity=null;
                    try{using(var file=new FileStream(Paths.Io(target),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read,65536,FileOptions.Asynchronous)){
                        created=true;identity=FileAccessNative.Stamp(file).Identity;WaitWrite(w=>{w.Execute("UPDATE original_exports SET file_identity=?,state='writing' WHERE id=? AND payload_hash=?",identity,operationId,payload);return null;});
                        using(var exported=ReadOriginal(db,group,epoch,t,p,file)){}file.Flush(true);VerifyExportFile(file,verified.Hash,verified.Size,t,p);RequireVault(epoch,t);
                        WaitWrite(w=>{Verification.Transaction(w,()=>{RequireVault(epoch,t);w.Execute("UPDATE original_exports SET state='complete' WHERE id=?",operationId);w.Execute("INSERT INTO operations VALUES(?,?,?,?)",operationId,payload,Catalog.Now(),Catalog.Bump(w));});return null;});complete=true;
                    }}finally{if(!complete){bool committed=false;try{committed=(bool)WaitWrite(w=>(object)Verification.Reconciled(w,operationId,payload));}catch{committed=true;}if(!committed){bool removed=!created || FileAccessNative.DeleteOwnedFile(target,identity);try{WaitWrite(w=>{if(removed)w.Execute("UPDATE original_exports SET state='aborted' WHERE id=? AND state<>'complete'",operationId);else Catalog.Log(w,"warning","Original export",target,"An incomplete output could not be safely removed; inspect its destination.");return null;});}catch{}}}}
                    return new OriginalReceipt {Kind="OriginalExport",OperationId=operationId,Message="Committed verified original export: "+target};
                }
            });
        }
        internal static void VerifyExportFile(FileStream file,string expected,long size,CancellationToken token,Action<long,long,string> progress){
            file.Position=0;byte[] buffer=new byte[1024*1024];long done=0;
            try{using(var hash=SHA256.Create()){int n;while((n=file.ReadAsync(buffer,0,buffer.Length,token).GetAwaiter().GetResult())>0){token.ThrowIfCancellationRequested();hash.TransformBlock(buffer,0,n,buffer,0);done+=n;progress(done,size,"export bytes verified");}hash.TransformFinalBlock(new byte[0],0,0);if(done!=size || EvidenceFormat.Hex(hash.Hash)!=expected)throw new InvalidDataException("Written original does not match the recorded full SHA-256");}}
            finally{Array.Clear(buffer,0,buffer.Length);}
        }
    }
}
