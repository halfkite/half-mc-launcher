using System;
using System.Windows.Forms;

namespace HalfMcLauncher
{
    internal static class Program
    {
        internal static MainForm MainWindow { get; private set; }

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AppPaths.Initialize();
            MainWindow = new MainForm();
            Application.Run(MainWindow);
        }
    }
}
