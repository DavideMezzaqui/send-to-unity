// Finto editor di Unity per i test: tiene bloccato Temp\UnityLockfile come fa Unity
// e apre una finestra con un titolo nello stesso formato.
// Senza -projectPath apre solo una finestra qualsiasi ("Altra finestra").
using System;
using System.IO;
using System.Windows.Forms;

static class FakeUnity
{
    [STAThread]
    static void Main(string[] args)
    {
        string project = null;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], "-projectPath", StringComparison.OrdinalIgnoreCase)) project = args[i + 1];
        }

        var form = new Form { Width = 640, Height = 400 };
        if (project == null)
        {
            form.Text = "Altra finestra";
            Application.Run(form);
            return;
        }

        Directory.CreateDirectory(Path.Combine(project, "Temp"));
        using (new FileStream(Path.Combine(project, @"Temp\UnityLockfile"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            form.Text = Path.GetFileName(project) + " - SampleScene - Windows, Mac, Linux - Unity 6000.0.23f1 <DX11>";
            Application.Run(form);
        }
    }
}
