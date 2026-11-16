using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WinXplorer.Services
{
    /// <summary>
    /// The settings inside a view.wxv file: how WinXplorer shows the folder it sits in (a page of projects, projects
    /// grouped by category, or a gallery of files) instead of the plain file list. Plain JSON inside, so it can also be
    /// edited by hand; images the user adds go to a hidden ".wxv" folder next to it, so the folder travels whole.
    /// </summary>
    public sealed class WxvView
    {
        /// <summary>Marks the file as a WinXplorer view (and its format version).</summary>
        public string Format { get; set; } = "WinXplorer view";
        public int Version { get; set; } = 1;

        public bool Enabled { get; set; } = true;
        public string Title { get; set; }
        public string Subtitle { get; set; }

        /// <summary>folders = each subfolder is a project · categories = subfolders are categories holding projects · files = a gallery of files.</summary>
        public string Source { get; set; } = "folders";
        /// <summary>Gallery: which files (";"-separated masks) and whether subfolders are searched too.</summary>
        public string FilePattern { get; set; } = "*.jpg;*.jpeg;*.png;*.webp;*.gif;*.bmp;*.svg;*.heic;*.avif";
        public bool IncludeSubfolders { get; set; }

        /// <summary>Cards in one row.</summary>
        public int Columns { get; set; } = 5;
        /// <summary>below = picture with the name under it · hover = picture, name on mouse over · name = name only on mouse over, no meta.</summary>
        public string CardStyle { get; set; } = "below";
        /// <summary>folder = the folder name · project = the name in package.json / composer.json / pubspec.yaml (else the folder name).</summary>
        public string TitleFrom { get; set; } = "folder";
        public bool ShowCreated { get; set; } = true;
        public bool ShowAge { get; set; } = true;
        public bool ShowModified { get; set; }
        public bool ShowStats { get; set; } = true;
        /// <summary>show = like the rest · last = at the end · hide = not shown.</summary>
        public string DoneProjects { get; set; } = "show";
        /// <summary>name | created | modified</summary>
        public string Sort { get; set; } = "name";
        public bool SortDesc { get; set; }

        /// <summary>"Open in browser" button on web projects (index.html / index.php found, or ticked per project).</summary>
        public bool WebButton { get; set; } = true;
        /// <summary>{rel} = the project's path inside this folder (with /), {name} = its folder name.</summary>
        public string UrlTemplate { get; set; } = "http://localhost/{rel}/";

        /// <summary>Per project / file, by its path relative to the view's folder.</summary>
        public Dictionary<string, WxvItem> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public WxvItem Item(string rel) => Items.TryGetValue(rel, out var i) ? i : null;
        public WxvItem EnsureItem(string rel) => Items.TryGetValue(rel, out var i) ? i : Items[rel] = new WxvItem();
    }

    public sealed class WxvItem
    {
        public string Title { get; set; }
        /// <summary>Picture chosen by the user: ".wxv/…" (copied next to the view) or a full path.</summary>
        public string Image { get; set; }
        public bool Done { get; set; }
        /// <summary>null = found out automatically (index.html / index.php), true / false = set by the user.</summary>
        public bool? Web { get; set; }
        public string Url { get; set; }
        public bool Hidden { get; set; }
        [JsonIgnore] public bool IsEmpty => Title == null && Image == null && !Done && Web == null && Url == null && !Hidden;
    }

    /// <summary>One card of the view (a project folder or a file), with what was found on disk.</summary>
    public sealed class WxvEntry
    {
        public string Path { get; init; }
        public string Rel { get; init; }
        public string FolderName { get; init; }
        public string Category { get; init; }
        public bool IsFile { get; init; }
        public string ProjectName { get; init; }
        public string ImagePath { get; set; }
        public bool HasWebIndex { get; init; }
        public DateTime Created { get; init; }
        public DateTime Modified { get; init; }
    }

    public static class FolderViews
    {
        public const string FileName = "view.wxv";
        public const string AssetDir = ".wxv";
        public const string Ext = ".wxv";

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>The view file of a folder (null when it has none).</summary>
        public static string FileOf(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            try { var p = Path.Combine(folder, FileName); return File.Exists(p) ? p : null; } catch { return null; }
        }

        /// <summary>Reads a view file; null when it is empty or not one (a freshly made file → the builder opens).</summary>
        public static WxvView Load(string file)
        {
            try
            {
                var text = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(text)) return null;
                var v = JsonSerializer.Deserialize<WxvView>(text, Json);
                if (v == null) return null;
                v.Items = new Dictionary<string, WxvItem>(v.Items ?? new(), StringComparer.OrdinalIgnoreCase);
                if (v.Columns < 1) v.Columns = 5;
                return v;
            }
            catch { return null; }
        }

        /// <summary>Writes the view (unused per-project entries are left out). Returns an error message or null.</summary>
        public static string Save(string file, WxvView v)
        {
            try
            {
                foreach (var k in v.Items.Where(kv => kv.Value == null || kv.Value.IsEmpty).Select(kv => kv.Key).ToList()) v.Items.Remove(k);
                var bytes = new System.Text.UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(v, Json));
                try { File.WriteAllBytes(file, bytes); }
                catch (Exception ex) when (ShellFileOp.IsDenied(ex))
                {
                    // e.g. a drive's root: Windows asks for permission (a new file only – an existing one stays)
                    if (File.Exists(file) || !ShellFileOp.NewFile(file, s => s.Write(bytes))) return ex.Message;
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>A picture the user picked: copied into the hidden ".wxv" folder next to the view; returns the value to store.</summary>
        public static string AddImage(string viewFolder, string source)
        {
            var dir = Path.Combine(viewFolder, AssetDir);
            var di = Directory.CreateDirectory(dir);
            try { di.Attributes |= FileAttributes.Hidden; } catch { }
            var name = Guid.NewGuid().ToString("N")[..10] + Path.GetExtension(source).ToLowerInvariant();
            File.Copy(source, Path.Combine(dir, name));
            return AssetDir + "/" + name;
        }

        public static string ResolveImage(string viewFolder, string stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            var p = Path.IsPathRooted(stored) ? stored : Path.Combine(viewFolder, stored.Replace('/', '\\'));
            return File.Exists(p) ? p : null;
        }

        public static string UrlOf(WxvView v, WxvEntry e)
        {
            var custom = v.Item(e.Rel)?.Url;
            if (!string.IsNullOrWhiteSpace(custom)) return custom;
            var rel = string.Join("/", e.Rel.Split('\\').Select(Uri.EscapeDataString));
            return (v.UrlTemplate ?? "http://localhost/{rel}/").Replace("{rel}", rel).Replace("{name}", Uri.EscapeDataString(e.FolderName));
        }

        public static bool IsWeb(WxvView v, WxvEntry e) => !e.IsFile && (v.Item(e.Rel)?.Web ?? e.HasWebIndex);

        public static string TitleOf(WxvView v, WxvEntry e)
        {
            var t = v.Item(e.Rel)?.Title;
            if (!string.IsNullOrWhiteSpace(t)) return t;
            if (e.IsFile) return Path.GetFileNameWithoutExtension(e.FolderName);
            return v.TitleFrom == "project" && !string.IsNullOrWhiteSpace(e.ProjectName) ? e.ProjectName : e.FolderName;
        }

        // ---------------- reading the folder ----------------
        private static readonly string[] Logos = { "logo.png", "logo.svg", "logo.webp", "logo.jpg", "logo.jpeg", "logo.ico", "icon.png", "icon.svg", "icon.ico",
                                                   "favicon.png", "favicon.svg", "favicon.ico", "screenshot.png", "screenshot.jpg", "preview.png", "preview.jpg", "cover.jpg", "cover.png" };
        private static readonly string[] LogoDirs = { "", "public", "assets", "img", "images", "static", "src\\assets", "web", "www", "_materials" };
        private static readonly string[] WebIndex = { "index.html", "index.htm", "index.php", "public\\index.php", "public\\index.html" };

        private static bool Skip(DirectoryInfo d) =>
            d.Name.StartsWith('.') || d.Name.Equals(AssetDir, StringComparison.OrdinalIgnoreCase)
            || (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0
            || d.Name is "node_modules" or "vendor" or "$RECYCLE.BIN" or "System Volume Information";

        /// <summary>Everything the view shows, read from disk (in the background).</summary>
        public static Task<List<WxvEntry>> ScanAsync(string root, WxvView v, CancellationToken ct) => Task.Run(() =>
        {
            var list = new List<WxvEntry>();
            var rootInfo = new DirectoryInfo(root);
            if (v.Source == "files")
            {
                var masks = (v.FilePattern ?? "*.*").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var opt = new EnumerationOptions { RecurseSubdirectories = v.IncludeSubfolders, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
                foreach (var f in rootInfo.EnumerateFiles("*", opt))
                {
                    if (ct.IsCancellationRequested) break;
                    if (f.Name.Equals(FileName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!masks.Any(m => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(m, f.Name))) continue;
                    var rel = Path.GetRelativePath(root, f.FullName);
                    list.Add(new WxvEntry
                    {
                        Path = f.FullName, Rel = rel, FolderName = f.Name, IsFile = true, ImagePath = f.FullName,
                        Category = v.IncludeSubfolders ? Path.GetDirectoryName(rel) : null,
                        Created = f.CreationTime, Modified = f.LastWriteTime,
                    });
                    if (list.Count >= 5000) break;
                }
                return list;
            }
            IEnumerable<(DirectoryInfo dir, string cat)> dirs = v.Source == "categories"
                ? Subdirs(rootInfo).SelectMany(c => Subdirs(c).Select(p => (p, c.Name)))
                : Subdirs(rootInfo).Select(p => (p, (string)null));
            foreach (var (d, cat) in dirs)
            {
                if (ct.IsCancellationRequested) break;
                list.Add(ReadProject(root, d, cat));
            }
            return list;
        }, ct);

        private static IEnumerable<DirectoryInfo> Subdirs(DirectoryInfo d)
        {
            try { return d.EnumerateDirectories().Where(x => !Skip(x)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList(); }
            catch { return Enumerable.Empty<DirectoryInfo>(); }
        }

        private static WxvEntry ReadProject(string root, DirectoryInfo d, string cat)
        {
            string image = null;
            foreach (var sub in LogoDirs)
            {
                var dir = sub.Length == 0 ? d.FullName : Path.Combine(d.FullName, sub);
                if (sub.Length > 0 && !Directory.Exists(dir)) continue;
                image = Logos.Select(l => Path.Combine(dir, l)).FirstOrDefault(File.Exists);
                // also a picture named like the project (WinXplorer\WinXplorer.svg)
                image ??= new[] { ".svg", ".png", ".ico", ".webp" }.Select(x => Path.Combine(dir, d.Name + x)).FirstOrDefault(File.Exists);
                if (image != null) break;
            }
            return new WxvEntry
            {
                Path = d.FullName, Rel = Path.GetRelativePath(root, d.FullName), FolderName = d.Name, Category = cat,
                ImagePath = image, ProjectName = ManifestName(d.FullName),
                HasWebIndex = WebIndex.Any(w => File.Exists(Path.Combine(d.FullName, w))),
                Created = d.CreationTime, Modified = d.LastWriteTime,
            };
        }

        /// <summary>The project's own name from its manifest (package.json, composer.json, pubspec.yaml, Cargo.toml).</summary>
        private static string ManifestName(string dir)
        {
            try
            {
                foreach (var f in new[] { "package.json", "composer.json" })
                {
                    var p = Path.Combine(dir, f);
                    if (!File.Exists(p) || new FileInfo(p).Length > 512 * 1024) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(p));
                    if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } s) return s;
                }
                foreach (var f in new[] { "pubspec.yaml", "Cargo.toml" })
                {
                    var p = Path.Combine(dir, f);
                    if (!File.Exists(p)) continue;
                    foreach (var line in File.ReadLines(p).Take(40))
                    {
                        var t = line.Trim();
                        if (t.StartsWith("name:") || t.StartsWith("name =") || t.StartsWith("name="))
                            return t[(t.IndexOfAny(new[] { ':', '=' }) + 1)..].Trim().Trim('"', '\'');
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>A first guess for a new view: projects, projects in categories, or a gallery – from what the folder holds.</summary>
        public static WxvView Detect(string folder)
        {
            var v = new WxvView { Title = Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : folder };
            try
            {
                var root = new DirectoryInfo(folder);
                var subs = Subdirs(root).ToList();
                int images = root.EnumerateFiles().Take(400).Count(f => FileTypes.Image.Contains(f.Extension.ToLowerInvariant()));
                if (images >= 6 && images > subs.Count * 2) { v.Source = "files"; v.ShowStats = false; v.WebButton = false; v.CardStyle = "hover"; v.ShowCreated = false; v.ShowAge = false; return v; }
                // a folder whose subfolders mostly hold only folders (no project files) groups projects in categories
                int looksLikeCategory = subs.Take(30).Count(s =>
                {
                    try { return !s.EnumerateFiles().Any(f => !f.Name.StartsWith('.')) && Subdirs(s).Any(); } catch { return false; }
                });
                if (subs.Count > 0 && looksLikeCategory * 2 > Math.Min(subs.Count, 30)) v.Source = "categories";
                bool anyWeb = subs.Take(30).Any(s => WebIndex.Any(w => File.Exists(Path.Combine(s.FullName, w))))
                    || subs.Take(10).SelectMany(Subdirs).Take(40).Any(s => WebIndex.Any(w => File.Exists(Path.Combine(s.FullName, w))));
                v.WebButton = anyWeb;
            }
            catch { }
            return v;
        }
    }
}
