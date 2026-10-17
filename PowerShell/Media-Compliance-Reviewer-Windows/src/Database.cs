using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace MediaReviewer {
    public static partial class Catalog {
        public const int ApplicationId=0x4D435257;
        public const int SchemaVersion=8;
        internal static string Now() { return DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture); }
        public static void Initialize(SqliteConnection db,bool create) {
            Initialize(db,create,CancellationToken.None,null);
        }
        internal static void Initialize(SqliteConnection db,bool create,CancellationToken token,Action<OperationProgress> progress) {
            if(create) {
                if(Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table'"))!=0) throw new InvalidDataException("Create requires an empty new database; an existing database is never overwritten.");
                db.Script("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; BEGIN IMMEDIATE;");
                try {
                    db.Script(Schema+Migrations.Schema2+Migrations.Schema3+Migrations.Schema4+Migrations.Schema5+Migrations.Schema6+Migrations.Schema7+Migrations.Schema8+" PRAGMA application_id="+ApplicationId+"; PRAGMA user_version=8;");
                    db.Execute("INSERT INTO app_info(key,value) VALUES('workspace_id',?)",Guid.NewGuid().ToString());
                    db.Execute("INSERT INTO app_info(key,value) VALUES('revision','0')"); db.Execute("COMMIT");
                } catch { Rollback(db); throw; }
            } else {
                long id=Convert.ToInt64(db.Scalar("PRAGMA application_id")),version=Convert.ToInt64(db.Scalar("PRAGMA user_version"));
                if(id!=ApplicationId) throw new InvalidDataException("This is not a native reviewer database. Use explicit Import HTML for an HTML database.");
                if(version<1 || version>SchemaVersion) throw new InvalidDataException("Unsupported native schema version "+version+"; no migration or overwrite was attempted.");
                if(HasIncompleteImport(db)) throw new InvalidDataException("This import is incomplete and cannot be opened for review. The HTML original remains intact.");
                Migrations.Apply(db,SchemaVersion,token,progress);
                db.Script("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
            }
            db.Script("PRAGMA cache_size=-4096; PRAGMA mmap_size=0; PRAGMA wal_autocheckpoint=1000; PRAGMA journal_size_limit=33554432;");
        }
        static bool HasIncompleteImport(SqliteConnection db) { object value=db.Scalar("SELECT value FROM app_info WHERE key='import_state'"); return value!=null && (string)value!="complete"; }
        internal const string Schema=@"
CREATE TABLE app_info(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE roots(id TEXT PRIMARY KEY,path TEXT NOT NULL,path_key TEXT NOT NULL UNIQUE,label TEXT NOT NULL,legacy_json TEXT);
CREATE TABLE scans(id TEXT PRIMARY KEY,root_id TEXT NOT NULL REFERENCES roots(id),state TEXT NOT NULL,started_at TEXT NOT NULL,completed_at TEXT,config_json TEXT NOT NULL,current_user INTEGER NOT NULL DEFAULT 0,manifest_complete INTEGER NOT NULL DEFAULT 0,legacy_json TEXT);
CREATE TABLE scan_users(scan_id TEXT NOT NULL REFERENCES scans(id),ordinal INTEGER,name TEXT NOT NULL,path TEXT NOT NULL,path_key TEXT NOT NULL,sort_key TEXT NOT NULL,state TEXT NOT NULL,root_files INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(scan_id,path_key),UNIQUE(scan_id,ordinal));
CREATE INDEX user_unordered ON scan_users(scan_id,root_files DESC,sort_key,path) WHERE ordinal IS NULL;
CREATE TABLE jobs(id INTEGER PRIMARY KEY AUTOINCREMENT,scan_id TEXT NOT NULL REFERENCES scans(id),user_ordinal INTEGER NOT NULL,kind TEXT NOT NULL,path TEXT NOT NULL,path_key TEXT NOT NULL,state TEXT NOT NULL,signature TEXT,attempts INTEGER NOT NULL DEFAULT 0,error TEXT,occurrence_id TEXT,legacy_seq INTEGER,UNIQUE(scan_id,kind,path_key));
CREATE INDEX jobs_pending ON jobs(scan_id,user_ordinal,state,kind,id);
CREATE UNIQUE INDEX jobs_preview_content ON jobs(scan_id,signature) WHERE kind='preview';
CREATE TABLE scan_counters(scan_id TEXT NOT NULL,kind TEXT NOT NULL,state TEXT NOT NULL,total INTEGER NOT NULL,PRIMARY KEY(scan_id,kind,state));
CREATE TRIGGER job_added AFTER INSERT ON jobs BEGIN INSERT INTO scan_counters VALUES(NEW.scan_id,NEW.kind,NEW.state,1) ON CONFLICT(scan_id,kind,state) DO UPDATE SET total=total+1; END;
CREATE TRIGGER job_moved AFTER UPDATE OF state ON jobs WHEN OLD.state<>NEW.state BEGIN UPDATE scan_counters SET total=total-1 WHERE scan_id=OLD.scan_id AND kind=OLD.kind AND state=OLD.state; INSERT INTO scan_counters VALUES(NEW.scan_id,NEW.kind,NEW.state,1) ON CONFLICT(scan_id,kind,state) DO UPDATE SET total=total+1; END;
CREATE TRIGGER job_removed AFTER DELETE ON jobs BEGIN UPDATE scan_counters SET total=total-1 WHERE scan_id=OLD.scan_id AND kind=OLD.kind AND state=OLD.state; END;
CREATE TABLE contents(id INTEGER PRIMARY KEY AUTOINCREMENT,algorithm TEXT NOT NULL,algorithm_version INTEGER NOT NULL,size INTEGER NOT NULL,digest TEXT NOT NULL,sampling TEXT NOT NULL DEFAULT '',legacy_hash TEXT NOT NULL,first_seen TEXT NOT NULL,last_seen TEXT NOT NULL,legacy_json TEXT,UNIQUE(algorithm,algorithm_version,size,digest,sampling));
CREATE INDEX content_legacy_hash ON contents(legacy_hash);
CREATE TABLE review_groups(id INTEGER PRIMARY KEY AUTOINCREMENT,root_id TEXT NOT NULL REFERENCES roots(id),content_id INTEGER NOT NULL REFERENCES contents(id),display_sequence INTEGER UNIQUE,status TEXT NOT NULL CHECK(status IN('TO_REVIEW','COMPLIANT','NON_COMPLIANT','EVIDENCE')),reviewer TEXT NOT NULL DEFAULT '',notes TEXT NOT NULL DEFAULT '',reviewed_at TEXT,legacy_key TEXT NOT NULL UNIQUE,legacy_json TEXT,UNIQUE(root_id,content_id));
CREATE INDEX review_bucket_sequence ON review_groups(status,display_sequence);
CREATE TABLE bucket_totals(status TEXT PRIMARY KEY,total INTEGER NOT NULL);
INSERT INTO bucket_totals VALUES('TO_REVIEW',0),('COMPLIANT',0),('NON_COMPLIANT',0),('EVIDENCE',0);
CREATE TRIGGER group_added AFTER INSERT ON review_groups BEGIN UPDATE bucket_totals SET total=total+1 WHERE status=NEW.status; END;
CREATE TRIGGER group_moved AFTER UPDATE OF status ON review_groups WHEN OLD.status<>NEW.status BEGIN UPDATE bucket_totals SET total=total-1 WHERE status=OLD.status; UPDATE bucket_totals SET total=total+1 WHERE status=NEW.status; END;
CREATE TRIGGER group_removed AFTER DELETE ON review_groups BEGIN UPDATE bucket_totals SET total=total-1 WHERE status=OLD.status; END;
CREATE TABLE occurrences(id TEXT PRIMARY KEY,root_id TEXT NOT NULL REFERENCES roots(id),content_id INTEGER NOT NULL REFERENCES contents(id),path TEXT NOT NULL,path_key TEXT NOT NULL,name TEXT NOT NULL,extension TEXT NOT NULL,user_folder TEXT NOT NULL,size INTEGER NOT NULL,modified_ticks INTEGER NOT NULL,file_identity TEXT,last_scan_id TEXT,superseded INTEGER NOT NULL DEFAULT 0,legacy_json TEXT,UNIQUE(root_id,path_key,content_id));
CREATE INDEX occurrence_active_path ON occurrences(root_id,path_key,superseded);
CREATE INDEX occurrence_content ON occurrences(root_id,content_id,superseded);
CREATE INDEX occurrence_filter ON occurrences(root_id,user_folder,extension,superseded);
CREATE TABLE previews(content_id INTEGER PRIMARY KEY REFERENCES contents(id),state TEXT NOT NULL,mime TEXT,width INTEGER,height INTEGER,bytes BLOB,error TEXT,decoder TEXT);
CREATE TABLE operations(id TEXT PRIMARY KEY,payload_hash TEXT NOT NULL,committed_at TEXT NOT NULL,revision INTEGER NOT NULL);
CREATE TABLE review_events(id TEXT PRIMARY KEY,group_id INTEGER REFERENCES review_groups(id),decision_key TEXT NOT NULL,previous_status TEXT,new_status TEXT,at TEXT,reviewer TEXT,notes TEXT,bulk_id TEXT);
CREATE INDEX review_history ON review_events(group_id,at);
CREATE TABLE diagnostics(id INTEGER PRIMARY KEY AUTOINCREMENT,at TEXT NOT NULL,level TEXT NOT NULL,stage TEXT NOT NULL,path TEXT,message TEXT NOT NULL);
CREATE TABLE import_extensions(kind TEXT NOT NULL,id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(kind,id));
CREATE TABLE evidence_vault(id INTEGER PRIMARY KEY CHECK(id=1),salt BLOB NOT NULL,iterations INTEGER NOT NULL,check_iv BLOB NOT NULL,check_ciphertext BLOB NOT NULL,created_at TEXT NOT NULL);
CREATE TABLE evidence_items(decision_key TEXT PRIMARY KEY,hash TEXT NOT NULL,iv BLOB NOT NULL,ciphertext BLOB NOT NULL,captured_at TEXT NOT NULL,ciphertext_size INTEGER NOT NULL);
CREATE TABLE evidence_manifests(decision_key TEXT PRIMARY KEY,hash TEXT NOT NULL,metadata_iv BLOB NOT NULL,metadata_ciphertext BLOB NOT NULL,preview_iv BLOB,preview_ciphertext BLOB,preview_mime TEXT,preview_width INTEGER,preview_height INTEGER,captured_at TEXT NOT NULL,original_size INTEGER NOT NULL,chunk_size INTEGER NOT NULL,chunk_count INTEGER NOT NULL,ciphertext_size INTEGER NOT NULL,version INTEGER NOT NULL);
CREATE TABLE evidence_chunks(decision_key TEXT NOT NULL,chunk_index INTEGER NOT NULL,iv BLOB NOT NULL,ciphertext BLOB NOT NULL,PRIMARY KEY(decision_key,chunk_index));
";
        public static string Root(SqliteConnection db,string path) {
            string friendly=Paths.Source(path),key=Paths.Key(friendly);
            object id=db.Scalar("SELECT r.id FROM source_roots r WHERE r.path_key=? LIMIT 1",key); if(id!=null) return (string)id;
            if(db.Scalar("SELECT 1 FROM roots WHERE path_key=?",key)!=null)throw new InvalidOperationException("This recorded source is connected elsewhere. Use its current source path for a new scan."); string value=Guid.NewGuid().ToString(); db.Execute("INSERT INTO roots(id,path,path_key,label) VALUES(?,?,?,?)",value,friendly,key,Path.GetFileName(friendly)); return value;
        }
        internal static long Revision(SqliteConnection db) { return Convert.ToInt64(db.Scalar("SELECT value FROM app_info WHERE key='revision'"),CultureInfo.InvariantCulture); }
        internal static long Bump(SqliteConnection db) { db.Execute("UPDATE app_info SET value=CAST(value AS INTEGER)+1 WHERE key='revision'"); return Revision(db); }
        public static long Publish(SqliteConnection db,FileRecord item) {
            db.Execute("BEGIN IMMEDIATE"); try { long id=PublishRaw(db,item); Bump(db); db.Execute("COMMIT"); return id; } catch { Rollback(db); throw; }
        }
        internal static void PublishBatch(SqliteConnection db,List<FileRecord> items) {
            db.Execute("BEGIN IMMEDIATE"); try { foreach(FileRecord item in items) PublishRaw(db,item); Bump(db); db.Execute("COMMIT"); } catch { Rollback(db); throw; }
        }
        internal static long PublishRaw(SqliteConnection db,FileRecord item) {
            bool sampled=item.Algorithm=="sampled-sha256-v1";string digest=sampled && item.Hash!=null && item.Hash.StartsWith("q2:",StringComparison.Ordinal) ? item.Hash.Substring(3) : item.Hash;
            if(item.Size<0 || digest==null || digest.Length!=64 || digest.Any(c=>!Uri.IsHexDigit(c)) || (sampled && !item.Hash.StartsWith("q2:",StringComparison.Ordinal)) || item.AlgorithmVersion!=1 || (!sampled && item.Algorithm!="sha256") || item.Sampling==null || item.Sampling.Length>256 || (sampled ? item.Sampling.Length==0 : item.Sampling.Length!=0)) throw new InvalidDataException("Invalid completed fingerprint");
            string hash=item.Hash.ToLowerInvariant(),now=Now(),relative=item.RelativePath.Replace('/','\\'),key=Paths.Key(relative);
            digest=digest.ToLowerInvariant();
            object existing=db.Scalar("SELECT id FROM contents WHERE algorithm=? AND algorithm_version=? AND size=? AND digest=? AND sampling=?",item.Algorithm,item.AlgorithmVersion,item.Size,digest,item.Sampling);
            // The final HTML q2 method has these fixed parameters. Preserve its keys and ciphertext aliases.
            if(existing==null && sampled && item.Sampling==new FingerprintPolicy().Parameters)existing=db.Scalar("SELECT id FROM contents WHERE algorithm='sampled-sha256-v1' AND algorithm_version=1 AND size=? AND digest=? AND sampling=? AND legacy_hash=?",item.Size,digest,"html-v4:"+hash,hash);
            if(existing==null){db.Execute("INSERT INTO contents(algorithm,algorithm_version,size,digest,sampling,legacy_hash,first_seen,last_seen) VALUES(?,?,?,?,?,?,?,?)",item.Algorithm,item.AlgorithmVersion,item.Size,digest,item.Sampling,hash,now,now);existing=db.LastId;}
            long content=Convert.ToInt64(existing);
            db.Execute("UPDATE contents SET last_seen=? WHERE id=?",now,content);
            string identity=hash+(sampled ? "|"+item.Size.ToString(CultureInfo.InvariantCulture)+"|"+item.Sampling : "");
            string legacyKey=item.RootId+"|"+identity;
            object savedGroup=db.Scalar("SELECT id FROM review_groups WHERE root_id=? AND content_id=? AND retired=0",item.RootId,content);
            if(savedGroup==null){
                if(db.Scalar("SELECT 1 FROM review_groups WHERE legacy_key=?",legacyKey)!=null)legacyKey=item.RootId+"|native:"+Guid.NewGuid().ToString("N");
                db.Execute("INSERT INTO review_groups(root_id,content_id,status,legacy_key) VALUES(?,?,'TO_REVIEW',?)",item.RootId,content,legacyKey);savedGroup=db.LastId;
            }
            long group=Convert.ToInt64(savedGroup);
            db.Execute("UPDATE review_groups SET display_sequence=id WHERE id=? AND display_sequence IS NULL",group);
            string occurrence=item.RootId+"|"+relative.Replace('\\','/')+"|"+identity;
            object resolved=db.Scalar("SELECT o.id FROM group_occurrences o WHERE o.root_id=? AND o.path_key=? AND o.content_id=? ORDER BY o.superseded,o.modified_ticks DESC,o.occurrence_order LIMIT 1",item.RootId,key,content);
            db.Execute("UPDATE occurrences SET superseded=1 WHERE id IN(SELECT o.id FROM group_occurrences o WHERE o.root_id=? AND o.path_key=? AND o.content_id<>? AND o.superseded=0)",item.RootId,key,content);
            string folder=relative.IndexOf('\\')<0 ? "" : relative.Substring(0,relative.IndexOf('\\'));
            if(resolved!=null){occurrence=(string)resolved;db.Execute("UPDATE occurrences SET size=?,modified_ticks=?,file_identity=?,last_scan_id=?,superseded=0 WHERE id=?",item.Size,item.ModifiedTicks,item.FileIdentity,item.ScanId,occurrence);}
            else {db.Execute("INSERT INTO occurrences(id,root_id,content_id,path,path_key,name,extension,user_folder,size,modified_ticks,file_identity,last_scan_id) VALUES(?,?,?,?,?,?,?,?,?,?,?,?) ON CONFLICT(root_id,path_key,content_id) DO UPDATE SET size=excluded.size,modified_ticks=excluded.modified_ticks,file_identity=excluded.file_identity,last_scan_id=excluded.last_scan_id,superseded=0",occurrence,item.RootId,content,relative,key,item.Name,item.Extension,folder,item.Size,item.ModifiedTicks,item.FileIdentity,item.ScanId);
                occurrence=(string)db.Scalar("SELECT id FROM occurrences WHERE root_id=? AND path_key=? AND content_id=?",item.RootId,key,content);}
            if(item.ScanId!=null){object binding=db.Scalar("SELECT connection_id FROM root_source_heads WHERE root_id=?",item.RootId);if(binding!=null)db.Execute("INSERT INTO source_observations VALUES(?,?,?,?,?) ON CONFLICT(occurrence_id) DO UPDATE SET connection_id=excluded.connection_id,size=excluded.size,modified_ticks=excluded.modified_ticks,file_identity=excluded.file_identity",occurrence,binding,item.Size,item.ModifiedTicks,item.FileIdentity);}
            if(item.JobId!=0) {
                db.Execute("UPDATE jobs SET state='complete',occurrence_id=?,signature=?,error=NULL WHERE id=?",occurrence,item.Size.ToString(CultureInfo.InvariantCulture)+":"+((item.ModifiedTicks-621355968000000000L)/10000).ToString(CultureInfo.InvariantCulture),item.JobId);
                if(db.Scalar("SELECT 1 FROM previews WHERE content_id=? AND state='available'",content)==null) db.Execute("INSERT OR IGNORE INTO jobs(scan_id,user_ordinal,kind,path,path_key,state,signature,occurrence_id) VALUES(?,?,'preview',?,?,'pending',?,?)",item.ScanId,item.UserOrdinal,relative,key,content.ToString(CultureInfo.InvariantCulture),occurrence);
            }
            return group;
        }
        internal static void Rollback(SqliteConnection db) { try { db.Execute("ROLLBACK"); } catch {} }
        internal static long[] Totals(SqliteConnection db) {
            var totals=new long[4]; string[] names={"TO_REVIEW","COMPLIANT","NON_COMPLIANT","EVIDENCE"};
            foreach(object[] row in db.Query("SELECT status,total FROM bucket_totals",new object[0],4)) { int i=Array.IndexOf(names,(string)row[0]); if(i>=0) totals[i]=(long)row[1]; } return totals;
        }
        internal static string Filter(PageRequest r,List<object> p) {
            if(!new[]{"TO_REVIEW","COMPLIANT","NON_COMPLIANT","EVIDENCE"}.Contains(r.Bucket)) throw new ArgumentException("Invalid bucket");
            p.Add(r.Bucket); string where="g.status=? AND g.retired=0";
            if(!String.IsNullOrEmpty(r.RootId)) { where+=" AND g.root_id=?"; p.Add(r.RootId); }
            string occurrence=OccurrenceFilter(r,"o",p);
            string stored=String.IsNullOrEmpty(r.Search) && String.IsNullOrEmpty(r.Folder) && String.IsNullOrEmpty(r.Type) ? " OR (NOT EXISTS(SELECT 1 FROM group_occurrences x WHERE x.group_id=g.id) AND (EXISTS(SELECT 1 FROM original_links l WHERE l.group_id=g.id) OR EXISTS(SELECT 1 FROM legacy_original_links l WHERE l.group_id=g.id)))" : "";
            where+=" AND (EXISTS(SELECT 1 FROM group_occurrences o WHERE "+occurrence+")"+stored+")";
            return where;
        }
        static string OccurrenceFilter(PageRequest r,string alias,List<object> p) {
            string sql=alias+".group_id=g.id AND ("+alias+".superseded=0 OR NOT EXISTS(SELECT 1 FROM group_occurrences live WHERE live.group_id=g.id AND live.superseded=0))";
            if(!String.IsNullOrEmpty(r.Type)) { string[] extensions=Formats.Filter(r.Type.ToLowerInvariant());sql+=" AND "+alias+".extension IN("+String.Join(",",extensions.Select(x=>"?"))+")";p.AddRange(extensions); }
            if(!String.IsNullOrEmpty(r.Folder)){sql+=" AND instr("+alias+".path_key,?)=1";p.Add(Paths.Key(r.Folder).TrimEnd('\\')+"\\");}
            if(!String.IsNullOrEmpty(r.Search)){sql+=" AND instr("+alias+".path_key,?)>0";p.Add(Paths.Key(r.Search));}
            return sql;
        }
        internal static PageData Page(SqliteConnection db,PageRequest r) {
            if(!new[]{20,40,60,100,200}.Contains(r.PageSize) || r.Page<1 || r.ExistingIds.Length>200 || r.Search.Length>512) throw new ArgumentException("Invalid bounded page request");
            if(r.Bucket=="EVIDENCE")return ProtectedPage(db,r);
            var p=new List<object>(); string where=Filter(r,p); long count=Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM review_groups g WHERE "+where,p.ToArray()));
            int page=(int)Math.Min(r.Page,Math.Max(1,(count+r.PageSize-1)/r.PageSize));
            string sort=r.Sort=="name" ? "sort_name COLLATE NOCASE,g.display_sequence" : r.Sort=="size" ? "c.size DESC,g.display_sequence" : "g.display_sequence";
            var data=new PageData { Total=count,Page=page,PageSize=r.PageSize,Revision=Revision(db),Totals=Totals(db),Refill=r.ExistingIds.Length>0 };
            var queryArgs=new List<object>();string matching=OccurrenceFilter(r,"q",queryArgs);
            string select=@"SELECT g.id,g.content_id,g.display_sequence,g.root_id,roots.path,COALESCE(o.path,''),COALESCE(o.name,'Stored original '||g.id),COALESCE(o.extension,''),c.size,c.legacy_hash,g.status,g.reviewer,g.notes,COALESCE(o.modified_ticks,0),(SELECT COUNT(DISTINCT n.path_key) FROM group_occurrences n WHERE n.group_id=g.id AND (n.superseded=0 OR NOT EXISTS(SELECT 1 FROM group_occurrences live WHERE live.group_id=g.id AND live.superseded=0))),COALESCE(p.state,CASE WHEN o.id IS NULL THEN 'unavailable' ELSE 'pending' END),COALESCE(p.error,CASE WHEN o.id IS NULL THEN 'No source location is recorded; use Stored original.' END),CASE WHEN length(p.bytes)<=32768 THEN p.bytes ELSE NULL END,p.width,p.height,COALESCE(o.name,'Stored original '||g.id) AS sort_name,o.file_identity,c.algorithm,COALESCE(o.superseded,0),(EXISTS(SELECT 1 FROM original_links l WHERE l.group_id=g.id) OR EXISTS(SELECT 1 FROM legacy_original_links l WHERE l.group_id=g.id)),g.review_revision,COALESCE(p.revision,0),(SELECT CASE WHEN b.state='interrupted' AND i.state IN('pending','running') THEN 'interrupted' WHEN i.state='pending' THEN 'queued' ELSE i.state END FROM preview_retry_items i JOIN preview_retry_batches b ON b.id=i.batch_id WHERE i.group_id=g.id AND i.content_id=g.content_id ORDER BY i.id DESC LIMIT 1) FROM review_groups g JOIN contents c ON c.id=g.content_id JOIN source_roots roots ON roots.id=g.root_id LEFT JOIN group_occurrences o ON o.group_id=g.id AND o.id=(SELECT q.id FROM group_occurrences q WHERE "+matching+@" ORDER BY q.superseded,q.modified_ticks DESC,q.path_key,q.occurrence_order LIMIT 1) LEFT JOIN previews p ON p.content_id=g.content_id WHERE ";
            queryArgs.AddRange(p); queryArgs.Add(r.PageSize+r.ExistingIds.Length); queryArgs.Add((long)(page-1)*r.PageSize);
            var existing=new HashSet<long>(r.ExistingIds); int needed=Math.Max(0,r.PageSize-existing.Count);
            using(var statement=db.Prepare(select+where+" ORDER BY "+sort+" LIMIT ? OFFSET ?")) { statement.Bind(queryArgs.ToArray()); long retained=0; while(statement.Step()) {
                if(existing.Contains(Convert.ToInt64(statement.Value(0))) || data.Rows.Count>=needed) continue;
                object[] row=statement.Row();
                if(existing.Contains((long)row[0]) || data.Rows.Count>=needed) continue;
                foreach(object value in row) retained+=value is string ? ((string)value).Length*2 : value is byte[] ? ((byte[])value).Length : 16;
                if(retained>12*1024*1024) throw new InvalidOperationException("This page exceeds the bounded payload limit. Choose a smaller items-per-page setting.");
                data.Rows.Add(new MediaRow { GroupId=(long)row[0],ContentId=(long)row[1],Sequence=(long)row[2],RootId=(string)row[3],RootPath=(string)row[4],RelativePath=(string)row[5],Name=(string)row[6],Extension=(string)row[7],Size=(long)row[8],Hash=(string)row[9],Status=(string)row[10],Reviewer=(string)row[11],Notes=(string)row[12],ModifiedTicks=(long)row[13],LocationCount=(long)row[14],PreviewState=(string)row[15],PreviewError=(string)row[16],PreviewBytes=(byte[])row[17],PreviewWidth=row[18]==null ? 0 : Convert.ToInt32(row[18]),PreviewHeight=row[19]==null ? 0 : Convert.ToInt32(row[19]),FileIdentity=(string)row[21],Algorithm=(string)row[22],SourceIsHistorical=(long)row[23]!=0,OriginalRetained=(long)row[24]!=0,ReviewRevision=(long)row[25],PreviewRevision=(long)row[26],PreviewRetryState=(string)row[27] });
            } }
            return data;
        }
        internal static bool Review(SqliteConnection db,ReviewRequest r) {
            if(String.IsNullOrEmpty(r.OperationId) || r.OperationId.Length>100 || r.GroupIds==null || r.GroupIds.Length==0 || r.GroupIds.Length>200 || !new[]{"TO_REVIEW","COMPLIANT","NON_COMPLIANT"}.Contains(r.Status) || r.Notes.Length>16384 || r.Reviewer.Length>128) throw new ArgumentException("Invalid bounded review action; Evidence requires verified capture.");
            long[] ids=r.GroupIds.Distinct().OrderBy(x=>x).ToArray(); string payload=r.Status+"\n"+r.Reviewer.Length+":"+r.Reviewer+"\n"+r.Notes.Length+":"+r.Notes+"\n"+String.Join(",",ids.Select(x=>x.ToString(CultureInfo.InvariantCulture)));
            string digest; using(var sha=SHA256.Create()) digest=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(payload))).Replace("-","").ToLowerInvariant();
            db.Execute("BEGIN IMMEDIATE"); try {
                object existing=db.Scalar("SELECT payload_hash FROM operations WHERE id=?",r.OperationId);
                if(existing!=null) { if((string)existing!=digest) throw new InvalidOperationException("Operation identifier has a different payload; it cannot be replayed."); db.Execute("COMMIT"); return true; }
                foreach(long id in ids) {
                    var rows=db.Query("SELECT status,legacy_key FROM review_groups WHERE id=? AND retired=0",new object[]{id},1);
                    if(rows.Count==0) throw new InvalidOperationException("A selected review group no longer exists; selection is retained.");
                    if((string)rows[0][0]=="EVIDENCE") throw new InvalidOperationException("Imported Evidence is preserved and locked. Unlocking/reclassifying Evidence is not implemented in this slice.");
                    db.Execute("UPDATE review_groups SET status=?,reviewer=?,notes=?,reviewed_at=? WHERE id=?",r.Status,r.Reviewer,r.Notes,Now(),id);
                    ImportedFields.Delete(db,"review_groups",id,"notes");ImportedFields.Delete(db,"review_groups",id,"reviewer");
                    db.Execute("INSERT INTO review_events VALUES(?,?,?,?,?,?,?,?,?)",Guid.NewGuid().ToString(),id,rows[0][1],rows[0][0],r.Status,Now(),r.Reviewer,r.Notes,r.OperationId);
                }
                long revision=Bump(db); db.Execute("INSERT INTO operations VALUES(?,?,?,?)",r.OperationId,digest,Now(),revision); db.Execute("COMMIT"); return false;
            } catch { Rollback(db); throw; }
        }
        internal static void Log(SqliteConnection db,string level,string stage,string path,string message) {
            db.Execute("INSERT INTO diagnostics(at,level,stage,path,message) VALUES(?,?,?,?,?)",Now(),level,stage,path,message.Length>4096 ? message.Substring(0,4096) : message);
            db.Execute("DELETE FROM diagnostics WHERE id<(SELECT COALESCE(MAX(id),0)-499 FROM diagnostics)");
        }
    }
    internal sealed class DbWork {
        internal Func<SqliteConnection,object> Body; internal TaskCompletionSource<object> Completion;
        internal Action<object> Done; internal Action<Exception> Failed; internal DateTime Queued=DateTime.UtcNow;
    }
    public sealed partial class ReviewerEngine:IDisposable {
        readonly BoundedQueue<DbWork> writes=new BoundedQueue<DbWork>(64,8*1024*1024),controls=new BoundedQueue<DbWork>(16,1024*1024),reads=new BoundedQueue<DbWork>(4,512*1024);
        readonly BoundedQueue<UiResult> results=new BoundedQueue<UiResult>(8,16*1024*1024);
        readonly CancellationTokenSource stop=new CancellationTokenSource(); readonly object statusGate=new object();
        readonly StatusSnapshot status=new StatusSnapshot(); readonly Thread writer; readonly List<Thread> readers=new List<Thread>();
        FileStream lease; string path; readonly bool create; readonly string importSource; volatile bool ready,faulted,closing,closed,detectedHtml; long viewGeneration;
        public bool Ready { get { return ready; } } public bool Faulted { get { return faulted; } } public bool Closed { get { return closed; } }
        public bool DetectedHtml { get { return detectedHtml; } }
        public string DatabasePath { get { return path; } }
        public StatusSnapshot Status { get { lock(statusGate) { StatusSnapshot copy=status.Copy(); copy.QueueCount=writes.Count+controls.Count+reads.Count+status.PreparedQueueCount; copy.RetainedBytes=writes.Bytes+controls.Bytes+reads.Bytes+results.Bytes+status.RetainedBytes; return copy; } } }
        ReviewerEngine(string file,bool newDatabase,string importing=null,Func<string,SqliteConnection> backgroundReader=null) { path=file; create=newDatabase; importSource=importing; operationReader=backgroundReader ?? (p=>new SqliteConnection(p,true)); writer=new Thread(WriterMain) { IsBackground=true,Name="MediaReviewer database writer" }; writer.Start(); }
        public static ReviewerEngine Open(string path,bool create) { return new ReviewerEngine(path,create); }
        public static ReviewerEngine ImportHtml(string input,string output) { return new ReviewerEngine(output,true,input); }
        internal void Update(Action<StatusSnapshot> action) { lock(statusGate) action(status); }
        internal Task<object> Write(Func<SqliteConnection,object> body,long bytes,bool priority) {
            if(faulted || stop.IsCancellationRequested) throw new InvalidOperationException("Database is not accepting new work: "+Status.Error);
            var item=new DbWork { Body=body,Completion=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously) };
            (priority ? controls : writes).Add(item,bytes,stop.Token); return item.Completion.Task;
        }
        internal object WaitWrite(Func<SqliteConnection,object> body,long bytes=1024) { return Write(body,bytes,false).GetAwaiter().GetResult(); }
        void Respond(UiResult result) {
            if(result.Evidence!=null)TrackProtectedResult(result.Evidence);
            long cost=512+(result.Value==null ? 0 : result.Value.Length*2)+(result.Error==null ? 0 : result.Error.Length*2);
            if(result.Page!=null) foreach(MediaRow row in result.Page.Rows) cost+=1024+(row.PreviewBytes==null ? 0 : row.PreviewBytes.Length)+(row.RootPath.Length+row.RelativePath.Length+row.Name.Length+row.Notes.Length+row.Reviewer.Length)*2;
            if(result.IdentityPreview!=null){var p=result.IdentityPreview;cost+=2048+(p.SourceNotes??"").Length*2+(p.SourceReviewer??"").Length*2+(p.Message??"").Length*2;foreach(var o in p.Outcomes)cost+=512+((o.Notes??"").Length+(o.Reviewer??"").Length+(o.ExamplePath??"").Length)*2;}
            if(result.Evidence!=null){var metadata=result.Evidence.Metadata;var preview=result.Evidence.Preview;var bitmap=result.Evidence.PreviewImage;cost+=(metadata==null ? 0 : metadata.Length)+(preview==null ? 0 : preview.Length)+(bitmap==null ? 0 : (long)bitmap.PixelWidth*bitmap.PixelHeight*4);}
            if(result.PreviewRetry!=null)cost+=result.PreviewRetry.Cost;if(result.PreviewRetries!=null)cost+=result.PreviewRetries.Sum(x=>x.Cost);
            if(result.SourceConnection!=null)cost+=result.SourceConnection.Cost;if(result.SourceRoots!=null)cost+=result.SourceRoots.Cost;
            if(result.Inspector!=null)cost+=result.Inspector.Cost;if(result.Locations!=null)cost+=result.Locations.Cost;
            if(result.Preferences!=null)cost+=result.Preferences.Cost;
            if(!closing) { try { results.Add(result,cost,stop.Token);return; } catch(OperationCanceledException) {} }if(result.Evidence!=null)result.Evidence.Dispose();
        }
        public bool TryTakeResult(out UiResult result) { result=results.Take(CancellationToken.None,0);if(result!=null && result.Evidence!=null && (!VaultUnlocked || result.Evidence.Epoch!=VaultGeneration)){result.Evidence.Dispose();result.Evidence=null;result.Success=false;result.Error="Vault locked; protected result discarded.";}return result!=null; }
        public string RequestPage(PageRequest request) {
            if(!ready || closing || faulted) throw new InvalidOperationException("Database is not ready");
            PageRequest r=request.Copy(); string id=Guid.NewGuid().ToString("N"); Interlocked.Exchange(ref viewGeneration,r.Generation);
            var work=new DbWork { Body=db=>Catalog.Page(db,r),Done=value=>Respond(new UiResult { RequestId=id,Kind="Page",Generation=r.Generation,Success=true,Page=(PageData)value }),Failed=error=>Respond(new UiResult { RequestId=id,Kind="Page",Generation=r.Generation,Error=error.Message }) };
            if(!reads.TryAdd(work,4096+r.ExistingIds.Length*8)) throw new InvalidOperationException("Reader queue is busy; retry the latest view shortly"); return id;
        }
        public string RequestReview(ReviewRequest request) { RequireReportWritable();
            if(request!=null && request.Status=="NON_COMPLIANT")return RequestOriginalRetention(request);
            if(!ready || closing || faulted) throw new InvalidOperationException("Database is not ready");
            ReviewRequest r=request.Copy(); string id=Guid.NewGuid().ToString("N");
            var work=new DbWork { Body=db=>{RequireReportWritable();return (object)Catalog.Review(db,r);},Done=value=>Respond(new UiResult { RequestId=id,Kind="Review",OperationId=r.OperationId,GroupIds=r.GroupIds,Success=true,Reconciled=(bool)value }),Failed=error=>Respond(new UiResult { RequestId=id,Kind="Review",OperationId=r.OperationId,GroupIds=r.GroupIds,Error=error.Message }) };
            long cost=2048+(r.Reviewer.Length+r.Notes.Length)*2+(r.GroupIds==null ? 0 : r.GroupIds.Length*8);
            if(!controls.TryAdd(work,cost)) throw new InvalidOperationException("Review queue is busy; selection is retained"); return id;
        }
        public string RequestBackup(string output) {
            if(String.IsNullOrEmpty(output) || output.Length>32767)throw new ArgumentException("Choose a bounded backup destination");
            return EnqueueOperation("Backup",(db,token,progress)=>{SqliteBackup.Create(db,output,token,(done,total)=>progress(done,total,"pages"));return "Backup saved: "+output;},2048+output.Length*2);
        }
        void WriterMain() {
            SqliteConnection db=null;
            try {
                if(!create && importSource==null){
                    path=Paths.Source(path);Update(s=>s.Stage="Checking database format");
                    if(Interchange.DetectHtml(path,stop.Token)){detectedHtml=true;throw new InvalidDataException("A final HTML database was detected. Choose a new local destination for its Windows copy; the original is preserved.");}
                }
                path=Paths.LocalDatabase(path);
                if(create && File.Exists(Paths.Io(path))) throw new IOException("Create cannot overwrite an existing file; choose a new database name.");
                if(!create && !File.Exists(Paths.Io(path))) throw new FileNotFoundException("The selected database no longer exists.");
                lease=new FileStream(Paths.Io(path+".mcr-owner"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                if(importSource!=null) Interchange.Import(importSource,path,stop.Token,phase=>Update(s=>{s.Stage=phase;s.LastProgressUtc=DateTime.UtcNow;}));
                db=new SqliteConnection(path,false); Catalog.Initialize(db,create && importSource==null,stop.Token,p=>Update(s=>{s.Stage=p.Stage;s.LastProgressUtc=p.LastProgressUtc;}));
                stop.Token.ThrowIfCancellationRequested();
                db.Execute("UPDATE jobs SET state='pending' WHERE state='processing'");
                db.Execute("UPDATE preview_retry_batches SET state='interrupted' WHERE state IN('queued','running')");db.Execute("UPDATE source_connections SET state='interrupted' WHERE state='checking'");
                Update(s=>{s.Stage="Ready";s.Revision=Catalog.Revision(db);s.PublishedGroups=Catalog.Totals(db).Sum();s.ResumeId=(string)db.Scalar("SELECT id FROM scans WHERE state NOT IN('complete','complete_errors') ORDER BY started_at DESC LIMIT 1")??"";});
                for(int i=0;i<2;i++) { var reader=new Thread(ReaderMain) { IsBackground=true,Name="MediaReviewer reader "+i }; readers.Add(reader); reader.Start(); }
                StartOperations(db);ready=true; DateTime checkpoint=DateTime.UtcNow;
                while(!stop.IsCancellationRequested || controls.Count+writes.Count>0) {
                    DbWork work=null;
                    // One scan command after every aged control burst prevents starvation.
                    if(writes.Count>0 && DateTime.UtcNow-checkpoint>TimeSpan.FromMilliseconds(500)) { work=writes.Take(CancellationToken.None,0); checkpoint=DateTime.UtcNow; }
                    if(work==null) work=controls.Take(CancellationToken.None,0);
                    if(work==null) { work=writes.Take(CancellationToken.None,25); if(work!=null) checkpoint=DateTime.UtcNow; }
                    if(work==null) continue;
                    try {
                        object value=work.Body(db); long revision=Catalog.Revision(db);
                        Update(s=>{ if(s.Revision!=revision){s.Revision=revision;s.LastCommitUtc=DateTime.UtcNow;s.LastProgressUtc=DateTime.UtcNow;s.PublishedGroups=Catalog.Totals(db).Sum();} });
                        if(work.Completion!=null) work.Completion.TrySetResult(value); if(work.Done!=null) work.Done(value);
                    } catch(Exception error) {
                        if(work.Completion!=null) work.Completion.TrySetException(error); if(work.Failed!=null) work.Failed(error);
                        var sqlite=error as DatabaseException;
                        if(sqlite!=null && !sqlite.IsTransient) throw;
                    }
                    try { FileInfo wal=new FileInfo(Paths.Io(path+"-wal")); if(wal.Exists) Update(s=>s.WalBytes=wal.Length); } catch(IOException) {}
                }
            } catch(Exception error) { faulted=true; ready=false; Update(s=>{s.Stage="Database stopped";s.Error=error.Message;}); stop.Cancel(); }
            finally {
                writes.Close(); controls.Close(); reads.Close();
                DbWork work; while((work=controls.Take(CancellationToken.None,0))!=null) FailAbandoned(work); while((work=writes.Take(CancellationToken.None,0))!=null) FailAbandoned(work);
                if(db!=null) db.Dispose(); if(lease!=null) lease.Dispose(); ready=false;
            }
        }
        void FailAbandoned(DbWork work) { var error=new IOException("Database owner stopped. Reopen and reconcile operation IDs before retrying unconfirmed writes."); if(work.Completion!=null) work.Completion.TrySetException(error); if(work.Failed!=null) work.Failed(error); }
        void ReaderMain() {
            try { using(var db=new SqliteConnection(path,true)) { db.Script("PRAGMA query_only=ON; PRAGMA cache_size=-2048; PRAGMA mmap_size=0;"); while(!stop.IsCancellationRequested) { DbWork work=reads.Take(stop.Token,100); if(work==null) continue; try { object value=work.Body(db); if(work.Done!=null) work.Done(value); } catch(Exception error) { if(work.Failed!=null) work.Failed(error); } } } }
            catch(OperationCanceledException) {} catch(Exception error) { Update(s=>s.Error="Reader failed: "+error.Message); }
        }
        partial void StopScanner(); partial void JoinScanner();
        public void Dispose() {
            if(closed) return; closing=true;results.Close();StopScanner(); JoinScanner(); StopOperations();stop.Cancel(); writes.Close(); controls.Close(); reads.Close();
            if(Thread.CurrentThread!=writer) writer.Join(); foreach(Thread reader in readers) if(Thread.CurrentThread!=reader) reader.Join(); closed=true;
        }
        public void CloseAsync() { if(closing) return; var closer=new Thread(Dispose) { IsBackground=true,Name="MediaReviewer shutdown" }; closer.Start(); }
    }
}
