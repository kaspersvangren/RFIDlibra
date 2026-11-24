using System;
using System.IO;
using System.Net;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace RFIDlibra
{
    /// <summary>
    /// SIP2 til Cicero over https (som selvbetjeningsautomater bruger): login, status og opslag af et materiale.
    /// Sender kun spørgsmål - ændrer intet i Cicero. Adgangskoden står aldrig i loggen.
    /// </summary>
    public sealed class Sip2Client
    {
        public sealed class Item
        {
            public string Code = "";        // cirkulationsstatus, fx "03" (hjemme) eller "04" (udlånt)
            public string Title = "";
            public string Author = "";
            public string Error;            // sat, hvis opslaget ikke lykkedes
            public bool Loaned { get { return Code == "04" || Code == "05" || Code == "07"; } }
        }

        private static readonly string[] ContentTypes = { "application/xml; charset=utf-8", "application/xml", "text/xml" };
        private int ctIndex;
        private volatile bool loggedIn;

        public string Url = "";
        public string User = "";
        public string Password = "";
        public string Institution = "";

        public bool Configured { get { return Url.Length > 0 && User.Length > 0 && Password.Length > 0; } }

        static Sip2Client()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }   // TLS 1.2
        }

        /// <summary>Login og status. Returnerer en kort tekst til brugeren, og ok=true hvis login blev godkendt.</summary>
        public string Test(out bool ok)
        {
            ok = false;
            if (!Configured) return "Udfyld adresse, bruger og adgangskode";
            string err;
            string r = Exchange("9300CN" + User + "|CO" + Password + "|CP|", out err);
            if (r == null) return err;
            if (!r.StartsWith("941")) return "Cicero afviste login - tjek bruger og adgangskode";
            loggedIn = true;
            r = Exchange("9900302.00", out err);
            if (r == null) return "Login godkendt, men status svarede ikke: " + err;
            ok = true;
            string name = Field(r, "AM", 36);
            return "Login godkendt" + (name.Length > 0 ? " – " + name : "");
        }

        /// <summary>Slår et materiale op. Logger ind først, hvis det ikke er sket.</summary>
        public Item Lookup(string barcode)
        {
            Item it = new Item();
            if (!Configured) { it.Error = "SIP2 er ikke sat op"; return it; }
            string err;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (!loggedIn)
                {
                    string l = Exchange("9300CN" + User + "|CO" + Password + "|CP|", out err);
                    if (l == null) { it.Error = err; return it; }
                    if (!l.StartsWith("941")) { it.Error = "Cicero afviste login"; return it; }
                    loggedIn = true;
                }
                string r = Exchange("17" + Sip2Date() + "AO" + Institution + "|AB" + barcode + "|AC|", out err);
                if (r != null && r.StartsWith("18") && r.Length >= 4)
                {
                    it.Code = r.Substring(2, 2);
                    string[] ch = Field(r, "CH", 26).Split('%');
                    it.Title = ch[0].Trim();
                    if (ch.Length > 1) it.Author = ch[1].Trim();
                    return it;
                }
                loggedIn = false;   // prøv igen med nyt login én gang
                it.Error = r == null ? err : "Uventet svar fra Cicero";
            }
            return it;
        }

        public static string CodeText(string code)
        {
            switch (code)
            {
                case "04": case "05": case "07": return "Udlånt";
                case "03": return "Ikke udlånt";
                case "08": return "Ikke udlånt – på reserveringshylden";
                case "09": return "Ikke udlånt – afleveret, ikke sat på plads";
                case "10": return "Ikke udlånt – undervejs";
                case "02": return "Ikke udlånt – bestilt";
                case "11": return "Ikke udlånt – krævet";
                case "12": return "Ikke udlånt – tabt";
                case "13": return "Ikke udlånt – mangler";
                default: return "Ikke udlånt (status " + code + ")";
            }
        }

        private static string Sip2Date() { DateTime n = DateTime.Now; return n.ToString("yyyyMMdd") + "    " + n.ToString("HHmmss"); }

        /// <summary>Et felt med en kode på to bogstaver, fx "CH". De faste tegn først i svaret springes over.</summary>
        private static string Field(string r, string code, int fixedLen)
        {
            if (r.Length <= fixedLen) return "";
            foreach (string f in r.Substring(fixedLen).Split('|'))
                if (f.StartsWith(code, StringComparison.Ordinal)) return f.Substring(code.Length);
            return "";
        }

        private string Exchange(string msg, out string error)
        {
            error = null;
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                + "<ns2:sip xmlns:ns2=\"http://axiell.com/Schema/sip.xsd\" login=\"" + SecurityElement.Escape(User) + "\" password=\"" + SecurityElement.Escape(Password) + "\">"
                + "<request>" + SecurityElement.Escape(msg) + "</request></ns2:sip>";
            byte[] body = Encoding.UTF8.GetBytes(xml);
            while (true)
            {
                int ci = ctIndex;
                int code;
                string text = Post(body, ContentTypes[ci], out code, out error);
                if (text == null) return null;
                if (code == 415 && ci < ContentTypes.Length - 1) { ctIndex = ci + 1; continue; }
                if (code == 401 || code == 403) { error = "Cicero afviste adgangen (HTTP " + code + ")"; return null; }
                Match err = Regex.Match(text, "<(?:[A-Za-z0-9_]+:)?error\\b[^>]*>(.*?)</(?:[A-Za-z0-9_]+:)?error>", RegexOptions.Singleline);
                if (err.Success) { error = "Cicero meldte fejl: " + WebUtility.HtmlDecode(err.Groups[1].Value).Trim(); return null; }
                Match m = Regex.Match(text, "<(?:[A-Za-z0-9_]+:)?response\\b[^>]*>(.*?)</(?:[A-Za-z0-9_]+:)?response>", RegexOptions.Singleline);
                if (!m.Success) { error = "Cicero svarede ikke som ventet (HTTP " + code + ")"; return null; }
                return WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            }
        }

        private string Post(byte[] body, string contentType, out int code, out string error)
        {
            code = 0;
            error = null;
            HttpWebRequest req;
            try { req = (HttpWebRequest)WebRequest.Create(Url); }
            catch { error = "Adressen er ikke gyldig"; return null; }
            req.Method = "POST";
            req.ContentType = contentType;
            req.Accept = "application/xml, text/xml";
            req.UserAgent = "RFIDlibra/" + AppInfo.Version;
            req.Timeout = 8000;
            req.ReadWriteTimeout = 8000;
            if (req.Proxy != null) req.Proxy.Credentials = CredentialCache.DefaultCredentials;
            req.ContentLength = body.Length;
            try
            {
                using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    code = (int)resp.StatusCode;
                    return ReadAll(resp);
                }
            }
            catch (WebException e)
            {
                HttpWebResponse resp = e.Response as HttpWebResponse;
                if (resp == null) { error = "Ingen forbindelse til Cicero (" + e.Status + ")"; return null; }
                code = (int)resp.StatusCode;
                string text = ReadAll(resp);
                resp.Close();
                return text;
            }
            catch (Exception e)
            {
                error = "Ingen forbindelse til Cicero: " + e.Message;
                return null;
            }
        }

        private static string ReadAll(HttpWebResponse resp)
        {
            using (Stream s = resp.GetResponseStream())
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buf = new byte[8192];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                byte[] b = ms.ToArray();
                try { return new UTF8Encoding(false, true).GetString(b); }
                catch (DecoderFallbackException) { return Encoding.GetEncoding(28591).GetString(b); }
            }
        }
    }
}
