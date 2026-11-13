using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RFIDlibra
{
    /// <summary>Log i hukommelsen (de seneste 3000 linjer, nummererede til brugerfladen) og i en fil under %LOCALAPPDATA%\RFIDlibra.</summary>
    public sealed class Log
    {
        private const int Keep = 3000;
        private readonly object sync = new object();
        private readonly List<string> lines = new List<string>();
        private long total;   // antal linjer skrevet i alt; lines[0] har nummer total - lines.Count
        private readonly string file;

        public event Action<string> Line;

        public string FilePath { get { return file; } }

        public Log()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RFIDlibra", "log");
                Directory.CreateDirectory(dir);
                file = Path.Combine(dir, "log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                // Ryd op i gamle logfiler (behold 14 dage)
                foreach (string f in Directory.GetFiles(dir, "log-*.txt"))
                {
                    try { if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f); } catch { }
                }
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
                lines.Add(line);
                total++;
                if (lines.Count > Keep) lines.RemoveRange(0, lines.Count - Keep);
                if (file != null)
                {
                    try { File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8); } catch { }
                }
            }
            Action<string> h = Line;
            if (h != null) try { h(line); } catch { }
        }

        /// <summary>Linjer fra nummer 'since' og frem. 'next' er det nummer, der skal spørges fra næste gang.</summary>
        public List<string> Since(long since, out long next)
        {
            lock (sync)
            {
                next = total;
                long first = total - lines.Count;
                long from = Math.Max(since, first);
                List<string> r = new List<string>();
                for (long i = from; i < next; i++) r.Add(lines[(int)(i - first)]);
                return r;
            }
        }

        public string Text
        {
            get { lock (sync) return string.Join(Environment.NewLine, lines) + Environment.NewLine; }
        }
    }
}
