// TCIA DICOM Downloader
// Search the public collections of The Cancer Imaging Archive (TCIA) and download
// DICOM series straight into a folder (by default Downloads\TCIA).
// Built with the C# compiler that ships with Windows (.NET Framework 4.8), so it needs nothing installed.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("TCIA DICOM Downloader")]
[assembly: AssemblyProduct("TCIA DICOM Downloader")]
[assembly: AssemblyDescription("Search and download DICOM data from The Cancer Imaging Archive")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace TciaDownloader
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; }
            catch (NotSupportedException) { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
            ServicePointManager.DefaultConnectionLimit = 24;

            if (args.Length >= 2 && args[0] == "--write-icon")
            {
                File.WriteAllBytes(args[1], AppIcon.BuildIco(new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }));
                return 0;
            }
            if (args.Length >= 2 && args[0] == "--selftest")
                return SelfTest.Run(args[1]).GetAwaiter().GetResult();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
                MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message, "TCIA Downloader",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Application.Run(new MainForm(args));
            return 0;
        }
    }

    // ------------------------------------------------------------------ data

    enum QState { Pending, Active, Done, Skipped, Failed, Canceled }

    class CollectionInfo
    {
        public string Name = "", Title = "", CancerTypes = "", Locations = "", Subjects = "", Species = "", Link = "", Abstract = "", Doi = "";
        public List<string> Modalities = new List<string>();
        public bool ModalitiesLoaded;
        string[] words = new string[0];
        string nameLower = "";

        public string TypeText { get { return CancerTypes != "" ? CancerTypes : Locations; } }
        public string ModText { get { return ModalitiesLoaded ? string.Join(", ", Modalities) : "…"; } }
        public int SubjectsNum { get { int n; return int.TryParse(Subjects.Replace(",", "").Trim(), out n) ? n : -1; } }
        public string SubjectsText { get { int n = SubjectsNum; return n >= 0 ? n.ToString("N0") : Subjects; } }

        public void UpdateSearch()
        {
            string all = Name + " " + Title + " " + CancerTypes + " " + Locations + " " + Species + " " + string.Join(" ", Modalities);
            words = Regex.Split(all.ToLowerInvariant(), "[^a-z0-9]+").Where(w => w != "").Distinct().ToArray();
            nameLower = Name.ToLowerInvariant();
        }

        public bool Matches(string[] terms, string raw)
        {
            if (raw != "" && nameLower.Contains(raw)) return true;
            foreach (var t in terms)
            {
                bool ok = false;
                foreach (var w in words) if (w.StartsWith(t, StringComparison.Ordinal)) { ok = true; break; }
                if (!ok) return false;
            }
            return true;
        }
    }

    class SeriesInfo
    {
        public string SeriesUID = "", StudyUID = "", Collection = "", PatientID = "", Modality = "", SeriesDesc = "",
            StudyDesc = "", StudyDate = "", SeriesDate = "", BodyPart = "", SeriesNumber = "";
        public int ImageCount;
        public long FileSize;

        public static SeriesInfo From(Dictionary<string, object> d)
        {
            var s = new SeriesInfo();
            s.SeriesUID = J.S(d, "SeriesInstanceUID");
            s.StudyUID = J.S(d, "StudyInstanceUID");
            s.Collection = J.S(d, "Collection");
            s.PatientID = J.S(d, "PatientID");
            s.Modality = J.S(d, "Modality");
            s.SeriesDesc = J.S(d, "SeriesDescription");
            if (s.SeriesDesc == "") s.SeriesDesc = J.S(d, "ProtocolName");
            s.StudyDesc = J.S(d, "StudyDesc");
            if (s.StudyDesc == "") s.StudyDesc = J.S(d, "StudyDescription");
            s.StudyDate = Util.DateOnly(J.S(d, "StudyDate"));
            s.SeriesDate = Util.DateOnly(J.S(d, "SeriesDate"));
            s.BodyPart = J.S(d, "BodyPartExamined");
            s.SeriesNumber = Util.CleanNumber(J.S(d, "SeriesNumber"));
            s.ImageCount = (int)J.L(d, "ImageCount");
            s.FileSize = J.L(d, "FileSize");
            return s;
        }

        public static int Compare(SeriesInfo a, SeriesInfo b)
        {
            int r = string.CompareOrdinal(a.StudyDate, b.StudyDate); if (r != 0) return r;
            r = string.CompareOrdinal(a.StudyUID, b.StudyUID); if (r != 0) return r;
            double x, y;
            bool hx = double.TryParse(a.SeriesNumber, NumberStyles.Float, CultureInfo.InvariantCulture, out x);
            bool hy = double.TryParse(b.SeriesNumber, NumberStyles.Float, CultureInfo.InvariantCulture, out y);
            if (hx && hy && x != y) return x.CompareTo(y);
            if (hx != hy) return hx ? -1 : 1;
            return string.CompareOrdinal(a.Modality + a.SeriesDesc, b.Modality + b.SeriesDesc);
        }
    }

    class QueueItem
    {
        public SeriesInfo S;
        public string Root, Folder, CollectionDir;
        public volatile QState State;
        public volatile string Status = "Waiting";
        public long Received;
        public long ZipBytes;
        public int Files;
        public CancellationTokenSource Cts;
        public bool Seen;
    }

    class ModItem
    {
        public string Code, Label; public int Count;
        public ModItem(string code, string label, int count) { Code = code; Label = label; Count = count; }
        public override string ToString()
        {
            return Count > 0 ? Label + "   · " + Count + (Count == 1 ? " collection" : " collections") : Label;
        }
    }

    class PermanentException : Exception { public PermanentException(string m) : base(m) { } }

    // ------------------------------------------------------------------ JSON helpers

    static class J
    {
        public static string S(Dictionary<string, object> d, string k)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return "";
            if (v is string) return ((string)v).Trim();
            var dict = v as Dictionary<string, object>;
            if (dict != null) return S(dict, "rendered");
            if (v is IEnumerable)
            {
                var parts = new List<string>();
                foreach (var o in (IEnumerable)v)
                {
                    if (o == null) continue;
                    string s = WebUtility.HtmlDecode(Convert.ToString(o, CultureInfo.InvariantCulture)).Trim();
                    if (s != "" && !parts.Contains(s)) parts.Add(s);
                }
                return string.Join(", ", parts);
            }
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static long L(Dictionary<string, object> d, string k)
        {
            double x;
            return double.TryParse(S(d, k), NumberStyles.Float, CultureInfo.InvariantCulture, out x) ? (long)x : 0;
        }

        public static List<Dictionary<string, object>> ParseList(string s)
        {
            var list = new List<Dictionary<string, object>>();
            if (string.IsNullOrWhiteSpace(s)) return list;
            s = s.Trim();
            if (!s.StartsWith("[") && !s.StartsWith("{")) return list;
            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            ser.RecursionLimit = 256;
            object o = ser.DeserializeObject(s);
            var one = o as Dictionary<string, object>;
            if (one != null) { list.Add(one); return list; }
            var arr = o as IEnumerable;
            if (arr != null)
                foreach (var x in arr) { var d = x as Dictionary<string, object>; if (d != null) list.Add(d); }
            return list;
        }
    }

    // ------------------------------------------------------------------ TCIA web services

    static class Tcia
    {
        public const string Base = "https://services.cancerimagingarchive.net/nbia-api/services/v1/";
        public const string Web = "https://www.cancerimagingarchive.net/api/v1/collections/";
        static readonly HttpClient http;

        static Tcia()
        {
            var h = new HttpClientHandler();
            h.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            http = new HttpClient(h);
            http.Timeout = Timeout.InfiniteTimeSpan;
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) TCIA-Downloader/1.0");
        }

        public static HttpClient Http { get { return http; } }

        public static async Task<string> GetString(string url, CancellationToken ct, int timeoutSec)
        {
            for (int attempt = 1; ; attempt++)
            {
                string err = null;
                bool retry = true;
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                    try
                    {
                        using (var resp = await http.GetAsync(url, cts.Token))
                        {
                            if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsStringAsync();
                            int code = (int)resp.StatusCode;
                            err = "the server answered " + code + " " + resp.ReasonPhrase;
                            retry = code >= 500 || code == 429;
                        }
                    }
                    catch (OperationCanceledException) { if (ct.IsCancellationRequested) throw; err = "the request timed out"; }
                    catch (HttpRequestException ex) { err = Util.Inner(ex); }
                }
                if (!retry || attempt >= 3) throw new HttpRequestException(err);
                await Task.Delay(1000 * attempt, ct);
            }
        }

        public static async Task<List<Dictionary<string, object>>> GetList(string pathAndQuery, CancellationToken ct)
        {
            return J.ParseList(await GetString(Base + pathAndQuery, ct, 120));
        }

        // Collection descriptions (cancer type, site, subjects, abstract) from the TCIA website.
        public static async Task<List<Dictionary<string, object>>> GetWebCollections(CancellationToken ct)
        {
            const string fields = "slug,link,title,cancer_types,cancer_locations,subjects,collection_title,collection_short_title,species,collection_doi," +
                "collection_abstract,collection_summary,collection_introduction";
            Func<int, string> url = p => Web + "?per_page=100&page=" + p + "&_fields=" + fields;
            var all = new List<Dictionary<string, object>>();
            int pages = 5;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(90));
                using (var resp = await http.GetAsync(url(1), cts.Token))
                {
                    resp.EnsureSuccessStatusCode();
                    IEnumerable<string> vals;
                    int n;
                    if (resp.Headers.TryGetValues("X-WP-TotalPages", out vals) && int.TryParse(vals.FirstOrDefault(), out n)) pages = n;
                    all.AddRange(J.ParseList(await resp.Content.ReadAsStringAsync()));
                }
            }
            if (pages > 1)
            {
                var tasks = Enumerable.Range(2, Math.Min(pages, 30) - 1).Select(p => GetString(url(p), ct, 90)).ToList();
                foreach (var t in tasks)
                {
                    try { all.AddRange(J.ParseList(await t)); }
                    catch (HttpRequestException) { }
                }
            }
            return all;
        }
    }

    // ------------------------------------------------------------------ downloading

    static class Downloader
    {
        public static long TotalReceived;
        public static double ZipRatio = 0.62;   // learned: zip bytes / uncompressed bytes
        static int ratioSamples;
        static readonly object ratioLock = new object();
        public const string StDownloading = "Downloading";
        public const string StUnpacking = "Unpacking…";
        static readonly TimeSpan Stall = TimeSpan.FromSeconds(120);

        public static string CollectionDir(string root, SeriesInfo s)
        {
            return Path.Combine(root, Util.Clean(s.Collection, 48, "Unknown collection"));
        }

        // <root>\<Collection>\<Patient>\<StudyDate StudyDesc (uid)>\<No Modality SeriesDesc (uid)>\1-001.dcm
        public static string SeriesFolder(string root, SeriesInfo s)
        {
            string coll = CollectionDir(root, s);
            string pat = Util.Clean(s.PatientID, 48, "Unknown patient");
            string study = Util.Clean(Util.JoinNonEmpty(" ", s.StudyDate, s.StudyDesc), 40, "Study") + " (" + Util.Tail(s.StudyUID, 5) + ")";
            string ser = Util.Clean(Util.JoinNonEmpty(" ", s.SeriesNumber, s.Modality, s.SeriesDesc), 44, "Series") + " (" + Util.Tail(s.SeriesUID, 5) + ")";
            string p = Path.Combine(coll, pat, study, ser);
            if (p.Length > 205)
            {
                study = Util.Clean(s.StudyDate, 12, "Study") + " (" + Util.Tail(s.StudyUID, 5) + ")";
                ser = Util.Clean(Util.JoinNonEmpty(" ", s.SeriesNumber, s.Modality), 16, "Series") + " (" + Util.Tail(s.SeriesUID, 5) + ")";
                p = Path.Combine(coll, pat, study, ser);
            }
            return p;
        }

        public static async Task DownloadOne(QueueItem q, CancellationToken ct)
        {
            SeriesInfo s = q.S;
            if (Util.IsComplete(q.Folder, s.ImageCount))
            {
                q.Status = "Already on your computer";
                q.State = QState.Skipped;
                return;
            }

            string partial = Path.Combine(q.Root, ".partial");
            Directory.CreateDirectory(partial);
            try { File.SetAttributes(partial, File.GetAttributes(partial) | FileAttributes.Hidden); } catch (IOException) { }

            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(q.Root)));
                if (s.FileSize > 0 && drive.AvailableFreeSpace < s.FileSize * 2 + 200L * 1024 * 1024)
                    throw new PermanentException("Not enough free disk space on " + drive.Name);
            }
            catch (ArgumentException) { }

            string zipPath = Path.Combine(partial, Util.Clean(s.SeriesUID, 80, "series") + ".zip");
            string url = Tcia.Base + "getImage?NewFileNames=Yes&SeriesInstanceUID=" + Uri.EscapeDataString(s.SeriesUID);
            const int MaxAttempts = 4;
            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                Interlocked.Exchange(ref q.Received, 0);
                q.Status = attempt == 1 ? "Connecting…" : "Retrying (" + attempt + " of " + MaxAttempts + ")…";
                string err = null;
                try { await FetchToFile(url, zipPath, q, ct); }
                catch (OperationCanceledException) { if (ct.IsCancellationRequested) throw; err = "the connection stalled"; }
                catch (HttpRequestException ex) { if (ct.IsCancellationRequested) throw new OperationCanceledException(ct); err = Util.Inner(ex); }
                catch (IOException ex) { if (ct.IsCancellationRequested) throw new OperationCanceledException(ct); err = ex.Message; }
                catch (WebException ex) { if (ct.IsCancellationRequested) throw new OperationCanceledException(ct); err = ex.Message; }
                catch (ObjectDisposedException) { if (ct.IsCancellationRequested) throw new OperationCanceledException(ct); err = "the connection stalled"; }
                if (err == null) break;
                Util.TryDelete(zipPath);
                if (attempt >= MaxAttempts) throw new IOException("Download failed: " + err);
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct);
            }

            q.ZipBytes = new FileInfo(zipPath).Length;
            if (q.ZipBytes < 22)
            {
                Util.TryDelete(zipPath);
                throw new PermanentException("TCIA sent no data for this series (it may be limited-access)");
            }

            q.Status = StUnpacking;
            string tmp = q.Folder + ".part";
            int files = 0;
            try
            {
                Util.TryDeleteDir(tmp);
                Directory.CreateDirectory(tmp);
                using (var za = ZipFile.OpenRead(zipPath))
                {
                    foreach (var e in za.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        string name = Path.GetFileName(e.FullName.Replace('/', '\\'));
                        if (string.IsNullOrEmpty(name)) continue;
                        if (name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LICENSE.", StringComparison.OrdinalIgnoreCase))
                        {
                            string lic = Path.Combine(q.CollectionDir, "LICENSE.txt");
                            try { if (!File.Exists(lic)) e.ExtractToFile(lic, false); } catch (IOException) { }
                            continue;
                        }
                        e.ExtractToFile(Path.Combine(tmp, name), true);
                        files++;
                    }
                }
                if (files == 0) throw new PermanentException("The download contained no DICOM files");
                Util.TryDeleteDir(q.Folder);
                Directory.Move(tmp, q.Folder);
            }
            catch (InvalidDataException)
            {
                Util.TryDeleteDir(tmp);
                throw new IOException("The downloaded file was damaged - please retry");
            }
            catch
            {
                Util.TryDeleteDir(tmp);
                throw;
            }
            finally
            {
                Util.TryDelete(zipPath);
            }

            if (s.FileSize > 0)
            {
                double r = (double)q.ZipBytes / s.FileSize;
                lock (ratioLock)
                {
                    if (r > 0.05 && r < 1.5)
                    {
                        ZipRatio = (ZipRatio * (ratioSamples + 1) + r) / (ratioSamples + 2);
                        ratioSamples = Math.Min(ratioSamples + 1, 20);
                    }
                }
            }
            q.Files = files;
            q.Status = "Done · " + files.ToString("N0") + (files == 1 ? " file" : " files");
            q.State = QState.Done;
        }

        static async Task FetchToFile(string url, string path, QueueItem q, CancellationToken ct)
        {
            using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                stall.CancelAfter(Stall);
                using (var resp = await Tcia.Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token))
                using (stall.Token.Register(() => { try { resp.Dispose(); } catch (Exception) { } }))
                {
                    int code = (int)resp.StatusCode;
                    if (code >= 400 && code < 500 && code != 408 && code != 429)
                        throw new PermanentException("TCIA refused this series (" + code + "); it may be limited-access");
                    if (!resp.IsSuccessStatusCode) throw new HttpRequestException("the server answered " + code + " " + resp.ReasonPhrase);
                    q.Status = StDownloading;
                    using (var src = await resp.Content.ReadAsStreamAsync())
                    using (var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                    {
                        var buf = new byte[1 << 17];
                        while (true)
                        {
                            int n = await src.ReadAsync(buf, 0, buf.Length, stall.Token);
                            if (n <= 0) break;
                            await dst.WriteAsync(buf, 0, n);
                            Interlocked.Add(ref q.Received, n);
                            Interlocked.Add(ref TotalReceived, n);
                            stall.CancelAfter(Stall);
                        }
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ utilities

    static class Util
    {
        static readonly char[] Bad = Path.GetInvalidFileNameChars();
        static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
        static readonly string[] ModOrder = { "CT", "MR", "PT", "NM", "CR", "DX", "MG", "US", "XA", "RF", "RTSTRUCT", "RTPLAN", "RTDOSE", "RTIMAGE", "RTRECORD", "SEG", "REG", "SR", "PR", "KO", "SM", "OT" };

        public static string Clean(string s, int max, string fallback)
        {
            var sb = new StringBuilder();
            foreach (char ch in (s ?? "")) sb.Append(Array.IndexOf(Bad, ch) >= 0 || char.IsControl(ch) ? '_' : ch);
            string r = Regex.Replace(sb.ToString(), @"\s+", " ").Trim().Trim('.', ' ');
            if (r.Length > max) r = r.Substring(0, max).Trim().Trim('.', ' ');
            if (r == "") r = fallback;
            if (Reserved.Contains(r)) r = "_" + r;
            return r;
        }

        public static string Tail(string s, int n) { s = s ?? ""; return s.Length <= n ? s : s.Substring(s.Length - n); }

        public static string JoinNonEmpty(string sep, params string[] parts)
        {
            return string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
        }

        public static string Inner(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }

        public static string DateOnly(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var m = Regex.Match(s, @"^\d{4}-\d{2}-\d{2}");
            if (m.Success) return m.Value;
            m = Regex.Match(s, @"^(\d{2})-(\d{2})-(\d{4})");
            if (m.Success) return m.Groups[3].Value + "-" + m.Groups[1].Value + "-" + m.Groups[2].Value;
            return s.Length > 10 ? s.Substring(0, 10) : s;
        }

        public static string CleanNumber(string s)
        {
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && Math.Abs(d - Math.Round(d)) < 1e-9)
                return ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
            return s ?? "";
        }

        public static string Size(long b)
        {
            if (b <= 0) return "";
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            string num = i == 0 ? v.ToString("0") : v < 10 ? v.ToString("0.0") : v.ToString("0");
            return num + " " + u[i];
        }

        public static string Duration(double sec)
        {
            if (double.IsNaN(sec) || double.IsInfinity(sec) || sec < 0) return "";
            if (sec < 60) return "less than a minute";
            if (sec < 3600) return (int)Math.Ceiling(sec / 60) + " min";
            double h = sec / 3600;
            return h < 10 ? h.ToString("0.0") + " hours" : (int)h + " hours";
        }

        public static string ShortPath(string p, int max)
        {
            if (p == null || p.Length <= max) return p;
            string root = Path.GetPathRoot(p) ?? "";
            var parts = p.Substring(root.Length).TrimEnd('\\').Split('\\');
            string tail = parts[parts.Length - 1];
            for (int i = parts.Length - 2; i >= 0; i--)
            {
                string cand = parts[i] + "\\" + tail;
                if (root.Length + 2 + cand.Length > max) break;
                tail = cand;
            }
            return root + "…\\" + tail;
        }

        public static string SeriesLabel(SeriesInfo s) { return s.SeriesDesc != "" ? s.SeriesDesc : "(no description)"; }

        public static string Norm(string s) { return Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", ""); }

        public static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            string s = Regex.Replace(html, @"<\s*(br|/p|/li|/h\d|/div)[^>]*>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<\s*li[^>]*>", "\n• ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "<[^>]+>", "");
            s = WebUtility.HtmlDecode(s).Replace(' ', ' ');
            s = Regex.Replace(s, @"[ \t\r\f\v]+", " ");
            s = Regex.Replace(s, @" *\n *", "\n");
            s = Regex.Replace(s, @"\n{2,}", "\n\n").Trim();
            return s.Replace("\n", "\r\n");
        }

        public static bool IsComplete(string folder, int count)
        {
            try { return Directory.Exists(folder) && Directory.GetFiles(folder).Length >= Math.Max(1, count); }
            catch (Exception) { return false; }
        }

        public static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch (Exception) { } }
        public static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (Exception) { } }

        public static List<string> SortModalities(IEnumerable<string> mods)
        {
            return mods.Distinct().OrderBy(m => { int i = Array.IndexOf(ModOrder, m); return i < 0 ? 100 : i; })
                .ThenBy(m => m, StringComparer.Ordinal).ToList();
        }

        public static string ModalityLabel(string m)
        {
            switch (m)
            {
                case "MR": return "MR (MRI)";
                case "PT": return "PT (PET)";
                case "NM": return "NM (nuclear medicine)";
                case "CR": return "CR (X-ray)";
                case "DX": return "DX (X-ray)";
                case "MG": return "MG (mammography)";
                case "US": return "US (ultrasound)";
                case "XA": return "XA (angiography)";
                case "RTSTRUCT": return "RTSTRUCT (contours)";
                case "RTPLAN": return "RTPLAN (treatment plan)";
                case "RTDOSE": return "RTDOSE (dose)";
                case "RTIMAGE": return "RTIMAGE (portal images)";
                case "SEG": return "SEG (segmentation)";
                case "REG": return "REG (registration)";
                case "SR": return "SR (structured report)";
                case "PR": return "PR (presentation state)";
                case "SM": return "SM (slide microscopy)";
                case "OT": return "OT (other)";
                default: return m;
            }
        }

        // A .tcia manifest is a few key=value lines followed by one SeriesInstanceUID per line.
        public static List<string> ParseManifest(IEnumerable<string> lines)
        {
            return lines.Select(l => l.Trim()).Where(l => Regex.IsMatch(l, @"^[0-9]+(\.[0-9]+){2,}$")).Distinct().ToList();
        }
    }

    // ------------------------------------------------------------------ Windows interop

    static class Native
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] static extern IntPtr SendMessageStr(IntPtr h, int msg, IntPtr w, string l);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr SendMessageItem(IntPtr h, int msg, IntPtr w, ref LVITEM l);
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] public static extern int StrCmpLogicalW(string a, string b);
        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
        [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr h, int index);
        public static bool HasVScroll(Control c) { return c.IsHandleCreated && (GetWindowLong(c.Handle, -16) & 0x00200000) != 0; }
        [DllImport("user32.dll", EntryPoint = "SendMessageW")] public static extern IntPtr SendMessagePtr(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool FlashWindowEx(ref FLASHWINFO f);

        [StructLayout(LayoutKind.Sequential)]
        struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

        [StructLayout(LayoutKind.Sequential)]
        struct LVITEM
        {
            public int mask, iItem, iSubItem, state, stateMask;
            public IntPtr pszText; public int cchTextMax, iImage; public IntPtr lParam;
            public int iIndent, iGroupId, cColumns; public IntPtr puColumns, piColFmt; public int iGroup;
        }

        public static string Downloads()
        {
            try
            {
                IntPtr p;
                if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) == 0)
                {
                    string s = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch (Exception) { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        public static void SetCue(TextBox tb, string text)
        {
            if (tb.IsHandleCreated) SendMessageStr(tb.Handle, 0x1501, (IntPtr)1, text ?? "");
        }

        public static void Cue(TextBox tb, string text)
        {
            tb.HandleCreated += (s, e) => SendMessageStr(tb.Handle, 0x1501, (IntPtr)1, text);
            if (tb.IsHandleCreated) SendMessageStr(tb.Handle, 0x1501, (IntPtr)1, text);
        }

        public static void SelectAll(ListView lv)
        {
            var it = new LVITEM();
            it.stateMask = 2; it.state = 2;   // LVIS_SELECTED
            SendMessageItem(lv.Handle, 0x102B, (IntPtr)(-1), ref it);   // LVM_SETITEMSTATE, all items
        }

        public static void Flash(IntPtr h)
        {
            var f = new FLASHWINFO();
            f.cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO));
            f.hwnd = h; f.dwFlags = 3 | 12; f.uCount = 5;   // FLASHW_ALL | FLASHW_TIMERNOFG
            FlashWindowEx(ref f);
        }
    }

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    class TaskbarInstance { }

    static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogCom { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        public static string Pick(IWin32Window owner, string title, string initial)
        {
            try
            {
                var dlg = (IFileDialog)new FileOpenDialogCom();
                uint opts;
                dlg.GetOptions(out opts);
                dlg.SetOptions(opts | 0x20 | 0x40);   // FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM
                dlg.SetTitle(title);
                dlg.SetOkButtonLabel("Save here");
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
                {
                    IShellItem start;
                    SHCreateItemFromParsingName(initial, IntPtr.Zero, typeof(IShellItem).GUID, out start);
                    dlg.SetFolder(start);
                }
                if (dlg.Show(owner.Handle) != 0) return null;
                IShellItem res;
                dlg.GetResult(out res);
                string path;
                res.GetDisplayName(0x80058000, out path);   // SIGDN_FILESYSPATH
                return path;
            }
            catch (COMException) { }
            catch (InvalidCastException) { }
            using (var d = new FolderBrowserDialog())
            {
                d.Description = title; d.SelectedPath = initial; d.ShowNewFolderButton = true;
                return d.ShowDialog(owner) == DialogResult.OK ? d.SelectedPath : null;
            }
        }
    }

    // ------------------------------------------------------------------ icon

    static class AppIcon
    {
        public static Bitmap Render(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float s = size, pad = Math.Max(0.5f, s * 0.04f);
                var r = new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad);
                using (var path = Rounded(r, s * 0.22f))
                using (var br = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s), Color.FromArgb(124, 134, 240), Color.FromArgb(52, 56, 150)))
                    g.FillPath(br, path);
                float rw = Math.Max(1.4f, s * 0.085f);
                using (var pen = new Pen(Color.FromArgb(110, 255, 255, 255), rw))
                    g.DrawEllipse(pen, s * 0.20f, s * 0.20f, s * 0.60f, s * 0.60f);
                using (var b = new SolidBrush(Color.White))
                {
                    float cx = s * 0.5f, w = Math.Max(1.5f, s * 0.12f);
                    g.FillRectangle(b, cx - w / 2, s * 0.29f, w, s * 0.24f);
                    g.FillPolygon(b, new PointF[] { new PointF(s * 0.32f, s * 0.49f), new PointF(s * 0.68f, s * 0.49f), new PointF(cx, s * 0.72f) });
                }
            }
            return bmp;
        }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Icon MakeIcon() { return new Icon(new MemoryStream(BuildIco(new int[] { 16, 20, 24, 32, 40, 48, 64, 256 }))); }

        public static byte[] BuildIco(int[] sizes)
        {
            var images = new List<byte[]>();
            foreach (int sz in sizes) using (var bmp = Render(sz)) images.Add(sz >= 256 ? Png(bmp) : Dib(bmp));
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32); w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
            w.Flush();
            return ms.ToArray();
        }

        static byte[] Png(Bitmap b) { using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); } }

        static byte[] Dib(Bitmap b)
        {
            int n = b.Width;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(40); w.Write(n); w.Write(n * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(n * n * 4); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = n - 1; y >= 0; y--)
                for (int x = 0; x < n; x++) { Color c = b.GetPixel(x, y); w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A); }
            w.Write(new byte[((n + 31) / 32) * 4 * n]);
            w.Flush();
            return ms.ToArray();
        }
    }

    // ------------------------------------------------------------------ command-line self test (no window)

    static class SelfTest
    {
        public static async Task<int> Run(string outDir)
        {
            var log = new StringBuilder();
            Action<string> W = m => log.AppendLine(DateTime.Now.ToString("HH:mm:ss ") + m);
            int rc = 0;
            try
            {
                Directory.CreateDirectory(outDir);
                var sw = Stopwatch.StartNew();
                var cols = await Tcia.GetList("getCollectionValues", CancellationToken.None);
                W("collections: " + cols.Count + " (" + sw.ElapsedMilliseconds + " ms)");
                sw.Restart();
                var web = await Tcia.GetWebCollections(CancellationToken.None);
                W("website records: " + web.Count + " (" + sw.ElapsedMilliseconds + " ms)");
                var nsclc = web.FirstOrDefault(r => Util.Norm(J.S(r, "collection_short_title")) == "nsclcradiomics");
                W("NSCLC-Radiomics on website: types=" + J.S(nsclc, "cancer_types") + " | subjects=" + J.S(nsclc, "subjects"));
                var mods = await Tcia.GetList("getModalityValues?Collection=NSCLC-Radiomics", CancellationToken.None);
                W("NSCLC-Radiomics modalities: " + string.Join(", ", mods.Select(r => J.S(r, "Modality"))));
                var pats = await Tcia.GetList("getPatient?Collection=NSCLC-Radiomics", CancellationToken.None);
                W("NSCLC-Radiomics patients: " + pats.Count);
                var series = (await Tcia.GetList("getSeries?Collection=NSCLC-Radiomics&PatientID=LUNG1-001", CancellationToken.None))
                    .Select(SeriesInfo.From).ToList();
                series.Sort(SeriesInfo.Compare);
                W("LUNG1-001 series: " + string.Join(" | ", series.Select(s => s.Modality + " #" + s.SeriesNumber + " " + s.ImageCount + " img " + Util.Size(s.FileSize))));

                foreach (var s in series.Where(x => x.Modality == "RTSTRUCT" || x.Modality == "SEG"))
                {
                    var q = new QueueItem();
                    q.S = s; q.Root = outDir;
                    q.CollectionDir = Downloader.CollectionDir(outDir, s);
                    q.Folder = Downloader.SeriesFolder(outDir, s);
                    sw.Restart();
                    await Downloader.DownloadOne(q, CancellationToken.None);
                    W("download " + s.Modality + ": " + q.State + " / " + q.Status + " / zip " + Util.Size(q.ZipBytes) + " in " + sw.ElapsedMilliseconds + " ms");
                    W("  -> " + q.Folder);
                    W("  files: " + string.Join(", ", Directory.GetFiles(q.Folder).Select(Path.GetFileName)));
                    var q2 = new QueueItem();
                    q2.S = s; q2.Root = outDir; q2.CollectionDir = q.CollectionDir; q2.Folder = q.Folder;
                    await Downloader.DownloadOne(q2, CancellationToken.None);
                    W("  second attempt: " + q2.State + " / " + q2.Status);
                }
                W("learned zip ratio: " + Downloader.ZipRatio.ToString("0.00"));
                W("license present: " + File.Exists(Path.Combine(Downloader.CollectionDir(outDir, series[0]), "LICENSE.txt")));
                W("partial folder left behind: " + (Directory.Exists(Path.Combine(outDir, ".partial")) && Directory.GetFiles(Path.Combine(outDir, ".partial")).Length > 0));

                var manifest = new[] {
                    "downloadServerUrl=https://nbia.cancerimagingarchive.net/nbia-download/servlet/DownloadServlet",
                    "includeAnnotation=true", "noOfrRetry=4", "databasketId=manifest-123.tcia", "manifestVersion=3.0",
                    "ListOfSeriesToDownload=", series[0].SeriesUID, series[1].SeriesUID, "" };
                var parsed = Util.ParseManifest(manifest);
                W("manifest parse: " + parsed.Count + " series (expected 2)");
                var meta = await Tcia.GetList("getSeries?SeriesInstanceUID=" + Uri.EscapeDataString(parsed[0]), CancellationToken.None);
                W("manifest lookup: " + (meta.Count > 0 ? SeriesInfo.From(meta[0]).PatientID + " " + SeriesInfo.From(meta[0]).Modality : "not found"));
                var find = await Tcia.GetList("getSeries?PatientID=PANCREAS_0001", CancellationToken.None);
                W("find PANCREAS_0001: " + string.Join(", ", find.Select(r => J.S(r, "Collection")).Distinct()));
                W("downloads folder: " + Native.Downloads());
            }
            catch (Exception ex) { W("ERROR " + ex); rc = 1; }
            File.WriteAllText(Path.Combine(outDir, "selftest.log"), log.ToString());
            return rc;
        }
    }
}
