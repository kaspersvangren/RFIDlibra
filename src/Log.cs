using System;
using System.IO;
using System.Text;

namespace RFIDlibra
{
    /// <summary>Log i hukommelsen, i en fil under %LOCALAPPDATA%\RFIDlibra og i vinduet.</summary>
    public sealed class Log
    {
        private readonly object sync = new object();
        private readonly StringBuilder all = new StringBuilder();
        private readonly string file;

        public event Action<string> Line;

        public string FilePath { get { return file; } }

        public Log()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RFIDlibra");
                Directory.CreateDirectory(dir);
                file = Path.Combine(dir, "log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            }
            catch
            {
                file = null;
            }
        }

        public void Write(string text)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text;
            lock (sync)
            {
                if (all.Length > 4000000) all.Remove(0, 1000000);
                all.AppendLine(line);
                if (file != null)
                {
                    try { File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8); } catch { }
                }
            }
            Action<string> h = Line;
            if (h != null) h(line);
        }

        public string Text
        {
            get { lock (sync) return all.ToString(); }
        }
    }
}
