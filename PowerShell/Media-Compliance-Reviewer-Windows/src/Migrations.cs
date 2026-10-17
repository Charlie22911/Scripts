using System;
using System.IO;
using System.Threading;
namespace MediaReviewer {
    internal static class SqliteBackup {
        internal static void Create(SqliteConnection source,string output,CancellationToken token,Action<long,long> progress) {
            string destination=Paths.LocalDatabase(output);
            if(File.Exists(Paths.Io(destination)))throw new IOException("Backup destination exists; choose a new file");
            string partial=destination+".partial-"+Guid.NewGuid().ToString("N");
            try {
                using(var target=new SqliteConnection(partial,false)) {
                    target.Script("PRAGMA synchronous=FULL; PRAGMA journal_mode=DELETE; PRAGMA cache_size=-2048;");
                    IntPtr handle=Sql.sqlite3_backup_init(target.Handle,Native.Text("main"),source.Handle,Native.Text("main"));
                    if(handle==IntPtr.Zero)throw new DatabaseException(1,Native.Utf8(Sql.sqlite3_errmsg(target.Handle)));
                    try {
                        int retries=0;
                        while(true) {
                            token.ThrowIfCancellationRequested();int rc=Sql.sqlite3_backup_step(handle,64);
                            if(rc==5 || rc==6) {
                                if(++retries>3)throw new DatabaseException(rc,"Backup remains blocked after three retries");
                                if(token.WaitHandle.WaitOne(retries==1 ? 250 : retries==2 ? 1000 : 3000))token.ThrowIfCancellationRequested();
                                continue;
                            }
                            target.Check(rc);retries=0;
                            long total=Sql.sqlite3_backup_pagecount(handle),remaining=Sql.sqlite3_backup_remaining(handle);
                            if(progress!=null)progress(total-remaining,total);
                            if(rc==101)break;
                        }
                    } finally {target.Check(Sql.sqlite3_backup_finish(handle));}
                    token.ThrowIfCancellationRequested();
                    if((string)target.Scalar("PRAGMA quick_check")!="ok")throw new InvalidDataException("Backup validation failed; the source is unchanged");
                    target.Script("PRAGMA journal_mode=DELETE;");
                }
                token.ThrowIfCancellationRequested();File.Move(Paths.Io(partial),Paths.Io(destination));
            } finally {
                // Only this call's unguessable, closed partial destination is eligible.
                foreach(string suffix in new[]{"","-journal","-wal","-shm"})try{if(File.Exists(Paths.Io(partial+suffix)))File.Delete(Paths.Io(partial+suffix));}catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }
    }
    public static class Migrations {
        internal const string Schema8=@"
CREATE TABLE source_connections(id TEXT PRIMARY KEY,root_id TEXT NOT NULL REFERENCES roots(id),path TEXT NOT NULL,path_key TEXT NOT NULL,base_head TEXT NOT NULL,identity_revision INTEGER NOT NULL,state TEXT NOT NULL,created_at TEXT NOT NULL,applied_at TEXT,cursor INTEGER NOT NULL DEFAULT 0,ceiling INTEGER NOT NULL,total INTEGER NOT NULL,full_count INTEGER NOT NULL DEFAULT 0,candidate_count INTEGER NOT NULL DEFAULT 0,missing_count INTEGER NOT NULL DEFAULT 0,failed_count INTEGER NOT NULL DEFAULT 0);
CREATE TABLE source_identity_versions(root_id TEXT PRIMARY KEY REFERENCES roots(id),revision INTEGER NOT NULL);
CREATE TRIGGER source_identity_group_added AFTER INSERT ON review_groups WHEN NEW.retired=0 BEGIN INSERT INTO source_identity_versions VALUES(NEW.root_id,1) ON CONFLICT(root_id) DO UPDATE SET revision=revision+1; END;
CREATE TRIGGER source_identity_group_changed AFTER UPDATE OF content_id,retired ON review_groups WHEN OLD.content_id<>NEW.content_id OR OLD.retired<>NEW.retired BEGIN INSERT INTO source_identity_versions VALUES(NEW.root_id,1) ON CONFLICT(root_id) DO UPDATE SET revision=revision+1; END;
CREATE TRIGGER source_identity_group_removed AFTER DELETE ON review_groups WHEN OLD.retired=0 BEGIN INSERT INTO source_identity_versions VALUES(OLD.root_id,1) ON CONFLICT(root_id) DO UPDATE SET revision=revision+1; END;
CREATE TRIGGER source_identity_published AFTER UPDATE OF state ON identity_runs WHEN NEW.state='published' AND OLD.state<>'published' BEGIN INSERT INTO source_identity_versions VALUES(NEW.root_id,1) ON CONFLICT(root_id) DO UPDATE SET revision=revision+1; END;
CREATE INDEX source_connections_root ON source_connections(root_id,created_at DESC);
CREATE UNIQUE INDEX source_one_pending ON source_connections(root_id) WHERE state IN('checking','interrupted','ready');
CREATE TABLE root_source_heads(root_id TEXT PRIMARY KEY REFERENCES roots(id),connection_id TEXT NOT NULL UNIQUE REFERENCES source_connections(id));
CREATE TABLE source_connection_files(connection_id TEXT NOT NULL REFERENCES source_connections(id),occurrence_id TEXT NOT NULL,content_id INTEGER NOT NULL,path TEXT NOT NULL,old_size INTEGER NOT NULL,old_ticks INTEGER NOT NULL,old_identity TEXT,size INTEGER,modified_ticks INTEGER,file_identity TEXT,state TEXT NOT NULL,error TEXT NOT NULL,PRIMARY KEY(connection_id,occurrence_id));
CREATE VIEW source_roots AS SELECT r.id,COALESCE(c.path,r.path) AS path,COALESCE(c.path_key,r.path_key) AS path_key,r.label,r.legacy_json FROM roots r LEFT JOIN root_source_heads h ON h.root_id=r.id LEFT JOIN source_connections c ON c.id=h.connection_id;
CREATE TABLE source_observations(occurrence_id TEXT PRIMARY KEY REFERENCES occurrences(id) ON DELETE CASCADE,connection_id TEXT NOT NULL,size INTEGER NOT NULL,modified_ticks INTEGER NOT NULL,file_identity TEXT NOT NULL);
CREATE VIEW bound_occurrences AS SELECT o.rowid AS rowid,o.id,o.root_id,o.content_id,o.path,o.path_key,o.name,o.extension,o.user_folder,o.size,
 CASE WHEN h.connection_id IS NULL OR observed.occurrence_id IS NOT NULL THEN o.modified_ticks ELSE COALESCE(f.modified_ticks,o.modified_ticks) END AS modified_ticks,
 CASE WHEN h.connection_id IS NULL OR observed.occurrence_id IS NOT NULL THEN o.file_identity WHEN f.state IN('full','candidate') THEN f.file_identity ELSE 'Unvalidated reconnected source' END AS file_identity,o.last_scan_id,o.superseded,o.legacy_json
 FROM occurrences o LEFT JOIN root_source_heads h ON h.root_id=o.root_id LEFT JOIN source_connections c ON c.id=h.connection_id LEFT JOIN source_observations observed ON observed.occurrence_id=o.id AND observed.connection_id=h.connection_id AND observed.size=o.size AND observed.modified_ticks=o.modified_ticks AND observed.file_identity=o.file_identity
 LEFT JOIN source_connection_files f ON f.connection_id=h.connection_id AND f.occurrence_id=o.id AND f.content_id=o.content_id AND f.path=o.path AND f.old_size=o.size AND f.old_ticks=o.modified_ticks AND f.old_identity IS o.file_identity;
DROP VIEW group_occurrences;
CREATE VIEW group_occurrences AS
 SELECT g.id AS group_id,o.rowid AS occurrence_order,o.id,o.root_id,g.content_id,o.path,o.path_key,o.name,o.extension,o.user_folder,o.size,o.modified_ticks,o.file_identity,o.last_scan_id,o.superseded FROM review_groups g JOIN bound_occurrences o ON o.root_id=g.root_id AND o.content_id=g.content_id WHERE g.retired=0 AND NOT EXISTS(SELECT 1 FROM resolved_locations l JOIN identity_runs r ON r.id=l.run_id WHERE l.occurrence_id=o.id AND r.state='published')
 UNION ALL SELECT g.id,o.rowid,o.id,o.root_id,g.content_id,o.path,o.path_key,o.name,o.extension,o.user_folder,o.size,o.modified_ticks,o.file_identity,o.last_scan_id,o.superseded FROM identity_plan p JOIN identity_runs r ON r.id=p.run_id AND r.state='published' JOIN resolved_locations l ON l.run_id=p.run_id AND l.digest=p.digest JOIN review_groups g ON g.id=p.group_id AND g.retired=0 JOIN bound_occurrences o ON o.id=l.occurrence_id;
";
        internal const string Schema7=@"
ALTER TABLE previews ADD COLUMN revision INTEGER NOT NULL DEFAULT 0;
CREATE TRIGGER preview_image_revision AFTER UPDATE OF bytes ON previews WHEN OLD.bytes IS NOT NEW.bytes BEGIN UPDATE previews SET revision=OLD.revision+1 WHERE content_id=NEW.content_id; END;
CREATE TABLE preview_retry_batches(id TEXT PRIMARY KEY,created_at TEXT NOT NULL,state TEXT NOT NULL);
CREATE TABLE preview_retry_items(id INTEGER PRIMARY KEY AUTOINCREMENT,batch_id TEXT NOT NULL REFERENCES preview_retry_batches(id) ON DELETE CASCADE,group_id INTEGER NOT NULL,content_id INTEGER NOT NULL,state TEXT NOT NULL,error TEXT NOT NULL DEFAULT '',UNIQUE(batch_id,group_id));
CREATE INDEX preview_retry_pending ON preview_retry_items(batch_id,state,id);
CREATE INDEX preview_retry_group ON preview_retry_items(group_id,content_id,id DESC);
";
        internal const string Schema6=@"
CREATE TABLE vault_key_wrap(id INTEGER PRIMARY KEY CHECK(id=1),version INTEGER NOT NULL,generation TEXT NOT NULL,iv BLOB NOT NULL,ciphertext BLOB NOT NULL);
CREATE TABLE legacy_original_links(group_id INTEGER PRIMARY KEY REFERENCES review_groups(id),decision_key TEXT NOT NULL,hash TEXT NOT NULL,size INTEGER NOT NULL);
ALTER TABLE original_targets RENAME TO original_targets_v5;
CREATE TABLE original_targets(request_id TEXT NOT NULL REFERENCES original_requests(id),group_id INTEGER NOT NULL REFERENCES review_groups(id),capture_id TEXT REFERENCES original_captures(id),content_id INTEGER NOT NULL,previous_status TEXT NOT NULL,reviewed_at TEXT,review_revision INTEGER NOT NULL,legacy_key TEXT,original_hash TEXT,original_size INTEGER,CHECK((capture_id IS NOT NULL AND legacy_key IS NULL) OR (capture_id IS NULL AND legacy_key IS NOT NULL AND original_hash IS NOT NULL AND original_size IS NOT NULL)),PRIMARY KEY(request_id,group_id));
INSERT INTO original_targets SELECT request_id,group_id,capture_id,content_id,previous_status,reviewed_at,review_revision,NULL,NULL,NULL FROM original_targets_v5;
DROP TABLE original_targets_v5;
";
        internal const string Schema5=@"
ALTER TABLE review_groups ADD COLUMN review_revision INTEGER NOT NULL DEFAULT 0;
CREATE TRIGGER review_revision_changed AFTER UPDATE OF status,reviewer,notes,retired,content_id ON review_groups BEGIN UPDATE review_groups SET review_revision=OLD.review_revision+1 WHERE id=NEW.id; END;
CREATE TABLE original_requests(id TEXT PRIMARY KEY,payload_hash TEXT NOT NULL,state TEXT NOT NULL,created_at TEXT NOT NULL);
CREATE TABLE original_captures(id TEXT PRIMARY KEY,request_id TEXT NOT NULL REFERENCES original_requests(id),group_id INTEGER NOT NULL REFERENCES review_groups(id),content_id INTEGER NOT NULL REFERENCES contents(id),decision_key TEXT NOT NULL,hash TEXT NOT NULL,size INTEGER NOT NULL,chunk_count INTEGER NOT NULL,state TEXT NOT NULL,metadata_iv BLOB,metadata_ciphertext BLOB,preview_iv BLOB,preview_ciphertext BLOB,created_at TEXT NOT NULL);
CREATE INDEX original_request ON original_captures(request_id,state);
CREATE TABLE original_chunks(capture_id TEXT NOT NULL REFERENCES original_captures(id),chunk_index INTEGER NOT NULL,iv BLOB NOT NULL,ciphertext BLOB NOT NULL,PRIMARY KEY(capture_id,chunk_index));
CREATE TABLE original_links(group_id INTEGER PRIMARY KEY REFERENCES review_groups(id),capture_id TEXT NOT NULL REFERENCES original_captures(id));
CREATE TABLE original_targets(request_id TEXT NOT NULL REFERENCES original_requests(id),group_id INTEGER NOT NULL REFERENCES review_groups(id),capture_id TEXT NOT NULL REFERENCES original_captures(id),content_id INTEGER NOT NULL,previous_status TEXT NOT NULL,reviewed_at TEXT,review_revision INTEGER NOT NULL,PRIMARY KEY(request_id,group_id));
CREATE TABLE original_exports(id TEXT PRIMARY KEY,payload_hash TEXT NOT NULL,path TEXT NOT NULL,file_identity TEXT NOT NULL,state TEXT NOT NULL,hash TEXT NOT NULL);
CREATE TRIGGER original_chunk_insert BEFORE INSERT ON original_chunks WHEN (SELECT state FROM original_captures WHERE id=NEW.capture_id)<>'staging' BEGIN SELECT RAISE(ABORT,'Published original chunks are immutable'); END;
CREATE TRIGGER original_chunk_update BEFORE UPDATE ON original_chunks WHEN (SELECT state FROM original_captures WHERE id=OLD.capture_id)<>'staging' BEGIN SELECT RAISE(ABORT,'Published original chunks are immutable'); END;
CREATE TRIGGER original_chunk_delete BEFORE DELETE ON original_chunks WHEN (SELECT state FROM original_captures WHERE id=OLD.capture_id) IN('verified','complete') BEGIN SELECT RAISE(ABORT,'Published original chunks are immutable'); END;
";
        internal const string Schema2=@"CREATE TABLE long_operations(id TEXT PRIMARY KEY,kind TEXT NOT NULL,state TEXT NOT NULL,stage TEXT NOT NULL,completed INTEGER NOT NULL DEFAULT 0,total INTEGER,unit TEXT NOT NULL DEFAULT '',error TEXT NOT NULL DEFAULT '',updated_at TEXT NOT NULL);";
        internal const string Schema3=@"CREATE TABLE imported_fields(owner_table TEXT NOT NULL,owner_rowid INTEGER NOT NULL,column_name TEXT NOT NULL,format TEXT NOT NULL,length INTEGER NOT NULL,sha256 TEXT NOT NULL,PRIMARY KEY(owner_table,owner_rowid,column_name));
CREATE TABLE imported_field_chunks(owner_table TEXT NOT NULL,owner_rowid INTEGER NOT NULL,column_name TEXT NOT NULL,chunk_index INTEGER NOT NULL,bytes BLOB NOT NULL,PRIMARY KEY(owner_table,owner_rowid,column_name,chunk_index),FOREIGN KEY(owner_table,owner_rowid,column_name) REFERENCES imported_fields(owner_table,owner_rowid,column_name) ON DELETE CASCADE);";
        internal const string Schema4=@"
CREATE TEMP TABLE mcr_group_sequence(value INTEGER);
INSERT INTO mcr_group_sequence SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name='review_groups'),0);
CREATE TABLE review_groups_v4(id INTEGER PRIMARY KEY AUTOINCREMENT,root_id TEXT NOT NULL REFERENCES roots(id),content_id INTEGER NOT NULL REFERENCES contents(id),display_sequence INTEGER UNIQUE,status TEXT NOT NULL CHECK(status IN('TO_REVIEW','COMPLIANT','NON_COMPLIANT','EVIDENCE')),reviewer TEXT NOT NULL DEFAULT '',notes TEXT NOT NULL DEFAULT '',reviewed_at TEXT,legacy_key TEXT NOT NULL UNIQUE,legacy_json TEXT,retired INTEGER NOT NULL DEFAULT 0 CHECK(retired IN(0,1)));
INSERT INTO review_groups_v4(id,root_id,content_id,display_sequence,status,reviewer,notes,reviewed_at,legacy_key,legacy_json) SELECT id,root_id,content_id,display_sequence,status,reviewer,notes,reviewed_at,legacy_key,legacy_json FROM review_groups;
DROP TABLE review_groups;
ALTER TABLE review_groups_v4 RENAME TO review_groups;
UPDATE sqlite_sequence SET seq=MAX(seq,(SELECT value FROM mcr_group_sequence)) WHERE name='review_groups';
DROP TABLE mcr_group_sequence;
CREATE UNIQUE INDEX active_root_content ON review_groups(root_id,content_id) WHERE retired=0;
CREATE INDEX review_bucket_sequence ON review_groups(status,retired,display_sequence);
CREATE TABLE identity_aliases(old_key TEXT PRIMARY KEY,group_key TEXT NOT NULL);
CREATE INDEX alias_group ON identity_aliases(group_key,old_key);
INSERT INTO identity_aliases SELECT json_extract(e.json,'$[0]'),json_extract(e.json,'$[1]') FROM import_extensions e WHERE e.kind='identity_aliases' AND json_valid(e.json) AND json_type(e.json,'$[0]')='text' AND json_type(e.json,'$[1]')='text' AND NOT EXISTS(SELECT 1 FROM imported_fields f WHERE f.owner_table='import_extensions' AND f.owner_rowid=e.rowid AND f.column_name IN('old_key','group_key'));
CREATE TRIGGER group_added AFTER INSERT ON review_groups WHEN NEW.retired=0 BEGIN UPDATE bucket_totals SET total=total+1 WHERE status=NEW.status; END;
CREATE TRIGGER group_moved AFTER UPDATE OF status,retired ON review_groups WHEN OLD.status<>NEW.status OR OLD.retired<>NEW.retired BEGIN UPDATE bucket_totals SET total=total-1 WHERE status=OLD.status AND OLD.retired=0; UPDATE bucket_totals SET total=total+1 WHERE status=NEW.status AND NEW.retired=0; END;
CREATE TRIGGER group_removed AFTER DELETE ON review_groups WHEN OLD.retired=0 BEGIN UPDATE bucket_totals SET total=total-1 WHERE status=OLD.status; END;
CREATE TABLE identity_runs(id TEXT PRIMARY KEY,group_id INTEGER NOT NULL REFERENCES review_groups(id),source_content INTEGER NOT NULL REFERENCES contents(id),root_id TEXT NOT NULL,state TEXT NOT NULL,created_at TEXT NOT NULL,preview_revision INTEGER,token TEXT,preview_json TEXT);
CREATE INDEX identity_source ON identity_runs(group_id,state,created_at);
CREATE TABLE identity_checks(run_id TEXT NOT NULL REFERENCES identity_runs(id),occurrence_id TEXT NOT NULL REFERENCES occurrences(id),occurrence_order INTEGER NOT NULL,path TEXT NOT NULL,size INTEGER NOT NULL,expected_ticks INTEGER NOT NULL,expected_identity TEXT,modified_ticks INTEGER NOT NULL,file_identity TEXT NOT NULL,digest TEXT NOT NULL,PRIMARY KEY(run_id,occurrence_id));
CREATE INDEX identity_check_order ON identity_checks(run_id,occurrence_order);
CREATE TABLE identity_plan(run_id TEXT NOT NULL REFERENCES identity_runs(id),digest TEXT NOT NULL,content_id INTEGER NOT NULL REFERENCES contents(id),group_id INTEGER REFERENCES review_groups(id),is_new INTEGER NOT NULL,retain_source INTEGER NOT NULL,PRIMARY KEY(run_id,digest));
CREATE INDEX resolved_group ON identity_plan(group_id,run_id,digest);
CREATE TABLE resolved_locations(run_id TEXT NOT NULL,source_group INTEGER NOT NULL REFERENCES review_groups(id),digest TEXT NOT NULL,occurrence_id TEXT NOT NULL REFERENCES occurrences(id),PRIMARY KEY(run_id,occurrence_id),FOREIGN KEY(run_id,digest) REFERENCES identity_plan(run_id,digest));
CREATE INDEX resolved_occurrence ON resolved_locations(occurrence_id,run_id);
CREATE VIEW group_occurrences AS
 SELECT g.id AS group_id,o.rowid AS occurrence_order,o.id,o.root_id,g.content_id,o.path,o.path_key,o.name,o.extension,o.user_folder,o.size,o.modified_ticks,o.file_identity,o.last_scan_id,o.superseded
 FROM review_groups g JOIN occurrences o ON o.root_id=g.root_id AND o.content_id=g.content_id
 WHERE g.retired=0 AND NOT EXISTS(SELECT 1 FROM resolved_locations l JOIN identity_runs r ON r.id=l.run_id WHERE l.occurrence_id=o.id AND r.state='published')
 UNION ALL
 SELECT g.id,o.rowid,o.id,o.root_id,g.content_id,o.path,o.path_key,o.name,o.extension,o.user_folder,o.size,o.modified_ticks,o.file_identity,o.last_scan_id,o.superseded
 FROM identity_plan p JOIN identity_runs r ON r.id=p.run_id AND r.state='published' JOIN resolved_locations l ON l.run_id=p.run_id AND l.digest=p.digest JOIN review_groups g ON g.id=p.group_id AND g.retired=0 JOIN occurrences o ON o.id=l.occurrence_id;
";
        internal static void Apply(SqliteConnection writer,int targetVersion,CancellationToken token,Action<OperationProgress> progress) {
            int version=Convert.ToInt32(writer.Scalar("PRAGMA user_version"));
            if(version==targetVersion)return;
            if(version<1 || version>=Catalog.SchemaVersion || targetVersion!=Catalog.SchemaVersion)throw new InvalidDataException("Unsupported native schema version "+version+"; no migration or overwrite was attempted.");
            foreach(string name in new[]{"app_info","roots","scans","jobs","contents","review_groups","occurrences","operations"})
                if(writer.Scalar("SELECT 1 FROM sqlite_master WHERE type='table' AND name=?",name)==null)throw new InvalidDataException("Native schema is missing "+name+"; no upgrade was attempted");
            string backup=writer.FilePath+".pre-upgrade-v"+version+"-"+Guid.NewGuid().ToString("N")+".sqlite";
            SqliteBackup.Create(writer,backup,token,(done,total)=>{if(progress!=null)progress(new OperationProgress {Kind="Upgrade",State="running",Stage="Backing up before database upgrade",Completed=done,Total=total,Unit="pages",LastProgressUtc=DateTime.UtcNow});});
            while(version<targetVersion){token.ThrowIfCancellationRequested();bool rebuilding=version==3;
                // SQLite's supported table-rebuild sequence needs enforcement disabled outside the transaction.
                // No other connection/worker is started during migrations; validate every FK before committing.
                long steps=0;DateTime reported=DateTime.MinValue;Exception progressError=null;
                Sql.ProgressCallback check=state=>{steps+=2000;var now=DateTime.UtcNow;if(progress!=null && (now-reported).TotalMilliseconds>=200){reported=now;try{progress(new OperationProgress {Kind="Upgrade",State="running",Stage="Upgrading database schema "+version+" to "+(version+1),Completed=steps,Unit="SQLite steps",LastProgressUtc=now});}catch(Exception error){progressError=error;return 1;}}return token.IsCancellationRequested ? 1 : 0;};
                try {if(rebuilding)writer.Execute("PRAGMA foreign_keys=OFF");
                    if(progress!=null)progress(new OperationProgress {Kind="Upgrade",State="running",Stage="Upgrading database schema "+version+" to "+(version+1),Unit="SQLite steps",LastProgressUtc=DateTime.UtcNow});
                    Sql.sqlite3_progress_handler(writer.Handle,2000,check,IntPtr.Zero);
                    writer.Execute("BEGIN IMMEDIATE");writer.Script(version==1 ? Schema2 : version==2 ? Schema3 : version==3 ? Schema4 : version==4 ? Schema5 : version==5 ? Schema6 : version==6 ? Schema7 : Schema8);token.ThrowIfCancellationRequested();
                    if(rebuilding && writer.Query("PRAGMA foreign_key_check",new object[0],1).Count!=0)throw new InvalidDataException("Upgrade foreign-key validation failed; the original schema is retained.");
                    token.ThrowIfCancellationRequested();Sql.sqlite3_progress_handler(writer.Handle,0,null,IntPtr.Zero);
                    writer.Execute("PRAGMA user_version="+(version+1));writer.Execute("COMMIT");version++;}
                catch{Sql.sqlite3_progress_handler(writer.Handle,0,null,IntPtr.Zero);Catalog.Rollback(writer);if(progressError!=null)throw progressError;token.ThrowIfCancellationRequested();throw;}
                finally{Sql.sqlite3_progress_handler(writer.Handle,0,null,IntPtr.Zero);GC.KeepAlive(check);if(rebuilding)writer.Execute("PRAGMA foreign_keys=ON");}
            }
            if(progress!=null)progress(new OperationProgress {Kind="Upgrade",State="complete",Stage="Database upgraded; pre-upgrade backup: "+backup,LastProgressUtc=DateTime.UtcNow});
        }
    }
}
