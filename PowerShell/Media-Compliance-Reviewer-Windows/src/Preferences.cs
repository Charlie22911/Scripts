using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
namespace MediaReviewer {
    public sealed class ReviewPreferences {
        public int PageSize=60,WorkerCount=4,ModifiedAgeYears;
        public string Fit="Uniform",Mode="Quick",Reviewer="",Warning="";
        public bool IncludeApplicationData,IncludeRootFiles,ReadOnly;
        public string[] Families=Formats.Families.Where(f=>f.Key!="raw").Select(f=>f.Key).ToArray();
        public string Signature {get{Validate();return Digest();}}
        internal long Cost {get{return 1024+2L*(Reviewer.Length+Warning.Length+String.Join(",",Families).Length);}}
        internal ReviewPreferences Copy(){var p=(ReviewPreferences)MemberwiseClone();p.Families=Families==null ? null : (string[])Families.Clone();return p;}
        internal void Validate(){
            if(!new[]{20,40,60,100,200}.Contains(PageSize) || WorkerCount<1 || WorkerCount>8 || ModifiedAgeYears<0 || ModifiedAgeYears>9999 ||
                (Fit!="Uniform" && Fit!="UniformToFill") || !new[]{"Quick","Deep","Custom"}.Contains(Mode) || Reviewer==null || Reviewer.Length>128 || Reviewer.IndexOf('\0')>=0 ||
                Families==null || Families.Length>Formats.Families.Length || Families.Any(k=>!Formats.Families.Any(f=>f.Key==k)) || Families.Distinct().Count()!=Families.Length)
                throw new ArgumentException("Invalid preference value; choose supported scan and gallery options and up to128 reviewer characters.");
            Catalog.ReviewUtf8.GetByteCount(Reviewer);
        }
        internal SortedDictionary<string,string> Values(){
            return new SortedDictionary<string,string>(StringComparer.Ordinal){
                {"page_size",PageSize.ToString(CultureInfo.InvariantCulture)},{"workers",WorkerCount.ToString(CultureInfo.InvariantCulture)},
                {"age",ModifiedAgeYears.ToString(CultureInfo.InvariantCulture)},{"fit",Fit},{"mode",Mode},{"reviewer",Reviewer},
                {"families",String.Join(",",Families.OrderBy(k=>Array.FindIndex(Formats.Families,f=>f.Key==k)))},
                {"appdata",IncludeApplicationData ? "1" : "0"},{"root_files",IncludeRootFiles ? "1" : "0"}
            };
        }
        // Length-prefix fields so embedded punctuation and newlines have one unambiguous identity.
        internal string Digest(){var text=new StringBuilder();foreach(var field in Values())text.Append(field.Key.Length).Append(':').Append(field.Key).Append(field.Value.Length).Append(':').Append(field.Value);using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Catalog.ReviewUtf8.GetBytes(text.ToString()))).Replace("-","").ToLowerInvariant();}
    }
    public static partial class Catalog {
        const string PreferenceKeys="'ui.version','ui.v1.page_size','ui.v1.workers','ui.v1.age','ui.v1.fit','ui.v1.mode','ui.v1.reviewer','ui.v1.families','ui.v1.appdata','ui.v1.root_files'";
        static bool PreferencesVersionSupported(SqliteConnection db){object v=db.Scalar("SELECT value='1' AND length(CAST(value AS BLOB))=1 FROM settings WHERE key='ui.version'");return v==null || (long)v==1;}
        internal static ReviewPreferences Preferences(SqliteConnection db){
            var p=new ReviewPreferences();
            if(!PreferencesVersionSupported(db)){p.ReadOnly=true;p.Warning="Preferences use an unsupported version. Defaults are shown; saved preferences will be preserved.";return p;}
            bool invalid=false;
            foreach(var row in db.Query("SELECT key,substr(CAST(value AS BLOB),1,1024),length(CAST(value AS BLOB)),typeof(value) FROM settings WHERE key IN("+PreferenceKeys+")",new object[0],10)){
                string key=(string)row[0];if(key=="ui.version")continue;
                if((long)row[2]>1024 || (string)row[3]!="text"){invalid=true;continue;}
                string value;try{value=ReviewUtf8.GetString((byte[])row[1]);}catch(DecoderFallbackException){invalid=true;continue;}
                if(value.IndexOf('\0')>=0){invalid=true;continue;}
                int n;bool valid=true;switch(key.Substring(6)){
                    case "page_size":if(Int32.TryParse(value,out n) && new[]{20,40,60,100,200}.Contains(n))p.PageSize=n;else valid=false;break;
                    case "workers":if(Int32.TryParse(value,out n) && n>=1 && n<=8)p.WorkerCount=n;else valid=false;break;
                    case "age":if(Int32.TryParse(value,out n) && n>=0 && n<=9999)p.ModifiedAgeYears=n;else valid=false;break;
                    case "fit":if(value=="Uniform" || value=="UniformToFill")p.Fit=value;else valid=false;break;
                    case "mode":if(new[]{"Quick","Deep","Custom"}.Contains(value))p.Mode=value;else valid=false;break;
                    case "reviewer":if(value.Length<=128){try{ReviewUtf8.GetByteCount(value);p.Reviewer=value;}catch(EncoderFallbackException){valid=false;}}else valid=false;break;
                    case "families":var families=value.Length==0 ? new string[0] : value.Split(',');if(families.Length<=Formats.Families.Length && families.Distinct().Count()==families.Length && families.All(k=>Formats.Families.Any(f=>f.Key==k)))p.Families=families;else valid=false;break;
                    case "appdata":if(value=="1" || value=="0")p.IncludeApplicationData=value=="1";else valid=false;break;
                    case "root_files":if(value=="1" || value=="0")p.IncludeRootFiles=value=="1";else valid=false;break;
                }invalid|=!valid;
            }
            if(invalid)p.Warning="Some saved preferences were invalid; defaults were used for those values.";
            return p;
        }
        internal static bool SavePreferences(SqliteConnection db,ReviewPreferences p,string operation){
            string digest=p.Digest();db.Execute("BEGIN IMMEDIATE");try{
                if(!PreferencesVersionSupported(db))throw new InvalidOperationException("Unsupported preference version; saved values were preserved.");
                if(db.Scalar("SELECT 1 FROM settings WHERE key='ui.operation' AND value=?",operation)!=null){if(db.Scalar("SELECT 1 FROM settings WHERE key='ui.payload' AND value=?",digest)==null)throw new InvalidOperationException("Preference operation identifier has a different payload.");db.Execute("ROLLBACK");return true;}
                using(var write=db.Prepare("INSERT INTO settings(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value")){
                    foreach(var field in p.Values()){write.Bind("ui.v1."+field.Key,field.Value);write.Step();}
                    foreach(var field in new[]{new[]{"ui.version","1"},new[]{"ui.operation",operation},new[]{"ui.payload",digest}}){write.Bind(field[0],field[1]);write.Step();}
                }db.Execute("COMMIT");return false;
            }catch{Rollback(db);throw;}
        }
    }
    public sealed partial class ReviewerEngine {
        public string RequestPreferences(){
            if(!ready || closing || faulted)throw new InvalidOperationException("Database is not ready");string id=Guid.NewGuid().ToString("N");
            var work=new DbWork {Body=db=>Catalog.Preferences(db),Done=value=>Respond(new UiResult {Kind="Preferences",RequestId=id,Success=true,Preferences=(ReviewPreferences)value}),Failed=error=>Respond(new UiResult {Kind="Preferences",RequestId=id,Error=error.Message})};
            if(!reads.TryAdd(work,1024))throw new InvalidOperationException("Preference reader is busy; retry shortly.");return id;
        }
        public string RequestSavePreferences(ReviewPreferences preferences,string operationId){
            if(!ready || closing || faulted)throw new InvalidOperationException("Database is not ready");
            Guid operation;if(preferences==null || !Guid.TryParseExact(operationId,"N",out operation))throw new ArgumentException("A preference snapshot and operation identifier are required.");
            var p=preferences.Copy();p.Warning="";p.ReadOnly=false;p.Validate();string id=Guid.NewGuid().ToString("N");
            var work=new DbWork {Body=db=>(object)Catalog.SavePreferences(db,p,operationId),Done=value=>Respond(new UiResult {Kind="PreferencesSaved",RequestId=id,OperationId=operationId,Success=true,Reconciled=(bool)value}),Failed=error=>Respond(new UiResult {Kind="PreferencesSaved",RequestId=id,OperationId=operationId,Error=error.Message})};
            if(!controls.TryAdd(work,p.Cost))throw new InvalidOperationException("Preference queue is busy; changes have not been queued.");return id;
        }
    }
}
