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
        public const string Name = "Invia a Unity";

        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SendToUnity");

        // Dentro Esplora risorse gira la DLL del menu, che sta nella stessa cartella del programma
        public static string ExePath
        {
            get
            {
                string self = Assembly.GetExecutingAssembly().Location;
                return self.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? self : Path.Combine(Dir, "SendToUnity.exe");
            }
        }
    }

    // ------------------------------------------------------------------ avvio

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Explorer avvia l'istanza per il file successivo solo quando questa risulta
            // "pronta all'input": lo segnalo subito, così con tanti file non ci sono pause
            Native.SignalIdle();
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
                    ModernMenu.TrustCertificate(list[1]);
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

            // --list <file> = elenco scritto dal menu (un percorso per riga): niente limiti di lunghezza
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
                Ui.Info("Invia a Unity si usa dal tasto destro: seleziona file o cartelle in Esplora risorse, " +
                        "poi tasto destro > Invia a Unity.\n\nSu Windows 11, se non la vedi subito, è sotto " +
                        "\"Mostra altre opzioni\" (oppure Maiusc + tasto destro).");
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
                // chi arriva mentre la prima istanza sta ancora copiando aspetta che finisca
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
                    wait = 500;   // da qui si aspetta il mutex invece di riprovare a vuoto
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

        public bool HasPending
        {
            get { lock (sync) return pending.Count > 0; }
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
                Native.SignalIdle();   // vedi Program.Main
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
            int status = 0;
            CopyJob job = null;
            var files = collector.Take();
            while (files.Count > 0)
            {
                // un giro per ogni invio, con il suo conteggio e la sua domanda sui file già presenti
                if (job == null) job = new CopyJob(project, folder, false);
                do
                {
                    if (!job.Run(files))
                    {
                        collector.Close();
                        return status;
                    }
                    // con molti file Explorer avvia le istanze a ondate (a volte con pause): si copia finché ne arrivano
                    collector.WaitQuiet(900, 3000);
                    files = collector.Take();
                }
                while (files.Count > 0);

                // tutto copiato: ora Unity in primo piano, così importa tutto in una volta
                Projects.FocusEditor(project);
                Log.Write("notifica: " + job.Result.Copied + " copiati, " + job.Result.Skipped + " saltati");
                if (job.Result.Errors.Count > 0) status = 1;
                // un nuovo invio mentre la notifica è visibile la chiude e viene copiato subito
                var shown = Stopwatch.StartNew();
                Ui.ShowResult(job.Result, () => collector.HasPending);
                files = collector.Take();
                if (files.Count == 0) files = collector.Close();
                // file arrivati appena dopo: sono ancora lo stesso invio (Explorer in ritardo), il conteggio continua
                if (shown.ElapsedMilliseconds > 1500) job = null;
            }
            Remember(project, folder);
            return status;
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
        public int Copied;          // file inviati (i .meta che li accompagnano non contano)
        public int Skipped;         // già presenti e lasciati com'erano
        public int AlreadyThere;    // erano già proprio in quella cartella
        public bool AccessDenied;   // Windows ha negato la scrittura
        public string FirstTarget;  // primo file arrivato: viene selezionato aprendo la cartella
        public readonly List<string> Errors = new List<string>();
    }

    class CopyJob
    {
        // File di sistema che non devono finire negli Assets quando si copia una cartella
        static readonly string[] JunkFiles = { "Thumbs.db", "desktop.ini", ".DS_Store" };

        class Step
        {
            public string Source;
            public string Target;
            public bool Conflict;
            public bool IsMeta;   // .meta di Unity che accompagna un file o una cartella
            public Step Owner;    // il file che il .meta accompagna (null per le cartelle)
            public bool Done;
        }

        public readonly Result Result;
        readonly string project;
        readonly string destination;
        readonly bool move;
        bool? overwrite;   // chiesto una volta sola per invio
        // cartelle d'origine e dove finiscono: spostando, servono per portare i .meta delle cartelle
        readonly Dictionary<string, string> folderTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public CopyJob(string project, string folder, bool move)
        {
            this.project = project;
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

            try { Directory.CreateDirectory(destination); }
            catch (Exception ex)
            {
                // per esempio "Accesso controllato alle cartelle": il messaggio finale spiega cosa fare
                AddError(Result.Folder, ex);
                Log.Write("cartella " + destination + " non creata: " + ex.Message);
                return true;
            }
            foreach (string folder in folders)
            {
                try { Directory.CreateDirectory(folder); }
                catch (Exception ex) { AddError(Path.GetFileName(folder), ex); }
            }

            using (var progress = new Progress(Ui.Where(Result), steps.Count(s => !s.IsMeta)))
            {
                foreach (var step in steps.Where(s => !s.IsMeta))
                {
                    progress.Step();
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
                        Log.Write("errore su " + step.Source + ": " + ex.Message);
                    }
                }
            }
            // poi i .meta, che seguono solo i file arrivati davvero
            foreach (var step in steps.Where(s => s.IsMeta))
            {
                if (step.Owner != null && !step.Owner.Done) continue;   // file saltato: il .meta resta con lui
                try { TransferMeta(step); }
                catch (Exception ex) { Log.Write("meta " + step.Source + ": " + ex.Message); }
            }
            if (move)
            {
                foreach (string root in movedRoots) Cleanup(root);
            }
            Log.Write("copiati " + Result.Copied + ", saltati " + Result.Skipped + ", errori " + Result.Errors.Count + " in " + destination);
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
            var metas = new List<KeyValuePair<string, string>>();   // .meta da valutare: origine, destinazione

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
                // i .meta non si mandano da soli: viaggiano con il loro file (vedi sotto)
                if (IsMeta(source) && File.Exists(source)) continue;

                string name = Path.GetFileName(source);
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
                        Result.Errors.Add(name + ": non posso copiare una cartella dentro se stessa");
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
                        ? ": è dentro un archivio (zip), va estratto prima"
                        : ": non trovato"));
                }
            }

            // .meta di Unity: spostando seguono sempre il loro file, così Unity non perde i riferimenti;
            // copiando si portano solo da un altro progetto (dentro lo stesso darebbero GUID doppi).
            // Un .meta che il progetto ha già non viene mai sostituito (vedi TransferMeta).
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
                        // .meta di una cartella: spostando ci pensa Cleanup, che sa se la cartella è stata svuotata
                        if (move) continue;
                    }
                    else if (!move) continue;   // .meta orfano: copiando resta dov'è, spostando segue la sua cartella
                    owner = null;
                }
                steps.Add(new Step { Source = meta.Key, Target = meta.Value, IsMeta = true, Owner = owner });
            }
            return steps;
        }

        // Elenca i file di una cartella e prepara le sottocartelle (anche vuote) nella destinazione
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
                    string name = Path.GetFileName(sub);
                    // .git, .svn e simili, e i resti degli zip fatti su Mac: non sono asset
                    if (name.StartsWith(".") || name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
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

        static bool IsJunk(string name)
        {
            if (IsMeta(name)) name = name.Substring(0, name.Length - ".meta".Length);   // anche il loro .meta
            return name.StartsWith("._") || JunkFiles.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        static bool IsMeta(string path)
        {
            return path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
        }

        // Explorer mostra il contenuto degli zip come cartelle, ma quei file sul disco non esistono
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
            // due file con lo stesso nome finirebbero uno sull'altro: conta come file già presente
            bool conflict = !targets.Add(target) || File.Exists(target) || Directory.Exists(target);
            var step = new Step { Source = source, Target = target, Conflict = conflict };
            steps.Add(step);
            return step;
        }

        void Transfer(Step step)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(step.Target));
            if (Directory.Exists(step.Target))
                throw new IOException("nel progetto c'è già una cartella con questo nome");
            if (File.Exists(step.Target) && (File.GetAttributes(step.Target) & FileAttributes.ReadOnly) != 0)
                throw new IOException("il file nel progetto è in sola lettura (serve il checkout?)");

            if (!move)
            {
                File.Copy(step.Source, step.Target, true);
            }
            else if (File.Exists(step.Target))
            {
                // prima la copia e poi via l'originale: se qualcosa va storto non si perde niente
                File.Copy(step.Source, step.Target, true);
                Delete(step.Source);
            }
            else
            {
                File.Move(step.Source, step.Target);
            }
            ClearReadOnly(step.Target);
        }

        // Il .meta segue il suo file ma non sostituisce mai quello che il progetto ha già:
        // lì ci sono il GUID e le impostazioni a cui Unity collega i riferimenti
        void TransferMeta(Step step)
        {
            if (File.Exists(step.Target))
            {
                if (move) Retry(() => Delete(step.Source));   // il file è stato spostato: il vecchio .meta non serve più
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

        // Unity tiene aperti i file mentre li importa (con file grandi anche qualche secondo): si riprova fino a 5 s
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
                    if ((code != 32 && code != 33) || attempt >= 25) throw;   // 32, 33 = file in uso
                    Thread.Sleep(200);
                }
            }
        }

        // una copia di un file in sola lettura resterebbe bloccata anche dentro Unity
        static void ClearReadOnly(string file)
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }

        // spostando, l'originale va via anche se è in sola lettura (come fa Esplora risorse)
        static void Delete(string file)
        {
            ClearReadOnly(file);
            File.Delete(file);
        }

        // Dopo uno spostamento toglie dall'origine le cartelle rimaste vuote (o con soli file di sistema),
        // dal basso verso l'alto, e porta il loro .meta accanto alla cartella di destinazione
        void Cleanup(string folder)
        {
            try
            {
                foreach (string sub in Directory.GetDirectories(folder)) Cleanup(sub);
                var left = Directory.GetFileSystemEntries(folder);
                if (!left.All(f => File.Exists(f) && IsJunk(Path.GetFileName(f)))) return;   // ci sono file saltati
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
                Log.Write("pulizia di " + folder + ": " + ex.Message);
            }
        }
    }

    // Notifica "Copia in corso" su un thread a parte: compare solo se la copia dura più di un attimo
    class Progress : IDisposable
    {
        readonly object sync = new object();
        readonly string where;
        readonly int total;
        readonly System.Threading.Timer delay;
        int done;
        bool finished;
        Thread thread;

        public Progress(string where, int total)
        {
            this.where = where;
            this.total = total;
            delay = new System.Threading.Timer(delegate { Show(); }, null, 800, Timeout.Infinite);
        }

        public bool Finished
        {
            get { lock (sync) return finished; }
        }

        public string Text
        {
            get { return "Copia in corso: " + Math.Min(Thread.VolatileRead(ref done), total) + " di " + total; }
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
                Log.Write("avanzamento: " + ex.Message);
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
            if (running != null) running.Join(2000);   // la notifica si chiude da sola appena vede che è finita
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
        public string Hint;   // cartella che lo contiene, solo se c'è un altro progetto con lo stesso nome

        public string DisplayName
        {
            get { return Hint == null ? Name : Name + "  (" + Hint + ")"; }
        }

        public override string ToString()
        {
            return Open ? DisplayName + "   (aperto in Unity)" : DisplayName;
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
            MarkDuplicates(found.Values);
            return found.Values
                .OrderByDescending(p => p.Open)
                .ThenByDescending(p => p.LastUsed)
                .ThenByDescending(p => p.HubTime)
                .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        // Due progetti con lo stesso nome: accanto al nome compare la cartella che li contiene
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
                // se anche le cartelle che li contengono si chiamano uguale, si mostra il percorso intero
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
            Settings settings;
            return Read(out settings) ? settings : new Settings();
        }

        // false se il file c'è ma resta occupato: salvando ora si perderebbero le preferenze
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
                    // di solito un altro invio lo sta salvando proprio ora: si riprova un attimo dopo
                    if (attempt >= 20)
                    {
                        Log.Write("impostazioni illeggibili: " + ex.Message);
                        return false;
                    }
                    Thread.Sleep(25);
                }
                catch (Exception ex)
                {
                    Log.Write("impostazioni rovinate, riparto da zero: " + ex.Message);
                    return true;
                }
            }
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
                    Settings settings;
                    if (!Read(out settings)) throw new IOException("le impostazioni sono bloccate da un altro programma");
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
    // "Invia a Unity" è un comando di Esplora risorse (IExplorerCommand, più sotto). install.cmd
    // compila questo stesso file anche come DLL, che Explorer carica e interroga ogni volta che
    // apre il menu: l'elenco dei progetti è sempre aggiornato e quelli aperti hanno il pallino verde.
    // Tutto è registrato per l'utente corrente, senza permessi di amministratore.

    static class ShellMenu
    {
        public const string ClassId = "{6F1D7C0E-3B8A-4E59-9C2D-5A4B8E7F1A23}";
        const string ClassKey = @"Software\Classes\CLSID\" + ClassId;
        const string DllPrefix = "SendToUnityMenu-";
        public const int MaxProjects = 10;

        static readonly string[] Keys =
        {
            @"Software\Classes\*\shell\SendToUnity",
            @"Software\Classes\Directory\shell\SendToUnity"
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

        // dll: la DLL appena compilata da install.cmd; null = la più recente nella cartella del programma.
        // Ogni installazione ha un nome nuovo, perché Explorer tiene bloccata quella che ha già caricato.
        public static void Register(string dll)
        {
            if (dll == null) dll = Dlls().OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (dll == null || !File.Exists(dll)) throw new FileNotFoundException("Manca la DLL del menu: rifai install.cmd.");
            dll = Path.GetFullPath(dll);

            Registry.CurrentUser.DeleteSubKeyTree(ClassKey, false);
            using (var key = Registry.CurrentUser.CreateSubKey(ClassKey))
            {
                key.SetValue("", App.Name);
                using (var server = key.CreateSubKey("InprocServer32"))
                {
                    server.SetValue("", "mscoree.dll");
                    server.SetValue("ThreadingModel", "Apartment");
                    server.SetValue("Class", typeof(MenuCommand).FullName);
                    server.SetValue("Assembly", Path.GetFileNameWithoutExtension(dll) + ", Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");
                    server.SetValue("RuntimeVersion", "v4.0.30319");
                    server.SetValue("CodeBase", "file:///" + dll.Replace('\\', '/'));
                }
            }

            string hub = HubExe();
            foreach (string keyPath in Keys)
            {
                Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
                if (ModernMenu.Active) continue;   // la voce arriva dal pacchetto, in tutti e due i menu
                using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
                {
                    key.SetValue("MUIVerb", "&" + App.Name);   // tasto destro, poi I
                    key.SetValue("ExplorerCommandHandler", ClassId);
                    if (hub != null) key.SetValue("Icon", hub + ",0");
                }
            }

            foreach (string old in Dlls())
            {
                if (Paths.Same(old, dll)) continue;
                try { File.Delete(old); }
                catch (Exception) { Log.Write("DLL del menu ancora in uso, la tolgo la prossima volta: " + old); }
            }
            Log.Write("menu registrato: " + dll);
        }

        public static void Remove()
        {
            foreach (string keyPath in Keys) Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
            Registry.CurrentUser.DeleteSubKeyTree(ClassKey, false);
            // quella caricata da Explorer non si cancella ma si può spostare: la cartella resta pulita
            foreach (string dll in Dlls())
            {
                try { File.Delete(dll); }
                catch (Exception)
                {
                    try { File.Move(dll, Path.Combine(Path.GetTempPath(), Path.GetFileName(dll))); }
                    catch (Exception ex) { Log.Write("DLL del menu non rimossa: " + ex.Message); }
                }
            }
        }

        static string[] Dlls()
        {
            try { return Directory.GetFiles(App.Dir, DllPrefix + "*.dll"); }
            catch (Exception) { return new string[0]; }
        }

        // Pallino verde dei progetti aperti, nelle misure che Windows usa nei menu ai vari DPI
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

        // Immagine di un'icona: BITMAPINFOHEADER, pixel BGRA dal basso verso l'alto, maschera vuota
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

        // Nei menu & indica la lettera sottolineata: per mostrarla va raddoppiata
        public static string Escape(string text)
        {
            return text.Replace("&", "&&");
        }
    }

    // Interfacce di Esplora risorse usate dal menu
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

    // La voce "Invia a Unity": è la classe che Explorer crea dalla DLL. Gira dentro Explorer,
    // quindi ogni metodo intercetta gli errori e la copia vera la fa il programma, avviato a parte.
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
            title = Marshal.StringToCoTaskMemUni("&" + App.Name);
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

        // Un progetto per riga (i più usati in alto, sempre nello stesso ordine) + "Scegli progetto e cartella..."
        static List<IExplorerCommand> Build()
        {
            MenuServer.Touch();
            var list = new List<IExplorerCommand>();
            int count = 0;
            try
            {
                var settings = Settings.Load();
                foreach (var p in Projects.Known(settings, false).Take(ShellMenu.MaxProjects))
                {
                    string folder = settings.FolderFor(p.Path);
                    string label = ShellMenu.Escape(p.DisplayName) + (folder != null ? "   →  " + ShellMenu.Escape(folder) : "");
                    list.Add(new ProjectCommand(label, new[] { "--to", p.Path }, Projects.IsOpen(p.Path), false));
                    count++;
                }
            }
            catch (Exception ex)
            {
                Log.Write("menu: progetti non letti: " + ex.Message);
            }
            list.Add(new ProjectCommand("Scegli progetto e cartella...", new[] { "--pick" }, false, count > 0));
            return list;
        }

        // Avvia il programma con l'elenco dei file in un file temporaneo
        public static void Launch(string[] arguments, IShellItemArray items)
        {
            var paths = ItemPaths(items);
            if (paths.Count == 0) return;
            string list = Path.Combine(Path.GetTempPath(), "SendToUnity-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(list, paths, new UTF8Encoding(false));
            string line = string.Join(" ", arguments.Select(Paths.Quote)) + " --list " + Paths.Quote(list);
            Log.Write("menu: " + line + " (" + paths.Count + " elementi)");
            Process.Start(new ProcessStartInfo(App.ExePath, line) { UseShellExecute = false });
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
            }
            return paths;
        }

        public static List<string> ReadList(string file)
        {
            var lines = File.ReadAllLines(file, Encoding.UTF8).Where(l => l.Length > 0).ToList();
            try { File.Delete(file); }
            catch (Exception) { }
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
            tip = Marshal.StringToCoTaskMemUni("Aperto in Unity");
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
                MenuCommand.Launch(arguments, items);
                return MenuCommand.Ok;
            }
            catch (Exception ex)
            {
                Log.Write("menu: avvio non riuscito: " + ex);
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
            return done == count ? MenuCommand.Ok : 1;   // S_FALSE: elenco finito
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

    // ------------------------------------------------------------------ menu ridotto di Windows 11
    // Nel menu ridotto compaiono solo i comandi dei programmi "impacchettati". Basta un pacchetto
    // leggero (sparse package) con il manifest e un logo: i file restano nella cartella del
    // programma. Windows lo accetta solo se è firmato con un certificato di cui il PC si fida, quindi
    // il programma ne crea uno suo, firma il pacchetto, cancella subito la chiave privata (nessuno
    // può più firmare altro con quel certificato) e chiede una volta i permessi di amministratore
    // per aggiungerlo ai certificati fidati. Il comando gira in SendToUnity.exe --menu-server,
    // che Windows avvia quando apri il menu.

    // Lo stesso comando, servito da SendToUnity.exe invece che dalla DLL dentro Explorer.
    // Windows lo mostra in entrambi i menu, quindi con il pacchetto attivo la voce del registro si toglie.
    [ComVisible(true), Guid("3C8E5A21-7D4F-4B6A-9E1C-2F5D8A0B7C64"), ClassInterface(ClassInterfaceType.None)]
    public class ModernMenuCommand : MenuCommand
    {
    }

    static class MenuServer
    {
        static long lastCall = DateTime.UtcNow.Ticks;

        public static void Touch()
        {
            Interlocked.Exchange(ref lastCall, DateTime.UtcNow.Ticks);
        }

        // Resta acceso qualche minuto dopo l'ultimo uso: la volta dopo il menu è subito pronto
        public static int Run()
        {
            var services = new RegistrationServices();
            int cookie = services.RegisterTypeForComClients(typeof(ModernMenuCommand),
                RegistrationClassContext.LocalServer, RegistrationConnectionType.MultipleUse);
            Log.Write("menu ridotto: pronto");
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
        public const string PackageName = "DAVFX.InviaAUnity";
        const string Publisher = "CN=InviaAUnity";

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

        // Pacchetto registrato: la voce del menu classico arriva da lì
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
                Log.Write("menu ridotto: saltato su richiesta");
                return;
            }
            try
            {
                string manifest = Manifest();
                string hash = Hash(manifest);
                string msix = Path.Combine(Folder, "InviaAUnity.msix");
                string[] state = null;
                try { state = File.ReadAllText(StateFile).Split('|'); }
                catch (Exception) { }

                // stesso manifest e certificato ancora fidato: niente da rifare, nessuna richiesta
                if (state != null && state.Length == 2 && state[0] == hash && File.Exists(msix) && IsTrusted(state[1]))
                {
                    Register(msix, false);
                    return;
                }

                Directory.CreateDirectory(Folder);
                string logo = Path.Combine(Folder, "logo.png");
                SaveLogo(logo);
                string manifestFile = Path.Combine(Folder, "AppxManifest.xml");
                File.WriteAllText(manifestFile, manifest, new UTF8Encoding(false));
                Package.Write(msix, manifestFile, logo);
                string cer = Path.Combine(Folder, "InviaAUnity.cer");
                string thumbprint = PackageSigner.SignWithNewCertificate(msix, Publisher, cer);
                if (!Trust(cer, thumbprint))
                {
                    Log.Write("menu ridotto: certificato non accettato, resta il menu classico");
                    return;
                }
                File.WriteAllText(StateFile, hash + "|" + thumbprint);
                Register(msix, true);
            }
            catch (Exception ex)
            {
                Log.Write("menu ridotto non installato: " + ex);
            }
        }

        public static void Remove()
        {
            if (!Supported) return;
            try
            {
                PowerShell("Get-AppxPackage -Name '" + PackageName + "' | Remove-AppxPackage");
                Directory.Delete(Folder, true);
            }
            catch (Exception ex)
            {
                Log.Write("menu ridotto non rimosso: " + ex.Message);
            }
        }

        static void Register(string msix, bool replace)
        {
            PowerShell(
                "$p = Get-AppxPackage -Name '" + PackageName + "'\n" +
                (replace ? "if ($p) { Remove-AppxPackage -Package $p.PackageFullName; $p = $null }\n" : "") +
                "if (-not $p) { Add-AppxPackage -Path " + PsQuote(msix) + " -ExternalLocation " + PsQuote(App.Dir) + " }");
            File.WriteAllText(ActiveFile, PackageName);
            Log.Write("menu ridotto registrato");
        }

        static void TryDelete(string file)
        {
            try { File.Delete(file); }
            catch (Exception) { }
        }

        static string PsQuote(string text)
        {
            return "'" + text.Replace("'", "''") + "'";
        }

        static void PowerShell(string script)
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference = 'Stop'\n" + script));
            var info = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(info))
            {
                var error = process.StandardError.ReadToEndAsync();
                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(180000)) throw new TimeoutException("PowerShell non risponde");
                if (process.ExitCode != 0) throw new Exception("PowerShell: " + (error.Result + " " + output).Trim());
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

        // L'unico passo con i permessi di amministratore: Windows chiede conferma
        static bool Trust(string cer, string thumbprint)
        {
            if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                TrustCertificate(cer);
            }
            else
            {
                try
                {
                    var info = new ProcessStartInfo(App.ExePath, "--trust " + Paths.Quote(cer)) { UseShellExecute = true, Verb = "runas" };
                    using (var process = Process.Start(info)) process.WaitForExit();
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    Log.Write("menu ridotto: permessi di amministratore negati (" + ex.NativeErrorCode + ")");
                    return false;
                }
            }
            return IsTrusted(thumbprint);
        }

        // Aggiunge il certificato a quelli fidati del PC e toglie quelli vecchi del programma
        public static void TrustCertificate(string cer)
        {
            var certificate = new X509Certificate2(cer);
            using (var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadWrite);
                foreach (var old in store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, Publisher, false))
                {
                    if (old.Thumbprint != certificate.Thumbprint) store.Remove(old);
                }
                store.Add(certificate);
            }
            Log.Write("menu ridotto: certificato aggiunto ai fidati");
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

        static string Manifest()
        {
            string clsid = typeof(ModernMenuCommand).GUID.ToString().ToUpperInvariant();
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
  <Identity Name=""" + PackageName + @""" Publisher=""" + Publisher + @""" Version=""1.0.0.0"" ProcessorArchitecture=""neutral"" />
  <Properties>
    <DisplayName>Invia a Unity</DisplayName>
    <PublisherDisplayName>Invia a Unity</PublisherDisplayName>
    <Logo>logo.png</Logo>
    <uap10:AllowExternalContent>true</uap10:AllowExternalContent>
    <desktop6:FileSystemWriteVirtualization>disabled</desktop6:FileSystemWriteVirtualization>
    <desktop6:RegistryWriteVirtualization>disabled</desktop6:RegistryWriteVirtualization>
  </Properties>
  <Resources>
    <Resource Language=""it-it"" />
  </Resources>
  <Dependencies>
    <TargetDeviceFamily Name=""Windows.Desktop"" MinVersion=""10.0.22000.0"" MaxVersionTested=""10.0.26100.0"" />
  </Dependencies>
  <Capabilities>
    <rescap:Capability Name=""runFullTrust"" />
    <rescap:Capability Name=""unvirtualizedResources"" />
  </Capabilities>
  <Applications>
    <Application Id=""InviaAUnity"" Executable=""SendToUnity.exe"" uap10:TrustLevel=""mediumIL"" uap10:RuntimeBehavior=""win32App"">
      <uap:VisualElements AppListEntry=""none"" DisplayName=""Invia a Unity"" Description=""Invia a Unity"" BackgroundColor=""transparent"" Square150x150Logo=""logo.png"" Square44x44Logo=""logo.png"" />
      <Extensions>
        <desktop4:Extension Category=""windows.fileExplorerContextMenus"">
          <desktop4:FileExplorerContextMenus>
            <desktop5:ItemType Type=""*"">
              <desktop5:Verb Id=""SendToUnity"" Clsid=""" + clsid + @""" />
            </desktop5:ItemType>
            <desktop5:ItemType Type=""Directory"">
              <desktop5:Verb Id=""SendToUnity"" Clsid=""" + clsid + @""" />
            </desktop5:ItemType>
          </desktop4:FileExplorerContextMenus>
        </desktop4:Extension>
        <com:Extension Category=""windows.comServer"">
          <com:ComServer>
            <com:ExeServer Executable=""SendToUnity.exe"" Arguments=""--menu-server"" DisplayName=""Invia a Unity"">
              <com:Class Id=""" + clsid + @""" DisplayName=""Invia a Unity"" />
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

    // Scrive il pacchetto .msix con le API di Windows (appxpackaging.dll), senza strumenti esterni
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

    // Firma il pacchetto con SignerSignEx2 (mssign32.dll), come fa signtool
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

        // Crea un certificato nuovo, firma, salva la parte pubblica in cer e cancella la chiave privata
        public static string SignWithNewCertificate(string file, string subject, string cer)
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
                Sign(file, certificate.Handle);
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
                Log.Write("chiave del certificato non cancellata: " + ex.Message);
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
                // i parametri puntano ai dati per il pacchetto e viceversa, come vuole la firma dei .msix
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

    static class Installer
    {
        static string SendToLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Progetto Unity.lnk"); }
        }

        public static void Install(string dll)
        {
            // resti della prima versione (PowerShell + VBScript)
            foreach (string old in new[] { "SendToUnity.ps1", "launcher.vbs", "config.json" })
                TryDelete(Path.Combine(App.Dir, old));
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), "SendToUnity-queue"), true); }
            catch (Exception) { }

            ShellMenu.SaveOpenIcon();
            ModernMenu.Install();
            ShellMenu.Register(dll);
            // "Invia a" apre direttamente la finestra di scelta
            LinkFile.Create(SendToLink, App.ExePath, "--pick", ShellMenu.HubExe());
            Log.Write("installato: " + App.ExePath);
        }

        public static void Uninstall()
        {
            ShellMenu.Remove();
            ModernMenu.Remove();
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

        // interrupt: se diventa vero la notifica si chiude prima (sono arrivati altri file da copiare)
        public static void ShowResult(Result result, Func<bool> interrupt)
        {
            if (result.Errors.Count > 0)
            {
                var lines = result.Errors.Take(10).ToList();
                if (result.Errors.Count > 10) lines.Add("... e altri " + (result.Errors.Count - 10));
                string text = Summary(result) + ".\n\nProblemi:\n" + string.Join("\n", lines);
                if (result.AccessDenied)
                    text += "\n\nSe in Sicurezza di Windows è attivo \"Accesso controllato alle cartelle\" " +
                            "(Protezione da ransomware), aggiungi SendToUnity.exe alle app consentite.";
                Box(text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // cliccando la notifica si apre la cartella con il primo file arrivato già selezionato
            string open = result.FirstTarget ?? (Directory.Exists(result.Destination) ? result.Destination : null);
            string title = (result.Copied > 0 ? "\u2713  " : "") + Summary(result);
            using (var toast = new Toast(title, Where(result), open != null ? "Clic per aprire la cartella" : null, open, null, interrupt))
                Application.Run(toast);
        }

        // "Progetto  ›  Assets/Cartella"
        public static string Where(Result result)
        {
            return Path.GetFileName(result.Project) + "  \u203A  " + result.Folder;
        }

        static string Summary(Result result)
        {
            string done = result.Moved ? "spostat" : "inviat";
            if (result.Copied == 0)
            {
                if (result.Skipped == 1) return "Nessun file " + done + "o: era già presente";
                if (result.Skipped > 1) return "Nessun file " + done + "o: erano già presenti";
                if (result.AlreadyThere > 0) return "I file sono già in questa cartella";
                return "Nessun file " + done + "o";
            }
            string text = result.Copied == 1 ? "1 file " + done + "o" : result.Copied + " file " + done + "i";
            if (result.Skipped > 0)
                text += ", " + result.Skipped + (result.Skipped == 1 ? " saltato (già presente)" : " saltati (già presenti)");
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
            projectCombo.DrawMode = DrawMode.OwnerDrawFixed;   // pallino verde accanto ai progetti aperti
            projectCombo.ItemHeight = Font.Height + Ui.S(8);
            projectCombo.DrawItem += DrawProject;
            projectCombo.Items.AddRange(projects.Cast<object>().ToArray());
            browse = new Button { Name = "browseButton", Text = "Sfoglia...", AutoSize = true, Margin = Padding.Empty };
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
            FitDropDown(projectCombo);
            if (projectCombo.Items.Count == 0)
            {
                pathLabel.Text = "Nessun progetto trovato in Unity Hub: aggiungine uno con \"Sfoglia...\".";
                folderCombo.Text = "Assets";
                ActiveControl = browse;
            }
            UpdateFilesLabel();
        }

        public static readonly Color OpenGreen = Color.FromArgb(22, 163, 74);

        // Progetto nell'elenco: pallino verde + nome + "aperto in Unity" in grigio se Unity lo ha aperto.
        // Lo spazio del pallino c'è sempre, così i nomi restano allineati.
        static void DrawProject(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0) return;
            var combo = (ComboBox)sender;
            object item = combo.Items[e.Index];
            var project = item as ProjectInfo;
            bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            Color text = selected ? SystemColors.HighlightText : SystemColors.ControlText;
            Color soft = selected ? SystemColors.HighlightText : SystemColors.GrayText;
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
                TextRenderer.DrawText(e.Graphics, "aperto in Unity", combo.Font, rest, soft, flags);
            }
            e.DrawFocusRectangle();
        }

        // L'elenco che si apre è largo quanto la voce più lunga (entro metà schermo)
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
            FitDropDown(folderCombo);
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

            string where = Ui.Where(result);
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

    // Colori delle notifiche: gli stessi dei menu di Windows, chiari o scuri come il tema delle app
    static class Look
    {
        public static readonly bool Dark = ReadDark();
        public static readonly Color Background = Dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
        public static readonly Color Border = Dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(222, 222, 222);
        public static readonly Color Text = Dark ? Color.White : Color.FromArgb(26, 26, 26);
        public static readonly Color Secondary = Dark ? Color.FromArgb(200, 200, 200) : Color.FromArgb(96, 96, 96);
        public static readonly Color Link = Dark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 95, 184);

        static bool ReadDark()
        {
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

        // Titolo in Segoe UI Semibold (quello dei titoli di Windows), se c'è
        public static Font TitleFont(Font font)
        {
            try { return new Font(new FontFamily("Segoe UI Semibold"), font.SizeInPoints + 1.5f, FontStyle.Regular); }
            catch (ArgumentException) { return new Font(font.FontFamily, font.SizeInPoints + 1.5f, FontStyle.Bold); }
        }
    }

    // Notifica in basso a destra che non ruba il focus a Unity: quella finale sparisce da sola
    // (non mentre il mouse ci è sopra), quella di avanzamento si aggiorna finché la copia non è finita
    class Toast : Form
    {
        const int VisibleMs = 3500;

        readonly string open;           // file o cartella da aprire con un clic (null = niente)
        readonly Progress progress;     // non null: notifica di avanzamento
        readonly Func<bool> interrupt;  // vero = deve lasciare il posto a un nuovo invio
        readonly Label titleLabel;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 100 };
        DateTime closeAt;
        Point mouseAtStart;   // se compare proprio sotto il mouse fermo, sparisce lo stesso
        bool mouseMoved;
        bool placed;

        public Toast(string title, string detail, string hint, string open, Progress progress, Func<bool> interrupt)
        {
            this.open = open;
            this.progress = progress;
            this.interrupt = interrupt;
            Name = "toast";
            Text = App.Name + (progress == null ? " - notifica" : " - avanzamento");
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
                // sotto il mouse non sparisce: magari la si sta per cliccare
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
                cp.ClassStyle |= 0x00020000;                           // ombra, come i menu
                return cp;
            }
        }

        // Angoli arrotondati e bordo sottile come i menu di Windows 11; su Windows 10 un bordo di 1 pixel
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

        // se il testo cambia lunghezza la notifica resta attaccata all'angolo in basso a destra
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
            Log.Write("apro " + open);
            Hide();
            try
            {
                if (!File.Exists(open))
                {
                    Process.Start("explorer.exe", Paths.Quote(open));
                }
                else
                {
                    // file selezionato, nella finestra già aperta se c'è
                    if (!Native.ShowInExplorer(open)) Process.Start("explorer.exe", "/select," + Paths.Quote(open));
                    // alcune versioni di Windows aprono la cartella senza selezionare il file: ci si pensa da qui
                    if (!ExplorerWindows.Select(open, 5000)) Log.Write("file non selezionato in Esplora risorse");
                }
            }
            catch (Exception ex)
            {
                Log.Write("cartella non aperta: " + ex.Message);
            }
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
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        // Windows 11: angoli arrotondati e colore del bordo; false se Windows non li supporta
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
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
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

        // Apre la cartella del file con il file selezionato; se la cartella è già aperta usa quella finestra
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

        // Guardare la coda dei messaggi (senza toglierli) fa risultare il processo "pronto all'input"
        public static void SignalIdle()
        {
            NativeMessage message;
            PeekMessage(out message, IntPtr.Zero, 0, 0, 0);
        }

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

    // Le finestre di Esplora risorse viste dall'automazione di Windows (Shell.Application), come da uno script
    static class ExplorerWindows
    {
        // Seleziona il file nelle finestre aperte sulla sua cartella, aspettando che compaiano,
        // finché la selezione resta stabile (la vista appena aperta a volte la perde)
        public static bool Select(string file, int timeoutMs)
        {
            bool done = false;
            var thread = new Thread(() =>
            {
                try { done = SelectLoop(file, timeoutMs); }
                catch (Exception ex) { Log.Write("selezione in Esplora risorse: " + ex.Message); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;   // se Esplora risorse non risponde il programma si chiude lo stesso
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
                        if (item != null) Call(view, "SelectItem", item, 1 | 4 | 8 | 16);   // solo lui, visibile, con il focus
                    }
                    catch (Exception) { }   // finestre che non mostrano cartelle o che si stanno chiudendo
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
