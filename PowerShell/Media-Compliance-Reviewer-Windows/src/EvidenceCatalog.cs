using System;
using System.Collections.Generic;
namespace MediaReviewer {
    public static partial class Catalog {
        // Protected cards do not require a reachable or exposed source location.
        internal static PageData ProtectedPage(SqliteConnection db,PageRequest r){
            var data=new PageData {Totals=Totals(db),Revision=Revision(db),PageSize=r.PageSize,Refill=r.ExistingIds.Length>0};
            string filter="status='EVIDENCE' AND retired=0";var args=new List<object>();if(!String.IsNullOrEmpty(r.RootId)){filter+=" AND root_id=?";args.Add(r.RootId);}
            if(!String.IsNullOrEmpty(r.Search) || !String.IsNullOrEmpty(r.Folder) || !String.IsNullOrEmpty(r.Type))filter+=" AND 0";
            data.Total=Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM review_groups WHERE "+filter,args.ToArray()));data.Page=(int)Math.Min(r.Page,Math.Max(1,(data.Total+r.PageSize-1)/r.PageSize));
            args.Add(r.PageSize+r.ExistingIds.Length);args.Add((long)(data.Page-1)*r.PageSize);var existing=new HashSet<long>(r.ExistingIds);
            foreach(var row in db.Query("SELECT id,content_id,display_sequence,root_id,review_revision FROM review_groups WHERE "+filter+" ORDER BY display_sequence LIMIT ? OFFSET ?",args.ToArray(),400)){
                if(existing.Contains((long)row[0]) || data.Rows.Count>=Math.Max(0,r.PageSize-r.ExistingIds.Length))continue;
                data.Rows.Add(new MediaRow {GroupId=(long)row[0],ContentId=(long)row[1],Sequence=(long)row[2],RootId=(string)row[3],RootPath="",RelativePath="",Name="Protected original "+row[0],Extension="",Hash="",Status="EVIDENCE",Reviewer="",Notes="",PreviewState="protected",PreviewError="Unlock and inspect the stored original",Algorithm="sha256",OriginalRetained=true,ReviewRevision=(long)row[4]});
            }return data;
        }
    }
}
