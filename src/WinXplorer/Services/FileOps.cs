using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using WinXplorer.Interop;

namespace WinXplorer.Services
{
    public static class FileOps
    {
        public static IntPtr MainHwnd { get; set; }

        private static void RunSta(Action a)
        {
            var t = new Thread(() => { try { a(); } catch { } }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        private static string Multi(string[] paths) => string.Join("\0", paths) + "\0\0";

        /// <summary>Packs files/folders into a new .zip next to them (background thread). Returns the zip path.</summary>
        public static string CompressToZip(string[] paths, Action<string> done = null)
        {
            if (paths == null || paths.Length == 0) return null;
            var dir = Path.GetDirectoryName(paths[0].TrimEnd('\\'));
            var baseName = paths.Length == 1 ? Path.GetFileNameWithoutExtension(paths[0].TrimEnd('\\')) : Path.GetFileName(dir.TrimEnd('\\'));
            if (string.IsNullOrEmpty(baseName)) baseName = "Archive";
            var zip = UniqueName(dir, baseName, ".zip");
            new Thread(() =>
            {
                string err = null;
                try
                {
                    Archives.CreateZip(zip, paths, null);
                }
                catch (Exception ex) { err = ex.Message; try { File.Delete(zip); } catch { } }
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (err != null) MessageBox.Show(Application.Current.MainWindow, err, "WinXplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
                    else { UndoService.Created(zip); done?.Invoke(zip); }
                });
            }) { IsBackground = true, Name = "Zip" }.Start();
            return zip;
        }

        /// <summary>
        /// Extracts an archive (all of it, or only <paramref name="inner"/> items) into a new folder named after it,
        /// next to the archive (background thread).
        /// </summary>
        public static void ExtractArchive(string archive, IList<string> inner = null, Action<string> done = null)
        {
            var dir = Path.GetDirectoryName(archive);
            var target = UniqueName(dir, Archives.BaseName(archive));
            Notify.Show(Loc.F("Extracting", Path.GetFileName(archive)));
            new Thread(() =>
            {
                string err = null;
                try { Archives.ExtractTo(archive, target, inner); }
                catch (Exception ex) { err = ex is SharpCompress.Common.CryptographicException || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ? Loc.T("ArchivePassword") : ex.Message; }
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (err != null) MessageBox.Show(Application.Current.MainWindow, err, "WinXplorer", MessageBoxButton.OK, MessageBoxImage.Warning);
                    else { UndoService.Created(target); Notify.Show(Loc.F("Extracted", Path.GetFileName(target))); done?.Invoke(target); }
                });
            }) { IsBackground = true, Name = "Extract" }.Start();
        }

        /// <summary>Copy/move with the native Windows progress &amp; conflict dialogs (undo supported).</summary>
        /// <param name="done">Called on the UI thread with the full paths of what arrived in <paramref name="destDir"/> (to select them).</param>
        public static void CopyOrMove(string[] src, string destDir, bool move, Action<List<string>> done = null)
        {
            if (src == null || src.Length == 0 || string.IsNullOrEmpty(destDir)) return;
            if (Archives.Split(destDir) != null) { Notify.Show(Loc.T("ArchiveReadOnly")); return; }
            List<string> extracted = null;
            if (src.Any(InArchive))
            {
                // out of an archive: extract, then copy (an archive can't give its files away)
                try { src = Pump(() => Archives.Materialize(src)).ToArray(); } catch (Exception ex) { MessageBox.Show(ex.Message, "WinXplorer"); return; }
                extracted = src.ToList();
                move = false;
            }
            // dropping a folder into itself or moving within the same folder is a no-op
            src = src.Where(s => !destDir.TrimEnd('\\').StartsWith(s.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(s.TrimEnd('\\'), destDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToArray();
            bool sameFolder = src.All(s => string.Equals(Path.GetDirectoryName(s.TrimEnd('\\')), destDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (src.Length == 0 || (move && sameFolder)) return;
            var hwnd = MainHwnd;
            RunSta(() =>
            {
                HashSet<string> before = null;
                try { before = UndoService.Listing(destDir); } catch { }
                var op = new Native.SHFILEOPSTRUCT
                {
                    hwnd = hwnd,
                    wFunc = move ? Native.FO_MOVE : Native.FO_COPY,
                    pFrom = Multi(src),
                    pTo = destDir + "\0\0",
                    fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMMKDIR | (sameFolder ? Native.FOF_RENAMEONCOLLISION : 0)),
                };
                Native.SHFileOperationW(ref op);
                if (before != null) UndoService.CopiedOrMoved(src, destDir, move, before);
                // the temporary copies taken out of an archive are not needed any more
                if (extracted != null) Archives.DeleteTemp(extracted);
                if (done != null)
                {
                    var arrived = new List<string>();
                    try
                    {
                        var after = UndoService.Listing(destDir);
                        if (before != null) arrived.AddRange(after.Where(n => !before.Contains(n)).Select(n => Path.Combine(destDir, n)));
                        // replaced files (same names) are new too
                        if (arrived.Count == 0) arrived.AddRange(src.Select(s => Path.Combine(destDir, Path.GetFileName(s.TrimEnd('\\')))).Where(p => after.Contains(Path.GetFileName(p))));
                    }
                    catch { }
                    if (arrived.Count > 0) Application.Current?.Dispatcher.BeginInvoke(() => done(arrived));
                }
            });
        }

        /// <summary>Recycle (or permanently delete) after asking – unless confirmation is switched off in Settings.</summary>
        public static void Delete(string[] paths, bool permanent)
        {
            if (paths != null && paths.Any(p => InArchive(p))) { Notify.Show(Loc.T("ArchiveReadOnly")); return; }
            if (paths == null || paths.Length == 0) return;
            var what = paths.Length == 1 ? "\"" + Path.GetFileName(paths[0].TrimEnd('\\')) + "\"" : Loc.Items(paths.Length);
            if (permanent || AppState.Settings.ConfirmDelete)
            {
                var msg = permanent ? Loc.F("ConfirmDeletePermanent", what) : Loc.F("ConfirmRecycle", what);
                if (!Views.InputDialog.Confirm(Application.Current.MainWindow, Loc.T("Delete"), msg, Loc.T("Delete"))) return;
            }
            var hwnd = MainHwnd;
            var when = DateTime.Now;
            RunSta(() =>
            {
                var op = new Native.SHFILEOPSTRUCT
                {
                    hwnd = hwnd,
                    wFunc = Native.FO_DELETE,
                    pFrom = Multi(paths),
                    fFlags = (ushort)(Native.FOF_NOCONFIRMATION | (permanent ? 0 : Native.FOF_ALLOWUNDO | Native.FOF_WANTNUKEWARNING)),
                };
                int r = Native.SHFileOperationW(ref op);
                var gone = paths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
                if (r != 0 || gone.Count == 0) return;
                if (permanent) { Notify.Show(Loc.F("DeletedPermanently", gone.Count == 1 ? what : Loc.Items(gone.Count))); return; }
                UndoService.Recycled(gone, when);
                Notify.Show(Loc.F("Recycled", gone.Count == 1 ? what : Loc.Items(gone.Count)));
            });
        }

        private static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        /// <summary>
        /// What is wrong with a new name for <paramref name="currentPath"/> (null = fine). <paramref name="bad"/> marks the
        /// characters at fault, so the rename box can colour them.
        /// </summary>
        public static string NameProblem(string currentPath, string name, out bool[] bad)
        {
            bad = new bool[name?.Length ?? 0];
            if (string.IsNullOrWhiteSpace(name)) return Loc.T("NameEmpty");
            var invalid = Path.GetInvalidFileNameChars();
            bool any = false;
            for (int i = 0; i < name.Length; i++) if (Array.IndexOf(invalid, name[i]) >= 0) { bad[i] = true; any = true; }
            if (any) return Loc.T("NameBadChars");
            if (name.EndsWith('.') || name.EndsWith(' '))
            {
                for (int i = name.Length - 1; i >= 0 && (name[i] == '.' || name[i] == ' '); i--) bad[i] = true;
                return Loc.T("NameEndsDot");
            }
            var stem = name.Split('.')[0].TrimEnd();
            if (Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase))
            {
                for (int i = 0; i < stem.Length; i++) bad[i] = true;
                return Loc.F("NameReserved", stem.ToUpperInvariant());
            }
            if (name.Length > 255) { for (int i = 255; i < name.Length; i++) bad[i] = true; return Loc.T("NameTooLong"); }
            var dir = Path.GetDirectoryName(currentPath.TrimEnd('\\'));
            if (!string.IsNullOrEmpty(dir))
            {
                var dest = Path.Combine(dir, name);
                // another item already has this name (changing only upper / lower case of the same item is fine)
                if (!string.Equals(dest, currentPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && (File.Exists(dest) || Directory.Exists(dest)))
                {
                    for (int i = 0; i < bad.Length; i++) bad[i] = true;
                    return Loc.F("NameExists", name);
                }
            }
            return null;
        }

        public static bool Rename(string path, string newName, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            { error = Loc.T("InvalidName"); return false; }
            var dest = Path.Combine(Path.GetDirectoryName(path.TrimEnd('\\')) ?? "", newName);
            if (string.Equals(dest, path, StringComparison.Ordinal)) return true;
            if (!Native.MoveFileExW(path, dest, 0))
            {
                int code = Marshal.GetLastWin32Error();
                // not allowed for this user (e.g. a drive's root): let Windows ask for permission, like Explorer does
                if (ShellFileOp.IsDenied(code))
                    return ShellFileOp.Rename(path, newName);   // false = the user said no; nothing more to tell
                error = new System.ComponentModel.Win32Exception(code).Message;
                return false;
            }
            UndoService.Renamed(path.TrimEnd('\\'), dest);
            return true;
        }

        /// <summary>
        /// Renames many items at once (full paths). Two steps through temporary names, so names can be swapped
        /// or shifted (a→b, b→c). Returns what was renamed; problems go to <paramref name="errors"/>.
        /// </summary>
        public static List<(string from, string to)> RenameBatch(List<(string from, string to)> pairs, List<string> errors, bool record = true)
        {
            var done = new List<(string from, string to)>();
            var work = pairs.Where(p => !string.Equals(p.from, p.to, StringComparison.Ordinal)).ToList();
            var temp = new List<(string from, string tmp, string to)>();
            int denied = 0;
            foreach (var (from, to) in work)
            {
                var tmp = Path.Combine(Path.GetDirectoryName(from) ?? "", ".wxren-" + Guid.NewGuid().ToString("N")[..12]);
                if (Native.MoveFileExW(from, tmp, 0)) { temp.Add((from, tmp, to)); continue; }
                int code = Marshal.GetLastWin32Error();
                if (ShellFileOp.IsDenied(code)) denied++;   // explained once below instead of "Access is denied" per file
                else errors.Add(Path.GetFileName(from) + ": " + new System.ComponentModel.Win32Exception(code).Message);
            }
            if (denied > 0) errors.Insert(0, Loc.F("RenameDeniedMany", Loc.Items(denied)));
            foreach (var (from, tmp, to) in temp)
            {
                if (Native.MoveFileExW(tmp, to, 0)) { done.Add((from, to)); continue; }
                errors.Add(Path.GetFileName(from) + " → " + Path.GetFileName(to) + ": " + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
                Native.MoveFileExW(tmp, from, 0); // put it back under its old name
            }
            if (record) UndoService.RenamedMany(done);
            return done;
        }

        /// <summary>Shortcuts (.lnk) to the given files / folders in <paramref name="dir"/> – Alt+drag, "Create shortcut on desktop".</summary>
        public static void CreateShortcuts(IEnumerable<string> targets, string dir)
        {
            dynamic sh = null;
            string last = null;
            try
            {
                sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                foreach (var t in targets)
                {
                    var clean = t.TrimEnd('\\');
                    var name = clean.Length <= 2 ? clean.TrimEnd(':') + " (" + clean + ")" : Path.GetFileName(clean);
                    last = UniqueName(dir, name + Loc.T("ShortcutSuffix"), ".lnk");
                    var l = sh.CreateShortcut(last);
                    l.TargetPath = t.Length <= 3 ? t : clean;
                    l.WorkingDirectory = Directory.Exists(t) ? clean : Path.GetDirectoryName(clean) ?? "";
                    l.Save();
                    Marshal.FinalReleaseComObject(l);
                }
                if (last != null) Notify.Show(Loc.T("ShortcutCreated") + ": " + Path.GetFileName(last));
            }
            catch (Exception ex) { Notify.Show(ex.Message); }
            finally { if (sh != null) Marshal.FinalReleaseComObject(sh); }
        }

        public static string UniqueName(string dir, string baseName, string ext = "")
        {
            var p = Path.Combine(dir, baseName + ext);
            for (int i = 2; File.Exists(p) || Directory.Exists(p); i++) p = Path.Combine(dir, $"{baseName} ({i}){ext}");
            return p;
        }

        public static string CreateFolder(string dir)
        {
            var p = UniqueName(dir, Loc.T("NewFolderName"));
            return NewFolderAt(p);
        }

        /// <summary>Creates this folder; where the user may not, Windows asks for permission. Null when the user said no.</summary>
        public static string NewFolderAt(string p)
        {
            try { Directory.CreateDirectory(p); }
            catch (Exception ex) when (ShellFileOp.IsDenied(ex))
            {
                if (!ShellFileOp.NewFolder(Path.GetDirectoryName(p)!, Path.GetFileName(p))) return null;
                return p;
            }
            UndoService.Created(p);
            return p;
        }

        public static string CreateTextFile(string dir)
        {
            var p = UniqueName(dir, Loc.T("NewTextName"), ".txt");
            return WriteNew(p, _ => { }) ? p : null;
        }

        /// <summary>
        /// Writes a new file. Where the user may not (a drive's root such as F:\, Program Files…), Windows asks for
        /// permission and puts it there – like Explorer. False when the user said no.
        /// </summary>
        public static bool WriteNew(string p, Action<Stream> write)
        {
            try
            {
                using (var fs = new FileStream(p, FileMode.CreateNew, FileAccess.Write)) write(fs);
            }
            catch (Exception ex) when (ShellFileOp.IsDenied(ex))
            {
                return ShellFileOp.NewFile(p, write);
            }
            UndoService.Created(p);
            return true;
        }

        // ---------- Clipboard ----------
        // ---- cut items are drawn faded until they are pasted (or the clipboard changes) ----
        private static HashSet<string> _cut = new(StringComparer.OrdinalIgnoreCase);
        public static event Action CutChanged;
        public static bool IsCut(string path) => _cut.Count > 0 && path != null && _cut.Contains(path);

        private static void SetCut(IEnumerable<string> paths)
        {
            var next = new HashSet<string>(paths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (next.SetEquals(_cut)) return;
            _cut = next;
            CutChanged?.Invoke();
        }

        /// <summary>Drops the faded look when the clipboard no longer holds our cut files (e.g. something else was copied).</summary>
        public static void ValidateCut()
        {
            if (_cut.Count == 0) return;
            try
            {
                bool still = Clipboard.ContainsFileDropList() && Clipboard.GetData("Preferred DropEffect") is MemoryStream ms
                    && ms.Length >= 4 && (BitConverter.ToInt32(ms.ToArray(), 0) & 2) != 0
                    && Clipboard.GetFileDropList().Cast<string>().All(_cut.Contains);
                if (!still) SetCut(null);
            }
            catch { }
        }

        public static void SetClipboard(string[] paths, bool cut)
        {
            if (paths == null || paths.Length == 0) return;
            if (paths.Any(InArchive))
            {
                try { paths = Pump(() => Archives.Materialize(paths)).ToArray(); } catch (Exception ex) { MessageBox.Show(ex.Message, "WinXplorer"); return; }
                cut = false;
            }
            var data = new DataObject();
            var sc = new StringCollection(); sc.AddRange(paths);
            data.SetFileDropList(sc);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
            data.SetText(string.Join(Environment.NewLine, paths));
            try { Clipboard.SetDataObject(data, true); SetCut(cut ? paths : null); } catch { }
        }

        /// <summary>
        /// Address bar commands: cmd, ps / powershell, pwsh, terminal / wt open in the given folder;
        /// any other program found on PATH (code, notepad, git…) is started there with the rest as arguments.
        /// Returns false when the text is not a command (then it's treated as a path that doesn't exist).
        /// </summary>
        public static bool RunFromAddressBar(string text, string dir)
        {
            var s = text?.Trim();
            if (string.IsNullOrEmpty(s)) return false;
            int sp = s.IndexOf(' ');
            var cmd = (sp < 0 ? s : s[..sp]).Trim('"');
            var rest = sp < 0 ? "" : s[(sp + 1)..].Trim();
            if (cmd.Length == 0 || cmd.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return false;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string exe, args = rest;
            switch (cmd.ToLowerInvariant())
            {
                case "cmd": exe = "cmd.exe"; break;
                case "ps": exe = FindOnPath("pwsh") ?? "powershell.exe"; break;
                case "powershell": exe = "powershell.exe"; break;
                case "terminal": case "wt":
                    exe = FindOnPath("wt") ?? "wt.exe";
                    args = ("-d . " + rest).Trim();   // "." = the working directory below
                    break;
                default:
                    exe = FindOnPath(cmd);
                    if (exe == null) return false;
                    break;
            }
            try
            {
                Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = dir, UseShellExecute = true });
                return true;
            }
            catch (Exception ex) { Notify.Show(ex.Message); return true; }
        }

        /// <summary>Full path of a program on PATH (with PATHEXT) or registered under App Paths, or null.</summary>
        internal static string FindOnPath(string name)
        {
            var exts = Path.HasExtension(name) ? new[] { "" }
                : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Prepend(Environment.GetFolderPath(Environment.SpecialFolder.System));
            foreach (var d in dirs)
                foreach (var e in exts)
                {
                    try { var f = Path.Combine(d.Trim().Trim('"'), name + e); if (File.Exists(f)) return f; } catch { }
                }
            foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                try
                {
                    using var k = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + (Path.HasExtension(name) ? name : name + ".exe"));
                    if (k?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
                }
                catch { }
            }
            return null;
        }

        public static void SetClipboardText(string text)
        {
            try { Clipboard.SetText(text); } catch { }
        }

        public static void Paste(string destDir)
        {
            if (Archives.Split(destDir) != null) { Notify.Show(Loc.T("ArchiveReadOnly")); return; }
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                    bool cut = false;
                    if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms)
                    {
                        var b = new byte[4]; ms.Read(b, 0, 4);
                        cut = (BitConverter.ToInt32(b, 0) & 2) != 0;
                    }
                    CopyOrMove(files, destDir, cut);
                    if (cut) { Clipboard.Clear(); SetCut(null); }
                }
                else if (Clipboard.ContainsImage())
                {
                    // Screenshots etc. can be pasted straight into a folder as PNG.
                    var img = Clipboard.GetImage();
                    var p = UniqueName(destDir, Loc.T("PastedImage") + " " + DateTime.Now.ToString("yyyy-MM-dd HHmmss"), ".png");
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(img));
                    WriteNew(p, fs => enc.Save(fs));
                }
                else if (Clipboard.ContainsText())
                {
                    var p = UniqueName(destDir, Loc.T("PastedText"), ".txt");
                    var text = System.Text.Encoding.UTF8.GetBytes(Clipboard.GetText());
                    WriteNew(p, fs => fs.Write(text));
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "WinXplorer"); }
        }

        public static bool CanPaste()
        {
            try { return Clipboard.ContainsFileDropList() || Clipboard.ContainsImage() || Clipboard.ContainsText(); }
            catch { return false; }
        }

        // ---------- Shell ----------
        /// <summary>Inside an archive (not the archive file itself).</summary>
        public static bool InArchive(string path) => Archives.Split(path) is { } s && s.inner.Length > 0;

        /// <summary>
        /// Runs slow work (extracting from a big archive) on a worker thread while the window keeps painting and
        /// answering – the caller still gets the result synchronously. Exceptions are passed on.
        /// </summary>
        public static T Pump<T>(Func<T> work)
        {
            var t = System.Threading.Tasks.Task.Run(work);
            if (!t.Wait(150))
            {
                Notify.Show(string.Format(Loc.T("Extracting"), "").Replace(" …", "…"));
                var frame = new System.Windows.Threading.DispatcherFrame();
                t.ContinueWith(_ => frame.Continue = false);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                // big archives leave hundreds of MB of buffers behind: give them back now (matters on small PCs)
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                t.ContinueWith(_ => GC.Collect(2, GCCollectionMode.Forced, false, true));
            }
            return t.GetAwaiter().GetResult();
        }

        /// <summary>A real file for <paramref name="path"/>: items inside archives are extracted to a temporary folder first.</summary>
        public static string Real(string path)
        {
            if (!InArchive(path)) return path;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                return Pump(() => Archives.Materialize(new[] { path })).FirstOrDefault();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "WinXplorer"); return null; }
            finally { Mouse.OverrideCursor = null; }
        }

        public static void Open(string path, string args = null)
        {
            path = Real(path);
            if (path == null) return;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Arguments = args ?? "" }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "WinXplorer"); }
        }

        /// <summary>Starts a program (or installer / script) with administrator rights – Windows asks for consent.</summary>
        public static void RunAsAdmin(string path)
        {
            path = Real(path);
            if (path == null) return;
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(path) ?? "" }); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { }   // the user said no
            catch (Exception ex) { MessageBox.Show(ex.Message, "WinXplorer"); }
        }

        /// <summary>Windows Explorer with this file selected in its folder.</summary>
        public static void ShowInExplorer(string path)
        {
            ExplorerWatch.AllowNext();
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
        }

        public static void OpenWith(string path)
        {
            path = Real(path);
            if (path == null) return;
            try { Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}") { UseShellExecute = false }); }
            catch { }
        }

        public static void ShowProperties(string path)
        {
            Native.SHObjectProperties(MainHwnd, Native.SHOP_FILEPATH, path, null);
        }

        /// <summary>Properties of several items at once (one window with their total size, like Explorer).</summary>
        public static void ShowProperties(IList<string> paths)
        {
            if (paths == null || paths.Count == 0) return;
            // Windows' multi-item properties need items of one folder (search results may come from several)
            var dir0 = Path.GetDirectoryName(paths[0].TrimEnd('\\'));
            paths = paths.Where(p => string.Equals(Path.GetDirectoryName(p.TrimEnd('\\')), dir0, StringComparison.OrdinalIgnoreCase)).ToList();
            if (paths.Count == 1) { ShowProperties(paths[0]); return; }
            try
            {
                using var m = Interop.ShellMenu.Create(MainHwnd, paths.ToArray(), Path.GetDirectoryName(paths[0].TrimEnd('\\')), false);
                var it = m?.ReadItems(1).FirstOrDefault(i => string.Equals(i.Verb, "properties", StringComparison.OrdinalIgnoreCase));
                if (it != null) { m.Invoke(MainHwnd, it.Id, 0, 0); return; }
            }
            catch { }
            ShowProperties(paths[0]);
        }

        /// <summary>
        /// A path written with Windows' own shortcuts: C:\Users\me\AppData\Local\Temp\x → %TEMP%\x. The address bar
        /// understands these, so the short form can be pasted back anywhere.
        /// </summary>
        public static string ShortPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string best = null; int bestLen = 0;
            foreach (var v in new[] { "TEMP", "LOCALAPPDATA", "APPDATA", "OneDrive", "USERPROFILE", "ProgramData", "ProgramFiles(x86)", "ProgramFiles", "windir", "PUBLIC" })
            {
                var val = Environment.GetEnvironmentVariable(v)?.TrimEnd('\\');
                if (string.IsNullOrEmpty(val) || val.Length <= 3 || val.Length <= bestLen) continue;
                if (path.Equals(val, StringComparison.OrdinalIgnoreCase) || path.StartsWith(val + "\\", StringComparison.OrdinalIgnoreCase))
                { best = "%" + v + "%" + path[val.Length..]; bestLen = val.Length; }
            }
            return best ?? path;
        }

        public static void OpenTerminal(string dir)
        {
            if (string.IsNullOrEmpty(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            try { Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{dir.TrimEnd('\\')}\\.\"") { UseShellExecute = true }); return; }
            catch { }
            try { Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = dir, UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "WinXplorer"); }
        }

        public static void OpenInExplorer(string path)
        {
            ExplorerWatch.AllowNext();   // asked for Explorer on purpose: WinXplorer doesn't take this window over
            try
            {
                // the explicit "open" verb is Explorer's own (Folder\shell\open) – the default action may be WinXplorer
                if (!string.IsNullOrEmpty(path) && !path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("::") && Directory.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" });
                else
                    Process.Start(new ProcessStartInfo("explorer.exe", string.IsNullOrEmpty(path) ? "" : $"\"{path}\"") { UseShellExecute = true });
            }
            catch { }
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }
}

