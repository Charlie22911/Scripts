using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
namespace MediaReviewer {
    public sealed partial class ReviewerEngine {
        public string RequestIntegrity() {
            return EnqueueOperation("Integrity",(db,token,progress)=>{
                long instructions=0;
                Sql.ProgressCallback check=state=>{instructions+=2000;progress(instructions,-1,"SQLite steps");return token.IsCancellationRequested ? 1 : 0;};
                Sql.sqlite3_progress_handler(db.Handle,2000,check,IntPtr.Zero);
                try{token.ThrowIfCancellationRequested();string value=(string)db.Scalar("PRAGMA integrity_check");token.ThrowIfCancellationRequested();if(value!="ok")throw new InvalidDataException("Integrity check: "+value);return "Database integrity check: ok";}
                catch(DatabaseException){token.ThrowIfCancellationRequested();throw;}
                finally{Sql.sqlite3_progress_handler(db.Handle,0,null,IntPtr.Zero);GC.KeepAlive(check);}
            });
        }
        public string RequestLog() {
            if(!Ready || closing) throw new InvalidOperationException("Open a database first"); string id=Guid.NewGuid().ToString("N");
            var work=new DbWork { Body=db=> {
                var text=new StringBuilder(); text.AppendLine("Media Compliance Reviewer for Windows — recent log"); text.AppendLine("Log may contain source names and paths.");
                foreach(object[] row in db.Query("SELECT at,level,stage,path,message FROM diagnostics ORDER BY id DESC LIMIT 500",new object[0],500)) { string line=row[0]+" ["+row[1]+"] "+row[2]+" | "+row[3]+" | "+row[4]; if(text.Length+line.Length>262144){text.AppendLine("Display is bounded; additional log details remain in the database.");break;} text.AppendLine(line); } return text.ToString();
            },Done=value=>Respond(new UiResult { RequestId=id,Kind="Log",Success=true,Value=(string)value }),Failed=error=>Respond(new UiResult { RequestId=id,Kind="Log",Error=error.Message }) };
            if(!reads.TryAdd(work,1024)) throw new InvalidOperationException("Reader queue is busy"); return id;
        }
    }
}
