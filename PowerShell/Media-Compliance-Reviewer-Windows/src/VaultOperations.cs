using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
namespace MediaReviewer {
    // The same bounded password envelope opens a database vault or an offline package.
    internal sealed class VaultDescriptor {
        public byte[] Salt,CheckIv,CheckCipher,WrapIv,WrapCipher;public int Iterations,WrapVersion;public string Generation;
        public VaultDescriptor(){}
        internal void Validate(){if(Salt==null || Salt.Length<8 || Salt.Length>1024 || Iterations<1 || Iterations>10000000 || CheckIv==null || CheckIv.Length!=12 || CheckCipher==null || CheckCipher.Length<16 || CheckCipher.Length>256 || (WrapVersion==0 ? Generation!=null || WrapIv!=null || WrapCipher!=null : WrapVersion!=1 || Generation==null || Generation.Length!=32 || WrapIv==null || WrapIv.Length!=12 || WrapCipher==null || WrapCipher.Length!=48))throw new InvalidDataException("Invalid or unsupported originals password envelope");}
        internal static VaultDescriptor Read(SqliteConnection db){
            var rows=db.Query("SELECT CASE WHEN length(salt) BETWEEN 8 AND 1024 THEN salt END,iterations,CASE WHEN length(check_iv)=12 THEN check_iv END,CASE WHEN length(check_ciphertext)<=256 THEN check_ciphertext END FROM evidence_vault WHERE id=1",new object[0],1);if(rows.Count!=1)throw new InvalidDataException("Missing originals vault");var r=rows[0];var result=new VaultDescriptor {Salt=r[0] as byte[],Iterations=checked((int)(long)r[1]),CheckIv=r[2] as byte[],CheckCipher=r[3] as byte[]};
            var wraps=db.Query("SELECT version,substr(generation,1,33),CASE WHEN length(iv)=12 THEN iv END,CASE WHEN length(ciphertext)=48 THEN ciphertext END FROM vault_key_wrap WHERE id=1",new object[0],1);if(wraps.Count!=0){if(!(wraps[0][0] is long) || (long)wraps[0][0]!=1)throw new InvalidDataException("Unsupported or damaged content-key envelope");result.WrapVersion=1;result.Generation=wraps[0][1] as string;result.WrapIv=wraps[0][2] as byte[];result.WrapCipher=wraps[0][3] as byte[];}result.Validate();return result;
        }
        internal VaultKey Open(string workspace,byte[] password,CancellationToken token){
            VaultKey derived=null;byte[] material=null,check=null;
            try{Validate();derived=Crypto.DeriveKey(password,Salt,Iterations);token.ThrowIfCancellationRequested();check=Crypto.Decrypt(derived,CheckIv,EvidenceFormat.CheckAad(workspace),CheckCipher);var expected=WrapVersion==0 ? Encoding.UTF8.GetBytes("ImageComplianceReviewer evidence vault v1") : ReviewerEngine.WrappedCheck(Generation);if(!check.SequenceEqual(expected))throw new CryptographicException("Incorrect password or damaged originals vault");if(WrapVersion==0){var key=derived;derived=null;return key;}material=Crypto.Decrypt(derived,WrapIv,ReviewerEngine.WrapAad(workspace,Generation),WrapCipher);if(material.Length!=32)throw new CryptographicException("Invalid wrapped content key");token.ThrowIfCancellationRequested();return new VaultKey(material);}
            finally{if(derived!=null)derived.Dispose();if(material!=null)Array.Clear(material,0,material.Length);if(check!=null)Array.Clear(check,0,check.Length);if(password!=null)Array.Clear(password,0,password.Length);}
        }
    }
    public sealed partial class ReviewerEngine {
        internal static byte[] WrappedCheck(string generation){return Encoding.UTF8.GetBytes("MediaComplianceReviewer.Windows vault envelope v1\n"+generation);}
        internal static byte[] WrapAad(string workspace,string generation){return Encoding.UTF8.GetBytes("{\"app\":\"MediaComplianceReviewer.Windows\",\"kind\":\"content-key\",\"version\":1,\"workspaceId\":"+EvidenceFormat.Quote(workspace)+",\"generation\":"+EvidenceFormat.Quote(generation)+"}");}
        VaultKey OpenVaultKey(SqliteConnection db,byte[] password,CancellationToken token){
            try{return VaultDescriptor.Read(db).Open(EvidenceFormat.Workspace(db),password,token);}finally{if(password!=null)Array.Clear(password,0,password.Length);}
        }
        public string RequestVaultPasswordChange(byte[] currentPassword,byte[] newPassword,string operationId){
            Action clear=()=>{if(currentPassword!=null)Array.Clear(currentPassword,0,currentPassword.Length);if(newPassword!=null)Array.Clear(newPassword,0,newPassword.Length);};
            if(currentPassword==null || currentPassword.Length<1 || currentPassword.Length>65536 || newPassword==null || newPassword.Length<1 || newPassword.Length>65536 || Object.ReferenceEquals(currentPassword,newPassword) || String.IsNullOrEmpty(operationId) || operationId.Length>100){clear();throw new ArgumentException("Enter bounded current and new passwords and a valid operation identifier");}
            long epoch=InvalidateVault();
            return EnqueueOperation("VaultPasswordChange",(db,t,p)=>{
                lock(vaultStateGate){t.ThrowIfCancellationRequested();if(epoch!=VaultGeneration)throw new OperationCanceledException();ClearVaultWorker();}
                string ws=EvidenceFormat.Workspace(db),payload=Identity.Digest("vault-password-change/v1\n"+ws);VaultKey content=null,wrapping=null;byte[] material=null;
                try{
                    if(Verification.Reconciled(db,operationId,payload)){using(var proven=OpenVaultKey(db,newPassword,t)){}return new OriginalReceipt {Kind="VaultPasswordChange",OperationId=operationId,Reconciled=true,Message="Password change was already committed. Originals remain locked."};}
                    p(0,2,"password keys derived");content=OpenVaultKey(db,currentPassword,t);byte[] salt=EvidenceFormat.Random(16);wrapping=Crypto.DeriveKey(newPassword,salt,600000);t.ThrowIfCancellationRequested();p(1,2,"password keys derived");
                    string generation=Guid.NewGuid().ToString("N");byte[] iv=EvidenceFormat.Random(12),checkIv=EvidenceFormat.Random(12);material=content.ExportMaterial();byte[] cipher=Crypto.Encrypt(wrapping,iv,WrapAad(ws,generation),material),check=Crypto.Encrypt(wrapping,checkIv,EvidenceFormat.CheckAad(ws),WrappedCheck(generation));
                    byte[] roundtrip=Crypto.Decrypt(wrapping,iv,WrapAad(ws,generation),cipher);try{if(!roundtrip.SequenceEqual(material))throw new CryptographicException("Content-key envelope verification failed");}finally{Array.Clear(roundtrip,0,roundtrip.Length);}
                    t.ThrowIfCancellationRequested();WaitWrite(w=>{Verification.Transaction(w,()=>{
                        t.ThrowIfCancellationRequested();if(epoch!=VaultGeneration)throw new OperationCanceledException();if(Verification.Reconciled(w,operationId,payload))return;
                        w.Execute("UPDATE evidence_vault SET salt=?,iterations=600000,check_iv=?,check_ciphertext=?,created_at=? WHERE id=1",salt,checkIv,check,Catalog.Now());
                        w.Execute("INSERT INTO vault_key_wrap VALUES(1,1,?,?,?) ON CONFLICT(id) DO UPDATE SET version=excluded.version,generation=excluded.generation,iv=excluded.iv,ciphertext=excluded.ciphertext",generation,iv,cipher);
                        w.Execute("INSERT INTO operations VALUES(?,?,?,?)",operationId,payload,Catalog.Now(),Catalog.Revision(w));Catalog.Log(w,"info","Originals password","","Password change committed; originals locked. Earlier database copies retain their previous password.");
                    });return null;});p(2,2,"password keys derived");return new OriginalReceipt {Kind="VaultPasswordChange",OperationId=operationId,Message="Originals password changed; originals locked. Earlier database copies still use their previous password."};
                }finally{if(content!=null)content.Dispose();if(wrapping!=null)wrapping.Dispose();if(material!=null)Array.Clear(material,0,material.Length);clear();}
            },4096+currentPassword.Length+newPassword.Length,clear);
        }
        public static void ValidateEvidenceReclassify(ReviewRequest request){EvidenceFormat.Validate(request,true);}
        public string RequestEvidenceReclassify(ReviewRequest request){
            EvidenceFormat.Validate(request,true);var r=request.Copy();r.GroupIds=r.GroupIds.Distinct().OrderBy(x=>x).ToArray();long epoch=VaultGeneration;
            return EnqueueOperation("EvidenceReclassify",(db,t,p)=>{
                string payload=Identity.Digest("evidence-reclassify/v1\n"+EvidenceFormat.Payload(r));if(Verification.Reconciled(db,r.OperationId,payload))return new OriginalReceipt {OperationId=r.OperationId,GroupIds=r.GroupIds,Reconciled=true,Message="Evidence reclassification was already committed."};
                var prepared=new List<object[]>();int done=0;
                foreach(long group in r.GroupIds){RequireVault(epoch,t);var rows=db.Query("SELECT content_id,legacy_key,status,review_revision FROM review_groups WHERE id=? AND retired=0",new object[]{group},1);long expected;
                    if(rows.Count!=1 || (string)rows[0][2]!="EVIDENCE" || (r.ExpectedRevisions!=null && (!r.ExpectedRevisions.TryGetValue(group,out expected) || expected!=(long)rows[0][3])))throw new InvalidOperationException("A selected Evidence review changed. Refresh before reclassifying it.");
                    using(var original=ReadOriginal(db,group,epoch,t,p,null)){prepared.Add(new object[]{group,rows[0][0],rows[0][1],rows[0][3],original.LegacyKey,original.Hash,original.Size});}p(++done,r.GroupIds.Length,"originals authenticated");
                }
                RequireVault(epoch,t);WaitWrite(w=>{Verification.Transaction(w,()=>{RequireVault(epoch,t);if(Verification.Reconciled(w,r.OperationId,payload))return;
                    foreach(var row in prepared){if(w.Scalar("SELECT 1 FROM review_groups WHERE id=? AND retired=0 AND content_id=? AND legacy_key=? AND status='EVIDENCE' AND review_revision=?",row[0],row[1],row[2],row[3])==null)throw new InvalidOperationException("A selected Evidence review changed; its newer decision was preserved.");
                        if(row[4]!=null)w.Execute("INSERT INTO legacy_original_links VALUES(?,?,?,?) ON CONFLICT(group_id) DO UPDATE SET decision_key=excluded.decision_key,hash=excluded.hash,size=excluded.size",row[0],row[4],row[5],row[6]);
                        w.Execute("UPDATE review_groups SET status=?,reviewer=?,notes=?,reviewed_at=? WHERE id=?",r.Status,r.Reviewer,r.Notes,Catalog.Now(),row[0]);ImportedFields.Delete(w,"review_groups",(long)row[0],"notes");ImportedFields.Delete(w,"review_groups",(long)row[0],"reviewer");
                        w.Execute("INSERT INTO review_events VALUES(?,?,?,?,?,?,?,?,?)",Guid.NewGuid().ToString(),row[0],row[2],"EVIDENCE",r.Status,Catalog.Now(),r.Reviewer,r.Notes,r.OperationId);
                    }w.Execute("INSERT INTO operations VALUES(?,?,?,?)",r.OperationId,payload,Catalog.Now(),Catalog.Bump(w));Catalog.Log(w,"info","Evidence reclassification","","Committed "+prepared.Count+" authenticated Evidence transitions; encrypted originals retained.");
                });return null;});return new OriginalReceipt {OperationId=r.OperationId,GroupIds=r.GroupIds,Message="Evidence reclassified; authenticated encrypted originals retained."};
            },4096+(r.Notes.Length+r.Reviewer.Length)*2+r.GroupIds.Length*128);
        }
    }
}
