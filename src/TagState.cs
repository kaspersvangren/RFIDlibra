using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RFIDlibra
{
    /// <summary>
    /// Et tag på læseren, som Cicero ser det. JSON-formatet er præcis som Deichmans go-feig
    /// (og NFClibra), fordi det er det, Ciceros "Deichman"-integration er bygget mod.
    /// </summary>
    public sealed class TagState
    {
        public const int AFI_ON = 0x07;   // alarm til (sikret)
        public const int AFI_OFF = 0xC2;  // alarm fra (udlånt)

        public byte[] Uid;          // som læseren melder det (E0 04 ...)
        public string Mac;          // "E0:04:..."
        public int Dsfid;
        public int? Afi;
        public TagContent Content;  // null = ikke læst
        public int Misses;          // antal inventory-runder i træk, hvor tagget ikke var med
        public int ReadFails;

        public string AlarmText
        {
            get { return Afi == null ? "?" : Afi == AFI_ON ? "TIL" : Afi == AFI_OFF ? "FRA" : "0x" + Afi.Value.ToString("X2"); }
        }

        public string Label
        {
            get
            {
                if (Content == null) return "ukendt tag (" + Mac + ")";
                string del = Content.NumItems > 1 || Content.SeqNum > 1 ? " del " + Content.SeqNum + "/" + Content.NumItems : "";
                return (Content.Barcode.Length > 0 ? Content.Barcode : "tomt tag") + del;
            }
        }

        public string ToJson()
        {
            TagContent c = Content ?? TagContent.Empty();
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"Trtype\":3,\"Dfsid\":").Append(Dsfid.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"Id\":").Append(Json.Str(Convert.ToBase64String(Uid)));
            sb.Append(",\"Mac\":").Append(Json.Str(Mac));
            sb.Append(",\"Content\":{");
            sb.Append("\"SeqNum\":").Append(c.SeqNum);
            sb.Append(",\"NumItems\":").Append(c.NumItems);
            sb.Append(",\"Barcode\":").Append(Json.Str(c.Barcode));
            sb.Append(",\"Crc\":").Append(c.Crc == null ? "null" : Json.Str(Convert.ToBase64String(c.Crc)));
            sb.Append(",\"Country\":").Append(Json.Str(c.Country));
            sb.Append(",\"Library\":").Append(Json.Str(c.Library));
            sb.Append("}}");
            return sb.ToString();
        }

        public static string InventoryJson(IEnumerable<TagState> tags)
        {
            StringBuilder sb = new StringBuilder("{");
            bool first = true;
            foreach (TagState t in tags)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Json.Str(t.Mac)).Append(':').Append(t.ToJson());
            }
            return sb.Append('}').ToString();
        }
    }

    public static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
