using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
namespace MediaReviewer {
    // This first adapter deliberately accepts the current normalized HTML schema only.
    // Raw extension fields and authenticated identifiers are retained; no crypto is rewritten.
    public static partial class Interchange {
        static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength=2*1024*1024,RecursionLimit=100 }; }
        static Dictionary<string,object> Object(string text) { return Json().Deserialize<Dictionary<string,object>>(text); }
        static object Get(Dictionary<string,object> row,string key,object fallback=null) { object value; return row.TryGetValue(key,out value) ? value : fallback; }
        static string Text(Dictionary<string,object> row,string key,string fallback="") { object value=Get(row,key); return value==null ? fallback : Convert.ToString(value,CultureInfo.InvariantCulture); }
        static long Number(Dictionary<string,object> row,string key,long fallback=0) { object value=Get(row,key); return value==null ? fallback : Convert.ToInt64(value,CultureInfo.InvariantCulture); }
        static bool Flag(Dictionary<string,object> row,string key,bool fallback=false) { object value=Get(row,key); return value==null ? fallback : Convert.ToBoolean(value,CultureInfo.InvariantCulture); }
        static Dictionary<string,object> Map(Dictionary<string,object> row,string key) { object value=Get(row,key); return value as Dictionary<string,object> ?? new Dictionary<string,object>(); }
        static IEnumerable<object[]> Rows(SqliteConnection db,string sql,params object[] args) { using(var stmt=db.Prepare(sql)) {stmt.Bind(args);while(stmt.Step()) yield return stmt.Row(); } }
        static bool Has(SqliteConnection db,string name) { return db.Scalar("SELECT 1 FROM sqlite_master WHERE type='table' AND name=?",name)!=null; }
        static void Extension(SqliteConnection db,string kind,string id,object row) { db.Execute("INSERT OR REPLACE INTO import_extensions VALUES(?,?,?)",kind,id,Json().Serialize(row)); }
        static long Content(SqliteConnection db,string hash) { object value=db.Scalar("SELECT id FROM contents WHERE legacy_hash=? ORDER BY id LIMIT 1",hash); if(value==null) throw new InvalidDataException("HTML record references missing content: "+hash); return Convert.ToInt64(value); }
        static string Relative(string path) {
            string normalized=path.Replace('/','\\');
            if(Path.IsPathRooted(normalized) || normalized.Split('\\').Any(x=>x==".." || x==".")) throw new InvalidDataException("Unsafe relative media path in imported data; original database is unchanged.");
            return normalized;
        }
        static readonly string[] EvidenceTables={"evidence_vault","evidence_items","evidence_manifests","evidence_chunks"};
        public static void Export(SqliteConnection source,string output) { Export(source,output,CancellationToken.None,null); }
        internal static void Export(SqliteConnection source,string output,CancellationToken token,Action<string> progress) {
            if(source.Scalar("SELECT 1 FROM original_requests LIMIT 1")!=null)throw new InvalidOperationException("HTML export cannot represent native retained originals; use a native portable backup.");
            if(source.Scalar("SELECT 1 FROM identity_runs LIMIT 1")!=null)throw new InvalidOperationException("HTML export cannot represent native verification history; use a native portable backup.");
            if(source.Scalar("SELECT 1 FROM contents WHERE algorithm='sampled-sha256-v1' AND sampling NOT LIKE 'html-v4:%' LIMIT 1")!=null)throw new InvalidOperationException("HTML export cannot represent native sampled identities and parameters; use a native portable backup.");
            if(Convert.ToInt64(source.Scalar("SELECT COUNT(*) FROM imported_fields"))!=0)throw new InvalidOperationException("HTML export is unavailable for streamed legacy fields; use a native portable backup.");
            if(Convert.ToInt64(source.Scalar("SELECT COUNT(*) FROM scans WHERE manifest_complete=0"))!=0) throw new InvalidOperationException("HTML export cannot preserve an incomplete user-folder manifest. Resume until manifest discovery finishes, then finish or cancel the scan before exporting. Saved native work is preserved.");
            foreach(var row in Rows(source,"SELECT config_json FROM scans"))if(ScanOptions.FromJson((string)row[0]).FingerprintPolicy.Mode=="Sampled")throw new InvalidOperationException("HTML export cannot represent native sampled scan policies; use a native portable backup.");
            string destination=Paths.LocalDatabase(output); if(File.Exists(Paths.Io(destination))) throw new IOException("Export destination already exists; choose a new file");
            string partial=destination+".partial-"+Guid.NewGuid().ToString("N");
            using(var target=new SqliteConnection(partial,false)) {
                target.Script("PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA cache_size=-4096; "+HtmlSchema);
                string old=(string)source.Scalar("SELECT value FROM app_info WHERE key='html_workspace_json'"); var ws=String.IsNullOrEmpty(old) ? new Dictionary<string,object>() : Object(old);
                ws["schemaVersion"]=1;ws["databaseSchemaVersion"]=4;ws["appVersion"]="3.3.12";ws["id"]=(string)source.Scalar("SELECT value FROM app_info WHERE key='workspace_id'");ws["catalogNormalized"]=true;ws["contents"]=new Dictionary<string,object>();ws["occurrences"]=new Dictionary<string,object>();ws["decisions"]=new Dictionary<string,object>();ws["scanCheckpoints"]=new Dictionary<string,object>();ws["events"]=new object[0];ws["maintenanceEvents"]=new object[0];
                if(!ws.ContainsKey("createdAt")) ws["createdAt"]=Catalog.Now(); if(!ws.ContainsKey("reviewer")) ws["reviewer"]="";
                if(!ws.ContainsKey("preferences")) ws["preferences"]=new Dictionary<string,object> { {"scanMode","custom"},{"scanExtensions",Formats.Quick},{"visibleExtensions",Formats.Deep},{"workerCount",4},{"itemsPerPage",60},{"evidenceDatabaseLimitGb",2.5},{"autoSaveMinutes",5},{"quickVideoHash",false} };
                var roots=new Dictionary<string,object>(); var scans=new Dictionary<string,object>();
                foreach(object[] row in Rows(source,"SELECT id,path,label,legacy_json FROM roots ORDER BY id")) {
                    token.ThrowIfCancellationRequested(); var root=row[3]==null ? new Dictionary<string,object>() : Object((string)row[3]); root["id"]=row[0];root["label"]=row[2];root["pathLabel"]=row[1];if(!root.ContainsKey("kind"))root["kind"]="homeshare";if(!root.ContainsKey("pathProvenance"))root["pathProvenance"]="user-supplied";
                    roots[(string)row[0]]=root;target.Execute("INSERT INTO roots VALUES(?,?,?,?)",row[0],row[2],row[1],root["pathProvenance"]);
                }
                foreach(object[] row in Rows(source,"SELECT id,root_id,state,started_at,completed_at,config_json,legacy_json FROM scans ORDER BY started_at")) {
                    token.ThrowIfCancellationRequested(); var scan=row[6]==null ? new Dictionary<string,object>() : Object((string)row[6]); var options=ScanOptions.FromJson((string)row[5]); bool complete=(string)row[2]=="complete" || (string)row[2]=="complete_errors";
                    scan["id"]=row[0];scan["rootId"]=row[1];scan["startedAt"]=row[3];scan["completedAt"]=row[4];scan["completed"]=complete;
                    if(!scan.ContainsKey("config"))scan["config"]=new Dictionary<string,object>{{"version",2},{"mode","custom"},{"extensions",options.Extensions},{"workerCount",options.WorkerCount},{"excludeUserApplicationData",options.ExcludeApplicationData},{"skipOlderYears",options.ModifiedAgeYears},{"cutoffMs",options.CutoffUtcTicks==0 ? (object)null : (options.CutoffUtcTicks-621355968000000000L)/10000},{"rootKind","homeshare"},{"scanArchives",false},{"quickVideoHash",false}};
                    if(!scan.ContainsKey("includedExtensions"))scan["includedExtensions"]=options.Extensions;if(!scan.ContainsKey("scanMode"))scan["scanMode"]="custom";if(!scan.ContainsKey("rootKind"))scan["rootKind"]="homeshare";
                    scans[(string)row[0]]=scan;target.Execute("INSERT INTO scan_sessions VALUES(?,?,?,?,?,?,?)",row[0],row[1],row[3],row[4],complete,Json().Serialize(scan["config"]),Json().Serialize(Get(scan,"errors",new object[0])));
                }
                ws["roots"]=roots;ws["scans"]=scans;target.Execute("INSERT INTO app_meta VALUES('workspace_json',?)",Json().Serialize(ws));
                long done=0;
                foreach(object[] row in Rows(source,"SELECT legacy_hash,size,algorithm,digest,first_seen,last_seen,legacy_json FROM contents ORDER BY id")) {
                    token.ThrowIfCancellationRequested(); var content=row[6]==null ? new Dictionary<string,object>() : Object((string)row[6]); content["hash"]=row[0];content["size"]=row[1];content["hashMethod"]=row[2];content["firstSeen"]=row[4];content["lastSeen"]=row[5];if((string)row[2]=="sha256")content["fullSha256"]=row[3];else if(!content.ContainsKey("sampleDigest"))content["sampleDigest"]=row[3];
                    Put(target,"contents",(string)row[0],"",(string)row[0],"","","","","",content);
                }
                foreach(object[] row in Rows(source,"SELECT o.id,o.root_id,o.path,o.name,o.extension,o.size,o.modified_ticks,o.last_scan_id,o.superseded,o.legacy_json,c.legacy_hash,c.algorithm FROM occurrences o JOIN contents c ON c.id=o.content_id ORDER BY o.rowid")) {
                    token.ThrowIfCancellationRequested(); var occurrence=row[9]==null ? new Dictionary<string,object>() : Object((string)row[9]); occurrence["id"]=row[0];occurrence["rootId"]=row[1];occurrence["path"]=((string)row[2]).Replace('\\','/');occurrence["name"]=row[3];occurrence["extension"]=row[4];occurrence["size"]=row[5];occurrence["lastModified"]=((long)row[6]-621355968000000000L)/10000;occurrence["lastScanId"]=row[7];occurrence["hash"]=row[10];occurrence["hashMethod"]=row[11];if(!occurrence.ContainsKey("sourceAvailable"))occurrence["sourceAvailable"]=false;if(!occurrence.ContainsKey("mediaKind"))occurrence["mediaKind"]=Formats.IsVideo((string)row[4]) ? "video" : "image";if((long)row[8]==0)occurrence.Remove("supersededAt");else if(!occurrence.ContainsKey("supersededAt"))occurrence["supersededAt"]=Catalog.Now();
                    Put(target,"occurrences",(string)row[0],(string)row[1],(string)row[10],"",(string)row[3],(string)occurrence["path"],(string)row[4],Text(occurrence,"lastSeen"),occurrence);
                    if(progress!=null && ++done%32==0)progress("Exporting occurrences: "+done);
                }
                foreach(object[] row in Rows(source,"SELECT g.legacy_key,g.root_id,c.legacy_hash,g.status,g.reviewer,g.notes,g.reviewed_at,g.legacy_json FROM review_groups g JOIN contents c ON c.id=g.content_id ORDER BY g.display_sequence")) {
                    token.ThrowIfCancellationRequested(); var decision=row[7]==null ? new Dictionary<string,object>() : Object((string)row[7]);decision["key"]=row[0];decision["rootId"]=row[1];decision["hash"]=row[2];decision["status"]=row[3];decision["reviewer"]=row[4];decision["notes"]=row[5];decision["reviewedAt"]=row[6];
                    Put(target,"decisions",(string)row[0],(string)row[1],(string)row[2],(string)row[3],"","","",Convert.ToString(row[6]),decision);
                }
                foreach(object[] row in Rows(source,"SELECT c.legacy_hash,p.mime,p.width,p.height,p.bytes FROM previews p JOIN contents c ON c.id=p.content_id WHERE p.bytes IS NOT NULL")) {token.ThrowIfCancellationRequested();target.Execute("INSERT INTO thumbnails VALUES(?,?,?,?,?)",row);}
                foreach(object[] row in Rows(source,"SELECT id,decision_key,previous_status,new_status,at,reviewer,notes,bulk_id FROM review_events ORDER BY rowid")) {token.ThrowIfCancellationRequested();target.Execute("INSERT INTO review_events VALUES(?,?,?,?,?,?,?,?)",row);}
                foreach(string table in new[]{"maintenance_events","identity_aliases","preview_access","scan_checkpoints","scan_job_meta"}) foreach(object[] raw in Rows(source,"SELECT json FROM import_extensions WHERE kind='"+table+"' ORDER BY CAST(id AS INTEGER)")) { token.ThrowIfCancellationRequested();object[] values=Json().Deserialize<object[]>((string)raw[0]);target.Execute("INSERT OR REPLACE INTO "+table+" VALUES("+String.Join(",",Enumerable.Repeat("?",values.Length))+")",values); }
                foreach(object[] row in Rows(source,"WITH maxima AS(SELECT scan_id,COALESCE(MAX(legacy_seq),0) AS last_seq FROM jobs GROUP BY scan_id) SELECT j.scan_id,COALESCE(j.legacy_seq,m.last_seq+ROW_NUMBER() OVER(PARTITION BY j.scan_id,(j.legacy_seq IS NULL) ORDER BY j.id)),s.root_id,j.path,j.kind,j.state,j.signature,j.occurrence_id,j.error FROM jobs j JOIN scans s ON s.id=j.scan_id JOIN maxima m ON m.scan_id=j.scan_id WHERE j.kind IN('directory','file') ORDER BY j.scan_id,j.id")) {
                    token.ThrowIfCancellationRequested();row[3]=((string)row[3]).Replace('\\','/');if((string)row[5]=="processing")row[5]="pending";target.Execute("INSERT INTO scan_jobs VALUES(?,?,?,?,?,?,?,?,?)",row);
                    if((string)row[4]=="file" && (string)row[5]=="complete") {
                        string oldCheckpoint=(string)target.Scalar("SELECT row_json FROM scan_checkpoints WHERE scan_id=? AND path=?",row[0],row[3]); var checkpoint=oldCheckpoint==null ? new Dictionary<string,object>() : Object(oldCheckpoint);
                        checkpoint["state"]="complete";checkpoint["occurrenceId"]=row[7];checkpoint["signature"]=row[6];if(!checkpoint.ContainsKey("updatedAt"))checkpoint["updatedAt"]=Catalog.Now();
                        target.Execute("INSERT OR REPLACE INTO scan_checkpoints VALUES(?,?,?,?)",row[0],row[3],row[7],Json().Serialize(checkpoint));
                    }
                }
                foreach(object[] scan in Rows(source,"SELECT id FROM scans")) {
                    object cursor=source.Scalar("SELECT name FROM scan_users WHERE scan_id=? AND state NOT IN('complete','complete_errors','skipped') ORDER BY ordinal LIMIT 1",scan[0]);
                    string oldMeta=(string)target.Scalar("SELECT config_json FROM scan_job_meta WHERE scan_id=?",scan[0]);var meta=oldMeta==null ? new Dictionary<string,object>() : Object(oldMeta);
                    if(oldMeta==null)meta["config"]=Map((Dictionary<string,object>)scans[(string)scan[0]],"config");meta["directoryOrder"]="user-folder-asc-v1";meta["lastUserFolder"]=cursor??"";meta["lastFolder"]=cursor??"";
                    target.Execute("INSERT OR REPLACE INTO scan_job_meta VALUES(?,?)",scan[0],Json().Serialize(meta));
                }
                foreach(string table in EvidenceTables) foreach(object[] row in Rows(source,"SELECT * FROM "+table)) { token.ThrowIfCancellationRequested();target.Execute("INSERT INTO "+table+" VALUES("+String.Join(",",Enumerable.Repeat("?",row.Length))+")",row); }
                target.Execute("PRAGMA user_version=4"); if((string)target.Scalar("PRAGMA integrity_check")!="ok") throw new InvalidDataException("Export integrity check failed");
            }
            File.Move(Paths.Io(partial),Paths.Io(destination));
        }
        static void Put(SqliteConnection db,string kind,string id,string root,string hash,string status,string name,string path,string extension,string time,Dictionary<string,object> json) { db.Execute("INSERT INTO catalog_records VALUES(?,?,?,?,?,?,?,?,?,?)",kind,id,root,hash,status,name,path,extension,time,Json().Serialize(json)); }
        internal const string HtmlSchema=@"
CREATE TABLE app_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE roots(id TEXT PRIMARY KEY,label TEXT,path_label TEXT,path_provenance TEXT);
CREATE TABLE scan_sessions(id TEXT PRIMARY KEY,root_id TEXT,started_at TEXT,completed_at TEXT,completed INTEGER,scope_json TEXT,errors_json TEXT);
CREATE TABLE catalog_records(kind TEXT NOT NULL,id TEXT NOT NULL,root_id TEXT,hash TEXT,status TEXT,name TEXT,path TEXT,extension TEXT,sort_time TEXT,row_json TEXT NOT NULL,PRIMARY KEY(kind,id));
CREATE INDEX catalog_status ON catalog_records(kind,status,sort_time);
CREATE INDEX catalog_root_hash ON catalog_records(kind,root_id,hash);
CREATE INDEX catalog_found_order ON catalog_records(kind,status);
CREATE INDEX catalog_hash ON catalog_records(kind,hash);
CREATE INDEX catalog_root_path ON catalog_records(root_id,path) WHERE kind='occurrences';
CREATE TABLE identity_aliases(old_key TEXT PRIMARY KEY,group_key TEXT NOT NULL);
CREATE TABLE preview_access(hash TEXT PRIMARY KEY,used_at INTEGER NOT NULL);
CREATE TABLE review_events(id TEXT PRIMARY KEY,decision_key TEXT,previous_status TEXT,new_status TEXT,at TEXT,reviewer TEXT,notes TEXT,bulk_id TEXT);
CREATE TABLE maintenance_events(id TEXT PRIMARY KEY,action TEXT NOT NULL,at TEXT NOT NULL,reviewer TEXT,details_json TEXT NOT NULL);
CREATE TABLE scan_jobs(scan_id TEXT NOT NULL,seq INTEGER NOT NULL,root_id TEXT NOT NULL,path TEXT NOT NULL,kind TEXT NOT NULL,state TEXT NOT NULL,signature TEXT,occurrence_id TEXT,error TEXT,PRIMARY KEY(scan_id,seq),UNIQUE(scan_id,kind,path));
CREATE INDEX scan_jobs_pending ON scan_jobs(scan_id,state,seq);
CREATE TABLE scan_job_meta(scan_id TEXT PRIMARY KEY,config_json TEXT NOT NULL);
CREATE TABLE scan_checkpoints(scan_id TEXT NOT NULL,path TEXT NOT NULL,occurrence_id TEXT,row_json TEXT NOT NULL,PRIMARY KEY(scan_id,path));
CREATE TABLE thumbnails(hash TEXT PRIMARY KEY,mime TEXT NOT NULL,width INTEGER NOT NULL,height INTEGER NOT NULL,bytes BLOB NOT NULL);
CREATE TABLE evidence_vault(id INTEGER PRIMARY KEY CHECK(id=1),salt BLOB NOT NULL,iterations INTEGER NOT NULL,check_iv BLOB NOT NULL,check_ciphertext BLOB NOT NULL,created_at TEXT NOT NULL);
CREATE TABLE evidence_items(decision_key TEXT PRIMARY KEY,hash TEXT NOT NULL,iv BLOB NOT NULL,ciphertext BLOB NOT NULL,captured_at TEXT NOT NULL,ciphertext_size INTEGER NOT NULL);
CREATE TABLE evidence_manifests(decision_key TEXT PRIMARY KEY,hash TEXT NOT NULL,metadata_iv BLOB NOT NULL,metadata_ciphertext BLOB NOT NULL,preview_iv BLOB,preview_ciphertext BLOB,preview_mime TEXT,preview_width INTEGER,preview_height INTEGER,captured_at TEXT NOT NULL,original_size INTEGER NOT NULL,chunk_size INTEGER NOT NULL,chunk_count INTEGER NOT NULL,ciphertext_size INTEGER NOT NULL,version INTEGER NOT NULL);
CREATE TABLE evidence_chunks(decision_key TEXT NOT NULL,chunk_index INTEGER NOT NULL,iv BLOB NOT NULL,ciphertext BLOB NOT NULL,PRIMARY KEY(decision_key,chunk_index));
CREATE VIEW contents AS SELECT hash,json_extract(row_json,'$.size') AS size,json_extract(row_json,'$.firstSeen') AS first_seen,json_extract(row_json,'$.lastSeen') AS last_seen FROM catalog_records WHERE kind='contents';
CREATE VIEW occurrences AS SELECT id,root_id,path,hash,extension,json_extract(row_json,'$.lastSeen') AS last_seen,COALESCE(json_extract(row_json,'$.sourceAvailable'),0) AS source_available FROM catalog_records WHERE kind='occurrences';
CREATE VIEW decisions AS SELECT id AS key,root_id,hash,status,json_extract(row_json,'$.reviewedAt') AS reviewed_at,COALESCE(json_extract(row_json,'$.reviewer'),'') AS reviewer,COALESCE(json_extract(row_json,'$.notes'),'') AS notes FROM catalog_records WHERE kind='decisions';
";
    }
}
