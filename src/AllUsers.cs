using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace RFIDlibra
{
    /// <summary>
    /// "Installer for alle brugere på denne pc": programmet og FEIG's feusb.dll lægges i C:\Program Files\RFIDlibra,
    /// en genvej i den fælles Start-mappe starter det for alle, der logger på, og biblioteksnummeret gemmes til hele pc'en
    /// i C:\ProgramData\RFIDlibra. Kræver administrative rettigheder: programmet starter en kopi af sig selv med "runas".
    /// </summary>
    public static class AllUsers
    {
        public const string InstallArg = "--installer-alle";
        public const string RemoveArg = "--fjern-alle";
        public const string GateArg = "--gem-porte-alle";

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RFIDlibra"); }
        }

        public static string Exe { get { return Path.Combine(Dir, "RFIDlibra.exe"); } }

        private static string StartupLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "RFIDlibra.lnk"); }
        }

        /// <summary>C:\ProgramData\RFIDlibra - fælles indstillinger (biblioteksnummer) og installationens log.</summary>
        public static string MachineDataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RFIDlibra"); }
        }

        private static string LogFile { get { return Path.Combine(MachineDataDir, "installation.log"); } }

        public static bool Installed
        {
            get { try { return File.Exists(StartupLink) && File.Exists(Exe); } catch { return false; } }
        }

        public static bool RunningFromInstall
        {
            get
            {
                try { return string.Equals(Path.GetFullPath(Assembly.GetExecutingAssembly().Location), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }
        }

        /// <summary>Biblioteksnummer gemt til hele pc'en (bruges, når brugeren ikke selv har sat et).</summary>
        public static string MachineLibrary
        {
            get
            {
                try { string f = Path.Combine(MachineDataDir, "library.txt"); return File.Exists(f) ? File.ReadAllText(f).Trim() : ""; }
                catch { return ""; }
            }
        }

        // ---------- Fra den almindelige bruger ----------

        /// <summary>Starter en kopi af programmet som administrator og venter på den. Returnerer en besked til brugeren.</summary>
        public static string RunElevated(bool install, string library, out bool ok)
        {
            string lib = new string((library ?? "").Where(char.IsLetterOrDigit).ToArray());
            string args = install ? InstallArg + (lib.Length > 0 ? " " + lib : "") : RemoveArg;
            string err = Elevate(args, out ok);
            if (err != null) return err;
            string last = LastLogLine();
            if (ok) return install
                ? "RFIDlibra er installeret for alle brugere på denne pc. Den starter derfra, næste gang nogen logger på."
                : "RFIDlibra er fjernet for alle brugere på denne pc.";
            return "Det lykkedes ikke" + (last.Length > 0 ? ": " + last : ".");
        }

        /// <summary>Gemmer brugerens gatealarm-indstillinger til alle brugere på pc'en (kræver administrator).</summary>
        public static string SaveGateForAll(string userFile, out bool ok)
        {
            string err = Elevate(GateArg + " \"" + userFile + "\"", out ok);
            if (err != null) return err;
            if (ok) return "Gatealarmerne er gemt for alle brugere på pc'en. Brugere, der selv har gemt indstillinger, beholder deres egne.";
            string last = LastLogLine();
            return "Det lykkedes ikke" + (last.Length > 0 ? ": " + last : ".");
        }

        /// <summary>Starter en kopi af programmet som administrator og venter. Returnerer en fejltekst, eller null hvis den kørte.</summary>
        private static string Elevate(string args, out bool ok)
        {
            ok = false;
            string me = Assembly.GetExecutingAssembly().Location;
            try
            {
                // Det forreste vindue (Edge-vinduet med Indstillinger) som "ejer", så Windows' spørgsmål om administrator kommer frem foran
                ProcessStartInfo psi = new ProcessStartInfo(me, args) { Verb = "runas", UseShellExecute = true, WorkingDirectory = Path.GetTempPath(), ErrorDialogParentHandle = GetForegroundWindow() };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return "Det lykkedes ikke at starte som administrator.";
                    if (!p.WaitForExit(5 * 60 * 1000)) return "Administrator-delen svarede ikke inden for 5 minutter.";
                    ok = p.ExitCode == 0;
                }
            }
            catch (Win32Exception e)
            {
                if (e.NativeErrorCode == 1223) return "Annulleret - der blev ikke givet administratoradgang.";
                return "Det lykkedes ikke at starte som administrator: " + e.Message;
            }
            return null;
        }

        private static string LastLogLine()
        {
            try
            {
                if (!File.Exists(LogFile)) return "";
                string l = File.ReadAllLines(LogFile).LastOrDefault(x => x.Trim().Length > 0) ?? "";
                int i = l.IndexOf("  ");
                return i > 0 ? l.Substring(i).Trim() : l;
            }
            catch { return ""; }
        }

        // ---------- Som administrator (egen proces) ----------

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string from, string to, int flags);
        private const int MOVEFILE_DELAY_UNTIL_REBOOT = 4;

        private static void Write(string line)
        {
            try
            {
                Directory.CreateDirectory(MachineDataDir);
                File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Kører i den administrator-proces, programmet selv har startet. Returnerer exit-koden.</summary>
        public static int RunAsAdmin(string[] args)
        {
            try
            {
                if (args[0] == InstallArg) Install(args.Length > 1 ? args[1] : "");
                else if (args[0] == GateArg) SaveGate(args.Length > 1 ? args[1] : "");
                else Remove();
                return 0;
            }
            catch (Exception e)
            {
                Write("FEJL: " + e.Message);
                return 2;
            }
        }

        /// <summary>Flytter en fil af vejen. En kørende exe-fil kan omdøbes, men ikke slettes; den slettes så ved genstart.</summary>
        private static void MoveAside(string path)
        {
            if (!File.Exists(path)) return;
            try { File.Delete(path); return; } catch { }
            string old = path + ".gammel";
            if (File.Exists(old))
            {
                try { File.Delete(old); } catch { MoveFileEx(old, null, MOVEFILE_DELAY_UNTIL_REBOOT); old = path + "." + DateTime.Now.Ticks + ".gammel"; }
            }
            File.Move(path, old);
            try { File.Delete(old); } catch { MoveFileEx(old, null, MOVEFILE_DELAY_UNTIL_REBOOT); }
        }

        private static void Install(string library)
        {
            Write("Installerer RFIDlibra " + AppInfo.Version + " for alle brugere");
            string me = Assembly.GetExecutingAssembly().Location;
            Directory.CreateDirectory(Dir);

            if (!string.Equals(Path.GetFullPath(me), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase))
            {
                MoveAside(Exe);
                File.Copy(me, Exe, false);
                Write("Programmet er lagt i " + Dir);
            }

            byte[] dll = Feusb.BundledBytes();
            if (dll == null) throw new InvalidOperationException("den indbyggede feusb.dll mangler");
            string dllPath = Path.Combine(Dir, "feusb.dll");
            MoveAside(dllPath);
            Feusb.WriteChecked(dllPath, dll);
            Write("FEIG's feusb.dll er lagt i " + Dir);

            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { StartupLink });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { Exe });
            lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { "--baggrund" });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Dir });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "RFIDlibra - FEIG-læser til Cicero" });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            Write("Genvej i den fælles Start-mappe: " + StartupLink);

            if (library.Length > 0)
            {
                Directory.CreateDirectory(MachineDataDir);
                File.WriteAllText(Path.Combine(MachineDataDir, "library.txt"), library);
                Write("Biblioteksnummer til hele pc'en: " + library);
            }
            Write("Færdig");
        }

        private static void SaveGate(string userFile)
        {
            if (!File.Exists(userFile)) throw new FileNotFoundException("indstillingerne blev ikke fundet: " + userFile);
            Directory.CreateDirectory(MachineDataDir);
            File.Copy(userFile, Path.Combine(MachineDataDir, GateSettings.FileName), true);
            Write("Gatealarmer gemt for alle brugere i " + MachineDataDir);
        }

        private static void Remove()
        {
            Write("Fjerner RFIDlibra for alle brugere");
            if (File.Exists(StartupLink)) File.Delete(StartupLink);
            MoveAside(Exe);
            MoveAside(Path.Combine(Dir, "feusb.dll"));
            try { if (Directory.Exists(Dir) && !Directory.EnumerateFileSystemEntries(Dir).Any()) Directory.Delete(Dir); } catch { }
            try { string f = Path.Combine(MachineDataDir, "library.txt"); if (File.Exists(f)) File.Delete(f); } catch { }
            Write("Færdig");
        }
    }
}
