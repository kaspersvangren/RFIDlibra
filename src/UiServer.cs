using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace RFIDlibra
{
    /// <summary>
    /// Brugerfladen: én HTML-side (ui/index.html, indlejret i programmet) og et lille API under /app/api.
    /// API'et kræver programmets hemmelige nøgle (t=...), som kun står i den adresse, programmet selv åbner -
    /// så andre hjemmesider ikke kan styre læseren.
    /// </summary>
    public sealed class UiServer
    {
        private readonly App app;
        private readonly string token;
        private static string page;

        public UiServer(App app, string token)
        {
            this.app = app;
            this.token = token;
        }

        private static string Page
        {
            get
            {
                if (page != null) return page;
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html"))
                using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                    page = r.ReadToEnd();
                return page;
            }
        }

        public void Handle(CiceroServer.Request req, Stream outp)
        {
            if (req.Path == "/app" && req.Method == "GET")
            {
                CiceroServer.Respond(outp, 200, "text/html; charset=utf-8", Page, null, new[] { "Cache-Control: no-store" });
                return;
            }
            string t;
            if (!req.Query.TryGetValue("t", out t) || t != token)
            {
                CiceroServer.Respond(outp, 403, "application/json", "{\"error\":\"forkert nøgle - åbn RFIDlibra fra ikonet\"}", null, null);
                return;
            }
            string api = req.Path.StartsWith("/app/api/") ? req.Path.Substring("/app/api/".Length) : "";
            try
            {
                switch (api)
                {
                    case "state":
                        Json(outp, app.StateJson());
                        return;
                    case "log":
                        {
                            long since = 0;
                            string v;
                            if (req.Query.TryGetValue("since", out v)) long.TryParse(v, out since);
                            long next;
                            List<string> lines = app.Log.Since(since, out next);
                            StringBuilder sb = new StringBuilder("{\"next\":").Append(next).Append(",\"lines\":[");
                            for (int i = 0; i < lines.Count; i++) { if (i > 0) sb.Append(','); sb.Append(RFIDlibra.Json.Str(lines[i])); }
                            Json(outp, sb.Append("]}").ToString());
                            return;
                        }
                    case "log.txt":
                        CiceroServer.Respond(outp, 200, "text/plain; charset=utf-8", app.Log.Text, null,
                            new[] { "Content-Disposition: attachment; filename=\"RFIDlibra-log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt\"" });
                        return;
                }

                if (req.Method != "POST") { Json(outp, "{\"error\":\"brug POST\"}", 405); return; }
                switch (api)
                {
                    case "selftest": app.SelfTest(); Ok(outp, "Selvtesten kører - se loggen"); return;
                    case "raw": app.SetLogRaw(Q(req, "on") == "1"); Ok(outp, ""); return;
                    case "settings":
                        {
                            string lib = Q(req, "library");
                            if (lib != null) app.SetLibrary(lib);
                            string auto = Q(req, "autostart");
                            string msg = auto != null ? app.SetAutostart(auto == "1") : "Gemt";
                            Ok(outp, msg);
                            return;
                        }
                    case "reconnect": app.Reconnect(); Ok(outp, "Forbinder igen"); return;
                    case "tool/alarm": Tool(outp, app.Worker == null ? null : app.Worker.ToolAlarm(Q(req, "mac"), Q(req, "on") == "1")); return;
                    case "tool/reset": Tool(outp, app.Worker == null ? null : app.Worker.ToolReset(Q(req, "mac"))); return;
                    case "tool/program":
                        {
                            int seq, num;
                            int.TryParse(Q(req, "seq"), out seq);
                            int.TryParse(Q(req, "num"), out num);
                            Tool(outp, app.Worker == null ? null : app.Worker.ToolProgram(Q(req, "mac"), (Q(req, "barcode") ?? "").Trim(), seq, num,
                                (Q(req, "library") ?? "").Trim(), Q(req, "confirmed") == "1"));
                            return;
                        }
                }
                Json(outp, "{\"error\":\"ukendt\"}", 404);
            }
            catch (Exception e)
            {
                Json(outp, "{\"ok\":false,\"message\":" + RFIDlibra.Json.Str(e.Message) + "}", 200);
            }
        }

        private static string Q(CiceroServer.Request req, string k)
        {
            string v;
            return req.Query.TryGetValue(k, out v) ? v : null;
        }

        private static void Json(Stream outp, string body, int code = 200)
        {
            CiceroServer.Respond(outp, code, "application/json; charset=utf-8", body, null, new[] { "Cache-Control: no-store" });
        }

        private static void Ok(Stream outp, string msg)
        {
            Json(outp, "{\"ok\":true,\"message\":" + RFIDlibra.Json.Str(msg) + "}");
        }

        private static void Tool(Stream outp, Worker.ToolResult r)
        {
            if (r == null) { Json(outp, "{\"ok\":false,\"message\":\"Læseren er ikke forbundet\"}"); return; }
            Json(outp, "{\"ok\":" + (r.Ok ? "true" : "false") + ",\"needsConfirm\":" + (r.NeedsConfirm ? "true" : "false")
                + ",\"message\":" + RFIDlibra.Json.Str(r.Message ?? "") + "}");
        }
    }
}
