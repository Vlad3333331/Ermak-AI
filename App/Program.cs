using ии;

namespace ии
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // WinExe без консоли: глушим консольный вывод, чтобы все Console.* из
            // движка обучения не падали (в GUI они никуда не пишут)
            try { Console.SetOut(TextWriter.Null); } catch { }
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }
}
