using System;
using System.Linq;
namespace MediaReviewer {
    public sealed class FormatFamily {
        public string Key,Label; public string[] Extensions;
    }
    public static class Formats {
        static FormatFamily Family(string key,string label,string extensions) { return new FormatFamily { Key=key,Label=label,Extensions=extensions.Split(',') }; }
        static readonly FormatFamily[] families={
            Family("jpeg","JPEG/JFIF","jpg,jpeg,jpe,jfif"),Family("png","PNG","png"),Family("gif","GIF","gif"),Family("bmp","BMP","bmp"),Family("webp","WebP","webp"),
            Family("avif","AVIF","avif"),Family("tiff","TIFF","tif,tiff"),Family("heif","HEIC/HEIF","heic,heif"),Family("ico","ICO","ico"),Family("svg","SVG","svg"),
            Family("raw","Camera RAW","dng,cr2,cr3,nef,nrw,arw,raf,orf,rw2,pef,x3f"),Family("video","Videos","mp4,m4v,mov,avi,wmv,mkv,webm,mpg,mpeg,mpe,mts,m2ts,ts")
        };
        public static FormatFamily[] Families { get { return families.Select(f=>Family(f.Key,f.Label,String.Join(",",f.Extensions))).ToArray(); } }
        public static string[] Deep { get { return families.SelectMany(f=>f.Extensions).ToArray(); } }
        public static string[] Quick { get { return families.Where(f=>f.Key!="raw").SelectMany(f=>f.Extensions).ToArray(); } }
        public static bool Supported(string extension) { return families.Any(f=>f.Extensions.Contains(extension)); }
        public static bool IsVideo(string extension) { return families.Last().Extensions.Contains(extension); }
        public static bool IsRaw(string extension) { return families.First(f=>f.Key=="raw").Extensions.Contains(extension); }
        public static string[] Filter(string key) {
            if(String.IsNullOrEmpty(key)) return new string[0];
            if(key=="image") return families.Where(f=>f.Key!="video" && f.Key!="raw").SelectMany(f=>f.Extensions).ToArray();
            var family=families.FirstOrDefault(f=>f.Key==key); if(family!=null) return (string[])family.Extensions.Clone();
            if(Supported(key)) return new[]{key}; throw new ArgumentException("Unknown media type filter");
        }
    }
}
