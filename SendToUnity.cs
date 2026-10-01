// Invia a Unity: dal tasto destro di Esplora risorse copia file e cartelle in un progetto Unity.
//
// install.cmd compila questo file con il compilatore C# incluso in Windows (.NET Framework 4),
// quindi il codice deve restare compatibile con C# 5: niente $"...", ?., nameof, out var,
// tuple e membri con =>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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
        public const string Name = "Invia a Unity";

        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SendToUnity");

        public static string ExePath
        {
            get { return Assembly.GetExecutingAssembly().Location; }
        }
    }

    // ------------------------------------------------------------------ avvio

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Log.Write("ERRORE " + ex);
                Ui.Error("Qualcosa è andato storto:\n" + ex.Message);
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
                    Installer.Install();
                    return 0;
                case "--uninstall":
                    Installer.Uninstall();
                    return 0;
                case "--refresh":
                    ShellMenu.Refresh(Settings.Load());
                    return 0;
            }

            // --to <progetto> = clic sul nome di un progetto nel menu; --pick = "Scegli progetto e cartella..."
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

            var files = list.Where(f => f.Trim().Length > 0).ToList();
            if (files.Count == 0)
            {
                Ui.Info("Seleziona dei file in Esplora risorse, poi tasto destro > Invia a Unity.");
                return 0;
            }

            string key = project == null ? "pick" : "to:" + project.ToLowerInvariant();
            return SingleInstance.Run(key, files, c => Sender.Lead(project, c));
        }
    }

    // ------------------------------------------------------------------ una sola istanza
    // Explorer avvia il programma una volta per ogni file selezionato. La prima istanza
    // (quella che ottiene il mutex) fa il lavoro; le altre le passano i loro file con una
    // named pipe e si chiudono subito. Un file viene confermato solo quando la prima istanza
    // l'ha preso in carico: se non arriva conferma, l'istanza riprova o diventa lei la prima.

    static class SingleInstance
    {
        public static int Run(string key, List<string> files, Func<Collector, int> lead)
        {
            string id = Hash(Environment.UserName + "|" + key);
            string pipeName = "SendToUnity_" + Process.GetCurrentProcess().SessionId + "_" + id;
            using (var mutex = new Mutex(false, @"Local\SendToUnity_" + id))
            {
                DateTime giveUp = DateTime.UtcNow.AddMinutes(2);
                while (true)
                {
                    bool owned;
                    try { owned = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { owned = true; }

                    if (owned)
                    {
                        try
                        {
                            Log.Write("leader " + key + " con " + files.Count + " elementi");
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
                        throw new Exception("Un altro invio verso lo stesso progetto è ancora in corso. Riprova tra poco.");
                    Thread.Sleep(50);
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
        const string End = "<FINE>";

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
                    if (line == null) return;   // il mittente si è interrotto: riproverà

                    lock (sync)
                    {
                        if (closed) return;      // nessuna conferma: il mittente aspetterà la prossima istanza
                        pending.AddRange(lines);
                        lastArrival = DateTime.UtcNow;
                    }
                    var writer = new StreamWriter(pipe, new UTF8Encoding(false), 64, true);
                    writer.WriteLine("OK");
                    writer.Flush();
                    pipe.WaitForPipeDrain();
                    Log.Write("ricevuti " + lines.Count + " elementi da un'altra istanza");

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

        // Prende i file arrivati finora
        public List<string> Take()
        {
            lock (sync)
            {
                var copy = new List<string>(pending);
                pending.Clear();
                return copy;
            }
        }

        // Smette di confermare: chi arriva dopo aspetta la fine di questa istanza e riparte da sé
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

        // Aspetta che le altre istanze abbiano finito di arrivare
        public void WaitQuiet(int quietMs, int maxMs)
        {
            DateTime started = DateTime.UtcNow;
            while (true)
            {
                DateTime last;
                lock (sync) last = lastArrival;
                DateTime now = DateTime.UtcNow;
                if ((now - last).TotalMilliseconds >= quietMs || (now - started).TotalMilliseconds >= maxMs) return;
                Thread.Sleep(20);
            }
        }
    }

    // ------------------------------------------------------------------ flusso principale

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
                    Ui.Error("Questo progetto non esiste più (o non è un progetto Unity):\n" + project);
                    ShellMenu.TryRefresh();
                    return 1;
                }
                string folder = settings.FolderFor(project);
                string error;
                if (folder != null && Folders.Clean(folder, out error) != null)
                    return Quick(project, folder, collector);
            }
            return WithDialog(project, collector, settings);
        }

        // Progetto e cartella già noti: nessuna finestra, solo la notifica finale
        static int Quick(string project, string folder, Collector collector)
        {
            var job = new CopyJob(project, folder, false);
            bool first = true;
            for (var files = collector.Take(); files.Count > 0; files = collector.Close())
            {
                if (!job.Run(files))
                {
                    collector.Close();
                    return 0;
                }
                if (first)
                {
                    first = false;
                    Projects.FocusEditor(project);
                }
                Ui.ShowResult(job.Result);   // mentre la notifica è visibile possono arrivare altri file
            }
            Remember(project, folder);
            return job.Result.Errors.Count > 0 ? 1 : 0;
        }

        static int WithDialog(string preselect, Collector collector, Settings settings)
        {
            var projects = Projects.Known(settings, true);
            foreach (var p in projects) Log.Write("progetto " + p.Path + (p.Open ? " (aperto)" : ""));

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
                Ui.ShowResult(job.Result);
                return job.Result.Errors.Count > 0 ? 1 : 0;
            }
        }

        static void Remember(string project, string folder)
        {
            try
            {
                ShellMenu.Refresh(Settings.Update(s => s.Remember(project, folder)));
            }
            catch (Exception ex)
            {
                Log.Write("preferenze non salvate: " + ex.Message);
            }
        }
    }

    // ------------------------------------------------------------------ copia

    class Result
    {
        public string Project;
        public string Folder;
        public string Destination;
        public bool Moved;
        public int Copied;
        public int Skipped;
        public readonly List<string> Errors = new List<string>();
    }

    class CopyJob
    {
        // File di sistema che non devono finire negli Assets quando si copia una cartella
        static readonly string[] Junk = { "Thumbs.db", "desktop.ini", ".DS_Store" };

        class Step
        {
            public string Source;
            public string Target;
            public bool Conflict;
        }

        public readonly Result Result;
        readonly string destination;
        readonly bool move;
        bool? overwrite;   // chiesto una volta sola per invio

        public CopyJob(string project, string folder, bool move)
        {
            this.move = move;
            destination = Path.Combine(project, folder.Replace('/', '\\'));
            Result = new Result { Project = project, Folder = folder, Destination = destination, Moved = move };
        }

        // false se l'utente annulla alla domanda sui file già presenti
        public bool Run(List<string> sources)
        {
            var folders = new List<string>();
            var movedRoots = new List<string>();
            var steps = Plan(sources, folders, movedRoots);

            var conflicts = steps.Where(s => s.Conflict).Select(s => s.Target).ToList();
            if (conflicts.Count > 0 && overwrite == null)
            {
                var answer = Ui.AskOverwrite(conflicts, Result);
                if (answer == DialogResult.Cancel)
                {
                    Log.Write("invio annullato dall'utente");
                    return false;
                }
                overwrite = answer == DialogResult.Yes;
            }

            Directory.CreateDirectory(destination);
            foreach (string folder in folders)
            {
                try { Directory.CreateDirectory(folder); }
                catch (Exception ex) { Result.Errors.Add(Path.GetFileName(folder) + ": " + ex.Message); }
            }
            foreach (var step in steps)
            {
                if (step.Conflict && overwrite != true)
                {
                    Result.Skipped++;
                    continue;
                }
                try
                {
                    Transfer(step);
                    Result.Copied++;
                }
                catch (Exception ex)
                {
                    Result.Errors.Add(Path.GetFileName(step.Source) + ": " + ex.Message);
                    Log.Write("errore su " + step.Source + ": " + ex.Message);
                }
            }
            if (move)
            {
                foreach (string root in movedRoots) RemoveEmptyFolders(root);
            }
            Log.Write("copiati " + Result.Copied + ", saltati " + Result.Skipped + ", errori " + Result.Errors.Count + " in " + destination);
            return true;
        }

        List<Step> Plan(List<string> sources, List<string> folders, List<string> movedRoots)
        {
            var steps = new List<Step>();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string raw in sources)
            {
                string source;
                try { source = Paths.Normalize(raw); }
                catch (Exception)
                {
                    Result.Errors.Add(raw + ": percorso non valido");
                    continue;
                }
                if (!seen.Add(source)) continue;

                string name = Path.GetFileName(source);
                string target = Path.Combine(destination, name);

                if (Directory.Exists(source))
                {
                    if (Paths.Same(source, target)) continue;   // è già lì
                    if (Paths.IsInside(destination, source))
                    {
                        Result.Errors.Add(name + ": non posso copiare una cartella dentro se stessa");
                        continue;
                    }
                    if (move) movedRoots.Add(source);
                    folders.Add(target);
                    var files = new List<string>();
                    Walk(source, files, folders, target);
                    foreach (string file in files)
                    {
                        if (Junk.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)) continue;
                        AddStep(steps, targets, file, Path.Combine(target, file.Substring(source.Length + 1)));
                    }
                }
                else if (File.Exists(source))
                {
                    if (Paths.Same(source, target)) continue;
                    AddStep(steps, targets, source, target);
                }
                else
                {
                    Result.Errors.Add(name + ": non trovato");
                }
            }
            return steps;
        }

        // Elenca i file di una cartella e prepara le sottocartelle (anche vuote) nella destinazione
        void Walk(string folder, List<string> files, List<string> folders, string target)
        {
            try
            {
                files.AddRange(Directory.GetFiles(folder));
                foreach (string sub in Directory.GetDirectories(folder))
                {
                    string subTarget = Path.Combine(target, Path.GetFileName(sub));
                    folders.Add(subTarget);
                    Walk(sub, files, folders, subTarget);
                }
            }
            catch (Exception ex)
            {
                Result.Errors.Add(Path.GetFileName(folder) + ": " + ex.Message);
            }
        }

        static void AddStep(List<Step> steps, HashSet<string> targets, string source, string target)
        {
            // due file con lo stesso nome finirebbero uno sull'altro: conta come file già presente
            bool conflict = !targets.Add(target) || File.Exists(target) || Directory.Exists(target);
            steps.Add(new Step { Source = source, Target = target, Conflict = conflict });
        }

        void Transfer(Step step)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(step.Target));
            if (Directory.Exists(step.Target))
                throw new IOException("nel progetto c'è già una cartella con questo nome");
            if (File.Exists(step.Target) && (File.GetAttributes(step.Target) & FileAttributes.ReadOnly) != 0)
                throw new IOException("il file nel progetto è in sola lettura (serve il checkout?)");

            if (move)
            {
                if (File.Exists(step.Target)) File.Delete(step.Target);
                File.Move(step.Source, step.Target);
            }
            else
            {
                File.Copy(step.Source, step.Target, true);
            }
            // una copia di un file in sola lettura resterebbe bloccata anche dentro Unity
            var attributes = File.GetAttributes(step.Target);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(step.Target, attributes & ~FileAttributes.ReadOnly);
        }

        static void RemoveEmptyFolders(string folder)
        {
            try
            {
                foreach (string sub in Directory.GetDirectories(folder)) RemoveEmptyFolders(sub);
                // se restano solo file di sistema come Thumbs.db la cartella si può togliere
                var left = Directory.GetFileSystemEntries(folder);
                if (left.All(f => File.Exists(f) && Junk.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)))
                {
                    foreach (string f in left) File.Delete(f);
                    Directory.Delete(folder);
                }
            }
            catch (Exception) { }
        }
    }

    // ------------------------------------------------------------------ progetti Unity

    public class ProjectInfo
    {
        public string Path;
        public string Name;
        public bool Open;
        public long HubTime;
        public long LastUsed;

        public override string ToString()
        {
            return Open ? Name + "   (aperto in Unity)" : Name;
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

        // Progetti di Unity Hub, recenti dell'editor, aggiunti a mano e già usati con questo programma
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
            return found.Values
                .OrderByDescending(p => p.Open)
                .ThenByDescending(p => p.LastUsed)
                .ThenByDescending(p => p.HubTime)
                .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
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
            // un disco di rete scollegato bloccherebbe il controllo per decine di secondi
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
                Log.Write("elenco di Unity Hub illeggibile: " + ex.Message);
            }
            return list;
        }

        // L'editor salva i progetti recenti nel registro (stringhe UTF-8)
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
                Log.Write("progetti recenti illeggibili: " + ex.Message);
            }
            return list;
        }

        static string LockFile(string project)
        {
            return Path.Combine(project, @"Temp\UnityLockfile");
        }

        // Unity tiene aperto Temp\UnityLockfile finché il progetto è aperto
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

        // Porta in primo piano l'editor che ha aperto il progetto: così Unity importa subito i file
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
                    // ripiego: il titolo della finestra di Unity inizia con il nome del progetto
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
                    Log.Write("finestra di Unity non trovata per " + project);
                    return;
                }
                Log.Write("Unity in primo piano: " + (Native.BringToFront(window) ? "si" : "no"));
            }
            catch (Exception ex)
            {
                Log.Write("primo piano non riuscito: " + ex.Message);
            }
        }
    }

    // ------------------------------------------------------------------ cartelle dentro Assets

    static class Folders
    {
        static readonly string[] Reserved =
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        // Restituisce la cartella ripulita (es. "Assets/Textures") oppure null, con il motivo in error
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
                error = "La cartella deve stare dentro Assets (per esempio Assets/Textures).";
                return null;
            }
            parts[0] = "Assets";

            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (string part in parts.Skip(1))
            {
                if (part == "." || part == "..")
                    error = "Non usare . o .. nel percorso.";
                else if (part.IndexOfAny(invalid) >= 0)
                    error = "\"" + part + "\" contiene caratteri non ammessi nei nomi di cartella.";
                else if (part.StartsWith(".") || part.EndsWith("~"))
                    error = "Unity ignora le cartelle che iniziano con un punto o finiscono con ~ (\"" + part + "\").";
                else if (part.EndsWith("."))
                    error = "Un nome di cartella non può finire con un punto (\"" + part + "\").";
                else if (Reserved.Contains(part.Split('.')[0].ToUpperInvariant()))
                    error = "\"" + part + "\" è un nome riservato da Windows.";
                if (error != null) return null;
            }
            return string.Join("/", parts);
        }

        // Sottocartelle esistenti di Assets, per il menu a tendina
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
                    if (name.StartsWith(".") || name.EndsWith("~")) continue;   // Unity le ignora
                    result.Add(sub.Substring(skip).Replace('\\', '/'));
                    if (item.Value + 1 < maxDepth) queue.Enqueue(new KeyValuePair<string, int>(sub, item.Value + 1));
                }
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }
    }

    // ------------------------------------------------------------------ impostazioni

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
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (loaded != null)
                    {
                        if (loaded.ExtraProjects == null) loaded.ExtraProjects = new List<string>();
                        if (loaded.Projects == null) loaded.Projects = new List<ProjectPrefs>();
                        loaded.ExtraProjects.RemoveAll(string.IsNullOrEmpty);
                        loaded.Projects.RemoveAll(p => p == null || string.IsNullOrEmpty(p.Path));
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("impostazioni illeggibili, riparto da zero: " + ex.Message);
            }
            return new Settings();
        }

        // Rilegge, modifica e salva sotto un mutex: due invii contemporanei non si cancellano a vicenda
        public static Settings Update(Action<Settings> change)
        {
            using (var mutex = new Mutex(false, @"Local\SendToUnity_settings"))
            {
                bool owned;
                try { owned = mutex.WaitOne(5000); }
                catch (AbandonedMutexException) { owned = true; }
                try
                {
                    var settings = Load();
                    change(settings);
                    Directory.CreateDirectory(App.Dir);
                    string temp = FilePath + ".tmp";
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
                            if (attempt >= 10) throw;   // qualcuno lo sta leggendo proprio ora
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

    // ------------------------------------------------------------------ menu del tasto destro

    static class ShellMenu
    {
        static readonly string[] Keys =
        {
            @"Software\Classes\*\shell\SendToUnity",
            @"Software\Classes\Directory\shell\SendToUnity"
        };
        const int MaxProjects = 10;

        public static string HubExe()
        {
            string hub = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Unity Hub\Unity Hub.exe");
            return File.Exists(hub) ? hub : null;
        }

        // Riscrive il sottomenu: un progetto per riga (i più usati in alto) + "Scegli progetto e cartella..."
        public static void Refresh(Settings settings)
        {
            var projects = Projects.Known(settings, false).Take(MaxProjects).ToList();
            string exe = Paths.Quote(App.ExePath);
            string hub = HubExe();
            foreach (string keyPath in Keys)
            {
                Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
                using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
                {
                    key.SetValue("MUIVerb", "&" + App.Name);   // tasto destro, poi I
                    key.SetValue("SubCommands", "");
                    key.SetValue("MultiSelectModel", "Player");
                    if (hub != null) key.SetValue("Icon", hub + ",0");
                    using (var shell = key.CreateSubKey("shell"))
                    {
                        for (int i = 0; i < projects.Count; i++)
                        {
                            var p = projects[i];
                            string folder = settings.FolderFor(p.Path);
                            string label = Escape(p.Name) + (folder != null ? "   →  " + Escape(folder) : "");
                            AddItem(shell, i.ToString("00"), label, exe + " --to " + Paths.Quote(p.Path) + " \"%1\"", false);
                        }
                        AddItem(shell, "99", "Scegli progetto e cartella...", exe + " --pick \"%1\"", projects.Count > 0);
                    }
                }
            }
        }

        public static void TryRefresh()
        {
            try { Refresh(Settings.Load()); }
            catch (Exception ex) { Log.Write("menu non aggiornato: " + ex.Message); }
        }

        public static void Remove()
        {
            foreach (string keyPath in Keys) Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
        }

        static void AddItem(RegistryKey shell, string id, string label, string command, bool separatorBefore)
        {
            using (var item = shell.CreateSubKey(id))
            {
                item.SetValue("MUIVerb", label);
                // Player: la voce resta anche con più di 15 file (Explorer avvia un'istanza per file)
                item.SetValue("MultiSelectModel", "Player");
                if (separatorBefore) item.SetValue("CommandFlags", 0x20, RegistryValueKind.DWord);
                using (var cmd = item.CreateSubKey("command")) cmd.SetValue("", command);
            }
        }

        // Nei menu & indica la lettera sottolineata: per mostrarla va raddoppiata
        static string Escape(string text)
        {
            return text.Replace("&", "&&");
        }
    }

    static class Installer
    {
        static string SendToLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Progetto Unity.lnk"); }
        }

        public static void Install()
        {
            // resti della prima versione (PowerShell + VBScript)
            foreach (string old in new[] { "SendToUnity.ps1", "launcher.vbs", "config.json" })
                TryDelete(Path.Combine(App.Dir, old));
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), "SendToUnity-queue"), true); }
            catch (Exception) { }

            ShellMenu.Refresh(Settings.Load());
            // "Invia a" passa tutti i file in una volta: serve quando se ne selezionano più di 15
            LinkFile.Create(SendToLink, App.ExePath, "--pick", ShellMenu.HubExe());
            Log.Write("installato: " + App.ExePath);
        }

        public static void Uninstall()
        {
            ShellMenu.Remove();
            TryDelete(SendToLink);
            Log.Write("disinstallato");
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

    // ------------------------------------------------------------------ interfaccia

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

        // Pixel a 100% convertiti per lo schermo attuale (125%, 150%, ...)
        public static int S(int pixels)
        {
            return (int)Math.Round(pixels * scale);
        }

        static DialogResult Box(string text, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            Init();
            // proprietario invisibile "sempre in primo piano": il messaggio non finisce dietro altre finestre
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

        // Yes = sostituisci, No = salta, Cancel = non inviare niente
        public static DialogResult AskOverwrite(List<string> targets, Result result)
        {
            Init();
            using (var dialog = new ConflictDialog(targets, result)) return dialog.ShowDialog();
        }

        public static void ShowResult(Result result)
        {
            if (result.Errors.Count > 0)
            {
                var lines = result.Errors.Take(10).ToList();
                if (result.Errors.Count > 10) lines.Add("... e altri " + (result.Errors.Count - 10));
                Box(Summary(result) + ".\n\nProblemi:\n" + string.Join("\n", lines),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (result.Copied + result.Skipped == 0) return;
            string where = Path.GetFileName(result.Project) + "  ›  " + result.Folder;
            using (var toast = new Toast(Summary(result), where, result.Destination))
                Application.Run(toast);
        }

        static string Summary(Result result)
        {
            string done = result.Moved ? "spostat" : "inviat";
            string text = result.Copied == 1 ? "1 file " + done + "o" : result.Copied + " file " + done + "i";
            if (result.Skipped > 0) text += ", " + result.Skipped + (result.Skipped == 1 ? " saltato" : " saltati");
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
            filesLabel.UseMnemonic = false;   // altrimenti la & nei nomi sparisce
            filesLabel.MaximumSize = new Size(width + Ui.S(110), 0);
            filesLabel.Margin = new Padding(0, 0, 0, Ui.S(14));
            AddRow(grid, filesLabel, null);

            AddRow(grid, Caption("Progetto", 0), null);
            projectCombo.Name = "projectCombo";
            projectCombo.AccessibleName = "Progetto";   // nome letto da screen reader e UI Automation
            projectCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            projectCombo.Width = width;
            projectCombo.Margin = new Padding(0, 0, Ui.S(6), 0);
            projectCombo.Items.AddRange(projects.Cast<object>().ToArray());
            var browse = new Button { Name = "browseButton", Text = "Sfoglia...", AutoSize = true, Margin = Padding.Empty };
            AddRow(grid, projectCombo, browse);

            pathLabel.Name = "pathLabel";
            pathLabel.AutoSize = true;
            pathLabel.UseMnemonic = false;
            pathLabel.ForeColor = SystemColors.GrayText;
            pathLabel.MaximumSize = new Size(width + Ui.S(110), 0);
            pathLabel.Margin = new Padding(0, Ui.S(3), 0, 0);
            AddRow(grid, pathLabel, null);

            AddRow(grid, Caption("Cartella dentro il progetto (puoi anche scriverne una nuova)", Ui.S(14)), null);
            folderCombo.Name = "folderCombo";
            folderCombo.AccessibleName = "Cartella";
            folderCombo.DropDownStyle = ComboBoxStyle.DropDown;
            folderCombo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            folderCombo.Margin = Padding.Empty;
            folderCombo.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            folderCombo.AutoCompleteSource = AutoCompleteSource.ListItems;
            folderCombo.MaxDropDownItems = 20;
            AddRow(grid, folderCombo, null);

            moveCheck.Name = "moveCheck";
            moveCheck.Text = "Sposta invece di copiare (gli originali vengono tolti)";
            moveCheck.AutoSize = true;
            moveCheck.Margin = new Padding(0, Ui.S(12), 0, 0);
            AddRow(grid, moveCheck, null);

            var send = new Button { Name = "sendButton", Text = "Invia", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Name = "cancelButton", Text = "Annulla", DialogResult = DialogResult.Cancel, AutoSize = true };
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
            AddRow(grid, buttons, null);

            Controls.Add(grid);
            AcceptButton = send;
            CancelButton = cancel;

            projectCombo.SelectedIndexChanged += delegate { ProjectChanged(); };
            browse.Click += delegate { Browse(); };
            ActiveControl = folderCombo;   // il progetto e' gia' scelto: si scrive subito la cartella
            Shown += delegate
            {
                Activate();
                folderCombo.SelectAll();
                PullFiles();
            };
            collector.Arrived += OnArrived;

            SelectInitial(projects, preselect);
            UpdateFilesLabel();
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
                    // progetto del menu che non è più nell'elenco (es. tolto da Unity Hub)
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
            folderCombo.Text = settings.FolderFor(project.Path) ?? "Assets";
        }

        void Browse()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Scegli la cartella principale di un progetto Unity (quella che contiene Assets).";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                string path = Paths.Normalize(dialog.SelectedPath);
                if (!Projects.IsUnityProject(path))
                {
                    MessageBox.Show(this, "Questa cartella non è un progetto Unity (mancano Assets o ProjectSettings).",
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
                Settings.Update(s =>
                {
                    if (!s.ExtraProjects.Any(p => Paths.Same(p, path))) s.ExtraProjects.Add(path);
                });
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
            catch (Exception) { }   // se la finestra si sta chiudendo i file restano in coda e vengono presi dopo
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
            if (names.Count > 4) list += " e altri " + (names.Count - 4);
            filesLabel.Text = (names.Count == 1 ? "Da inviare: " : names.Count + " elementi da inviare: ") + list;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (DialogResult != DialogResult.OK) return;

            string error = null;
            string folder = null;
            var project = projectCombo.SelectedItem as ProjectInfo;
            if (project == null)
                error = "Scegli un progetto, oppure aggiungilo con \"Sfoglia...\".";
            else if (!Projects.IsUnityProject(project.Path))
                error = "Il progetto non esiste più:\n" + project.Path;
            else
                folder = Folders.Clean(folderCombo.Text, out error);

            if (error != null)
            {
                MessageBox.Show(this, error, App.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;   // la finestra resta aperta per correggere
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

    // Domanda sui file già presenti, con pulsanti che dicono cosa fanno
    class ConflictDialog : Form
    {
        public ConflictDialog(List<string> targets, Result result)
        {
            Name = "ConflictDialog";
            Text = "File già presenti";
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

            string where = Path.GetFileName(result.Project) + "  \u203A  " + result.Folder;
            grid.Controls.Add(new Label
            {
                AutoSize = true,
                UseMnemonic = false,
                MaximumSize = new Size(width, 0),
                Font = new Font(Font, FontStyle.Bold),
                Text = (targets.Count == 1 ? "1 file esiste già" : targets.Count + " file esistono già") + " in " + where
            });

            var names = targets.Take(8).Select(t => Path.GetFileName(t)).ToList();
            if (targets.Count > 8) names.Add("... e altri " + (targets.Count - 8));
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
                Text = "Sostituendoli, le impostazioni di import di Unity (file .meta) restano invariate."
            });

            var replace = new Button { Text = "Sostituisci", DialogResult = DialogResult.Yes, AutoSize = true };
            var skip = new Button { Text = "Salta questi", DialogResult = DialogResult.No, AutoSize = true };
            var cancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, AutoSize = true };
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
            grid.Controls.Add(buttons);

            Controls.Add(grid);
            AcceptButton = replace;   // il caso tipico: texture aggiornate da rimandare
            CancelButton = cancel;
            ActiveControl = replace;  // altrimenti il focus finisce su Annulla e Invio annulla
            Shown += delegate { Activate(); };
        }
    }

    // Notifica in basso a destra che sparisce da sola e non ruba il focus a Unity
    class Toast : Form
    {
        readonly string folder;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        public Toast(string title, string detail, string folder)
        {
            this.folder = folder;
            Name = "toast";
            Text = App.Name + " - notifica";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Color.FromArgb(40, 40, 40);
            Font = SystemFonts.MessageBoxFont;
            Cursor = Cursors.Hand;

            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = new Padding(Ui.S(16), Ui.S(12), Ui.S(22), Ui.S(12))
            };
            panel.Controls.Add(Line("✓  " + title, Color.White, new Font(Font.FontFamily, Font.SizeInPoints + 1.5f, FontStyle.Bold), 0));
            panel.Controls.Add(Line(detail, Color.FromArgb(220, 220, 220), Font, Ui.S(4)));
            panel.Controls.Add(Line("Clic per aprire la cartella", Color.FromArgb(150, 150, 150), Font, Ui.S(2)));
            Controls.Add(panel);

            Click += delegate { OpenFolder(); };
            panel.Click += delegate { OpenFolder(); };
            foreach (Control label in panel.Controls) label.Click += delegate { OpenFolder(); };

            timer.Interval = 2600;
            timer.Tick += delegate { Close(); };
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
                return cp;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var area = Screen.FromPoint(Control.MousePosition).WorkingArea;
            Location = new Point(area.Right - Width - Ui.S(16), area.Bottom - Height - Ui.S(16));
            timer.Start();
        }

        void OpenFolder()
        {
            try { Process.Start("explorer.exe", Paths.Quote(folder)); }
            catch (Exception) { }
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ utilità

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

        // true se path coincide con folder o sta al suo interno
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

        // Argomento tra virgolette per la riga di comando: i backslash finali vanno raddoppiati
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
                    Thread.Sleep(15);   // un'altra istanza sta scrivendo
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
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint thread, uint otherThread, bool attach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        public static bool BringToFront(IntPtr window)
        {
            if (IsIconic(window)) ShowWindow(window, 9);   // SW_RESTORE
            if (SetForegroundWindow(window)) return true;

            // Windows a volte rifiuta: ci si aggancia al thread in primo piano e si riprova
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

    // Restart Manager di Windows: dice quali processi tengono aperto un file
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

        // null se Restart Manager non risponde
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
                if (code != 234) return null;   // 234 = ERROR_MORE_DATA: c'è almeno un processo
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

    // Collegamento .lnk (per "Invia a") senza passare da VBScript
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
