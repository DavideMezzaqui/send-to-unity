// Send to Unity: copies files and folders into a Unity project from the File Explorer right-click menu.
//
// install.cmd compiles this file with the C# compiler included in Windows (.NET Framework 4),
// so the code must stay compatible with C# 5: no $"...", ?., nameof, out var,
// tuples or => members.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SendToUnity
{
    static class App
    {
        public const string Name = "Send to Unity";
        public const string MenuTitle = "Send to &Unity";

        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SendToUnity");

        // Inside File Explorer the menu DLL runs, and it sits in the same folder as the program
        public static string ExePath
        {
            get
            {
                string self = Assembly.GetExecutingAssembly().Location;
                return self.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? self : Path.Combine(Dir, "SendToUnity.exe");
            }
        }
    }

    // ------------------------------------------------------------------ startup

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Explorer starts the instance for the next file only once this one reports
            // "ready for input": signal it right away, so there are no pauses with many files
            Native.SignalIdle();
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Log.Write("ERROR " + ex);
                Ui.Error("Something went wrong:\n" + ex.Message);
                return 1;
            }
        }

        static int Run(string[] args)
        {
            var list = new List<string>(args);
            string command = list.Count > 0 ? list[0] : "";

            switch (command)
            {
                case "--install":
                    Installer.Install(list.Count > 1 ? list[1] : null);
                    return 0;
                case "--uninstall":
                    Installer.Uninstall();
                    return 0;
                case "--refresh":
                    ShellMenu.Register(null);
                    return 0;
                case "--menu-server":
                    return MenuServer.Run();
                case "--trust":
                    ModernMenu.TrustCertificate(list[1], list.Count > 2 ? list[2] : "");
                    return 0;
                case "--hotkey":
                    return HotkeyService.Run();
                case "--settings":
                    Ui.Init();
                    using (var dialog = new SettingsDialog()) dialog.ShowDialog();
                    return 0;
            }

            // --to <project> = click on a project name in the menu; --pick = "Choose project and folder..."
            string project = null;
            if (command == "--to" && list.Count >= 2)
            {
                project = Paths.Normalize(list[1]);
                list.RemoveRange(0, 2);
            }
            else if (command == "--pick")
            {
                list.RemoveAt(0);
            }

            // --list <file> = list written by the menu (one path per line): no length limits
            int at = list.IndexOf("--list");
            if (at >= 0 && at + 1 < list.Count)
            {
                string listFile = list[at + 1];
                list.RemoveRange(at, 2);
                list.AddRange(MenuCommand.ReadList(listFile));
            }

            var files = list.Where(f => f.Trim().Length > 0).ToList();
            if (files.Count == 0)
            {
                // started by hand (double-click on the program): the settings
                Ui.Init();
                using (var dialog = new SettingsDialog()) dialog.ShowDialog();
                return 0;
            }

            string key = project == null ? "pick" : "to:" + project.ToLowerInvariant();
            return SingleInstance.Run(key, files, c => Sender.Lead(project, c));
        }
    }

    // ------------------------------------------------------------------ single instance
    // Explorer starts the program once for every selected file. The first instance
    // (the one that gets the mutex) does the work; the others hand it their files over a
    // named pipe and exit right away. A file is acknowledged only once the first instance
    // has taken it over: if no acknowledgment arrives, the instance retries or becomes the first itself.

    static class SingleInstance
    {
        public static int Run(string key, List<string> files, Func<Collector, int> lead)
        {
            string id = Hash(Environment.UserName + "|" + key);
            string pipeName = "SendToUnity_" + Process.GetCurrentProcess().SessionId + "_" + id;
            using (var mutex = new Mutex(false, @"Local\SendToUnity_" + id))
            {
                // whoever arrives while the first instance is still copying waits for it to finish
                DateTime giveUp = DateTime.UtcNow.AddMinutes(30);
                int wait = 0;
                while (true)
                {
                    bool owned;
                    try { owned = mutex.WaitOne(wait); }
                    catch (AbandonedMutexException) { owned = true; }

                    if (owned)
                    {
                        try
                        {
                            Log.Write("leader " + key + " with " + files.Count + " items");
                            var collector = new Collector(pipeName, files);
                            collector.Start();
                            return lead(collector);
                        }
                        finally
                        {
                            mutex.ReleaseMutex();
                        }
                    }

                    if (Collector.TrySend(pipeName, files)) return 0;
                    if (DateTime.UtcNow > giveUp)
                        throw new Exception("Another send to the same project is still in progress. Try again shortly.");
                    wait = 500;   // from here on, wait for the mutex instead of retrying in vain
                }
            }
        }

        static string Hash(string text)
        {
            ulong hash = 14695981039346656037UL;   // FNV-1a
            foreach (char c in text.ToLowerInvariant())
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16");
        }
    }

    class Collector
    {
        const string End = "<END>";

        readonly object sync = new object();
        readonly List<string> pending = new List<string>();
        readonly string pipeName;
        bool closed;
        DateTime lastArrival = DateTime.UtcNow;

        public event Action Arrived;

        public Collector(string pipeName, IEnumerable<string> files)
        {
            this.pipeName = pipeName;
            pending.AddRange(files);
        }

        public void Start()
        {
            var thread = new Thread(AcceptLoop);
            thread.IsBackground = true;
            thread.Start();
        }

        void AcceptLoop()
        {
            while (true)
            {
                lock (sync)
                {
                    if (closed) return;
                }
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    var connected = server;
                    server = null;
                    ThreadPool.QueueUserWorkItem(_ => Serve(connected));
                }
                catch (Exception ex)
                {
                    Log.Write("pipe: " + ex.Message);
                    if (server != null) server.Dispose();
                    Thread.Sleep(50);
                }
            }
        }

        void Serve(NamedPipeServerStream pipe)
        {
            try
            {
                using (pipe)
                {
                    var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
                    var lines = new List<string>();
                    string line;
                    while ((line = reader.ReadLine()) != null && line != End) lines.Add(line);
                    if (line == null) return;   // the sender was interrupted: it will retry

                    lock (sync)
                    {
                        if (closed) return;      // no acknowledgment: the sender will wait for the next instance
                        pending.AddRange(lines);
                        lastArrival = DateTime.UtcNow;
                    }
                    var writer = new StreamWriter(pipe, new UTF8Encoding(false), 64, true);
                    writer.WriteLine("OK");
                    writer.Flush();
                    pipe.WaitForPipeDrain();
                    Log.Write("received " + lines.Count + " items from another instance");

                    var handler = Arrived;
                    if (handler != null) handler();
                }
            }
            catch (Exception ex)
            {
                Log.Write("pipe: " + ex.Message);
            }
        }

        public static bool TrySend(string pipeName, List<string> files)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    pipe.Connect(300);
                    var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true);
                    foreach (string f in files) writer.WriteLine(f);
                    writer.WriteLine(End);
                    writer.Flush();
                    var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 64, true);
                    var answer = Task.Factory.StartNew(() => reader.ReadLine());
                    return answer.Wait(5000) && answer.Result == "OK";
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Takes the files that arrived so far
        public List<string> Take()
        {
            lock (sync)
            {
                var copy = new List<string>(pending);
                pending.Clear();
                return copy;
            }
        }

        public bool HasPending
        {
            get { lock (sync) return pending.Count > 0; }
        }

        // Stops acknowledging: whoever arrives later waits for this instance to end and starts over on its own
        public List<string> Close()
        {
            lock (sync)
            {
                closed = true;
                var copy = new List<string>(pending);
                pending.Clear();
                return copy;
            }
        }

        // Waits until the other instances have finished arriving
        public void WaitQuiet(int quietMs, int maxMs)
        {
            DateTime started = DateTime.UtcNow;
            while (true)
            {
                DateTime last;
                lock (sync) last = lastArrival;
                DateTime now = DateTime.UtcNow;
                if ((now - last).TotalMilliseconds >= quietMs || (now - started).TotalMilliseconds >= maxMs) return;
                Native.SignalIdle();   // see Program.Main
                Thread.Sleep(20);
            }
        }
    }

    // ------------------------------------------------------------------ main flow

    static class Sender
    {
        public static int Lead(string project, Collector collector)
        {
            Ui.Init();
            collector.WaitQuiet(350, 2500);
            var settings = Settings.Load();

            if (project != null)
            {
                if (!Projects.IsUnityProject(project))
                {
                    collector.Close();
                    Ui.Error("This project no longer exists (or is not a Unity project):\n" + project);
                    return 1;
                }
                string folder = settings.FolderFor(project);
                string error;
                string clean = folder == null ? null : Folders.Clean(folder, out error);
                if (clean != null) return Quick(project, clean, collector);
            }
            return WithDialog(project, collector, settings);
        }

        // Project and folder already known: no window, only the final notification
        static int Quick(string project, string folder, Collector collector)
        {
            int status = 0;
            CopyJob job = null;
            var files = collector.Take();
            while (files.Count > 0)
            {
                // one round per send, with its own count and its own question about files already there
                if (job == null) job = new CopyJob(project, folder, false);
                bool cancelled = false;
                do
                {
                    if (!job.Run(files))
                    {
                        cancelled = true;
                        break;
                    }
                    // with many files Explorer starts the instances in waves (sometimes with pauses): keep copying while they arrive
                    collector.WaitQuiet(900, 3000);
                    files = collector.Take();
                }
                while (files.Count > 0);

                // canceled before sending anything: no notification, but sends that arrived meanwhile
                // (from another window) must not be lost and start over with their own question
                if (cancelled && job.Result.Copied == 0 && job.Result.Errors.Count == 0)
                {
                    files = collector.Take();
                    if (files.Count == 0) files = collector.Close();
                    job = null;
                    continue;
                }

                // everything copied: now bring Unity to the front, so it imports everything at once
                Projects.FocusEditor(project);
                Log.Write("notification: " + job.Result.Copied + " copied, " + job.Result.Skipped + " skipped");
                if (job.Result.Errors.Count > 0) status = 1;
                // a new send while the notification is visible closes it and is copied right away
                var shown = Stopwatch.StartNew();
                Ui.ShowResult(job.Result, () => collector.HasPending);
                files = collector.Take();
                if (files.Count == 0) files = collector.Close();
                // files that arrived just after: still the same send (Explorer running late), the count continues
                if (shown.ElapsedMilliseconds > 1500) job = null;
            }
            Remember(project, folder);
            return status;
        }

        static int WithDialog(string preselect, Collector collector, Settings settings)
        {
            var projects = Projects.Known(settings, true);
            foreach (var p in projects) Log.Write("project " + p.Path + (p.Open ? " (open)" : ""));

            using (var dialog = new SendDialog(collector.Take(), projects, settings, preselect, collector))
            {
                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    collector.Close();
                    return 0;
                }
                var files = new List<string>(dialog.Files);
                files.AddRange(collector.Close());

                var job = new CopyJob(dialog.Project.Path, dialog.Folder, dialog.MoveFiles);
                if (!job.Run(files)) return 0;
                Remember(dialog.Project.Path, dialog.Folder);
                Projects.FocusEditor(dialog.Project.Path);
                Ui.ShowResult(job.Result, null);
                return job.Result.Errors.Count > 0 ? 1 : 0;
            }
        }

        static void Remember(string project, string folder)
        {
            try
            {
                Settings.Update(s => s.Remember(project, folder));
            }
            catch (Exception ex)
            {
                Log.Write("preferences not saved: " + ex.Message);
            }
        }
    }

    // ------------------------------------------------------------------ copy

    class Result
    {
        public string Project;
        public string Folder;
        public string Destination;
        public bool Moved;
        public int Copied;          // files sent (the .meta files that go with them don't count)
        public int Skipped;         // already there and left as they were
        public int AlreadyThere;    // were already in that very folder
        public bool AccessDenied;   // Windows denied writing
        public string FirstTarget;  // first file delivered: it gets selected when opening the folder
        public readonly List<string> Errors = new List<string>();
    }

    class CopyJob
    {
        // System files that must not end up in Assets when copying a folder
        static readonly string[] JunkFiles = { "Thumbs.db", "desktop.ini", ".DS_Store" };

        class Step
        {
            public string Source;
            public string Target;
            public bool Conflict;
            public bool Duplicate;   // same name as another file in the same send
            public bool IsMeta;   // Unity .meta that goes with a file or a folder
            public Step Owner;    // the file the .meta goes with (null for folders)
            public bool Done;
        }

        public readonly Result Result;
        readonly string project;
        readonly string destination;
        readonly bool move;
        bool? overwrite;   // asked only once per send
        // source folders and where they end up: when moving, needed to carry the folders' .meta files
        readonly Dictionary<string, string> folderTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public CopyJob(string project, string folder, bool move)
        {
            this.project = project;
            this.move = move;
            destination = Path.Combine(project, folder.Replace('/', '\\'));
            Result = new Result { Project = project, Folder = folder, Destination = destination, Moved = move };
        }

        // false if the user cancels at the question about files already there
        public bool Run(List<string> sources)
        {
            var folders = new List<string>();
            var movedRoots = new List<string>();
            var steps = Plan(sources, folders, movedRoots);

            var conflicts = steps.Where(s => s.Conflict && !s.Duplicate).Select(s => s.Target).ToList();
            if (conflicts.Count > 0 && overwrite == null)
            {
                var answer = Ui.AskOverwrite(conflicts, Result);
                if (answer == DialogResult.Cancel)
                {
                    Log.Write("send canceled by the user");
                    return false;
                }
                overwrite = answer == DialogResult.Yes;
            }

            try { Directory.CreateDirectory(destination); }
            catch (Exception ex)
            {
                // for example "Controlled folder access": the final message explains what to do
                AddError(Result.Folder, ex);
                Log.Write("folder " + destination + " not created: " + ex.Message);
                return true;
            }
            foreach (string folder in folders)
            {
                try { Directory.CreateDirectory(folder); }
                catch (Exception ex) { AddError(Path.GetFileName(folder), ex); }
            }

            using (var progress = new Progress(Ui.Where(Result), steps.Count(s => !s.IsMeta), move))
            {
                foreach (var step in steps.Where(s => !s.IsMeta))
                {
                    progress.Step();
                    if (step.Duplicate)
                    {
                        // replacing would lose one of the two (forever, when moving)
                        Result.Errors.Add(Path.GetFileName(step.Source) + ": there is another file with the same name in this send");
                        continue;
                    }
                    if (step.Conflict && overwrite != true)
                    {
                        Result.Skipped++;
                        continue;
                    }
                    try
                    {
                        Retry(() => Transfer(step));
                        step.Done = true;
                        Result.Copied++;
                        if (Result.FirstTarget == null) Result.FirstTarget = step.Target;
                    }
                    catch (Exception ex)
                    {
                        AddError(Path.GetFileName(step.Source), ex);
                        Log.Write("error on " + step.Source + ": " + ex.Message);
                    }
                }
            }
            // then the .meta files, which follow only the files actually delivered
            foreach (var step in steps.Where(s => s.IsMeta))
            {
                if (step.Owner != null && !step.Owner.Done) continue;   // file skipped: the .meta stays with it
                try { TransferMeta(step); }
                catch (Exception ex)
                {
                    // without its .meta Unity gives the file a new GUID and references break: this must be reported
                    AddError(Path.GetFileName(step.Source), ex);
                    Log.Write("meta " + step.Source + ": " + ex.Message);
                }
            }
            if (move)
            {
                foreach (string root in movedRoots) Cleanup(root);
            }
            Log.Write("copied " + Result.Copied + ", skipped " + Result.Skipped + ", errors " + Result.Errors.Count + " in " + destination);
            return true;
        }

        void AddError(string name, Exception ex)
        {
            if (ex is UnauthorizedAccessException) Result.AccessDenied = true;
            Result.Errors.Add(name + ": " + ex.Message);
        }

        List<Step> Plan(List<string> sources, List<string> folders, List<string> movedRoots)
        {
            var steps = new List<Step>();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assets = new Dictionary<string, Step>(StringComparer.OrdinalIgnoreCase);
            var metas = new List<KeyValuePair<string, string>>();   // .meta files to evaluate: source, destination

            var normalized = new List<string>();
            foreach (string raw in sources)
            {
                try { normalized.Add(Paths.Normalize(raw)); }
                catch (Exception)
                {
                    Result.Errors.Add(raw + ": invalid path");
                }
            }
            // a file inside a folder of the same send already travels with the folder
            var selectedFolders = normalized.Where(Directory.Exists).ToList();

            foreach (string source in normalized)
            {
                if (!seen.Add(source)) continue;
                if (selectedFolders.Any(f => !Paths.Same(f, source) && Paths.IsInside(source, f))) continue;
                // .meta files are not sent on their own: they travel with their file (see below)
                if (IsMeta(source) && File.Exists(source)) continue;

                string name = Path.GetFileName(source);
                if (name.Length == 0)
                {
                    Result.Errors.Add(source + ": a whole drive can't be sent");
                    continue;
                }
                string target = Path.Combine(destination, name);

                if (Directory.Exists(source))
                {
                    if (Paths.Same(source, target))
                    {
                        Result.AlreadyThere++;
                        continue;
                    }
                    if (Paths.IsInside(destination, source))
                    {
                        Result.Errors.Add(name + ": can't copy a folder into itself");
                        continue;
                    }
                    if (move) movedRoots.Add(source);
                    folders.Add(target);
                    folderTargets[source] = target;
                    metas.Add(new KeyValuePair<string, string>(source + ".meta", target + ".meta"));
                    var files = new List<string>();
                    Walk(source, files, folders, target);
                    foreach (string file in files)
                    {
                        string fileTarget = Path.Combine(target, file.Substring(source.Length + 1));
                        if (IsMeta(file)) metas.Add(new KeyValuePair<string, string>(file, fileTarget));
                        else assets[file] = AddStep(steps, targets, file, fileTarget);
                    }
                }
                else if (File.Exists(source))
                {
                    if (Paths.Same(source, target))
                    {
                        Result.AlreadyThere++;
                        continue;
                    }
                    assets[source] = AddStep(steps, targets, source, target);
                    metas.Add(new KeyValuePair<string, string>(source + ".meta", target + ".meta"));
                }
                else
                {
                    Result.Errors.Add(name + (InsideArchive(source)
                        ? ": it's inside an archive (zip), extract it first"
                        : ": not found"));
                }
            }

            // Unity .meta files: when moving they always follow their file, so Unity doesn't lose references;
            // when copying they are brought only from another project (within the same one they would duplicate GUIDs).
            // A .meta the project already has is never replaced (see TransferMeta).
            var seenMeta = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var meta in metas)
            {
                if (!seenMeta.Add(meta.Key) || !File.Exists(meta.Key)) continue;
                if (!move && Paths.IsInside(meta.Key, project)) continue;
                string ownerPath = meta.Key.Substring(0, meta.Key.Length - ".meta".Length);
                Step owner;
                if (!assets.TryGetValue(ownerPath, out owner))
                {
                    if (Directory.Exists(ownerPath))
                    {
                        // .meta of a folder: when moving, Cleanup handles it, since it knows whether the folder was emptied
                        if (move) continue;
                    }
                    else if (!move) continue;   // orphan .meta: when copying it stays where it is, when moving it follows its folder
                    owner = null;
                }
                steps.Add(new Step { Source = meta.Key, Target = meta.Value, IsMeta = true, Owner = owner });
            }
            return steps;
        }

        // Lists the files of a folder and prepares the subfolders (even empty ones) in the destination
        void Walk(string folder, List<string> files, List<string> folders, string target)
        {
            try
            {
                foreach (string file in Directory.GetFiles(folder))
                {
                    if (!IsJunk(Path.GetFileName(file))) files.Add(file);
                }
                foreach (string sub in Directory.GetDirectories(folder))
                {
                    if (Skipped(sub)) continue;
                    string name = Path.GetFileName(sub);
                    string subTarget = Path.Combine(target, name);
                    folders.Add(subTarget);
                    folderTargets[sub] = subTarget;
                    Walk(sub, files, folders, subTarget);
                }
            }
            catch (Exception ex)
            {
                AddError(Path.GetFileName(folder), ex);
            }
        }

        // .git, .svn and the like, leftovers from zips made on a Mac, and folder links (junctions):
        // they are not assets, and following a link would touch files that live elsewhere
        static bool Skipped(string folder)
        {
            string name = Path.GetFileName(folder);
            if (name.StartsWith(".") || name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)) return true;
            try { return (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0; }
            catch (Exception) { return true; }
        }

        static bool IsJunk(string name)
        {
            if (IsMeta(name)) name = name.Substring(0, name.Length - ".meta".Length);   // their .meta too
            return name.StartsWith("._") || JunkFiles.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        static bool IsMeta(string path)
        {
            return path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
        }

        // Explorer shows the contents of zips as folders, but those files don't exist on disk
        static bool InsideArchive(string path)
        {
            try
            {
                for (string parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                {
                    if (File.Exists(parent)) return true;
                    if (Directory.Exists(parent)) return false;
                }
            }
            catch (Exception) { }
            return false;
        }

        static Step AddStep(List<Step> steps, HashSet<string> targets, string source, string target)
        {
            // two files of the same send with the same name would land on top of each other: the second one doesn't go
            bool duplicate = !targets.Add(target);
            bool conflict = duplicate || File.Exists(target) || Directory.Exists(target);
            var step = new Step { Source = source, Target = target, Conflict = conflict, Duplicate = duplicate };
            steps.Add(step);
            return step;
        }

        void Transfer(Step step)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(step.Target));
            if (Directory.Exists(step.Target))
                throw new IOException("the project already has a folder with this name");
            if (File.Exists(step.Target) && (File.GetAttributes(step.Target) & FileAttributes.ReadOnly) != 0)
                throw new IOException("the file in the project is read-only (does it need a checkout?)");

            if (!move)
            {
                File.Copy(step.Source, step.Target, true);
            }
            else if (File.Exists(step.Target))
            {
                // copy first and then remove the original: if something goes wrong nothing is lost
                File.Copy(step.Source, step.Target, true);
                Delete(step.Source);
            }
            else
            {
                File.Move(step.Source, step.Target);
            }
            ClearReadOnly(step.Target);
        }

        // The .meta follows its file but never replaces the one the project already has:
        // it holds the GUID and the settings Unity links references to
        void TransferMeta(Step step)
        {
            if (File.Exists(step.Target))
            {
                if (move) Retry(() => Delete(step.Source));   // the file was moved: the old .meta is no longer needed
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(step.Target));
            Retry(() =>
            {
                if (move) File.Move(step.Source, step.Target);
                else File.Copy(step.Source, step.Target, false);
            });
            ClearReadOnly(step.Target);
        }

        // Unity keeps files open while importing them (a few seconds for large files): retry for up to 5 s
        static void Retry(Action action)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (IOException ex)
                {
                    int code = ex.HResult & 0xFFFF;
                    if ((code != 32 && code != 33) || attempt >= 25) throw;   // 32, 33 = file in use
                    Thread.Sleep(200);
                }
            }
        }

        // a copy of a read-only file would stay locked inside Unity too
        static void ClearReadOnly(string file)
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }

        // when moving, the original goes away even if it's read-only (as File Explorer does)
        static void Delete(string file)
        {
            ClearReadOnly(file);
            File.Delete(file);
        }

        // After a move, removes from the source the folders left empty (or with only system files),
        // bottom-up, and brings their .meta next to the destination folder
        void Cleanup(string folder)
        {
            try
            {
                foreach (string sub in Directory.GetDirectories(folder))
                {
                    if (!Skipped(sub)) Cleanup(sub);
                }
                var left = Directory.GetFileSystemEntries(folder);
                if (!left.All(f => File.Exists(f) && IsJunk(Path.GetFileName(f)))) return;   // there are skipped files
                foreach (string f in left) Delete(f);
                Directory.Delete(folder);

                string meta = folder + ".meta";
                string target;
                if (!File.Exists(meta) || !folderTargets.TryGetValue(folder, out target)) return;
                if (File.Exists(target + ".meta")) File.Delete(meta);
                else File.Move(meta, target + ".meta");
            }
            catch (Exception ex)
            {
                Log.Write("cleanup of " + folder + ": " + ex.Message);
            }
        }
    }

    // "Copying" notification on a separate thread: it appears only if the copy takes more than a moment
    class Progress : IDisposable
    {
        readonly object sync = new object();
        readonly string where;
        readonly int total;
        readonly System.Threading.Timer delay;
        int done;
        bool finished;
        Thread thread;

        readonly bool move;

        public Progress(string where, int total, bool move)
        {
            this.where = where;
            this.total = total;
            this.move = move;
            delay = new System.Threading.Timer(delegate { Show(); }, null, 800, Timeout.Infinite);
        }

        public bool Finished
        {
            get { lock (sync) return finished; }
        }

        public string Text
        {
            get { return (move ? "Moving: " : "Copying: ") + Math.Min(Thread.VolatileRead(ref done), total) + " of " + total; }
        }

        public void Step()
        {
            Interlocked.Increment(ref done);
        }

        void Show()
        {
            lock (sync)
            {
                if (finished) return;
                thread = new Thread(new ThreadStart(RunToast));
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
            }
        }

        void RunToast()
        {
            try
            {
                using (var toast = new Toast(Text, where, null, null, this, null)) Application.Run(toast);
            }
            catch (Exception ex)
            {
                Log.Write("progress: " + ex.Message);
            }
        }

        public void Dispose()
        {
            Thread running;
            lock (sync)
            {
                finished = true;
                running = thread;
            }
            delay.Dispose();
            if (running != null) running.Join(2000);   // the notification closes by itself as soon as it sees it's finished
        }
    }

    // ------------------------------------------------------------------ Unity projects

    public class ProjectInfo
    {
        public string Path;
        public string Name;
        public bool Open;
        public long HubTime;
        public long LastUsed;
        public string Hint;   // folder that contains it, only if there is another project with the same name

        public string DisplayName
        {
            get { return Hint == null ? Name : Name + "  (" + Hint + ")"; }
        }

        public override string ToString()
        {
            return Open ? DisplayName + "   (open in Unity)" : DisplayName;
        }
    }

    static class Projects
    {
        public static bool IsUnityProject(string path)
        {
            try
            {
                return Directory.Exists(Path.Combine(path, "Assets")) &&
                       Directory.Exists(Path.Combine(path, "ProjectSettings"));
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Projects from Unity Hub, the editor's recent ones, ones added by hand and ones already used with this program
        public static List<ProjectInfo> Known(Settings settings, bool checkOpen)
        {
            var found = new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in FromHub()) Add(found, entry.Key, entry.Value);
            foreach (string path in FromEditor()) Add(found, path, 0);
            foreach (string path in settings.ExtraProjects) Add(found, path, 0);
            foreach (var prefs in settings.Projects) Add(found, prefs.Path, 0);

            foreach (var info in found.Values)
            {
                var prefs = settings.Find(info.Path);
                info.LastUsed = prefs == null ? 0 : prefs.LastUsed;
                if (checkOpen) info.Open = IsOpen(info.Path);
            }
            MarkDuplicates(found.Values);
            return found.Values
                .OrderByDescending(p => p.Open)
                .ThenByDescending(p => p.LastUsed)
                .ThenByDescending(p => p.HubTime)
                .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        // Two projects with the same name: the folder that contains them appears next to the name
        static void MarkDuplicates(IEnumerable<ProjectInfo> projects)
        {
            foreach (var group in projects.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                foreach (var p in group)
                {
                    string parent = Path.GetDirectoryName(p.Path) ?? p.Path;
                    string name = Path.GetFileName(parent);
                    p.Hint = string.IsNullOrEmpty(name) ? parent : name;
                }
                // if the folders that contain them also have the same name, the full path is shown
                if (group.Select(p => p.Hint).Distinct(StringComparer.OrdinalIgnoreCase).Count() < group.Count())
                {
                    foreach (var p in group) p.Hint = Path.GetDirectoryName(p.Path) ?? p.Path;
                }
            }
        }

        static void Add(Dictionary<string, ProjectInfo> found, string path, long hubTime)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Paths.Normalize(path); }
            catch (Exception) { return; }

            ProjectInfo info;
            if (found.TryGetValue(full, out info))
            {
                info.HubTime = Math.Max(info.HubTime, hubTime);
                return;
            }
            // a disconnected network drive would block the check for tens of seconds
            if (!Paths.IsNetwork(full) && !IsUnityProject(full)) return;
            string name = Path.GetFileName(full);
            found[full] = new ProjectInfo { Path = full, Name = name.Length > 0 ? name : full, HubTime = hubTime };
        }

        static List<KeyValuePair<string, long>> FromHub()
        {
            var list = new List<KeyValuePair<string, long>>();
            try
            {
                string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"UnityHub\projects-v1.json");
                if (!File.Exists(file)) return list;
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = json.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                object data;
                if (root == null || !root.TryGetValue("data", out data)) return list;
                var projects = data as Dictionary<string, object>;
                if (projects == null) return list;

                foreach (var pair in projects)
                {
                    string path = pair.Key;
                    long time = 0;
                    var entry = pair.Value as Dictionary<string, object>;
                    if (entry != null)
                    {
                        object value;
                        if (entry.TryGetValue("path", out value) && value is string) path = (string)value;
                        if (entry.TryGetValue("lastModified", out value) && value != null)
                        {
                            try { time = Convert.ToInt64(value); }
                            catch (Exception) { }
                        }
                    }
                    list.Add(new KeyValuePair<string, long>(path, time));
                }
            }
            catch (Exception ex)
            {
                Log.Write("Unity Hub list unreadable: " + ex.Message);
            }
            return list;
        }

        // The editor saves recent projects in the registry (UTF-8 strings)
        static List<string> FromEditor()
        {
            var list = new List<string>();
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(@"Software\Unity Technologies"))
                {
                    if (root == null) return list;
                    foreach (string sub in root.GetSubKeyNames())
                    {
                        if (!sub.StartsWith("Unity Editor", StringComparison.OrdinalIgnoreCase)) continue;
                        using (var key = root.OpenSubKey(sub))
                        {
                            if (key == null) continue;
                            foreach (string name in key.GetValueNames())
                            {
                                if (!name.StartsWith("RecentlyUsedProjectPaths", StringComparison.OrdinalIgnoreCase)) continue;
                                object value = key.GetValue(name);
                                var bytes = value as byte[];
                                string text = bytes != null ? Encoding.UTF8.GetString(bytes) : value as string;
                                if (text != null) list.Add(text.TrimEnd('\0'));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("recent projects unreadable: " + ex.Message);
            }
            return list;
        }

        static string LockFile(string project)
        {
            return Path.Combine(project, @"Temp\UnityLockfile");
        }

        // Unity keeps Temp\UnityLockfile open while the project is open
        public static bool IsOpen(string project)
        {
            if (Paths.IsNetwork(project)) return false;
            string file = LockFile(project);
            if (!File.Exists(file)) return false;
            var holders = RestartManager.Holders(file);
            if (holders != null) return holders.Count > 0;
            try
            {
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Brings to the front the editor that has the project open: this way Unity imports the files right away
        public static void FocusEditor(string project)
        {
            try
            {
                if (!IsOpen(project)) return;
                IntPtr window = IntPtr.Zero;
                foreach (int pid in RestartManager.Holders(LockFile(project)) ?? new List<int>())
                {
                    try
                    {
                        using (var process = Process.GetProcessById(pid))
                        {
                            if (process.MainWindowHandle != IntPtr.Zero)
                            {
                                window = process.MainWindowHandle;
                                break;
                            }
                        }
                    }
                    catch (Exception) { }
                }
                if (window == IntPtr.Zero)
                {
                    // fallback: the Unity window title starts with the project name
                    string prefix = Path.GetFileName(project) + " - ";
                    foreach (var process in Process.GetProcessesByName("Unity"))
                    {
                        using (process)
                        {
                            try
                            {
                                if (window == IntPtr.Zero && process.MainWindowHandle != IntPtr.Zero &&
                                    process.MainWindowTitle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                    window = process.MainWindowHandle;
                            }
                            catch (Exception) { }
                        }
                    }
                }
                if (window == IntPtr.Zero)
                {
                    Log.Write("Unity window not found for " + project);
                    return;
                }
                Log.Write("Unity brought to front: " + (Native.BringToFront(window) ? "yes" : "no"));
            }
            catch (Exception ex)
            {
                Log.Write("bring to front failed: " + ex.Message);
            }
        }
    }

    // ------------------------------------------------------------------ folders inside Assets

    static class Folders
    {
        static readonly string[] Reserved =
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        // Returns the cleaned-up folder (e.g. "Assets/Textures") or null, with the reason in error
        public static string Clean(string text, out string error)
        {
            error = null;
            var parts = (text ?? "").Replace('\\', '/').Split('/')
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();
            if (parts.Count == 0) parts.Add("Assets");

            if (!string.Equals(parts[0], "Assets", StringComparison.OrdinalIgnoreCase))
            {
                error = "The folder must be inside Assets (for example Assets/Textures).";
                return null;
            }
            parts[0] = "Assets";

            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (string part in parts.Skip(1))
            {
                if (part == "." || part == "..")
                    error = "Don't use . or .. in the path.";
                else if (part.IndexOfAny(invalid) >= 0)
                    error = "\"" + part + "\" contains characters not allowed in folder names.";
                else if (part.StartsWith(".") || part.EndsWith("~"))
                    error = "Unity ignores folders that start with a dot or end with ~ (\"" + part + "\").";
                else if (part.EndsWith("."))
                    error = "A folder name can't end with a dot (\"" + part + "\").";
                else if (Reserved.Contains(part.Split('.')[0].ToUpperInvariant()))
                    error = "\"" + part + "\" is a name reserved by Windows.";
                if (error != null) return null;
            }
            return string.Join("/", parts);
        }

        // Existing subfolders of Assets, for the drop-down list
        public static List<string> Existing(string project)
        {
            const int maxDepth = 4;
            const int maxCount = 3000;
            var result = new List<string> { "Assets" };
            int skip = project.TrimEnd('\\').Length + 1;
            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(Path.Combine(project, "Assets"), 0));

            while (queue.Count > 0 && result.Count < maxCount)
            {
                var item = queue.Dequeue();
                string[] subs;
                try { subs = Directory.GetDirectories(item.Key); }
                catch (Exception) { continue; }
                foreach (string sub in subs)
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith(".") || name.EndsWith("~")) continue;   // Unity ignores them
                    result.Add(sub.Substring(skip).Replace('\\', '/'));
                    if (item.Value + 1 < maxDepth) queue.Enqueue(new KeyValuePair<string, int>(sub, item.Value + 1));
                }
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }
    }

    // ------------------------------------------------------------------ settings

    public class ProjectPrefs
    {
        public string Path { get; set; }
        public string Folder { get; set; }
        public long LastUsed { get; set; }
    }

    public class Settings
    {
        public List<string> ExtraProjects { get; set; }
        public List<ProjectPrefs> Projects { get; set; }
        public string Theme { get; set; }   // "dark" (default), "light" or "system" (follows Windows)
        public string Hotkey { get; set; }  // null = default (Ctrl+Alt+U), "" = no shortcut
        public bool HideQuickItem { get; set; }   // hides "Send to <last project>" from the main menu

        public const string DefaultHotkey = "Ctrl+Alt+U";

        public string EffectiveHotkey
        {
            get { return Hotkey ?? DefaultHotkey; }
        }

        public Settings()
        {
            ExtraProjects = new List<string>();
            Projects = new List<ProjectPrefs>();
        }

        static string FilePath
        {
            get { return System.IO.Path.Combine(App.Dir, "settings.json"); }
        }

        public ProjectPrefs Find(string project)
        {
            return Projects.FirstOrDefault(p => Paths.Same(p.Path, project));
        }

        public string FolderFor(string project)
        {
            var prefs = Find(project);
            return prefs == null ? null : prefs.Folder;
        }

        public void Remember(string project, string folder)
        {
            var prefs = Find(project);
            if (prefs == null)
            {
                prefs = new ProjectPrefs { Path = project };
                Projects.Add(prefs);
            }
            prefs.Folder = folder;
            prefs.LastUsed = DateTime.UtcNow.Ticks;
        }

        public static Settings Load()
        {
            Settings settings;
            return Read(out settings) ? settings : new Settings();
        }

        // false if the file exists but stays busy: saving now would lose the preferences
        static bool Read(out Settings settings)
        {
            settings = new Settings();
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (!File.Exists(FilePath)) return true;
                    var loaded = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (loaded != null)
                    {
                        if (loaded.ExtraProjects == null) loaded.ExtraProjects = new List<string>();
                        if (loaded.Projects == null) loaded.Projects = new List<ProjectPrefs>();
                        loaded.ExtraProjects.RemoveAll(string.IsNullOrEmpty);
                        loaded.Projects.RemoveAll(p => p == null || string.IsNullOrEmpty(p.Path));
                        settings = loaded;
                    }
                    return true;
                }
                catch (IOException ex)
                {
                    // usually another send is saving it right now: retry a moment later
                    if (attempt >= 20)
                    {
                        Log.Write("settings unreadable: " + ex.Message);
                        return false;
                    }
                    Thread.Sleep(25);
                }
                catch (Exception ex)
                {
                    Log.Write("settings corrupted, starting from scratch: " + ex.Message);
                    return true;
                }
            }
        }

        // Rereads, changes and saves under a mutex: two simultaneous sends don't overwrite each other
        public static Settings Update(Action<Settings> change)
        {
            using (var mutex = new Mutex(false, @"Local\SendToUnity_settings"))
            {
                bool owned;
                try { owned = mutex.WaitOne(5000); }
                catch (AbandonedMutexException) { owned = true; }
                try
                {
                    Settings settings;
                    if (!Read(out settings)) throw new IOException("the settings are locked by another program");
                    change(settings);
                    Directory.CreateDirectory(App.Dir);
                    string temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(temp, new JavaScriptSerializer().Serialize(settings), Encoding.UTF8);
                    for (int attempt = 0; ; attempt++)
                    {
                        try
                        {
                            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
                            else File.Move(temp, FilePath);
                            break;
                        }
                        catch (IOException)
                        {
                            if (attempt >= 10) throw;   // someone is reading it right now
                            Thread.Sleep(30);
                        }
                    }
                    return settings;
                }
                finally
                {
                    if (owned) mutex.ReleaseMutex();
                }
            }
        }
    }

    // ------------------------------------------------------------------ right-click menu
    // "Send to Unity" is a File Explorer command (IExplorerCommand, further below). install.cmd
    // also compiles this same file as a DLL, which Explorer loads and queries every time it
    // opens the menu: the project list is always up to date and open projects have the green dot.
    // Everything is registered for the current user, without administrator rights.

    static class ShellMenu
    {
        public const string ClassId = "{6F1D7C0E-3B8A-4E59-9C2D-5A4B8E7F1A23}";
        const string ClassKey = @"Software\Classes\CLSID\" + ClassId;
        const string DllPrefix = "SendToUnityMenu-";
        public const int MaxProjects = 10;

        public const string QuickClassId = "{8B2F4C6D-1E3A-4F5B-9C7D-0A1B2C3D4E5F}";
        const string QuickClassKey = @"Software\Classes\CLSID\" + QuickClassId;

        static readonly string[] Keys =
        {
            @"Software\Classes\*\shell\SendToUnity",
            @"Software\Classes\Directory\shell\SendToUnity"
        };
        static readonly string[] QuickKeys =
        {
            @"Software\Classes\*\shell\SendToUnityQuick",
            @"Software\Classes\Directory\shell\SendToUnityQuick"
        };

        public static string OpenIcon
        {
            get { return Path.Combine(App.Dir, "aperto.ico"); }
        }

        public static string HubExe()
        {
            string hub = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Unity Hub\Unity Hub.exe");
            return File.Exists(hub) ? hub : null;
        }

        // dll: the DLL just compiled by install.cmd; null = the most recent one in the program folder.
        // Every installation has a new name, because Explorer keeps the one it already loaded locked.
        public static void Register(string dll)
        {
            if (dll == null) dll = Dlls().OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (dll == null || !File.Exists(dll)) throw new FileNotFoundException("The menu DLL is missing: run install.cmd again.");
            dll = Path.GetFullPath(dll);

            RegisterClass(ClassKey, typeof(MenuCommand), dll);
            RegisterClass(QuickClassKey, typeof(QuickCommand), dll);

            string hub = HubExe();
            AddVerbs(Keys, App.MenuTitle, ClassId, hub);   // right-click, then U
            AddVerbs(QuickKeys, "Send to the last project", QuickClassId, hub);

            foreach (string old in Dlls())
            {
                if (Paths.Same(old, dll)) continue;
                try { File.Delete(old); }
                catch (Exception) { Log.Write("menu DLL still in use, removing it next time: " + old); }
            }
            Log.Write("menu registered: " + dll);
        }

        static void RegisterClass(string classKey, Type type, string dll)
        {
            Registry.CurrentUser.DeleteSubKeyTree(classKey, false);
            using (var key = Registry.CurrentUser.CreateSubKey(classKey))
            {
                key.SetValue("", App.Name);
                using (var server = key.CreateSubKey("InprocServer32"))
                {
                    server.SetValue("", "mscoree.dll");
                    server.SetValue("ThreadingModel", "Apartment");
                    server.SetValue("Class", type.FullName);
                    server.SetValue("Assembly", Path.GetFileNameWithoutExtension(dll) + ", Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");
                    server.SetValue("RuntimeVersion", "v4.0.30319");
                    server.SetValue("CodeBase", "file:///" + dll.Replace('\\', '/'));
                }
            }
        }

        static void AddVerbs(string[] keys, string title, string classId, string hub)
        {
            foreach (string keyPath in keys)
            {
                Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
                if (ModernMenu.Active) continue;   // the items come from the package, in both menus
                using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
                {
                    key.SetValue("MUIVerb", title);
                    key.SetValue("ExplorerCommandHandler", classId);
                    if (hub != null) key.SetValue("Icon", hub + ",0");
                }
            }
        }

        public static void Remove()
        {
            foreach (string keyPath in Keys.Concat(QuickKeys)) Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
            Registry.CurrentUser.DeleteSubKeyTree(ClassKey, false);
            Registry.CurrentUser.DeleteSubKeyTree(QuickClassKey, false);
            // the one loaded by Explorer can't be deleted but can be moved: the folder stays clean
            foreach (string dll in Dlls())
            {
                try { File.Delete(dll); }
                catch (Exception)
                {
                    try { File.Move(dll, Path.Combine(Path.GetTempPath(), Path.GetFileName(dll))); }
                    catch (Exception ex) { Log.Write("menu DLL not removed: " + ex.Message); }
                }
            }
        }

        static string[] Dlls()
        {
            try { return Directory.GetFiles(App.Dir, DllPrefix + "*.dll"); }
            catch (Exception) { return new string[0]; }
        }

        // Green dot for open projects, in the sizes Windows uses in menus at the various DPIs
        public static void SaveOpenIcon()
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64 };
            var images = new List<byte[]>();
            foreach (int size in sizes) images.Add(DotImage(size));
            Directory.CreateDirectory(App.Dir);
            using (var writer = new BinaryWriter(File.Create(OpenIcon)))
            {
                writer.Write((short)0);
                writer.Write((short)1);
                writer.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    writer.Write((byte)sizes[i]);
                    writer.Write((byte)sizes[i]);
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((short)1);
                    writer.Write((short)32);
                    writer.Write(images[i].Length);
                    writer.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var image in images) writer.Write(image);
            }
        }

        // Icon image: BITMAPINFOHEADER, BGRA pixels bottom-up, empty mask
        static byte[] DotImage(int size)
        {
            using (var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float dot = size / 2f;
                    float at = (size - dot) / 2f;
                    using (var brush = new SolidBrush(SendDialog.OpenGreen)) g.FillEllipse(brush, at, at, dot, dot);
                }
                var data = bitmap.LockBits(new Rectangle(0, 0, size, size), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var pixels = new byte[size * size * 4];
                try
                {
                    for (int y = 0; y < size; y++)
                        Marshal.Copy(data.Scan0 + (size - 1 - y) * data.Stride, pixels, y * size * 4, size * 4);
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
                int maskRow = (size + 31) / 32 * 4;
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(40);
                    writer.Write(size);
                    writer.Write(size * 2);
                    writer.Write((short)1);
                    writer.Write((short)32);
                    writer.Write(0);
                    writer.Write(pixels.Length + maskRow * size);
                    writer.Write(0);
                    writer.Write(0);
                    writer.Write(0);
                    writer.Write(0);
                    writer.Write(pixels);
                    writer.Write(new byte[maskRow * size]);
                    writer.Flush();
                    return stream.ToArray();
                }
            }
        }

        // In menus & marks the underlined letter: to show it, it must be doubled
        public static string Escape(string text)
        {
            return text.Replace("&", "&&");
        }
    }

    // File Explorer interfaces used by the menu
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint type, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetAttributes(int flags, uint mask, out uint attributes);
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemAt(uint index, out IShellItem item);
        [PreserveSig] int EnumItems(out IntPtr items);
    }

    [ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IExplorerCommand
    {
        [PreserveSig] int GetTitle(IShellItemArray items, out IntPtr title);
        [PreserveSig] int GetIcon(IShellItemArray items, out IntPtr icon);
        [PreserveSig] int GetToolTip(IShellItemArray items, out IntPtr tip);
        [PreserveSig] int GetCanonicalName(out Guid name);
        [PreserveSig] int GetState(IShellItemArray items, [MarshalAs(UnmanagedType.Bool)] bool okToBeSlow, out uint state);
        [PreserveSig] int Invoke(IShellItemArray items, IntPtr bindContext);
        [PreserveSig] int GetFlags(out uint flags);
        [PreserveSig] int EnumSubCommands(out IEnumExplorerCommand commands);
    }

    [ComImport, Guid("a88826f8-186f-4987-aade-ea0cef8fbfe8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumExplorerCommand
    {
        [PreserveSig] int Next(uint count, IntPtr commands, IntPtr fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumExplorerCommand copy);
    }

    // The "Send to Unity" item: the class Explorer creates from the DLL. It runs inside Explorer,
    // so every method catches errors and the actual copy is done by the program, started separately.
    [ComVisible(true), Guid("6F1D7C0E-3B8A-4E59-9C2D-5A4B8E7F1A23"), ClassInterface(ClassInterfaceType.None)]
    public class MenuCommand : IExplorerCommand
    {
        public const int Ok = 0;
        public const int NotImplemented = unchecked((int)0x80004001);
        public const uint HasSubCommands = 0x1;      // ECF_HASSUBCOMMANDS
        public const uint SeparatorBefore = 0x20;    // ECF_SEPARATORBEFORE
        const uint FileSystemPath = 0x80058000;      // SIGDN_FILESYSPATH

        public int GetTitle(IShellItemArray items, out IntPtr title)
        {
            MenuServer.Touch();
            title = Marshal.StringToCoTaskMemUni(App.MenuTitle);
            return Ok;
        }

        public int GetIcon(IShellItemArray items, out IntPtr icon)
        {
            icon = IntPtr.Zero;
            string hub = ShellMenu.HubExe();
            if (hub == null) return NotImplemented;
            icon = Marshal.StringToCoTaskMemUni(hub + ",0");
            return Ok;
        }

        public int GetToolTip(IShellItemArray items, out IntPtr tip)
        {
            tip = IntPtr.Zero;
            return NotImplemented;
        }

        public int GetCanonicalName(out Guid name)
        {
            name = GetType().GUID;
            return Ok;
        }

        public int GetState(IShellItemArray items, bool okToBeSlow, out uint state)
        {
            state = 0;   // ECS_ENABLED
            return Ok;
        }

        public int Invoke(IShellItemArray items, IntPtr bindContext)
        {
            return Ok;
        }

        public int GetFlags(out uint flags)
        {
            flags = HasSubCommands;
            return Ok;
        }

        public int EnumSubCommands(out IEnumExplorerCommand commands)
        {
            commands = new CommandList(Build());
            return Ok;
        }

        // One project per line (most used at the top, always in the same order) + "Choose project and folder..."
        // The menu opens on every right-click: the project list (which reads files and checks folders) is
        // recalculated only if Unity Hub or the settings changed, or after half a minute. The
        // "open in Unity" state instead is checked every time, so the dot is always right.
        static readonly object cacheSync = new object();
        static List<KeyValuePair<string, string>> cachedItems;   // project path, item text
        static string cachedKey;
        static DateTime cachedAt;

        static List<KeyValuePair<string, string>> MenuItems()
        {
            string key = Stamp(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"UnityHub\projects-v1.json")) +
                         "|" + Stamp(Path.Combine(App.Dir, "settings.json"));
            lock (cacheSync)
            {
                if (cachedItems != null && cachedKey == key && DateTime.UtcNow - cachedAt < TimeSpan.FromSeconds(30)) return cachedItems;
                var settings = Settings.Load();
                var items = new List<KeyValuePair<string, string>>();
                foreach (var p in Projects.Known(settings, false).Take(ShellMenu.MaxProjects))
                {
                    string folder = settings.FolderFor(p.Path);
                    string label = ShellMenu.Escape(p.DisplayName) + (folder != null ? "   →  " + ShellMenu.Escape(folder) : "");
                    items.Add(new KeyValuePair<string, string>(p.Path, label));
                }
                cachedItems = items;
                cachedKey = key;
                cachedAt = DateTime.UtcNow;
                return items;
            }
        }

        static string Stamp(string file)
        {
            try { return File.GetLastWriteTimeUtc(file).Ticks.ToString(); }
            catch (Exception) { return ""; }
        }

        static List<IExplorerCommand> Build()
        {
            MenuServer.Touch();
            var list = new List<IExplorerCommand>();
            int count = 0;
            try
            {
                foreach (var item in MenuItems())
                {
                    list.Add(new ProjectCommand(item.Value, new[] { "--to", item.Key }, Projects.IsOpen(item.Key), false));
                    count++;
                }
            }
            catch (Exception ex)
            {
                Log.Write("menu: projects not read: " + ex.Message);
            }
            list.Add(new ProjectCommand("Choose project and folder...", new[] { "--pick" }, false, count > 0));
            list.Add(new ProjectCommand("Settings...", new[] { "--settings" }, false, false));
            return list;
        }

        // Starts the program with the file list in a temporary file
        public static void Launch(string[] arguments, IShellItemArray items)
        {
            Start(arguments, ItemPaths(items));
        }

        public static void Start(string[] arguments, List<string> paths)
        {
            if (paths.Count == 0) return;
            string list = Path.Combine(Path.GetTempPath(), "SendToUnity-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(list, paths, new UTF8Encoding(false));
            string line = string.Join(" ", arguments.Select(Paths.Quote)) + " --list " + Paths.Quote(list);
            Log.Write("menu: " + line + " (" + paths.Count + " items)");
            try
            {
                Process.Start(new ProcessStartInfo(App.ExePath, line) { UseShellExecute = false, WorkingDirectory = App.Dir }).Dispose();
            }
            catch (Exception)
            {
                File.Delete(list);
                throw;
            }
        }

        static List<string> ItemPaths(IShellItemArray items)
        {
            var paths = new List<string>();
            uint count;
            if (items == null || items.GetCount(out count) != Ok) return paths;
            for (uint i = 0; i < count; i++)
            {
                IShellItem item;
                if (items.GetItemAt(i, out item) != Ok || item == null) continue;
                IntPtr name;
                if (item.GetDisplayName(FileSystemPath, out name) == Ok && name != IntPtr.Zero)
                {
                    paths.Add(Marshal.PtrToStringUni(name));
                    Marshal.FreeCoTaskMem(name);
                }
                Marshal.ReleaseComObject(item);   // inside Explorer: no objects left to the garbage collector
            }
            return paths;
        }

        public static List<string> ReadList(string file)
        {
            var lines = File.ReadAllLines(file, Encoding.UTF8).Where(l => l.Length > 0).ToList();
            // delete only the list written by the menu, never an arbitrary file passed by hand
            bool ours = Paths.Same(Path.GetDirectoryName(Path.GetFullPath(file)), Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\')) &&
                        System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), "^SendToUnity-[0-9a-f]{32}\\.txt$");
            if (ours)
            {
                try { File.Delete(file); }
                catch (Exception) { }
            }
            return lines;
        }
    }

    public class ProjectCommand : IExplorerCommand
    {
        readonly string title;
        readonly string[] arguments;
        readonly bool open;
        readonly bool separatorBefore;

        public ProjectCommand(string title, string[] arguments, bool open, bool separatorBefore)
        {
            this.title = title;
            this.arguments = arguments;
            this.open = open;
            this.separatorBefore = separatorBefore;
        }

        public int GetTitle(IShellItemArray items, out IntPtr text)
        {
            MenuServer.Touch();
            text = Marshal.StringToCoTaskMemUni(title);
            return MenuCommand.Ok;
        }

        public int GetIcon(IShellItemArray items, out IntPtr icon)
        {
            icon = IntPtr.Zero;
            if (!open || !File.Exists(ShellMenu.OpenIcon)) return MenuCommand.NotImplemented;
            icon = Marshal.StringToCoTaskMemUni(ShellMenu.OpenIcon);
            return MenuCommand.Ok;
        }

        public int GetToolTip(IShellItemArray items, out IntPtr tip)
        {
            tip = IntPtr.Zero;
            if (!open) return MenuCommand.NotImplemented;
            tip = Marshal.StringToCoTaskMemUni("Open in Unity");
            return MenuCommand.Ok;
        }

        public int GetCanonicalName(out Guid name)
        {
            name = Guid.Empty;
            return MenuCommand.NotImplemented;
        }

        public int GetState(IShellItemArray items, bool okToBeSlow, out uint state)
        {
            state = 0;
            return MenuCommand.Ok;
        }

        public int Invoke(IShellItemArray items, IntPtr bindContext)
        {
            MenuServer.Touch();
            try
            {
                if (arguments[0] == "--settings")
                    Process.Start(new ProcessStartInfo(App.ExePath, "--settings") { UseShellExecute = false, WorkingDirectory = App.Dir }).Dispose();
                else
                    MenuCommand.Launch(arguments, items);
                return MenuCommand.Ok;
            }
            catch (Exception ex)
            {
                Log.Write("menu: launch failed: " + ex);
                return Marshal.GetHRForException(ex);
            }
        }

        public int GetFlags(out uint flags)
        {
            flags = separatorBefore ? MenuCommand.SeparatorBefore : 0;
            return MenuCommand.Ok;
        }

        public int EnumSubCommands(out IEnumExplorerCommand commands)
        {
            commands = null;
            return MenuCommand.NotImplemented;
        }
    }

    public class CommandList : IEnumExplorerCommand
    {
        readonly List<IExplorerCommand> commands;
        int next;

        public CommandList(List<IExplorerCommand> commands)
        {
            this.commands = commands;
        }

        public int Next(uint count, IntPtr output, IntPtr fetched)
        {
            int done = 0;
            while (done < count && next < commands.Count)
            {
                IntPtr command = Marshal.GetComInterfaceForObject(commands[next], typeof(IExplorerCommand));
                Marshal.WriteIntPtr(output, done * IntPtr.Size, command);
                done++;
                next++;
            }
            if (fetched != IntPtr.Zero) Marshal.WriteInt32(fetched, done);
            return done == count ? MenuCommand.Ok : 1;   // S_FALSE: end of list
        }

        public int Skip(uint count)
        {
            next = (int)Math.Min(commands.Count, next + (long)count);
            return next < commands.Count ? MenuCommand.Ok : 1;
        }

        public int Reset()
        {
            next = 0;
            return MenuCommand.Ok;
        }

        public int Clone(out IEnumExplorerCommand copy)
        {
            var list = new CommandList(commands);
            list.next = next;
            copy = list;
            return MenuCommand.Ok;
        }
    }

    // "Send to Gioco  →  Assets/Kits": the last project and folder used, right in the main menu,
    // without opening the submenu. Hidden if nothing was sent yet or if turned off in the settings.
    [ComVisible(true), Guid("8B2F4C6D-1E3A-4F5B-9C7D-0A1B2C3D4E5F"), ClassInterface(ClassInterfaceType.None)]
    public class QuickCommand : IExplorerCommand
    {
        const uint Hidden = 0x2;   // ECS_HIDDEN

        public int GetTitle(IShellItemArray items, out IntPtr title)
        {
            MenuServer.Touch();
            var target = QuickTarget.Get();
            title = Marshal.StringToCoTaskMemUni(target == null ? App.Name : QuickTarget.Label(target, true));
            return MenuCommand.Ok;
        }

        public int GetIcon(IShellItemArray items, out IntPtr icon)
        {
            icon = IntPtr.Zero;
            var target = QuickTarget.Get();
            if (target != null && Projects.IsOpen(target.Path) && File.Exists(ShellMenu.OpenIcon))
            {
                icon = Marshal.StringToCoTaskMemUni(ShellMenu.OpenIcon);
                return MenuCommand.Ok;
            }
            string hub = ShellMenu.HubExe();
            if (hub == null) return MenuCommand.NotImplemented;
            icon = Marshal.StringToCoTaskMemUni(hub + ",0");
            return MenuCommand.Ok;
        }

        public int GetToolTip(IShellItemArray items, out IntPtr tip)
        {
            tip = IntPtr.Zero;
            return MenuCommand.NotImplemented;
        }

        public int GetCanonicalName(out Guid name)
        {
            name = GetType().GUID;
            return MenuCommand.Ok;
        }

        public int GetState(IShellItemArray items, bool okToBeSlow, out uint state)
        {
            MenuServer.Touch();
            state = QuickTarget.Get() == null ? Hidden : 0;
            return MenuCommand.Ok;
        }

        public int Invoke(IShellItemArray items, IntPtr bindContext)
        {
            MenuServer.Touch();
            try
            {
                var target = QuickTarget.Get();
                MenuCommand.Launch(target == null ? new[] { "--pick" } : new[] { "--to", target.Path }, items);
                return MenuCommand.Ok;
            }
            catch (Exception ex)
            {
                Log.Write("menu: launch failed: " + ex);
                return Marshal.GetHRForException(ex);
            }
        }

        public int GetFlags(out uint flags)
        {
            flags = 0;
            return MenuCommand.Ok;
        }

        public int EnumSubCommands(out IEnumExplorerCommand commands)
        {
            commands = null;
            return MenuCommand.NotImplemented;
        }
    }

    // The last project and folder used: target of the quick menu item and of the keyboard shortcut
    static class QuickTarget
    {
        public class Target
        {
            public string Path;
            public string Name;
            public string Folder;
        }

        static readonly object sync = new object();
        static Target cached;
        static string cachedStamp;
        static DateTime cachedAt;

        public static Target Get()
        {
            string stamp;
            try { stamp = File.GetLastWriteTimeUtc(System.IO.Path.Combine(App.Dir, "settings.json")).Ticks.ToString(); }
            catch (Exception) { stamp = ""; }
            lock (sync)
            {
                if (cachedStamp == stamp && DateTime.UtcNow - cachedAt < TimeSpan.FromSeconds(30)) return cached;
                cached = Find();
                cachedStamp = stamp;
                cachedAt = DateTime.UtcNow;
                return cached;
            }
        }

        static Target Find()
        {
            try
            {
                var settings = Settings.Load();
                if (settings.HideQuickItem) return null;
                foreach (var prefs in settings.Projects.Where(p => p.Folder != null).OrderByDescending(p => p.LastUsed))
                {
                    // a network drive that isn't there would block the menu: it's trusted as it is
                    if (!Paths.IsNetwork(prefs.Path) && !Projects.IsUnityProject(prefs.Path)) continue;
                    string error;
                    string folder = Folders.Clean(prefs.Folder, out error);
                    if (folder == null) continue;
                    return new Target { Path = prefs.Path, Name = System.IO.Path.GetFileName(prefs.Path), Folder = folder };
                }
            }
            catch (Exception ex)
            {
                Log.Write("quick item: " + ex.Message);
            }
            return null;
        }

        public static string Label(Target target, bool menu)
        {
            string name = menu ? ShellMenu.Escape(target.Name) : target.Name;
            string folder = menu ? ShellMenu.Escape(target.Folder) : target.Folder;
            return "Send to " + name + "   →  " + folder;
        }
    }

    // ------------------------------------------------------------------ Windows 11 short menu
    // Only commands from "packaged" programs appear in the short menu. A lightweight package
    // (sparse package) with the manifest and a logo is enough: the files stay in the program
    // folder. Windows accepts it only if it's signed with a certificate the PC trusts, so
    // the program creates its own, signs the package, deletes the private key right away (nobody
    // can sign anything else with that certificate) and asks once for administrator rights
    // to add it to the trusted certificates. The command runs in SendToUnity.exe --menu-server,
    // which Windows starts when you open the menu.

    // The same command, served by SendToUnity.exe instead of the DLL inside Explorer.
    // Windows shows it in both menus, so with the package active the registry item is removed.
    [ComVisible(true), Guid("3C8E5A21-7D4F-4B6A-9E1C-2F5D8A0B7C64"), ClassInterface(ClassInterfaceType.None)]
    public class ModernMenuCommand : MenuCommand
    {
    }

    [ComVisible(true), Guid("5D7E9F1A-2B3C-4D5E-8F6A-7B8C9D0E1F2A"), ClassInterface(ClassInterfaceType.None)]
    public class ModernQuickCommand : QuickCommand
    {
    }

    static class MenuServer
    {
        static long lastCall = DateTime.UtcNow.Ticks;

        public static void Touch()
        {
            Interlocked.Exchange(ref lastCall, DateTime.UtcNow.Ticks);
        }

        // Stays running a few minutes after the last use: next time the menu is ready right away
        public static int Run()
        {
            var services = new RegistrationServices();
            // each package starts its own copy: it serves only the item of that package
            string package = Native.CurrentPackage() ?? "";
            Type served = package.Contains(".Quick.") ? typeof(ModernQuickCommand) : typeof(ModernMenuCommand);
            int cookie = services.RegisterTypeForComClients(served,
                RegistrationClassContext.LocalServer, RegistrationConnectionType.MultipleUse);
            Log.Write("short menu: ready (" + served.Name + ", " + package + ")");
            var timer = new System.Windows.Forms.Timer { Interval = 5000 };
            timer.Tick += (s, e) =>
            {
                if (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastCall) > TimeSpan.FromMinutes(10).Ticks) Application.ExitThread();
            };
            timer.Start();
            Application.Run();
            services.UnregisterTypeForComClients(cookie);
            return 0;
        }
    }

    static class ModernMenu
    {
        // Different name and certificate for each user of the PC: each has their own package and doesn't touch the others'
        static readonly string UserTag = UserHash();
        public static readonly string PackageName = "DAVFX.InviaAUnity." + UserTag;
        static readonly string QuickPackageName = "DAVFX.InviaAUnity.Quick." + UserTag;
        static readonly string Publisher = "CN=InviaAUnity-" + UserTag;

        // the first versions used the same name for everyone: it must be removed, otherwise the item would appear twice
        const string LegacyPackage = "DAVFX.InviaAUnity";
        const string LegacyPublisher = "CN=InviaAUnity";

        static string UserHash()
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User.Value));
                return BitConverter.ToString(hash, 0, 4).Replace("-", "");
            }
        }

        static string Folder
        {
            get { return Path.Combine(App.Dir, "pacchetto"); }
        }

        static string StateFile
        {
            get { return Path.Combine(Folder, "stato.txt"); }
        }

        static string ActiveFile
        {
            get { return Path.Combine(Folder, "attivo.txt"); }
        }

        // Package registered: the classic menu item comes from there
        public static bool Active
        {
            get { return File.Exists(ActiveFile); }
        }

        public static bool Supported
        {
            get
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                        return int.Parse((string)key.GetValue("CurrentBuildNumber")) >= 22000;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static void Install()
        {
            TryDelete(ActiveFile);
            if (!Supported) return;
            if (Environment.GetEnvironmentVariable("SENDTOUNITY_SOLO_MENU_CLASSICO") == "1")
            {
                Log.Write("short menu: skipped on request");
                Remove();
                return;
            }
            try
            {
                Update();
            }
            catch (Exception ex)
            {
                Log.Write("short menu not installed: " + ex);
            }
            // what counts is the package actually there (for example the old one, if permission was denied):
            // if there is one, the registry item isn't needed and would appear twice
            try
            {
                if (PowerShell("if (Get-AppxPackage -Name $env:STU_PACKAGE) { 'si' }").Trim() == "si")
                    File.WriteAllText(ActiveFile, PackageName);
            }
            catch (Exception ex)
            {
                Log.Write("short menu: package state not read: " + ex.Message);
            }
        }

        static void Update()
        {
            string manifest = Manifest(false);
            string quickManifest = Manifest(true);
            string hash = Hash(manifest + quickManifest);
            string msix = Path.Combine(Folder, "InviaAUnity.msix");
            string quickMsix = Path.Combine(Folder, "InviaAUnityQuick.msix");
            string[] state = null;
            try { state = File.ReadAllText(StateFile).Split('|'); }
            catch (Exception) { }

            // same manifest and certificate still trusted: nothing to redo, no prompt
            if (state != null && state.Length == 2 && state[0] == hash && File.Exists(msix) && File.Exists(quickMsix) && IsTrusted(state[1]))
            {
                Register(msix, quickMsix, false);
                return;
            }

            Directory.CreateDirectory(Folder);
            string logo = Path.Combine(Folder, "logo.png");
            SaveLogo(logo);
            string manifestFile = Path.Combine(Folder, "AppxManifest.xml");
            File.WriteAllText(manifestFile, manifest, new UTF8Encoding(false));
            Package.Write(msix, manifestFile, logo);
            string quickManifestFile = Path.Combine(Folder, "AppxManifestQuick.xml");
            File.WriteAllText(quickManifestFile, quickManifest, new UTF8Encoding(false));
            Package.Write(quickMsix, quickManifestFile, logo);
            string cer = Path.Combine(Folder, "InviaAUnity.cer");
            // one certificate for both: still a single administrator prompt
            string thumbprint = PackageSigner.SignWithNewCertificate(new[] { msix, quickMsix }, Publisher, cer);
            if (!Trust(cer, thumbprint))
            {
                Log.Write("short menu: certificate not accepted, keeping the classic menu");
                return;
            }
            File.WriteAllText(StateFile, hash + "|" + thumbprint);
            Register(msix, quickMsix, true);
        }

        public static void Remove()
        {
            if (!Supported) return;
            try
            {
                PowerShell("Get-AppxPackage -Name $env:STU_PACKAGE | Remove-AppxPackage\n" +
                           "Get-AppxPackage -Name $env:STU_PACKAGE2 | Remove-AppxPackage\n" +
                           "Get-AppxPackage -Name '" + LegacyPackage + "' | Remove-AppxPackage");
                if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
            }
            catch (Exception ex)
            {
                Log.Write("short menu not removed: " + ex.Message);
            }
        }

        static void Register(string msix, string quickMsix, bool replace)
        {
            // paths go through environment variables: no character in the user name can break the script
            string add =
                "$p = Get-AppxPackage -Name $env:NAME\n" +
                (replace ? "if ($p) { Remove-AppxPackage -Package $p.PackageFullName; $p = $null }\n" : "") +
                "if (-not $p) { Add-AppxPackage -Path $env:MSIX -ExternalLocation $env:STU_DIR }\n";
            PowerShell(
                "Get-AppxPackage -Name '" + LegacyPackage + "' | Remove-AppxPackage\n" +
                add.Replace("$env:NAME", "$env:STU_PACKAGE").Replace("$env:MSIX", "$env:STU_MSIX") +
                add.Replace("$env:NAME", "$env:STU_PACKAGE2").Replace("$env:MSIX", "$env:STU_MSIX2"),
                "STU_MSIX", msix, "STU_MSIX2", quickMsix);
            Log.Write("short menu registered");
        }

        static void TryDelete(string file)
        {
            try { File.Delete(file); }
            catch (Exception) { }
        }

        static string PowerShell(string script, params string[] variables)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference = 'Stop'\n" + script));
            var info = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.EnvironmentVariables["STU_PACKAGE"] = PackageName;
            info.EnvironmentVariables["STU_PACKAGE2"] = QuickPackageName;
            info.EnvironmentVariables["STU_DIR"] = App.Dir;
            for (int i = 0; i + 1 < variables.Length; i += 2) info.EnvironmentVariables[variables[i]] = variables[i + 1];
            using (var process = Process.Start(info))
            {
                var error = process.StandardError.ReadToEndAsync();
                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(180000))
                {
                    try { process.Kill(); }
                    catch (Exception) { }
                    throw new TimeoutException("PowerShell is not responding");
                }
                process.WaitForExit();
                if (process.ExitCode != 0) throw new Exception("PowerShell: " + (error.Result + " " + output.Result).Trim());
                return output.Result;
            }
        }

        static bool IsTrusted(string thumbprint)
        {
            using (var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadOnly);
                return store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false).Count > 0;
            }
        }

        // The only step with administrator rights: Windows asks for confirmation
        static bool Trust(string cer, string thumbprint)
        {
            if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                TrustCertificate(cer, thumbprint);
            }
            else
            {
                try
                {
                    var info = new ProcessStartInfo(App.ExePath, "--trust " + Paths.Quote(cer) + " " + thumbprint) { UseShellExecute = true, Verb = "runas" };
                    using (var process = Process.Start(info)) process.WaitForExit();
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    Log.Write("short menu: administrator rights denied (" + ex.NativeErrorCode + ")");
                    return false;
                }
            }
            return IsTrusted(thumbprint);
        }

        // Adds the certificate to the PC's trusted ones and removes the program's old ones (for this user).
        // Runs with administrator rights: accepts only the certificate just created, for code signing only.
        public static void TrustCertificate(string cer, string thumbprint)
        {
            var certificate = new X509Certificate2(cer);
            bool codeSigningOnly = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .Any(e => e.EnhancedKeyUsages.Count == 1 && e.EnhancedKeyUsages[0].Value == "1.3.6.1.5.5.7.3.3");
            bool ca = certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority);
            if (!string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase) ||
                certificate.Subject != Publisher || !codeSigningOnly || ca)
                throw new InvalidOperationException("unexpected certificate, not adding it: " + certificate.Subject);
            using (var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadWrite);
                foreach (var old in store.Certificates)
                {
                    bool ours = old.Subject == Publisher || old.Subject == LegacyPublisher;
                    if (ours && old.Thumbprint != certificate.Thumbprint) store.Remove(old);
                }
                store.Add(certificate);
            }
            Log.Write("short menu: certificate added to trusted ones");
        }

        static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }

        static void SaveLogo(string file)
        {
            using (var bitmap = new Bitmap(44, 44))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var brush = new SolidBrush(SendDialog.OpenGreen)) g.FillEllipse(brush, 11, 11, 22, 22);
                }
                bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        // Two packages: Windows 11 groups the items of one program in a submenu with its name, so the
        // quick item has a package of its own to stay in the main menu
        static string Manifest(bool quick)
        {
            string clsid = (quick ? typeof(ModernQuickCommand) : typeof(ModernMenuCommand)).GUID.ToString().ToUpperInvariant();
            string name = quick ? QuickPackageName : PackageName;
            string title = quick ? "Send to Unity (last project)" : "Send to Unity";
            string app = quick ? "InviaAUnityQuick" : "InviaAUnity";
            string verb = quick ? "SendToUnityQuick" : "SendToUnity";
            return
@"<?xml version=""1.0"" encoding=""utf-8""?>
<Package xmlns=""http://schemas.microsoft.com/appx/manifest/foundation/windows10""
  xmlns:uap=""http://schemas.microsoft.com/appx/manifest/uap/windows10""
  xmlns:uap10=""http://schemas.microsoft.com/appx/manifest/uap/windows10/10""
  xmlns:desktop4=""http://schemas.microsoft.com/appx/manifest/desktop/windows10/4""
  xmlns:desktop5=""http://schemas.microsoft.com/appx/manifest/desktop/windows10/5""
  xmlns:desktop6=""http://schemas.microsoft.com/appx/manifest/desktop/windows10/6""
  xmlns:com=""http://schemas.microsoft.com/appx/manifest/com/windows10""
  xmlns:rescap=""http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities""
  IgnorableNamespaces=""uap uap10 desktop4 desktop5 desktop6 com rescap"">
  <Identity Name=""" + name + @""" Publisher=""" + Publisher + @""" Version=""1.0.0.0"" ProcessorArchitecture=""neutral"" />
  <Properties>
    <DisplayName>" + title + @"</DisplayName>
    <PublisherDisplayName>Send to Unity</PublisherDisplayName>
    <Logo>logo.png</Logo>
    <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
    <desktop6:FileSystemWriteVirtualization>disabled</desktop6:FileSystemWriteVirtualization>
    <desktop6:RegistryWriteVirtualization>disabled</desktop6:RegistryWriteVirtualization>
  </Properties>
  <Resources>
    <Resource Language=""en-us"" />
  </Resources>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Desktop"" MinVersion=""10.0.22000.0"" MaxVersionTested=""10.0.26100.0"" />
  </Dependencies>
  <Capabilities>
    <rescap:Capability Name=""runFullTrust"" />
    <rescap:Capability Name=""unvirtualizedResources"" />
  </Capabilities>
  <Applications>
    <Application Id=""" + app + @""" Executable=""SendToUnity.exe"" uap10:TrustLevel=""mediumIL"" uap10:RuntimeBehavior=""win32App"">
      <uap:VisualElements AppListEntry=""none"" DisplayName=""" + title + @""" Description=""" + title + @""" BackgroundColor=""transparent"" Square150x150Logo=""logo.png"" Square44x44Logo=""logo.png"" />
      <Extensions>
        <desktop4:Extension Category=""windows.fileExplorerContextMenus"">
          <desktop4:FileExplorerContextMenus>
            <desktop5:ItemType Type=""*"">
              <desktop5:Verb Id=""" + verb + @""" Clsid=""" + clsid + @""" />
            </desktop5:ItemType>
            <desktop5:ItemType Type=""Directory"">
              <desktop5:Verb Id=""" + verb + @""" Clsid=""" + clsid + @""" />
            </desktop5:ItemType>
          </desktop4:FileExplorerContextMenus>
        </desktop4:Extension>
        <com:Extension Category=""windows.comServer"">
          <com:ComServer>
            <com:ExeServer Executable=""SendToUnity.exe"" Arguments=""--menu-server"" DisplayName=""" + title + @""">
              <com:Class Id=""" + clsid + @""" DisplayName=""" + title + @""" />
            </com:ExeServer>
          </com:ComServer>
        </com:Extension>
      </Extensions>
    </Application>
  </Applications>
</Package>
";
        }
    }

    // Writes the .msix package with the Windows APIs (appxpackaging.dll), without external tools
    static class Package
    {
        [ComImport, Guid("5842a140-ff9f-4166-8f5c-62f5b7b0c781")]
        class AppxFactory { }

        [ComImport, Guid("beb94909-e451-438b-b5a7-d79e767b75d8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAppxFactory
        {
            void CreatePackageWriter(IntPtr output, ref PackageSettings settings, out IAppxPackageWriter writer);
        }

        [ComImport, Guid("9099e33b-246f-41e4-881a-008eb613f858"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAppxPackageWriter
        {
            void AddPayloadFile([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string contentType,
                int compression, IntPtr input);
            void Close(IntPtr manifest);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PackageSettings
        {
            public int ForceZip32;
            public IntPtr HashMethod;
        }

        [DllImport("urlmon.dll", CharSet = CharSet.Unicode)]
        static extern int CreateUri(string uri, uint flags, IntPtr reserved, out IntPtr result);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IntPtr template, out IntPtr stream);

        const uint Read = 0x20;                    // STGM_READ | STGM_SHARE_DENY_WRITE
        const uint Create = 0x1000 | 0x1 | 0x10;   // STGM_CREATE | STGM_WRITE | STGM_SHARE_EXCLUSIVE

        public static void Write(string msix, string manifest, string logo)
        {
            IntPtr output = IntPtr.Zero, uri = IntPtr.Zero, logoStream = IntPtr.Zero, manifestStream = IntPtr.Zero;
            IAppxPackageWriter writer = null;
            try
            {
                Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(msix, Create, 0x80, true, IntPtr.Zero, out output));
                Marshal.ThrowExceptionForHR(CreateUri("http://www.w3.org/2001/04/xmlenc#sha256", 0, IntPtr.Zero, out uri));
                Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(logo, Read, 0x80, false, IntPtr.Zero, out logoStream));
                Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(manifest, Read, 0x80, false, IntPtr.Zero, out manifestStream));
                var settings = new PackageSettings { ForceZip32 = 1, HashMethod = uri };
                ((IAppxFactory)new AppxFactory()).CreatePackageWriter(output, ref settings, out writer);
                writer.AddPayloadFile("logo.png", "image/png", 1, logoStream);   // APPX_COMPRESSION_OPTION_NORMAL
                writer.Close(manifestStream);
            }
            finally
            {
                if (writer != null) Marshal.ReleaseComObject(writer);
                foreach (IntPtr p in new[] { output, uri, logoStream, manifestStream })
                    if (p != IntPtr.Zero) Marshal.Release(p);
            }
        }
    }

    // Signs the package with SignerSignEx2 (mssign32.dll), as signtool does
    static class PackageSigner
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SignerFileInfo { public int Size; public IntPtr FileName; public IntPtr File; }

        [StructLayout(LayoutKind.Sequential)]
        struct SignerSubjectInfo { public int Size; public IntPtr Index; public int Choice; public IntPtr FileInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct SignerCertStoreInfo { public int Size; public IntPtr Certificate; public int Policy; public IntPtr Store; }

        [StructLayout(LayoutKind.Sequential)]
        struct SignerCert { public int Size; public int Choice; public IntPtr StoreInfo; public IntPtr Window; }

        [StructLayout(LayoutKind.Sequential)]
        struct SignerSignatureInfo { public int Size; public int HashAlgorithm; public int AttributeChoice; public IntPtr Authcode; public IntPtr Authenticated; public IntPtr Unauthenticated; }

        [StructLayout(LayoutKind.Sequential)]
        struct SignerSignEx2Params
        {
            public int Flags; public IntPtr SubjectInfo; public IntPtr SigningCert; public IntPtr SignatureInfo; public IntPtr ProviderInfo;
            public int TimestampFlags; public IntPtr TimestampAlgorithm; public IntPtr TimestampUrl; public IntPtr Attributes;
            public IntPtr SipData; public IntPtr SignerContext; public IntPtr CryptoPolicy; public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct AppxSipClientData { public IntPtr SignerParams; public IntPtr AppxSipState; }

        [DllImport("mssign32.dll")]
        static extern int SignerSignEx2(int flags, IntPtr subject, IntPtr cert, IntPtr signature, IntPtr provider, int timestampFlags,
            IntPtr timestampAlgorithm, IntPtr timestampUrl, IntPtr attributes, IntPtr sipData, IntPtr signerContext, IntPtr cryptoPolicy, IntPtr reserved);

        [DllImport("mssign32.dll")]
        static extern int SignerFreeSignerContext(IntPtr context);

        // Creates a new certificate, signs, saves the public part in cer and deletes the private key
        public static string SignWithNewCertificate(string[] files, string subject, string cer)
        {
            string password = Guid.NewGuid().ToString("N");
            byte[] pfx;
            using (var rsa = new RSACng(2048))
            {
                var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, false));
                using (var created = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(30)))
                    pfx = created.Export(X509ContentType.Pfx, password);
            }
            var certificate = new X509Certificate2(pfx, password, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
            try
            {
                File.WriteAllBytes(cer, certificate.Export(X509ContentType.Cert));
                foreach (string file in files) Sign(file, certificate.Handle);
                return certificate.Thumbprint;
            }
            finally
            {
                DeleteKey(certificate);
                certificate.Reset();
            }
        }

        static void DeleteKey(X509Certificate2 certificate)
        {
            try
            {
                var key = certificate.GetRSAPrivateKey();
                var cng = key as RSACng;
                if (cng != null) cng.Key.Delete();
                var csp = key as RSACryptoServiceProvider;
                if (csp != null)
                {
                    csp.PersistKeyInCsp = false;
                    csp.Clear();
                }
            }
            catch (Exception ex)
            {
                Log.Write("certificate key not deleted: " + ex.Message);
            }
        }

        static void Sign(string file, IntPtr certificate)
        {
            var memory = new List<IntPtr>();
            Func<object, IntPtr> put = value =>
            {
                IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(value));
                memory.Add(p);
                Marshal.StructureToPtr(value, p, false);
                return p;
            };
            Func<int, IntPtr> zeros = size =>
            {
                IntPtr p = Marshal.AllocHGlobal(size);
                memory.Add(p);
                for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
                return p;
            };
            try
            {
                IntPtr name = Marshal.StringToHGlobalUni(file);
                memory.Add(name);
                IntPtr fileInfo = put(new SignerFileInfo { Size = Marshal.SizeOf(typeof(SignerFileInfo)), FileName = name });
                IntPtr subject = put(new SignerSubjectInfo
                {
                    Size = Marshal.SizeOf(typeof(SignerSubjectInfo)), Index = zeros(4), Choice = 1, FileInfo = fileInfo   // SIGNER_SUBJECT_FILE
                });
                IntPtr storeInfo = put(new SignerCertStoreInfo
                {
                    Size = Marshal.SizeOf(typeof(SignerCertStoreInfo)), Certificate = certificate, Policy = 2   // SIGNER_CERT_POLICY_CHAIN
                });
                IntPtr cert = put(new SignerCert { Size = Marshal.SizeOf(typeof(SignerCert)), Choice = 2, StoreInfo = storeInfo });   // SIGNER_CERT_STORE
                IntPtr signature = put(new SignerSignatureInfo { Size = Marshal.SizeOf(typeof(SignerSignatureInfo)), HashAlgorithm = 0x800C });   // SHA-256
                IntPtr context = zeros(IntPtr.Size);
                IntPtr parameters = put(new SignerSignEx2Params
                {
                    SubjectInfo = subject, SigningCert = cert, SignatureInfo = signature, SignerContext = context
                });
                IntPtr sip = put(new AppxSipClientData { SignerParams = parameters });
                // the parameters point to the package data and vice versa, as .msix signing requires
                var withSip = (SignerSignEx2Params)Marshal.PtrToStructure(parameters, typeof(SignerSignEx2Params));
                withSip.SipData = sip;
                Marshal.StructureToPtr(withSip, parameters, false);

                int hr = SignerSignEx2(0, subject, cert, signature, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    sip, context, IntPtr.Zero, IntPtr.Zero);
                var sipData = (AppxSipClientData)Marshal.PtrToStructure(sip, typeof(AppxSipClientData));
                if (sipData.AppxSipState != IntPtr.Zero) Marshal.Release(sipData.AppxSipState);
                IntPtr signerContext = Marshal.ReadIntPtr(context);
                if (signerContext != IntPtr.Zero) SignerFreeSignerContext(signerContext);
                Marshal.ThrowExceptionForHR(hr);
            }
            finally
            {
                foreach (IntPtr p in memory) Marshal.FreeHGlobal(p);
            }
        }
    }

    // ------------------------------------------------------------------ keyboard shortcut
    // SendToUnity.exe --hotkey starts with Windows and stays in the background (no window, almost no
    // memory). With the shortcut (Ctrl+Alt+U unless changed) the files selected in the File Explorer
    // window in front are sent to the last project and folder used. Any mouse with programmable
    // buttons (Logi Options+, Razer Synapse...) can press the shortcut for you.

    static class HotkeyService
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "SendToUnity";
        const string ReloadEvent = @"Local\SendToUnity_hotkey_reload";
        const string QuitEvent = @"Local\SendToUnity_hotkey_quit";

        public static void Install()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                key.SetValue(RunValue, Paths.Quote(App.ExePath) + " --hotkey");
            Start();
        }

        public static void Uninstall()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (key != null) key.DeleteValue(RunValue, false);
            Signal(QuitEvent);
        }

        // Starts it if it isn't running; if it is, it rereads the settings
        public static void Start()
        {
            if (Signal(ReloadEvent)) return;
            try
            {
                // ShellExecute: the new process doesn't inherit the handles (and the console) of whoever starts it
                Process.Start(new ProcessStartInfo(App.ExePath, "--hotkey") { UseShellExecute = true, WorkingDirectory = App.Dir }).Dispose();
            }
            catch (Exception ex)
            {
                Log.Write("shortcut: not started: " + ex.Message);
            }
        }

        static bool Signal(string name)
        {
            try
            {
                using (var e = EventWaitHandle.OpenExisting(name))
                {
                    e.Set();
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static int Run()
        {
            using (var mutex = new Mutex(false, @"Local\SendToUnity_hotkey"))
            {
                bool owned;
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) return 0;   // already running

                Ui.Init();
                using (var reload = new EventWaitHandle(false, EventResetMode.AutoReset, ReloadEvent))
                using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEvent))
                using (var window = new HotkeyWindow())
                {
                    window.Load();
                    var watcher = new Thread(() =>
                    {
                        while (true)
                        {
                            int which = WaitHandle.WaitAny(new WaitHandle[] { reload, quit });
                            try
                            {
                                if (which == 1) { window.BeginInvoke(new MethodInvoker(window.Close)); return; }
                                window.BeginInvoke(new MethodInvoker(window.Load));
                            }
                            catch (Exception) { return; }
                        }
                    });
                    watcher.IsBackground = true;
                    watcher.Start();
                    Application.Run(window);
                }
                return 0;
            }
        }
    }

    // Invisible window that receives the shortcut
    class HotkeyWindow : Form
    {
        const int Id = 1;
        string registered;

        public HotkeyWindow()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            Size = new Size(0, 0);
            CreateHandle();
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);   // never shown
        }

        public void Load()
        {
            Native.UnregisterHotKey(Handle, Id);
            registered = null;
            string text = Settings.Load().EffectiveHotkey;
            if (text.Length == 0)
            {
                Log.Write("shortcut: off");
                return;
            }
            int modifiers;
            Keys key;
            if (!Shortcut.TryParse(text, out modifiers, out key))
            {
                Log.Write("shortcut: not valid: " + text);
                return;
            }
            if (Native.RegisterHotKey(Handle, Id, modifiers | 0x4000, (int)key))   // MOD_NOREPEAT
            {
                registered = text;
                Log.Write("shortcut: " + text);
            }
            else
            {
                Log.Write("shortcut: " + text + " already used by another program");
                Notify(text + " is used by another program", "Choose another shortcut in Send to Unity > Settings");
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && registered != null)   // WM_HOTKEY
            {
                try { Pressed(); }
                catch (Exception ex) { Log.Write("shortcut: " + ex); }
            }
            base.WndProc(ref m);
        }

        void Pressed()
        {
            List<string> files = ExplorerWindows.SelectionOf(Native.GetForeground());
            if (files == null)
            {
                Notify("Select files in File Explorer first", "Then press " + registered + " to send them to Unity");
                return;
            }
            if (files.Count == 0)
            {
                Notify("Nothing selected", "Select the files to send in File Explorer");
                return;
            }
            var target = QuickTarget.Get();
            Log.Write("shortcut: " + files.Count + " items to " + (target == null ? "(choose)" : target.Path));
            MenuCommand.Start(target == null ? new[] { "--pick" } : new[] { "--to", target.Path }, files);
        }

        static void Notify(string title, string detail)
        {
            var toast = new Toast(title, detail, null, null, null, null);
            toast.FormClosed += delegate { toast.Dispose(); };
            toast.Show();
        }

        protected override void Dispose(bool disposing)
        {
            if (IsHandleCreated) Native.UnregisterHotKey(Handle, Id);
            base.Dispose(disposing);
        }
    }

    // "Ctrl+Alt+U" <-> modifiers and key for RegisterHotKey
    static class Shortcut
    {
        public static bool TryParse(string text, out int modifiers, out Keys key)
        {
            modifiers = 0;
            key = Keys.None;
            foreach (string raw in text.Split('+'))
            {
                string part = raw.Trim();
                if (part.Length == 0) return false;
                switch (part.ToLowerInvariant())
                {
                    case "ctrl": case "control": modifiers |= 0x2; continue;
                    case "alt": modifiers |= 0x1; continue;
                    case "shift": modifiers |= 0x4; continue;
                    case "win": modifiers |= 0x8; continue;
                }
                if (key != Keys.None) return false;
                if (part.Length == 1 && char.IsDigit(part[0])) part = "D" + part;
                if (!Enum.TryParse(part, true, out key)) return false;
            }
            // a shortcut without modifiers would steal a normal key from every program (F-keys aside)
            bool function = key >= Keys.F1 && key <= Keys.F24;
            return key != Keys.None && (modifiers != 0 || function);
        }

        public static string Format(Keys modifiers, Keys key)
        {
            var parts = new List<string>();
            if ((modifiers & Keys.Control) != 0) parts.Add("Ctrl");
            if ((modifiers & Keys.Alt) != 0) parts.Add("Alt");
            if ((modifiers & Keys.Shift) != 0) parts.Add("Shift");
            string name = key.ToString();
            if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name.Substring(1);
            parts.Add(name);
            return string.Join("+", parts);
        }
    }

    // ------------------------------------------------------------------ settings window

    class SettingsDialog : Form
    {
        readonly TextBox shortcutBox = new TextBox();
        readonly CheckBox quickCheck = new CheckBox();
        readonly ComboBox themeCombo = new ComboBox();
        string shortcut;

        static readonly string[] Themes = { "dark", "light", "system" };

        public SettingsDialog()
        {
            var settings = Settings.Load();
            shortcut = settings.EffectiveHotkey;

            Name = "SettingsDialog";
            Text = App.Name + " - Settings";
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            int width = Ui.S(420);
            var grid = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                Padding = new Padding(Ui.S(16)),
                Margin = Padding.Empty
            };

            var target = QuickTarget.Get();
            grid.Controls.Add(Caption("Keyboard shortcut", 0));
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            shortcutBox.Name = "shortcutBox";
            shortcutBox.AccessibleName = "Keyboard shortcut";
            shortcutBox.ReadOnly = true;
            shortcutBox.Width = Ui.S(220);
            shortcutBox.Margin = new Padding(0, 0, Ui.S(6), 0);
            shortcutBox.Text = Display(shortcut);
            shortcutBox.KeyDown += CaptureShortcut;
            shortcutBox.PreviewKeyDown += (s, e) => e.IsInputKey = true;   // Tab, arrows and Enter become part of the shortcut too
            var clear = new Button { Name = "clearButton", Text = "None", AutoSize = true, Margin = Padding.Empty };
            clear.Click += delegate
            {
                shortcut = "";
                shortcutBox.Text = Display(shortcut);
            };
            row.Controls.Add(shortcutBox);
            row.Controls.Add(clear);
            grid.Controls.Add(row);
            grid.Controls.Add(Note("Click the box and press the keys. It sends the files selected in File Explorer to " +
                (target == null ? "the last project and folder used." : target.Name + "  ›  " + target.Folder + " (the last used)."), width));
            grid.Controls.Add(Note(MouseHint(), width));

            quickCheck.Name = "quickCheck";
            quickCheck.Text = "Show \"Send to <last project>\" in the right-click menu";
            quickCheck.AutoSize = true;
            quickCheck.Checked = !settings.HideQuickItem;
            quickCheck.Margin = new Padding(0, Ui.S(16), 0, 0);
            grid.Controls.Add(quickCheck);

            grid.Controls.Add(Caption("Theme", Ui.S(16)));
            themeCombo.Name = "themeCombo";
            themeCombo.AccessibleName = "Theme";
            themeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            themeCombo.Width = Ui.S(220);
            themeCombo.Margin = Padding.Empty;
            themeCombo.Items.AddRange(new object[] { "Dark", "Light", "Same as Windows" });
            int theme = Array.IndexOf(Themes, (settings.Theme ?? "dark").Trim().ToLowerInvariant());
            themeCombo.SelectedIndex = theme < 0 ? 0 : theme;
            grid.Controls.Add(themeCombo);
            grid.Controls.Add(Note("The new theme is used from the next window.", width));

            var save = new Button { Name = "saveButton", Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Name = "cancelButton", Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            save.MinimumSize = cancel.MinimumSize = new Size(Ui.S(92), 0);
            cancel.Margin = new Padding(Ui.S(8), 0, 0, 0);
            save.Margin = Padding.Empty;
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                WrapContents = false,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0, Ui.S(18), 0, 0)
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            save.TabIndex = 0;
            cancel.TabIndex = 1;
            grid.Controls.Add(buttons);

            Controls.Add(grid);
            AcceptButton = save;
            CancelButton = cancel;
            ActiveControl = save;
            FormClosing += (s, e) =>
            {
                if (DialogResult == DialogResult.OK) Save();
            };
            Shown += delegate
            {
                if (!Native.BringToFront(Handle)) Activate();
            };
            Look.Apply(this);
        }

        static string Display(string text)
        {
            return text.Length == 0 ? "(none)" : text;
        }

        void CaptureShortcut(object sender, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            var key = e.KeyCode;
            if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin) return;
            string text = Shortcut.Format(e.Modifiers, key);
            int modifiers;
            Keys parsed;
            if (!Shortcut.TryParse(text, out modifiers, out parsed))
            {
                shortcutBox.Text = "Add Ctrl, Alt or Shift (or use F1-F24)";
                return;
            }
            shortcut = text;
            shortcutBox.Text = text;
        }

        void Save()
        {
            string theme = Themes[Math.Max(0, themeCombo.SelectedIndex)];
            bool hide = !quickCheck.Checked;
            string chosen = shortcut;
            try
            {
                Settings.Update(s =>
                {
                    s.Hotkey = chosen == Settings.DefaultHotkey ? null : chosen;
                    s.HideQuickItem = hide;
                    s.Theme = theme == "dark" ? null : theme;
                });
                HotkeyService.Start();
                Log.Write("settings saved: shortcut " + Display(chosen) + ", quick item " + (hide ? "hidden" : "shown") + ", theme " + theme);
            }
            catch (Exception ex)
            {
                Ui.Error("Settings not saved: " + ex.Message);
            }
        }

        // Mouse software (Logitech above all) can press the shortcut with a button
        static string MouseHint()
        {
            bool logi = Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LogiOptionsPlus")) ||
                        Process.GetProcessesByName("logioptionsplus_agent").Length > 0;
            if (logi)
                return "Logi Options+ found: open it, choose a mouse button > Keyboard shortcut and press this shortcut. " +
                       "Tip: add it as an app-specific setting for File Explorer, so the button keeps its usual job elsewhere.";
            return "Mouse with programmable buttons (Logitech, Razer, ...)? In its software, assign this shortcut to a button.";
        }

        static Label Caption(string text, int top)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(0, top, 0, Ui.S(4)) };
        }

        static Label Note(string text, int width)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                UseMnemonic = false,
                MaximumSize = new Size(width, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, Ui.S(6), 0, 0)
            };
        }
    }

    static class Installer
    {
        static string SendToLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Unity project.lnk"); }
        }

        public static void Install(string dll)
        {
            // leftovers from the first version (PowerShell + VBScript)
            foreach (string old in new[] { "SendToUnity.ps1", "launcher.vbs", "config.json" })
                TryDelete(Path.Combine(App.Dir, old));
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Progetto Unity.lnk"));   // Italian versions
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), "SendToUnity-queue"), true); }
            catch (Exception) { }

            ShellMenu.SaveOpenIcon();
            ModernMenu.Install();
            ShellMenu.Register(dll);
            HotkeyService.Install();
            // "Send to" opens the choice window directly
            LinkFile.Create(SendToLink, App.ExePath, "--pick", ShellMenu.HubExe());
            Log.Write("installed: " + App.ExePath);
        }

        public static void Uninstall()
        {
            HotkeyService.Uninstall();
            ShellMenu.Remove();
            ModernMenu.Remove();
            TryDelete(SendToLink);
            Log.Write("uninstalled");
        }

        static void TryDelete(string file)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception) { }
        }
    }

    // ------------------------------------------------------------------ user interface

    static class Ui
    {
        static bool ready;
        static float scale = 1f;

        public static void Init()
        {
            if (ready) return;
            ready = true;
            try { Native.SetProcessDPIAware(); }
            catch (Exception) { }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            }
            catch (Exception) { }
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            }
            catch (Exception) { }
        }

        // Pixels at 100% converted for the current screen (125%, 150%, ...)
        public static int S(int pixels)
        {
            return (int)Math.Round(pixels * scale);
        }

        static DialogResult Box(string text, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            Init();
            // invisible "always on top" owner: the message doesn't end up behind other windows
            using (var owner = new Form { TopMost = true, ShowInTaskbar = false })
                return MessageBox.Show(owner, text, App.Name, buttons, icon);
        }

        public static void Error(string text)
        {
            Box(text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        public static void Info(string text)
        {
            Box(text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // Yes = replace, No = skip, Cancel = send nothing
        public static DialogResult AskOverwrite(List<string> targets, Result result)
        {
            Init();
            using (var dialog = new ConflictDialog(targets, result)) return dialog.ShowDialog();
        }

        // interrupt: if it becomes true the notification closes early (more files to copy have arrived)
        public static void ShowResult(Result result, Func<bool> interrupt)
        {
            if (result.Errors.Count > 0)
            {
                var lines = result.Errors.Take(10).ToList();
                if (result.Errors.Count > 10) lines.Add("... and " + (result.Errors.Count - 10) + " more");
                string text = Summary(result) + ".\n\nProblems:\n" + string.Join("\n", lines);
                if (result.AccessDenied)
                    text += "\n\nIf \"Controlled folder access\" is turned on in Windows Security " +
                            "(Ransomware protection), add SendToUnity.exe to the allowed apps.";
                Box(text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // clicking the notification opens the folder with the first delivered file already selected
            string open = result.FirstTarget ?? (Directory.Exists(result.Destination) ? result.Destination : null);
            string title = (result.Copied > 0 ? "\u2713  " : "") + Summary(result);
            using (var toast = new Toast(title, Where(result), open != null ? "Click to open the folder" : null, open, null, interrupt))
                Application.Run(toast);
        }

        // "Project  ›  Assets/Folder"
        public static string Where(Result result)
        {
            return Path.GetFileName(result.Project) + "  \u203A  " + result.Folder;
        }

        static string Summary(Result result)
        {
            string done = result.Moved ? "moved" : "sent";
            if (result.Copied == 0)
            {
                if (result.Skipped == 1) return "No files " + done + ": it was already there";
                if (result.Skipped > 1) return "No files " + done + ": they were already there";
                if (result.AlreadyThere == 1) return "The file is already in this folder";
                if (result.AlreadyThere > 1) return "The files are already in this folder";
                return "No files " + done;
            }
            string text = result.Copied == 1 ? "1 file " + done : result.Copied + " files " + done;
            if (result.Skipped > 0)
                text += ", " + result.Skipped + (result.Skipped == 1 ? " skipped (already there)" : " skipped (already there)");
            if (result.AlreadyThere > 0)
                text += ", " + result.AlreadyThere + " already in this folder";
            return text;
        }
    }

    class SendDialog : Form
    {
        public readonly List<string> Files;
        public ProjectInfo Project { get; private set; }
        public string Folder { get; private set; }
        public bool MoveFiles { get; private set; }

        readonly Collector collector;
        readonly Settings settings;
        readonly Label filesLabel = new Label();
        readonly Label pathLabel = new Label();
        readonly ComboBox projectCombo = new ComboBox();
        readonly ComboBox folderCombo = new ComboBox();
        readonly CheckBox moveCheck = new CheckBox();
        readonly Button browse;

        public SendDialog(List<string> files, List<ProjectInfo> projects, Settings settings, string preselect, Collector collector)
        {
            Files = files;
            this.settings = settings;
            this.collector = collector;

            Name = "SendDialog";
            Text = App.Name;
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            int width = Ui.S(430);
            var grid = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Padding = new Padding(Ui.S(14)),
                Margin = Padding.Empty
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            filesLabel.Name = "filesLabel";
            filesLabel.AutoSize = true;
            filesLabel.UseMnemonic = false;   // otherwise the & in names disappears
            filesLabel.MaximumSize = new Size(width + Ui.S(110), 0);
            filesLabel.Margin = new Padding(0, 0, 0, Ui.S(14));
            AddRow(grid, filesLabel, null);

            AddRow(grid, Caption("Project", 0), null);
            projectCombo.Name = "projectCombo";
            projectCombo.AccessibleName = "Project";   // name read by screen readers and UI Automation
            projectCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            projectCombo.Width = width;
            projectCombo.Margin = new Padding(0, 0, Ui.S(6), 0);
            projectCombo.DrawMode = DrawMode.OwnerDrawFixed;   // green dot next to open projects
            projectCombo.ItemHeight = Font.Height + Ui.S(8);
            projectCombo.DrawItem += DrawProject;
            projectCombo.Items.AddRange(projects.Cast<object>().ToArray());
            browse = new Button { Name = "browseButton", Text = "Browse...", AutoSize = true, Margin = Padding.Empty };
            AddRow(grid, projectCombo, browse);

            pathLabel.Name = "pathLabel";
            pathLabel.AutoSize = true;
            pathLabel.UseMnemonic = false;
            pathLabel.ForeColor = SystemColors.GrayText;
            pathLabel.MaximumSize = new Size(width + Ui.S(110), 0);
            pathLabel.Margin = new Padding(0, Ui.S(3), 0, 0);
            AddRow(grid, pathLabel, null);

            AddRow(grid, Caption("Folder inside the project (you can also type a new one)", Ui.S(14)), null);
            folderCombo.Name = "folderCombo";
            folderCombo.AccessibleName = "Folder";
            folderCombo.DropDownStyle = ComboBoxStyle.DropDown;
            folderCombo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            folderCombo.Margin = Padding.Empty;
            folderCombo.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            folderCombo.AutoCompleteSource = AutoCompleteSource.ListItems;
            folderCombo.MaxDropDownItems = 20;
            AddRow(grid, folderCombo, null);

            moveCheck.Name = "moveCheck";
            moveCheck.Text = "Move instead of copy (the originals are removed)";
            moveCheck.AutoSize = true;
            moveCheck.Margin = new Padding(0, Ui.S(12), 0, 0);
            AddRow(grid, moveCheck, null);

            var send = new Button { Name = "sendButton", Text = "Send", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Name = "cancelButton", Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            send.MinimumSize = cancel.MinimumSize = new Size(Ui.S(92), 0);
            cancel.Margin = new Padding(Ui.S(8), 0, 0, 0);
            send.Margin = Padding.Empty;
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                WrapContents = false,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0, Ui.S(16), 0, 0)
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(send);
            send.TabIndex = 0;   // right to left: Tab must go to Send first
            cancel.TabIndex = 1;
            AddRow(grid, buttons, null);

            Controls.Add(grid);
            AcceptButton = send;
            CancelButton = cancel;

            projectCombo.SelectedIndexChanged += delegate { ProjectChanged(); };
            browse.Click += delegate { Browse(); };
            ActiveControl = folderCombo;   // the project is already chosen: type the folder right away
            Shown += delegate
            {
                // opened by a background service: without this, keystrokes would still go to File Explorer
                if (!Native.BringToFront(Handle)) Activate();
                folderCombo.SelectAll();
                PullFiles();
            };
            collector.Arrived += OnArrived;

            SelectInitial(projects, preselect);
            FitDropDown(projectCombo);
            if (projectCombo.Items.Count == 0)
            {
                pathLabel.Text = "No projects found in Unity Hub: add one with \"Browse...\".";
                folderCombo.Text = "Assets";
                ActiveControl = browse;
            }
            UpdateFilesLabel();
            Look.Apply(this);
        }

        public static readonly Color OpenGreen = Color.FromArgb(22, 163, 74);

        // Project in the list: green dot + name + "open in Unity" in gray if Unity has it open.
        // The space for the dot is always there, so names stay aligned.
        static void DrawProject(object sender, DrawItemEventArgs e)
        {
            // the selected row (and the closed box when it has focus) is highlighted: the text must follow it
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var back = new SolidBrush(selected ? SystemColors.Highlight : Look.Field)) e.Graphics.FillRectangle(back, e.Bounds);
            if (e.Index < 0) return;
            var combo = (ComboBox)sender;
            object item = combo.Items[e.Index];
            var project = item as ProjectInfo;
            Color text = selected ? SystemColors.HighlightText : Look.Dark ? Look.Text : SystemColors.ControlText;
            Color soft = selected ? SystemColors.HighlightText : Look.Muted;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;

            int dot = Ui.S(8);
            int x = e.Bounds.Left + Ui.S(6);
            if (project != null && project.Open)
            {
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(OpenGreen))
                    e.Graphics.FillEllipse(brush, x, e.Bounds.Top + (e.Bounds.Height - dot) / 2, dot, dot);
            }
            x += dot + Ui.S(8);

            string name = project != null ? project.DisplayName : item.ToString();
            var area = new Rectangle(x, e.Bounds.Top, Math.Max(0, e.Bounds.Right - x), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, name, combo.Font, area, text, flags);
            if (project != null && project.Open)
            {
                int used = TextRenderer.MeasureText(e.Graphics, name, combo.Font, area.Size, flags).Width;
                var rest = new Rectangle(area.Left + used + Ui.S(6), area.Top, Math.Max(0, area.Width - used - Ui.S(6)), area.Height);
                TextRenderer.DrawText(e.Graphics, "open in Unity", combo.Font, rest, soft, flags);
            }
            e.DrawFocusRectangle();
        }

        // The drop-down list is as wide as the longest item (up to half the screen)
        static void FitDropDown(ComboBox combo)
        {
            int widest = 0;
            var longest = combo.Items.Cast<object>().Select(i => i.ToString()).OrderByDescending(t => t.Length).Take(30);
            foreach (string text in longest) widest = Math.Max(widest, TextRenderer.MeasureText(text, combo.Font).Width + Ui.S(22));
            int max = Screen.FromControl(combo).WorkingArea.Width / 2;
            combo.DropDownWidth = Math.Max(combo.Width, Math.Min(max, widest + SystemInformation.VerticalScrollBarWidth + Ui.S(12)));
        }

        static Label Caption(string text, int top)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(0, top, 0, Ui.S(4)) };
        }

        static void AddRow(TableLayoutPanel grid, Control left, Control right)
        {
            int row = grid.RowCount;
            grid.RowCount = row + 1;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(left, 0, row);
            if (right != null) grid.Controls.Add(right, 1, row);
            else grid.SetColumnSpan(left, 2);
        }

        void SelectInitial(List<ProjectInfo> projects, string preselect)
        {
            int index = 0;
            if (preselect != null)
            {
                index = projects.FindIndex(p => Paths.Same(p.Path, preselect));
                if (index < 0)
                {
                    // project from the menu that is no longer in the list (e.g. removed from Unity Hub)
                    var info = new ProjectInfo
                    {
                        Path = preselect,
                        Name = Path.GetFileName(preselect),
                        Open = Projects.IsOpen(preselect)
                    };
                    index = projectCombo.Items.Add(info);
                }
            }
            if (projectCombo.Items.Count > 0) projectCombo.SelectedIndex = index;
        }

        void ProjectChanged()
        {
            var project = projectCombo.SelectedItem as ProjectInfo;
            folderCombo.Items.Clear();
            if (project == null)
            {
                pathLabel.Text = "";
                return;
            }
            pathLabel.Text = project.Path;
            folderCombo.Items.AddRange(Folders.Existing(project.Path).Cast<object>().ToArray());
            FitDropDown(folderCombo);
            folderCombo.Text = settings.FolderFor(project.Path) ?? "Assets";
        }

        void Browse()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose the main folder of a Unity project (the one that contains Assets).";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                string path = Paths.Normalize(dialog.SelectedPath);
                if (!Projects.IsUnityProject(path))
                {
                    MessageBox.Show(this, "This folder is not a Unity project (Assets or ProjectSettings is missing).",
                        App.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                foreach (ProjectInfo existing in projectCombo.Items)
                {
                    if (Paths.Same(existing.Path, path))
                    {
                        projectCombo.SelectedItem = existing;
                        return;
                    }
                }
                try
                {
                    Settings.Update(s =>
                    {
                        if (!s.ExtraProjects.Any(p => Paths.Same(p, path))) s.ExtraProjects.Add(path);
                    });
                }
                catch (Exception ex)
                {
                    Log.Write("added project not saved: " + ex.Message);   // fine for this send anyway
                }
                var info = new ProjectInfo { Path = path, Name = Path.GetFileName(path), Open = Projects.IsOpen(path) };
                projectCombo.SelectedIndex = projectCombo.Items.Add(info);
            }
        }

        void OnArrived()
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(new MethodInvoker(PullFiles));
            }
            catch (Exception) { }   // if the window is closing, the files stay queued and are taken later
        }

        void PullFiles()
        {
            var more = collector.Take();
            if (more.Count == 0) return;
            Files.AddRange(more);
            UpdateFilesLabel();
        }

        void UpdateFilesLabel()
        {
            var names = Files.Select(f => Path.GetFileName(f.TrimEnd('\\'))).ToList();
            string list = string.Join(", ", names.Take(4));
            if (names.Count > 4) list += " and " + (names.Count - 4) + " more";
            filesLabel.Text = (names.Count == 1 ? "To send: " : names.Count + " items to send: ") + list;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (DialogResult != DialogResult.OK) return;

            string error = null;
            string folder = null;
            var project = projectCombo.SelectedItem as ProjectInfo;
            if (project == null)
                error = "Choose a project, or add it with \"Browse...\".";
            else if (!Projects.IsUnityProject(project.Path))
                error = "The project no longer exists:\n" + project.Path;
            else
                folder = Folders.Clean(folderCombo.Text, out error);

            if (error != null)
            {
                MessageBox.Show(this, error, App.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;   // the window stays open for corrections
                return;
            }
            Project = project;
            Folder = folder;
            MoveFiles = moveCheck.Checked;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            collector.Arrived -= OnArrived;
            base.OnFormClosed(e);
        }
    }

    // Question about files already there, with buttons that say what they do
    class ConflictDialog : Form
    {
        public ConflictDialog(List<string> targets, Result result)
        {
            Name = "ConflictDialog";
            Text = "Files already there";
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            int width = Ui.S(440);
            var grid = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                Padding = new Padding(Ui.S(16)),
                Margin = Padding.Empty
            };

            string where = Ui.Where(result);
            grid.Controls.Add(new Label
            {
                AutoSize = true,
                UseMnemonic = false,
                MaximumSize = new Size(width, 0),
                Font = new Font(Font, FontStyle.Bold),
                Text = (targets.Count == 1 ? "1 file already exists" : targets.Count + " files already exist") + " in " + where
            });

            var names = targets.Take(8).Select(t => Path.GetFileName(t)).ToList();
            if (targets.Count > 8) names.Add("... and " + (targets.Count - 8) + " more");
            grid.Controls.Add(new Label
            {
                AutoSize = true,
                UseMnemonic = false,
                MaximumSize = new Size(width, 0),
                Margin = new Padding(Ui.S(12), Ui.S(8), 0, 0),
                Text = string.Join("\n", names)
            });

            grid.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(width, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, Ui.S(12), 0, 0),
                Text = "If you replace them, Unity's import settings (.meta files) stay unchanged."
            });

            var replace = new Button { Text = "Replace", DialogResult = DialogResult.Yes, AutoSize = true };
            var skip = new Button { Text = "Skip these", DialogResult = DialogResult.No, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            foreach (var b in new[] { replace, skip, cancel })
            {
                b.MinimumSize = new Size(Ui.S(92), 0);
                b.Margin = new Padding(Ui.S(8), 0, 0, 0);
            }
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                WrapContents = false,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0, Ui.S(18), 0, 0)
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(skip);
            buttons.Controls.Add(replace);
            replace.TabIndex = 0;
            skip.TabIndex = 1;
            cancel.TabIndex = 2;
            grid.Controls.Add(buttons);

            Controls.Add(grid);
            AcceptButton = replace;   // the typical case: updated textures to send again
            CancelButton = cancel;
            ActiveControl = replace;  // otherwise focus lands on Cancel and Enter cancels
            Look.Apply(this);
            Shown += delegate
            {
                if (!Native.BringToFront(Handle)) Activate();
            };
        }
    }

    // Colors of the windows and notifications: dark by default, like Unity.
    // In settings.json, "Theme": "light" or "system" (follows the Windows app theme) changes it.
    static class Look
    {
        public static readonly bool Dark = ReadDark();
        public static readonly Color Window = Dark ? Color.FromArgb(32, 32, 32) : SystemColors.Control;
        public static readonly Color Field = Dark ? Color.FromArgb(45, 45, 45) : SystemColors.Window;
        public static readonly Color ButtonFace = Dark ? Color.FromArgb(55, 55, 55) : SystemColors.Control;
        public static readonly Color Muted = Dark ? Color.FromArgb(160, 160, 160) : SystemColors.GrayText;
        public static readonly Color Background = Dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
        public static readonly Color Border = Dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(222, 222, 222);
        public static readonly Color Text = Dark ? Color.White : Color.FromArgb(26, 26, 26);
        public static readonly Color Secondary = Dark ? Color.FromArgb(200, 200, 200) : Color.FromArgb(96, 96, 96);
        public static readonly Color Link = Dark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 95, 184);

        static bool ReadDark()
        {
            string theme = (Settings.Load().Theme ?? "dark").Trim().ToLowerInvariant();
            if (theme == "light") return false;
            if (theme != "system") return true;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return value is int && (int)value == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Dark colors for a dialog and all its controls (nothing to do with the light theme)
        public static void Apply(Form form)
        {
            if (!Dark) return;
            form.BackColor = Window;
            form.ForeColor = Text;
            WhenCreated(form, () => Native.DarkTitleBar(form.Handle));
            Paint(form.Controls);
            // the button that Enter presses stands out, as in the Windows dialogs
            var main = form.AcceptButton as Button;
            if (main != null) main.FlatAppearance.BorderColor = Link;
        }

        static void Paint(Control.ControlCollection controls)
        {
            foreach (Control c in controls)
            {
                var button = c as Button;
                var combo = c as ComboBox;
                var check = c as CheckBox;
                if (button != null)
                {
                    button.FlatStyle = FlatStyle.Flat;
                    button.BackColor = ButtonFace;
                    button.ForeColor = Text;
                    button.FlatAppearance.BorderColor = Border;
                    button.FlatAppearance.MouseOverBackColor = Color.FromArgb(68, 68, 68);
                    button.FlatAppearance.MouseDownBackColor = Color.FromArgb(80, 80, 80);
                }
                else if (combo != null)
                {
                    combo.BackColor = Field;
                    combo.ForeColor = Text;
                    var target = combo;
                    WhenCreated(combo, () => Native.DarkControl(target.Handle, "DarkMode_CFD"));
                }
                else if (check != null)
                {
                    check.FlatStyle = FlatStyle.Flat;
                    check.ForeColor = Text;
                    check.FlatAppearance.BorderColor = Border;
                    check.FlatAppearance.CheckedBackColor = Field;
                }
                else if (c is Label)
                {
                    c.ForeColor = c.ForeColor == SystemColors.GrayText ? Muted : Text;
                }
                Paint(c.Controls);
            }
        }

        static void WhenCreated(Control control, Action action)
        {
            if (control.IsHandleCreated) action();
            else control.HandleCreated += delegate { action(); };
        }

        // Title in Segoe UI Semibold (the one Windows uses for titles), if available
        public static Font TitleFont(Font font)
        {
            try { return new Font(new FontFamily("Segoe UI Semibold"), font.SizeInPoints + 1.5f, FontStyle.Regular); }
            catch (ArgumentException) { return new Font(font.FontFamily, font.SizeInPoints + 1.5f, FontStyle.Bold); }
        }
    }

    // Notification at the bottom right that doesn't steal focus from Unity: the final one disappears by itself
    // (not while the mouse is over it), the progress one updates until the copy is finished
    class Toast : Form
    {
        const int VisibleMs = 3500;

        readonly string open;           // file or folder to open with a click (null = nothing)
        readonly Progress progress;     // not null: progress notification
        readonly Func<bool> interrupt;  // true = must make way for a new send
        readonly Label titleLabel;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 100 };
        DateTime closeAt;
        Point mouseAtStart;   // if it appears right under a still mouse, it disappears anyway
        bool mouseMoved;
        bool placed;

        public Toast(string title, string detail, string hint, string open, Progress progress, Func<bool> interrupt)
        {
            this.open = open;
            this.progress = progress;
            this.interrupt = interrupt;
            Name = "toast";
            Text = App.Name + (progress == null ? " - notification" : " - progress");
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Look.Background;
            Font = SystemFonts.MessageBoxFont;

            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = new Padding(Ui.S(16), Ui.S(12), Ui.S(22), Ui.S(12))
            };
            titleLabel = Line(title, Look.Text, Look.TitleFont(Font), 0);
            panel.Controls.Add(titleLabel);
            panel.Controls.Add(Line(detail, Look.Secondary, Font, Ui.S(4)));
            if (hint != null) panel.Controls.Add(Line(hint, Look.Link, Font, Ui.S(6)));
            Controls.Add(panel);

            if (open != null)
            {
                Cursor = Cursors.Hand;
                Click += delegate { OpenTarget(); };
                panel.Click += delegate { OpenTarget(); };
                foreach (Control label in panel.Controls) label.Click += delegate { OpenTarget(); };
            }

            timer.Tick += delegate { Tick(); };
        }

        void Tick()
        {
            if (progress != null)
            {
                if (progress.Finished) Close();
                else titleLabel.Text = progress.Text;
            }
            else if (interrupt != null && interrupt())
            {
                Close();
            }
            else
            {
                // under the mouse it doesn't disappear: the user may be about to click it
                Point mouse = Control.MousePosition;
                if (mouse != mouseAtStart) mouseMoved = true;
                if (mouseMoved && Bounds.Contains(mouse)) closeAt = DateTime.UtcNow.AddMilliseconds(1500);
                else if (DateTime.UtcNow >= closeAt) Close();
            }
        }

        static Label Line(string text, Color color, Font font, int top)
        {
            return new Label
            {
                Text = text,
                ForeColor = color,
                Font = font,
                AutoSize = true,
                UseMnemonic = false,
                MaximumSize = new Size(Ui.S(460), 0),
                Margin = new Padding(0, top, 0, 0)
            };
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008;   // NOACTIVATE | TOOLWINDOW | TOPMOST
                cp.ClassStyle |= 0x00020000;                           // shadow, like menus
                return cp;
            }
        }

        // Rounded corners and a thin border like Windows 11 menus; on Windows 10 a 1-pixel border
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!Native.RoundCorners(Handle, Look.Border))
            {
                Padding = new Padding(1);
                BackColor = Look.Border;
                foreach (Control c in Controls) c.BackColor = Look.Background;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            placed = true;
            Place();
            closeAt = DateTime.UtcNow.AddMilliseconds(VisibleMs);
            mouseAtStart = Control.MousePosition;
            timer.Start();
        }

        // if the text changes length the notification stays anchored to the bottom right corner
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (placed) Place();
        }

        void Place()
        {
            var area = Screen.FromPoint(Control.MousePosition).WorkingArea;
            Location = new Point(area.Right - Width - Ui.S(16), area.Bottom - Height - Ui.S(16));
        }

        void OpenTarget()
        {
            Log.Write("opening " + open);
            Hide();
            Native.AllowSetForegroundWindow(-1);   // the folder opens in front of Unity, not behind it
            try
            {
                if (!File.Exists(open))
                {
                    Process.Start("explorer.exe", Paths.Quote(open));
                }
                else
                {
                    // file selected, in the window already open if there is one
                    if (!Native.ShowInExplorer(open)) Process.Start("explorer.exe", "/select," + Paths.Quote(open));
                    // some versions of Windows open the folder without selecting the file: handle it from here
                    if (!ExplorerWindows.Select(open, 5000)) Log.Write("file not selected in File Explorer");
                }
            }
            catch (Exception ex)
            {
                Log.Write("folder not opened: " + ex.Message);
            }
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ utilities

    static class Paths
    {
        public static string Normalize(string path)
        {
            string full = Path.GetFullPath(path.Trim().Trim('"'));
            string root = Path.GetPathRoot(full) ?? "";
            if (full.Length > root.Length) full = full.TrimEnd('\\', '/');
            return full;
        }

        public static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // true if path is the same as folder or is inside it
        public static bool IsInside(string path, string folder)
        {
            return Same(path, folder) ||
                   path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsNetwork(string path)
        {
            if (path.StartsWith(@"\\")) return true;
            try { return new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.Network; }
            catch (Exception) { return false; }
        }

        // Quoted command-line argument: trailing backslashes must be doubled
        public static string Quote(string text)
        {
            int slashes = 0;
            while (slashes < text.Length && text[text.Length - 1 - slashes] == '\\') slashes++;
            return "\"" + text + new string('\\', slashes) + "\"";
        }
    }

    static class Log
    {
        static readonly string FilePath = Path.Combine(App.Dir, "log.txt");
        static readonly int Pid = Process.GetCurrentProcess().Id;

        public static void Write(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + Pid + "] " + message + Environment.NewLine;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(App.Dir);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > 512 * 1024)
                    {
                        string old = FilePath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, line, Encoding.UTF8);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(15);   // another instance is writing
                }
                catch (Exception)
                {
                    return;
                }
            }
        }
    }

    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr window, string app, string idList);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetCurrentPackageFullName(ref int length, StringBuilder name);

        // Full name of the package this process runs in (null outside a package)
        public static string CurrentPackage()
        {
            try
            {
                int length = 0;
                if (GetCurrentPackageFullName(ref length, null) != 122) return null;   // ERROR_INSUFFICIENT_BUFFER
                var name = new StringBuilder(length);
                return GetCurrentPackageFullName(ref length, name) == 0 ? name.ToString() : null;
            }
            catch (Exception)
            {
                return null;   // Windows without packages
            }
        }

        // Dark title bar (Windows 10 2004 and later); older versions just ignore it
        public static void DarkTitleBar(IntPtr window)
        {
            try
            {
                int on = 1;
                DwmSetWindowAttribute(window, 20, ref on, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
            }
            catch (Exception) { }
        }

        // Dark version of a standard control (drop-down arrow, list, scroll bar), where Windows has one
        public static void DarkControl(IntPtr window, string theme)
        {
            try { SetWindowTheme(window, theme, null); }
            catch (Exception) { }
        }

        // Windows 11: rounded corners and border color; false if Windows doesn't support them
        public static bool RoundCorners(IntPtr window, Color border)
        {
            try
            {
                int round = 2;   // DWMWCP_ROUND
                if (DwmSetWindowAttribute(window, 33, ref round, 4) != 0) return false;
                int color = border.R | (border.G << 8) | (border.B << 16);
                DwmSetWindowAttribute(window, 34, ref color, 4);   // DWMWA_BORDER_COLOR
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr window, int id, int modifiers, int key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, int flags);

        public static IntPtr GetForeground()
        {
            return GetAncestor(GetForegroundWindow(), 2);   // GA_ROOT
        }

        public static string WindowText(IntPtr window)
        {
            var text = new StringBuilder(512);
            GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint thread, uint otherThread, bool attach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        struct NativeMessage
        {
            public IntPtr Window;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")] static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);
        [DllImport("shell32.dll")] static extern int SHOpenFolderAndSelectItems(IntPtr pidl, uint count, IntPtr[] items, uint flags);
        [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr memory);

        // Opens the file's folder with the file selected; if the folder is already open it uses that window
        public static bool ShowInExplorer(string file)
        {
            IntPtr pidl;
            uint ignored;
            if (SHParseDisplayName(file, IntPtr.Zero, out pidl, 0, out ignored) != 0 || pidl == IntPtr.Zero) return false;
            try
            {
                return SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0;
            }
            finally
            {
                CoTaskMemFree(pidl);
            }
        }

        // Peeking at the message queue (without removing messages) makes the process count as "ready for input"
        public static void SignalIdle()
        {
            NativeMessage message;
            PeekMessage(out message, IntPtr.Zero, 0, 0, 0);
        }

        public static bool BringToFront(IntPtr window)
        {
            if (IsIconic(window)) ShowWindow(window, 9);   // SW_RESTORE
            if (SetForegroundWindow(window)) return true;

            // Windows sometimes refuses: attach to the foreground thread and try again
            uint ignored;
            uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), out ignored);
            uint current = GetCurrentThreadId();
            if (foreground == 0 || foreground == current) return false;
            if (!AttachThreadInput(current, foreground, true)) return false;
            try
            {
                BringWindowToTop(window);
                return SetForegroundWindow(window);
            }
            finally
            {
                AttachThreadInput(current, foreground, false);
            }
        }
    }

    // File Explorer windows as seen through Windows automation (Shell.Application), as from a script
    static class ExplorerWindows
    {
        // Selects the file in the windows open on its folder, waiting for them to appear,
        // until the selection stays stable (a freshly opened view sometimes loses it)
        public static bool Select(string file, int timeoutMs)
        {
            bool done = false;
            var thread = new Thread(() =>
            {
                try { done = SelectLoop(file, timeoutMs); }
                catch (Exception ex) { Log.Write("selection in File Explorer: " + ex.Message); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;   // if File Explorer doesn't respond the program exits anyway
            thread.Start();
            thread.Join(timeoutMs + 2000);
            return done;
        }

        static bool SelectLoop(string file, int timeoutMs)
        {
            string folder = Path.GetDirectoryName(file);
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            var watch = Stopwatch.StartNew();
            long stableSince = -1;
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                int found = 0;
                int selected = 0;
                object windows = Call(shell, "Windows");
                int count = (int)Get(windows, "Count");
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        object view = Get(Call(windows, "Item", i), "Document");
                        object shown = Get(view, "Folder");
                        if (!Paths.Same((string)Get(Get(shown, "Self"), "Path"), folder)) continue;
                        found++;
                        if (IsSelected(view, file))
                        {
                            selected++;
                            continue;
                        }
                        object item = Call(shown, "ParseName", Path.GetFileName(file));
                        if (item != null) Call(view, "SelectItem", item, 1 | 4 | 8 | 16);   // only it, visible, with focus
                    }
                    catch (Exception) { }   // windows that don't show folders or that are closing
                }

                if (found > 0 && selected == found)
                {
                    if (stableSince < 0) stableSince = watch.ElapsedMilliseconds;
                    else if (watch.ElapsedMilliseconds - stableSince >= 1000) return true;
                }
                else
                {
                    stableSince = -1;
                }
                Thread.Sleep(200);
            }
            return stableSince >= 0;
        }

        // Files selected in the File Explorer window (null if the window isn't File Explorer).
        // With tabs (Windows 11) every tab has the same window: the one in front is the one in the title.
        public static List<string> SelectionOf(IntPtr window)
        {
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            object windows = Call(shell, "Windows");
            int count = (int)Get(windows, "Count");
            string title = Native.WindowText(window);
            object best = null;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    object explorer = Call(windows, "Item", i);
                    if (explorer == null || new IntPtr(Convert.ToInt64(Get(explorer, "HWND"))) != window) continue;
                    object view = Get(explorer, "Document");
                    string name = (string)Get(Get(view, "Folder"), "Title");
                    if (best == null || title == name || title.StartsWith(name + " - ")) best = view;
                }
                catch (Exception) { }   // windows that don't show folders or that are closing
            }
            if (best == null) return null;
            var files = new List<string>();
            object items = Call(best, "SelectedItems");
            int selected = (int)Get(items, "Count");
            for (int i = 0; i < selected; i++)
            {
                string path = (string)Get(Call(items, "Item", i), "Path");
                if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path))) files.Add(path);
            }
            return files;
        }

        static bool IsSelected(object view, string file)
        {
            object items = Call(view, "SelectedItems");
            int count = (int)Get(items, "Count");
            for (int i = 0; i < count; i++)
            {
                if (Paths.Same((string)Get(Call(items, "Item", i), "Path"), file)) return true;
            }
            return false;
        }

        static object Get(object target, string name)
        {
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
        }

        static object Call(object target, string name, params object[] args)
        {
            return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);
        }
    }

    // Windows Restart Manager: tells which processes keep a file open
    static class RestartManager
    {
        [StructLayout(LayoutKind.Sequential)]
        struct UniqueProcess
        {
            public int ProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcessInfo
        {
            public UniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint SessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        static extern int RmStartSession(out uint session, int flags, StringBuilder key);

        [DllImport("rstrtmgr.dll")]
        static extern int RmEndSession(uint session);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        static extern int RmRegisterResources(uint session, uint fileCount, string[] files,
            uint processCount, UniqueProcess[] processes, uint serviceCount, string[] services);

        [DllImport("rstrtmgr.dll")]
        static extern int RmGetList(uint session, out uint needed, ref uint count,
            [In, Out] ProcessInfo[] infos, ref uint rebootReasons);

        // null if Restart Manager doesn't respond
        public static List<int> Holders(string file)
        {
            uint session;
            if (RmStartSession(out session, 0, new StringBuilder(64)) != 0) return null;
            try
            {
                if (RmRegisterResources(session, 1, new[] { file }, 0, null, 0, null) != 0) return null;
                uint needed;
                uint count = 0;
                uint reasons = 0;
                int code = RmGetList(session, out needed, ref count, null, ref reasons);
                if (code == 0) return new List<int>();
                if (code != 234) return null;   // 234 = ERROR_MORE_DATA: there is at least one process
                var infos = new ProcessInfo[needed];
                count = needed;
                if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0) return null;
                return infos.Take((int)count).Select(i => i.Process.ProcessId).ToList();
            }
            finally
            {
                RmEndSession(session);
            }
        }
    }

    // .lnk shortcut (for "Send to") without going through VBScript
    static class LinkFile
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
            void GetIDList(out IntPtr idList);
            void SetIDList(IntPtr idList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        public static void Create(string linkPath, string target, string arguments, string icon)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(target);
                link.SetArguments(arguments);
                link.SetDescription(App.Name);
                if (icon != null) link.SetIconLocation(icon, 0);
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(linkPath, true);
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }
    }
}
