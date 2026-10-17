using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
namespace MediaReviewer {
    public static partial class Interchange {
        internal static bool DetectHtml(string input,CancellationToken token){
            using(var guard=new FileStream(Paths.Io(input),FileMode.Open,FileAccess.Read,FileShare.ReadWrite)){
                byte[] header=new byte[100];int read=0,n;while(read<header.Length && (n=guard.Read(header,read,header.Length-read))>0){token.ThrowIfCancellationRequested();read+=n;}
                if(read!=100 || Encoding.ASCII.GetString(header,0,16)!="SQLite format 3\0")throw new InvalidDataException("The selected file is not a complete SQLite database");
                int id=(header[68]<<24)|(header[69]<<16)|(header[70]<<8)|header[71],version=(header[60]<<24)|(header[61]<<16)|(header[62]<<8)|header[63];
                if(id==Catalog.ApplicationId || version!=4)return false;
                if(header[18]!=1 || header[19]!=1)throw new InvalidDataException("Save a finished portable database in the HTML application before opening it here; a live WAL main file cannot be converted safely.");
            }
            using(var guard=new FileStream(Paths.Io(input),FileMode.Open,FileAccess.Read,FileShare.Read))using(var db=new SqliteConnection(input,true)){
                token.ThrowIfCancellationRequested();
                if(!Has(db,"app_meta") || !Has(db,"catalog_records") || !Has(db,"roots") || !Has(db,"scan_sessions"))return false;
                var workspace=ImportData.Rows(db,"app_meta","value","WHERE key='workspace_json'",token).SingleOrDefault();if(workspace==null)return false;
                var metadata=ImportData.Object(workspace[0]);return Number(metadata,"schemaVersion")==1 && Number(metadata,"databaseSchemaVersion")==4 && Flag(metadata,"catalogNormalized") && !String.IsNullOrEmpty(Text(metadata,"id"));
            }
        }
        public static void Import(string input,string output){Import(input,output,CancellationToken.None,null);}
        internal static void Import(string input,string output,CancellationToken token,Action<string> progress){
            string destination=Paths.LocalDatabase(output);
            if(File.Exists(Paths.Io(destination)))throw new IOException("Import output already exists; choose a new file. Originals are never overwritten.");
            string partial=destination+".importing-"+Guid.NewGuid().ToString("N");
            try {
                ImportLatest(input,partial,token,progress);
                using(var check=new SqliteConnection(partial,false)){
                    token.ThrowIfCancellationRequested();if((string)check.Scalar("PRAGMA integrity_check")!="ok" || check.Query("PRAGMA foreign_key_check",new object[0],1).Count!=0)throw new InvalidDataException("Imported database validation failed");
                    check.Script("PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;");
                }
                token.ThrowIfCancellationRequested();File.Move(Paths.Io(partial),Paths.Io(destination));
                if(progress!=null)progress("Import complete; original HTML database retained");
            }finally{foreach(string suffix in new[]{"","-wal","-shm","-journal"})try{if(File.Exists(Paths.Io(partial+suffix)))File.Delete(Paths.Io(partial+suffix));}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
        static string Columns(SqliteConnection source,string table){return String.Join(",",source.Query("PRAGMA table_info("+table+")",new object[0],64).Select(x=>(string)x[1]));}
        static void ImportLatest(string input,string partial,CancellationToken token,Action<string> progress){
            using(var guard=new FileStream(Paths.Io(input),FileMode.Open,FileAccess.Read,FileShare.Read)){
                byte[] header=new byte[100];int count=0,n;while(count<header.Length && (n=guard.Read(header,count,header.Length-count))>0)count+=n;
                if(count!=100 || Encoding.ASCII.GetString(header,0,16)!="SQLite format 3\0")throw new InvalidDataException("Input is not a SQLite database");
                if(header[18]!=1 || header[19]!=1)throw new InvalidDataException("Import needs a finished portable snapshot, not a live WAL main file. Save it in the HTML application first.");
                using(var source=new SqliteConnection(input,true)){
                    source.Script("PRAGMA query_only=ON; PRAGMA cache_size=-4096; PRAGMA mmap_size=0;");
                    if(Convert.ToInt64(source.Scalar("PRAGMA user_version"))!=4 || !Has(source,"app_meta") || !Has(source,"catalog_records"))throw new InvalidDataException("Import supports databases saved by the final HTML build (schema 4). The original remains intact.");
                    object[] workspace=ImportData.Rows(source,"app_meta","value","WHERE key='workspace_json'",token,progress).Single();var ws=ImportData.Object(workspace[0]);
                    if(Number(ws,"schemaVersion")!=1 || Number(ws,"databaseSchemaVersion")!=4 || !Flag(ws,"catalogNormalized") || String.IsNullOrEmpty(Text(ws,"id")))throw new InvalidDataException("Unsupported HTML metadata");
                    using(var target=new SqliteConnection(partial,false)){
                        Catalog.Initialize(target,true);target.Execute("INSERT INTO app_info VALUES('import_state','incomplete')");
                        target.Execute("UPDATE app_info SET value=? WHERE key='workspace_id'",Text(ws,"id"));target.Execute("INSERT INTO app_info VALUES('html_workspace_json',?)",workspace[0]);
                        ImportData.Preserve(workspace,target,"app_info",target.LastId,new[]{"value"});
                        var roots=Map(ws,"roots");var scans=Map(ws,"scans");long done=0;
                        var streamedWorkspace=workspace[0] as ImportCell;
                        if(streamedWorkspace!=null)foreach(var scan in streamedWorkspace.Members("scans")){
                            token.ThrowIfCancellationRequested();
                            if(target.Scalar("SELECT 1 FROM import_extensions WHERE kind='workspace_scan' AND id=?",scan.Key)!=null)throw new InvalidDataException("Duplicate imported scan identifier");
                            Extension(target,"workspace_scan",scan.Key,scan.Value);
                        }
                        foreach(object[] row in ImportData.Rows(source,"roots","id,label,path_label,path_provenance","",token,progress)){
                            string root=(string)row[0],sourcePath=Convert.ToString(row[2],CultureInfo.InvariantCulture)??"";object metadata;roots.TryGetValue(root,out metadata);
                            target.Execute("INSERT INTO roots(id,path,path_key,label,legacy_json) VALUES(?,?,?,?,?)",root,sourcePath,"IMPORTED:"+root,row[1]??root,metadata==null ? null : Json().Serialize(metadata));
                            ImportData.Preserve(new[]{row[1],row[2]},target,"roots",target.LastId,new[]{"label","path"});
                        }
                        foreach(object[] row in ImportData.Rows(source,"scan_sessions","id,root_id,started_at,completed_at,completed,scope_json,errors_json","",token,progress)){
                            string scan=(string)row[0];object raw;scans.TryGetValue(scan,out raw);
                            if(raw==null){object saved=target.Scalar("SELECT json FROM import_extensions WHERE kind='workspace_scan' AND id=?",scan);if(saved!=null)raw=ImportData.Object(saved);}
                            var meta=raw as Dictionary<string,object>??new Dictionary<string,object>();var config=Map(meta,"config");
                            if(config.Count==0 && Has(source,"scan_job_meta"))foreach(var saved in ImportData.Rows(source,"scan_job_meta","config_json","WHERE scan_id=?",token,progress,scan)){config=Map(ImportData.Object(saved[0]),"config");break;}
                            if(config.Count==0)config=ImportData.Object(row[5]);string root=(string)row[1],rootPath=(string)target.Scalar("SELECT path FROM roots WHERE id=?",root);
                            var options=new ScanOptions {RootPath=rootPath,WorkerCount=(int)Number(config,"workerCount",4),ExcludeApplicationData=Flag(config,"excludeUserApplicationData",true),ModifiedAgeYears=(int)Number(config,"skipOlderYears"),Extensions=(Get(config,"extensions") as System.Collections.IEnumerable??new object[]{"png"}).Cast<object>().Select(x=>Convert.ToString(x)).ToArray()};
                            double threshold=Get(config,"quickVideoThresholdMiB")==null ? 1 : Convert.ToDouble(Get(config,"quickVideoThresholdMiB"),CultureInfo.InvariantCulture);
                            if(Double.IsNaN(threshold) || Double.IsInfinity(threshold) || threshold<.25)threshold=1;threshold=Math.Min(threshold,102400);
                            options.FingerprintPolicy=new FingerprintPolicy {Mode=Flag(config,"quickVideoHash",true) ? "Sampled" : "Full",ThresholdBytes=(long)Math.Floor(threshold*1024*1024+.5)};
                            object cutoff=Get(config,"cutoffMs");if(cutoff!=null)options.CutoffUtcTicks=621355968000000000L+Convert.ToInt64(cutoff)*10000;
                            bool complete=Convert.ToInt64(row[4])!=0;string state=complete ? (Convert.ToString(row[6],CultureInfo.InvariantCulture)!="[]" ? "complete_errors" : "complete") : "imported_pending";
                            target.Execute("INSERT INTO scans(id,root_id,state,started_at,completed_at,config_json,manifest_complete,legacy_json) VALUES(?,?,?,?,?,?,1,?)",scan,root,state,row[2]??Catalog.Now(),row[3],Json().Serialize(options),raw==null ? null : Json().Serialize(raw));
                            ImportData.Preserve(new[]{row[5],row[6]},target,"scans",target.LastId,new[]{"legacy_scope_json","legacy_errors_json"});
                        }
                        foreach(object[] row in ImportData.Rows(source,"catalog_records","id,row_json","WHERE kind='contents' ORDER BY rowid",token,progress)){
                            var content=ImportData.Object(row[1]);string hash=(string)row[0],method=Text(content,"hashMethod",hash.Length==64 ? "sha256" : "sampled-sha256-v1");string digest=method=="sha256" ? hash : Text(content,"sampleDigest",hash.Split(':').Length>1 ? hash.Split(':')[1] : hash);
                            target.Execute("INSERT INTO contents(algorithm,algorithm_version,size,digest,sampling,legacy_hash,first_seen,last_seen,legacy_json) VALUES(?,1,?,?,?,?,?,?,?)",method,Number(content,"size"),digest,method=="sha256" ? "" : "html-v4:"+hash,hash,Text(content,"firstSeen",Catalog.Now()),Text(content,"lastSeen",Catalog.Now()),row[1]);ImportData.Preserve(new[]{row[1]},target,"contents",target.LastId,new[]{"legacy_json"});
                            if(progress!=null && ++done%32==0)progress("Importing content identities: "+done);
                        }
                        foreach(object[] row in ImportData.Rows(source,"catalog_records","id,row_json","WHERE kind='decisions' ORDER BY rowid",token,progress)){
                            var decision=ImportData.Object(row[1]);string root=Text(decision,"rootId"),hash=Text(decision,"hash");long content=Content(target,hash);
                            target.Execute("INSERT INTO review_groups(root_id,content_id,status,reviewer,notes,reviewed_at,legacy_key,legacy_json) VALUES(?,?,?,?,?,?,?,?)",root,content,Text(decision,"status","TO_REVIEW"),Text(decision,"reviewer"),Text(decision,"notes"),Get(decision,"reviewedAt"),row[0],row[1]);long id=target.LastId;target.Execute("UPDATE review_groups SET display_sequence=id WHERE id=?",id);ImportData.Preserve(new[]{row[1]},target,"review_groups",id,new[]{"legacy_json"});
                        }
                        foreach(object[] row in ImportData.Rows(source,"catalog_records","id,row_json","WHERE kind='occurrences' ORDER BY rowid",token,progress)){
                            var occurrence=ImportData.Object(row[1]);string relative=Relative(Text(occurrence,"path")),root=Text(occurrence,"rootId");string folder=relative.Contains("\\") ? relative.Substring(0,relative.IndexOf('\\')) : "";
                            target.Execute("INSERT INTO occurrences(id,root_id,content_id,path,path_key,name,extension,user_folder,size,modified_ticks,last_scan_id,superseded,legacy_json) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)",row[0],root,Content(target,Text(occurrence,"hash")),relative,Paths.Key(relative),Text(occurrence,"name",Path.GetFileName(relative)),Text(occurrence,"extension"),folder,Number(occurrence,"size"),621355968000000000L+Number(occurrence,"lastModified")*10000,Get(occurrence,"lastScanId"),!String.IsNullOrEmpty(Text(occurrence,"supersededAt")),row[1]);ImportData.Preserve(new[]{row[1]},target,"occurrences",target.LastId,new[]{"legacy_json"});
                            if(progress!=null && ++done%32==0)progress("Importing file locations: "+done);
                        }
                        if(Has(source,"thumbnails"))foreach(object[] row in ImportData.Rows(source,"thumbnails","hash,mime,width,height,bytes","",token,progress)){
                            long content=Content(target,(string)row[0]);bool valid=row[4] is byte[] && ((byte[])row[4]).Length>0 && ((byte[])row[4]).Length<=32768 && (long)row[2]>0 && (long)row[3]>0 && (long)row[2]<=256 && (long)row[3]<=256;
                            target.Execute("INSERT INTO previews(content_id,state,mime,width,height,bytes,decoder,error) VALUES(?,?,?,?,?,?,?,?)",content,valid ? "available" : "unavailable",row[1],row[2],row[3],valid ? row[4] : null,"Imported saved preview",valid ? null : "Imported preview exceeds gallery limits; record retained");ImportData.Preserve(new[]{row[4]},target,"previews",content,new[]{"bytes"});
                        }
                        if(Has(source,"review_events"))foreach(object[] row in ImportData.Rows(source,"review_events","id,decision_key,previous_status,new_status,at,reviewer,notes,bulk_id","ORDER BY rowid",token,progress)){
                            object group=target.Scalar("SELECT id FROM review_groups WHERE legacy_key=?",row[1]);target.Execute("INSERT INTO review_events VALUES(?,?,?,?,?,?,?,?,?)",row[0],group,row[1],row[2],row[3],row[4],row[5]??"",row[6]??"",row[7]);ImportData.Preserve(row,target,"review_events",target.LastId,new[]{"id","decision_key","previous_status","new_status","at","reviewer","notes","bulk_id"});
                        }
                        foreach(string table in new[]{"maintenance_events","identity_aliases","preview_access","scan_checkpoints","scan_job_meta"})if(Has(source,table)){
                            string[] columns=Columns(source,table).Split(',');long index=0;foreach(object[] row in ImportData.Rows(source,table,String.Join(",",columns),"ORDER BY rowid",token,progress)){
                                Extension(target,table,(++index).ToString(CultureInfo.InvariantCulture),ImportData.Values(row).Select(x=>x is ImportCell ? ((ImportCell)x).BindingValue : x).ToArray());ImportData.Preserve(row,target,"import_extensions",target.LastId,columns);
                                if(table=="identity_aliases" && row[0] is string && row[1] is string)target.Execute("INSERT INTO identity_aliases VALUES(?,?)",row[0],row[1]);
                            }
                        }
                        if(Has(source,"scan_jobs"))foreach(object[] row in ImportData.Rows(source,"scan_jobs","scan_id,seq,root_id,path,kind,state,signature,occurrence_id,error","ORDER BY scan_id,seq",token,progress)){
                            string scan=(string)row[0],relative=Relative((string)row[3]);string user=relative.Contains("\\") ? relative.Substring(0,relative.IndexOf('\\')) : (string)row[4]=="directory" ? relative : "";
                            target.Execute("INSERT OR IGNORE INTO scan_users(scan_id,name,path,path_key,sort_key,state,root_files) VALUES(?,?,?,?,?,'pending',?)",scan,String.IsNullOrEmpty(user) ? "(Root files)" : user,user,Paths.Key(user),user.ToUpperInvariant(),String.IsNullOrEmpty(user));
                            target.Execute("INSERT INTO jobs(scan_id,user_ordinal,kind,path,path_key,state,signature,error,occurrence_id,legacy_seq) VALUES(?,-1,?,?,?,?,?,?,?,?)",scan,row[4],relative,Paths.Key(relative),(string)row[5]=="processing" ? "pending" : row[5],row[6],row[8],row[7],row[1]);
                        }
                        FinishImportedJobs(target,token,progress);
                        foreach(string table in EvidenceTables)if(Has(source,table)){
                            string columns=Columns(source,table);string[] names=columns.Split(',');long copied=0;
                            foreach(object[] row in ImportData.Rows(source,table,columns,"ORDER BY rowid",token,progress)){
                                long rowId=(long)row[names.Length];var args=new List<object>{rowId};args.AddRange(ImportData.Values(row));target.Execute("INSERT INTO "+table+"(rowid,"+columns+") VALUES("+String.Join(",",Enumerable.Repeat("?",args.Count))+")",args.ToArray());ImportData.Preserve(row,target,table,rowId,names);
                                if(progress!=null)progress("Preserving encrypted "+table+": "+(++copied)+" records");
                            }
                        }
                        target.Execute("UPDATE app_info SET value='complete' WHERE key='import_state'");Catalog.Bump(target);Catalog.Log(target,"info","Import",input,"Final HTML schema 4 imported; original and encrypted Evidence retained. Evidence operations are not yet available.");
                    }
                }
            }
        }
        static void FinishImportedJobs(SqliteConnection target,CancellationToken token,Action<string> progress){
            foreach(object[] scan in Rows(target,"SELECT id FROM scans")){
                int ordinal=0;
                foreach(object[] user in Rows(target,"SELECT path_key FROM scan_users WHERE scan_id=? ORDER BY root_files DESC,sort_key,path",scan[0])){token.ThrowIfCancellationRequested();target.Execute("UPDATE scan_users SET ordinal=? WHERE scan_id=? AND path_key=?",ordinal++,scan[0],user[0]);}
                long after=0;
                while(true){token.ThrowIfCancellationRequested();var jobs=target.Query("SELECT id,path,kind FROM jobs WHERE scan_id=? AND id>? ORDER BY id LIMIT 128",new[]{scan[0],(object)after},128);if(jobs.Count==0)break;
                    target.Execute("BEGIN IMMEDIATE");try{foreach(object[] job in jobs){string relative=(string)job[1],user=relative.Contains("\\") ? relative.Substring(0,relative.IndexOf('\\')) : (string)job[2]=="directory" ? relative : "";object number=target.Scalar("SELECT ordinal FROM scan_users WHERE scan_id=? AND path_key=?",scan[0],Paths.Key(user));if(number==null)throw new InvalidDataException("Imported job has no saved user folder");target.Execute("UPDATE jobs SET user_ordinal=? WHERE id=?",number,job[0]);after=(long)job[0];}target.Execute("COMMIT");}catch{Catalog.Rollback(target);throw;}
                    if(progress!=null)progress("Assigning saved scan work: "+after);
                }
                target.Execute("UPDATE scan_users SET state=CASE WHEN EXISTS(SELECT 1 FROM jobs j WHERE j.scan_id=scan_users.scan_id AND j.user_ordinal=scan_users.ordinal AND j.state IN('pending','processing')) THEN 'pending' WHEN EXISTS(SELECT 1 FROM jobs j WHERE j.scan_id=scan_users.scan_id AND j.user_ordinal=scan_users.ordinal AND j.state='failed') THEN 'complete_errors' ELSE 'complete' END WHERE scan_id=?",scan[0]);
                if(Convert.ToInt64(target.Scalar("SELECT COUNT(*) FROM jobs WHERE scan_id=? AND kind='directory' AND path='' AND state='pending'",scan[0]))>0)target.Execute("UPDATE scans SET manifest_complete=0 WHERE id=?",scan[0]);
            }
        }
    }
}
