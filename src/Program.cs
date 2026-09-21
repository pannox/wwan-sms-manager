using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Windows.Devices.Sms;
using Windows.Foundation;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;
using Color = System.Drawing.Color;
using Font = System.Drawing.Font;

namespace SmsManagerApp
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // Carica DLL WinRT eventualmente incorporata nell'EXE (portable single-file)
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        static System.Reflection.Assembly ResolveEmbedded(object sender, ResolveEventArgs args)
        {
            try
            {
                string name = new System.Reflection.AssemblyName(args.Name).Name + ".dll";
                string resourceName = null;
                System.Reflection.Assembly exec = System.Reflection.Assembly.GetExecutingAssembly();
                foreach (string res in exec.GetManifestResourceNames())
                {
                    if (res.EndsWith(name, StringComparison.OrdinalIgnoreCase) ||
                        res.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                        res.EndsWith("System.Runtime.WindowsRuntime.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        if (name.Equals("System.Runtime.WindowsRuntime.dll", StringComparison.OrdinalIgnoreCase) ||
                            res.IndexOf("WindowsRuntime", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            resourceName = res;
                            if (res.EndsWith(name, StringComparison.OrdinalIgnoreCase)) break;
                        }
                    }
                }
                if (resourceName == null)
                {
                    foreach (string res in exec.GetManifestResourceNames())
                    {
                        if (res.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                        {
                            resourceName = res;
                            break;
                        }
                    }
                }
                if (resourceName == null) return null;
                using (Stream stream = exec.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return null;
                    byte[] data = new byte[stream.Length];
                    stream.Read(data, 0, data.Length);
                    return System.Reflection.Assembly.Load(data);
                }
            }
            catch
            {
                return null;
            }
        }
    }

    sealed class SmsItem
    {
        public uint Id;
        public List<uint> PartIds = new List<uint>();
        public string From = "";
        public string Body = "";
        public string Timestamp = "";
        public DateTime SortKey = DateTime.MinValue;
        public DateTime LastSortKey = DateTime.MinValue;
        public int PartCount = 1;
        // Multipart UDH
        public bool HasConcat;
        public int ConcatRef = -1;
        public int PartNumber = 1;
        public int TotalParts = 1;

        public string Preview
        {
            get
            {
                string b = (Body ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                if (b.Length > 90) b = b.Substring(0, 90) + "...";
                return b;
            }
        }
    }

    /// <summary>
    /// Decoder SMS-DELIVER (PDU) — evita i @@@@@ causati dal padding 0x00 del modem
    /// e legge correttamente UDH multi-parte.
    /// </summary>
    static class GsmPdu
    {
        static readonly char[] Gsm7 = new char[] {
            '@','£','$','¥','è','é','ù','ì','ò','Ç','\n','Ø','ø','\r','Å','å',
            'Δ','_','Φ','Γ','Λ','Ω','Π','Ψ','Σ','Θ','Ξ','\x1B','Æ','æ','ß','É',
            ' ','!','"','#','¤','%','&','\'','(',')','*','+',',','-','.','/',
            '0','1','2','3','4','5','6','7','8','9',':',';','<','=','>','?',
            '¡','A','B','C','D','E','F','G','H','I','J','K','L','M','N','O',
            'P','Q','R','S','T','U','V','W','X','Y','Z','Ä','Ö','Ñ','Ü','§',
            '¿','a','b','c','d','e','f','g','h','i','j','k','l','m','n','o',
            'p','q','r','s','t','u','v','w','x','y','z','ä','ö','ñ','ü','à'
        };

        public static bool TryParseDeliver(byte[] pdu, out string from, out string body, out DateTime time,
            out bool hasConcat, out int concatRef, out int partNum, out int totalParts)
        {
            from = ""; body = ""; time = DateTime.MinValue;
            hasConcat = false; concatRef = -1; partNum = 1; totalParts = 1;
            try
            {
                if (pdu == null || pdu.Length < 10) return false;
                int i = 0;
                int smscLen = pdu[i++];
                i += smscLen;
                if (i >= pdu.Length) return false;

                int firstOctet = pdu[i++];
                bool udhi = (firstOctet & 0x40) != 0;

                // Originating address
                int oaLenDigits = pdu[i++];
                int oaType = pdu[i++];
                int oaBytes = (oaLenDigits + 1) / 2;
                from = DecodeAddress(pdu, i, oaLenDigits, oaType);
                i += oaBytes;

                i++; // PID
                int dcs = pdu[i++];
                // SCTS 7 bytes
                if (i + 7 > pdu.Length) return false;
                time = DecodeScts(pdu, i);
                i += 7;

                int udl = pdu[i++];
                if (i > pdu.Length) return false;

                // Copia UD e togli padding 0x00 finale del record modem
                int udAvail = pdu.Length - i;
                byte[] ud = new byte[udAvail];
                Buffer.BlockCopy(pdu, i, ud, 0, udAvail);
                int udLen = ud.Length;
                while (udLen > 0 && ud[udLen - 1] == 0x00) udLen--;

                int alphabet = (dcs >> 2) & 0x03; // 0=7bit 1=8bit 2=ucs2
                // general data coding
                if ((dcs & 0xF0) == 0x00 || (dcs & 0xC0) == 0x00)
                    alphabet = (dcs >> 2) & 0x03;
                if ((dcs & 0xE0) == 0xC0 || (dcs & 0xE0) == 0xD0) alphabet = 0;
                if ((dcs & 0xE0) == 0xE0) alphabet = 2;
                if ((dcs & 0xF4) == 0xF0) alphabet = ((dcs & 0x04) != 0) ? 1 : 0;

                int headerSeptets = 0;
                int headerBytes = 0;
                if (udhi && udLen > 0)
                {
                    int udhl = ud[0];
                    headerBytes = udhl + 1;
                    ParseUdh(ud, udhl, out hasConcat, out concatRef, out partNum, out totalParts);
                    // 7-bit: header occupies ceil((udhl+1)*8/7) septets
                    headerSeptets = ((headerBytes * 8) + 6) / 7;
                }

                if (alphabet == 2)
                {
                    // UCS2
                    int start = headerBytes;
                    int byteCount = Math.Min(udl * (alphabet == 2 ? 1 : 1), udLen) - start;
                    // For UCS2 UDL is bytes
                    int ucs2Bytes = udl - headerBytes;
                    if (ucs2Bytes < 0) ucs2Bytes = 0;
                    if (start + ucs2Bytes > udLen) ucs2Bytes = Math.Max(0, udLen - start);
                    // trim trailing null pairs
                    while (ucs2Bytes >= 2 && ud[start + ucs2Bytes - 1] == 0 && ud[start + ucs2Bytes - 2] == 0)
                        ucs2Bytes -= 2;
                    body = Encoding.BigEndianUnicode.GetString(ud, start, ucs2Bytes - (ucs2Bytes % 2));
                }
                else if (alphabet == 1)
                {
                    int start = headerBytes;
                    int n = udl - headerBytes;
                    if (n < 0) n = 0;
                    if (start + n > udLen) n = Math.Max(0, udLen - start);
                    while (n > 0 && ud[start + n - 1] == 0) n--;
                    body = Encoding.GetEncoding("ISO-8859-1").GetString(ud, start, n);
                }
                else
                {
                    // GSM 7-bit packed; UDL è in septet, ma il modem spesso pad-da con 0x00
                    int maxSeptets = (udLen * 8) / 7;
                    int septetCount = udl;
                    if (septetCount > maxSeptets) septetCount = maxSeptets;
                    if (septetCount > headerSeptets)
                        body = UnpackGsm7(ud, septetCount, headerSeptets);
                    else
                        body = "";
                    body = StripPaddingArtifacts(body);
                }

                body = StripPaddingArtifacts(body ?? "");
                return true;
            }
            catch
            {
                return false;
            }
        }

        static void ParseUdh(byte[] ud, int udhl, out bool hasConcat, out int concatRef, out int partNum, out int totalParts)
        {
            hasConcat = false; concatRef = -1; partNum = 1; totalParts = 1;
            int p = 1;
            int end = Math.Min(ud.Length, 1 + udhl);
            while (p + 1 < end)
            {
                int iei = ud[p++];
                int iedl = ud[p++];
                if (p + iedl > end) break;
                if (iei == 0x00 && iedl >= 3)
                {
                    hasConcat = true;
                    concatRef = ud[p];
                    totalParts = ud[p + 1];
                    partNum = ud[p + 2];
                }
                else if (iei == 0x08 && iedl >= 4)
                {
                    hasConcat = true;
                    concatRef = (ud[p] << 8) | ud[p + 1];
                    totalParts = ud[p + 2];
                    partNum = ud[p + 3];
                }
                p += iedl;
            }
        }

        static string UnpackGsm7(byte[] ud, int septetCount, int skipSeptets)
        {
            // Unpack all septets from packed bytes, then skip header septets
            List<byte> septets = new List<byte>();
            int bitOffset = 0;
            int byteIndex = 0;
            for (int s = 0; s < septetCount; s++)
            {
                int val = 0;
                for (int b = 0; b < 7; b++)
                {
                    int srcByte = byteIndex;
                    if (srcByte >= ud.Length) { val = -1; break; }
                    int bit = (ud[srcByte] >> bitOffset) & 1;
                    val |= (bit << b);
                    bitOffset++;
                    if (bitOffset == 8) { bitOffset = 0; byteIndex++; }
                }
                if (val < 0) break;
                septets.Add((byte)val);
            }
            StringBuilder sb = new StringBuilder();
            for (int s = skipSeptets; s < septets.Count; s++)
            {
                int v = septets[s];
                if (v == 0x1B && s + 1 < septets.Count)
                {
                    // escape
                    s++;
                    sb.Append(Gsm7Esc(septets[s]));
                }
                else if (v >= 0 && v < Gsm7.Length)
                    sb.Append(Gsm7[v]);
            }
            return sb.ToString();
        }

        static char Gsm7Esc(byte v)
        {
            switch (v)
            {
                case 0x0A: return '\f';
                case 0x14: return '^';
                case 0x28: return '{';
                case 0x29: return '}';
                case 0x2F: return '\\';
                case 0x3C: return '[';
                case 0x3D: return '~';
                case 0x3E: return ']';
                case 0x40: return '|';
                case 0x65: return '€';
                default: return '?';
            }
        }

        static string DecodeAddress(byte[] pdu, int offset, int digitCount, int type)
        {
            StringBuilder sb = new StringBuilder();
            bool intl = (type & 0x70) == 0x10;
            if (intl) sb.Append('+');
            for (int d = 0; d < digitCount; d++)
            {
                int b = pdu[offset + (d / 2)];
                int nibble = ((d % 2) == 0) ? (b & 0x0F) : ((b >> 4) & 0x0F);
                if (nibble == 0x0F) break;
                if (nibble < 10) sb.Append((char)('0' + nibble));
                else sb.Append((char)('A' + (nibble - 10)));
            }
            return sb.ToString();
        }

        static DateTime DecodeScts(byte[] pdu, int offset)
        {
            try
            {
                int yy = SwapBcd(pdu[offset]);
                int mo = SwapBcd(pdu[offset + 1]);
                int dd = SwapBcd(pdu[offset + 2]);
                int hh = SwapBcd(pdu[offset + 3]);
                int mi = SwapBcd(pdu[offset + 4]);
                int ss = SwapBcd(pdu[offset + 5]);
                int year = 2000 + yy;
                if (year > DateTime.Now.Year + 1) year -= 100;
                return new DateTime(year, mo, dd, hh, mi, ss);
            }
            catch { return DateTime.MinValue; }
        }

        static int SwapBcd(byte b)
        {
            return ((b & 0x0F) * 10) + ((b >> 4) & 0x0F);
        }

        public static string StripPaddingArtifacts(string body)
        {
            if (string.IsNullOrEmpty(body)) return "";
            // Taglia da una sequenza di @@@@@ (padding GSM 0x00) in poi
            int idx = body.IndexOf("@@@");
            if (idx >= 0) body = body.Substring(0, idx);
            body = body.TrimEnd('\0', '@', 'Φ', '\x00');
            // a volte resta una lettera spuria prima del padding (es. F/Φ)
            body = body.TrimEnd();
            while (body.Length > 0)
            {
                char c = body[body.Length - 1];
                if (c == '@' || c == 'Φ' || c == '\0')
                    body = body.Substring(0, body.Length - 1).TrimEnd();
                else break;
            }
            return body;
        }

        public static string SanitizeWindowsBody(string body)
        {
            return StripPaddingArtifacts(body ?? "");
        }
    }

    sealed class MainForm : Form
    {
        readonly ListView list;
        readonly TextBox txtFrom;
        readonly TextBox txtDate;
        readonly TextBox txtBody;
        readonly TextBox txtTo;
        readonly TextBox txtCompose;
        readonly Label lblStatus;
        readonly Label lblPhone;
        readonly Label lblCount;
        readonly Label lblChars;
        readonly Button btnRefresh;
        readonly Button btnDelete;
        readonly Button btnDeleteAll;
        readonly Button btnSend;
        readonly Button btnCopy;
        readonly Button btnExport;
        readonly ProgressBar progress;
        readonly List<SmsItem> items = new List<SmsItem>();
        SmsDevice device;
        bool busy;
        volatile bool cancelWork;

        static readonly Color Bg = Color.FromArgb(18, 22, 28);
        static readonly Color Panel = Color.FromArgb(28, 34, 42);
        static readonly Color Panel2 = Color.FromArgb(36, 44, 54);
        static readonly Color Accent = Color.FromArgb(0, 168, 150);
        static readonly Color AccentDark = Color.FromArgb(0, 130, 116);
        static readonly Color TextMain = Color.FromArgb(236, 240, 244);
        static readonly Color TextMuted = Color.FromArgb(150, 162, 176);
        static readonly Color Danger = Color.FromArgb(210, 78, 78);
        static readonly Color Border = Color.FromArgb(55, 66, 80);

        const int DeleteTimeoutMs = 5000;
        const int GeneralTimeoutMs = 45000;
        const int RefreshAfterDeleteMs = 12000;

        public MainForm()
        {
            Text = "SMS Manager — Mobile Broadband  v1.0.1";
            Width = 1040;
            Height = 720;
            MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = TextMain;
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            DoubleBuffered = true;
            Padding = new Padding(0);
            try
            {
                this.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }

            // ===== HEADER =====
            Panel header = MakePanel(Panel);
            header.Dock = DockStyle.Top;
            header.Height = 88;
            header.Padding = new Padding(18, 14, 18, 12);

            TableLayoutPanel headGrid = new TableLayoutPanel();
            headGrid.Dock = DockStyle.Fill;
            headGrid.ColumnCount = 2;
            headGrid.RowCount = 2;
            headGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            headGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            headGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
            headGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
            header.Controls.Add(headGrid);

            Label title = new Label();
            title.Text = "SMS Manager";
            title.Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold);
            title.ForeColor = TextMain;
            title.Dock = DockStyle.Fill;
            title.TextAlign = ContentAlignment.BottomLeft;
            title.Margin = new Padding(0);
            headGrid.Controls.Add(title, 0, 0);

            lblCount = new Label();
            lblCount.Text = "0 messaggi";
            lblCount.ForeColor = Accent;
            lblCount.Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
            lblCount.AutoSize = true;
            lblCount.Anchor = AnchorStyles.Right;
            lblCount.TextAlign = ContentAlignment.MiddleRight;
            lblCount.Margin = new Padding(16, 4, 0, 0);
            headGrid.Controls.Add(lblCount, 1, 0);

            lblPhone = new Label();
            lblPhone.Text = "Modem: in ricerca...";
            lblPhone.ForeColor = TextMuted;
            lblPhone.Font = new Font("Segoe UI", 9f);
            lblPhone.Dock = DockStyle.Fill;
            lblPhone.AutoSize = false;
            lblPhone.AutoEllipsis = true;
            lblPhone.TextAlign = ContentAlignment.TopLeft;
            lblPhone.Margin = new Padding(0, 2, 8, 0);
            headGrid.Controls.Add(lblPhone, 0, 1);
            headGrid.SetColumnSpan(lblPhone, 2);

            // ===== STATUS BAR =====
            Panel bottom = MakePanel(Panel);
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 34;
            bottom.Padding = new Padding(14, 0, 10, 0);

            progress = new ProgressBar();
            progress.Style = ProgressBarStyle.Marquee;
            progress.MarqueeAnimationSpeed = 25;
            progress.Dock = DockStyle.Right;
            progress.Width = 120;
            progress.Height = 16;
            progress.Visible = false;
            bottom.Controls.Add(progress);

            lblStatus = new Label();
            lblStatus.Text = "Pronto";
            lblStatus.ForeColor = TextMuted;
            lblStatus.Dock = DockStyle.Fill;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            bottom.Controls.Add(lblStatus);

            // ===== BODY SPLIT =====
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.BackColor = Bg;
            split.Orientation = Orientation.Vertical;
            split.SplitterWidth = 6;
            // MinSize bassi in costruzione (evita crash prima del layout)
            split.Panel1MinSize = 100;
            split.Panel2MinSize = 100;

            // --- LEFT: toolbar + list ---
            Panel left = MakePanel(Bg);
            left.Dock = DockStyle.Fill;
            left.Padding = new Padding(14, 12, 8, 12);

            FlowLayoutPanel tools = new FlowLayoutPanel();
            tools.Dock = DockStyle.Top;
            tools.Height = 44;
            tools.FlowDirection = FlowDirection.LeftToRight;
            tools.WrapContents = false;
            tools.Padding = new Padding(0, 0, 0, 8);
            tools.BackColor = Bg;

            btnRefresh = MakeButton("Aggiorna", Accent, AccentDark);
            btnRefresh.Width = 96;
            btnRefresh.Margin = new Padding(0, 0, 8, 0);
            btnRefresh.Click += delegate { RefreshInboxAsync(); };
            tools.Controls.Add(btnRefresh);

            btnDelete = MakeButton("Elimina", Danger, Color.FromArgb(160, 50, 50));
            btnDelete.Width = 88;
            btnDelete.Margin = new Padding(0, 0, 8, 0);
            btnDelete.Click += delegate { DeleteSelectedAsync(); };
            tools.Controls.Add(btnDelete);

            btnDeleteAll = MakeButton("Svuota", Color.FromArgb(120, 80, 80), Color.FromArgb(90, 55, 55));
            btnDeleteAll.Width = 88;
            btnDeleteAll.Margin = new Padding(0, 0, 8, 0);
            btnDeleteAll.Click += delegate { DeleteAllAsync(); };
            tools.Controls.Add(btnDeleteAll);

            btnExport = MakeButton("Esporta", Panel2, Border);
            btnExport.Width = 88;
            btnExport.Margin = new Padding(0);
            btnExport.Click += delegate { ExportInbox(); };
            tools.Controls.Add(btnExport);

            list = new ListView();
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.MultiSelect = true;
            list.HideSelection = false;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.BackColor = Panel;
            list.ForeColor = TextMain;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.Font = new Font("Segoe UI", 9f);
            list.Columns.Add("Da", 120);
            list.Columns.Add("Data", 120);
            list.Columns.Add("Anteprima", 150);
            list.SelectedIndexChanged += delegate { ShowSelected(); };
            list.Resize += delegate { ResizeListColumns(); };

            left.Controls.Add(list);
            left.Controls.Add(tools);
            split.Panel1.Controls.Add(left);

            // --- RIGHT: detail + compose with TableLayout ---
            Panel right = MakePanel(Bg);
            right.Dock = DockStyle.Fill;
            right.Padding = new Padding(8, 12, 14, 12);

            TableLayoutPanel rightGrid = new TableLayoutPanel();
            rightGrid.Dock = DockStyle.Fill;
            rightGrid.ColumnCount = 1;
            rightGrid.RowCount = 2;
            rightGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 48f));
            rightGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 52f));
            right.Controls.Add(rightGrid);

            // DETAIL CARD
            Panel detailHost;
            Panel detailCard = MakeCard("Messaggio selezionato", out detailHost);
            detailCard.Margin = new Padding(0, 0, 0, 0);
            TableLayoutPanel detailGrid = new TableLayoutPanel();
            detailGrid.Dock = DockStyle.Fill;
            detailGrid.ColumnCount = 2;
            detailGrid.RowCount = 4;
            detailGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55f));
            detailGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45f));
            detailGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            detailGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
            detailGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
            detailGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            detailHost.Controls.Add(detailGrid);

            Label l1 = MakeMutedLabel("Mittente");
            l1.Margin = new Padding(0, 0, 8, 2);
            detailGrid.Controls.Add(l1, 0, 0);

            Label l2 = MakeMutedLabel("Data");
            l2.Margin = new Padding(0, 0, 0, 2);
            detailGrid.Controls.Add(l2, 1, 0);

            txtFrom = MakeTextBox(false);
            txtFrom.Dock = DockStyle.Fill;
            txtFrom.Margin = new Padding(0, 0, 8, 8);
            detailGrid.Controls.Add(txtFrom, 0, 1);

            txtDate = MakeTextBox(false);
            txtDate.Dock = DockStyle.Fill;
            txtDate.Margin = new Padding(0, 0, 0, 8);
            detailGrid.Controls.Add(txtDate, 1, 1);

            Panel textHead = new Panel();
            textHead.Dock = DockStyle.Fill;
            textHead.Height = 28;
            textHead.Margin = new Padding(0, 0, 0, 2);
            Label l3 = MakeMutedLabel("Testo");
            l3.Dock = DockStyle.Left;
            l3.AutoSize = true;
            btnCopy = MakeButton("Copia", Panel2, Border);
            btnCopy.Width = 72;
            btnCopy.Height = 26;
            btnCopy.Dock = DockStyle.Right;
            btnCopy.Click += delegate
            {
                if (!string.IsNullOrEmpty(txtBody.Text))
                {
                    Clipboard.SetText(txtBody.Text);
                    SetStatus("Testo copiato negli appunti");
                }
            };
            textHead.Controls.Add(btnCopy);
            textHead.Controls.Add(l3);
            detailGrid.Controls.Add(textHead, 0, 2);
            detailGrid.SetColumnSpan(textHead, 2);

            txtBody = MakeTextBox(false);
            txtBody.Multiline = true;
            txtBody.ScrollBars = ScrollBars.Vertical;
            txtBody.Dock = DockStyle.Fill;
            txtBody.Margin = new Padding(0, 0, 0, 0);
            detailGrid.Controls.Add(txtBody, 0, 3);
            detailGrid.SetColumnSpan(txtBody, 2);

            rightGrid.Controls.Add(detailCard, 0, 0);

            // COMPOSE CARD
            Panel composeHost;
            Panel composeCard = MakeCard("Nuovo SMS", out composeHost);
            composeCard.Margin = new Padding(0, 8, 0, 0);
            TableLayoutPanel composeGrid = new TableLayoutPanel();
            composeGrid.Dock = DockStyle.Fill;
            composeGrid.ColumnCount = 1;
            composeGrid.RowCount = 5;
            composeGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            composeGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32f));
            composeGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            composeGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            composeGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
            composeHost.Controls.Add(composeGrid);

            Label l4 = MakeMutedLabel("Destinatario (+39...)");
            l4.Margin = new Padding(0, 0, 0, 2);
            composeGrid.Controls.Add(l4, 0, 0);

            txtTo = MakeTextBox(true);
            txtTo.Dock = DockStyle.Fill;
            txtTo.Margin = new Padding(0, 0, 0, 8);
            composeGrid.Controls.Add(txtTo, 0, 1);

            Label l5 = MakeMutedLabel("Messaggio");
            l5.Margin = new Padding(0, 0, 0, 2);
            composeGrid.Controls.Add(l5, 0, 2);

            txtCompose = MakeTextBox(true);
            txtCompose.Multiline = true;
            txtCompose.ScrollBars = ScrollBars.Vertical;
            txtCompose.Dock = DockStyle.Fill;
            txtCompose.Margin = new Padding(0, 0, 0, 8);
            txtCompose.TextChanged += delegate { UpdateCharCount(); };
            composeGrid.Controls.Add(txtCompose, 0, 3);

            TableLayoutPanel sendRow = new TableLayoutPanel();
            sendRow.Dock = DockStyle.Fill;
            sendRow.ColumnCount = 2;
            sendRow.RowCount = 1;
            sendRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            sendRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            sendRow.Margin = new Padding(0);

            lblChars = MakeMutedLabel("0 caratteri");
            lblChars.Dock = DockStyle.Fill;
            lblChars.TextAlign = ContentAlignment.MiddleLeft;
            lblChars.Margin = new Padding(0);
            sendRow.Controls.Add(lblChars, 0, 0);

            btnSend = MakeButton("Invia SMS", Accent, AccentDark);
            btnSend.Width = 120;
            btnSend.Height = 34;
            btnSend.Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
            btnSend.Margin = new Padding(8, 0, 0, 0);
            btnSend.Click += delegate { SendSmsAsync(); };
            sendRow.Controls.Add(btnSend, 1, 0);

            composeGrid.Controls.Add(sendRow, 0, 4);
            rightGrid.Controls.Add(composeCard, 0, 1);

            split.Panel2.Controls.Add(right);

            // Order matters for Dock
            Controls.Add(split);
            Controls.Add(bottom);
            Controls.Add(header);

            Load += delegate
            {
                try
                {
                    split.Panel1MinSize = 280;
                    split.Panel2MinSize = 320;
                    int dist = ClientSize.Width * 42 / 100;
                    if (dist < split.Panel1MinSize) dist = split.Panel1MinSize;
                    if (dist > split.Width - split.Panel2MinSize - split.SplitterWidth)
                        dist = Math.Max(split.Panel1MinSize, split.Width - split.Panel2MinSize - split.SplitterWidth);
                    split.SplitterDistance = dist;
                }
                catch { }
                ResizeListColumns();
            };

            Shown += delegate { RefreshInboxAsync(); };
            FormClosing += delegate
            {
                cancelWork = true;
                device = null;
            };
        }

        Panel MakeCard(string title, out Panel contentHost)
        {
            Panel card = new Panel();
            card.Dock = DockStyle.Fill;
            card.BackColor = Panel;
            card.Padding = new Padding(0);
            card.Margin = new Padding(0);

            TableLayoutPanel shell = new TableLayoutPanel();
            shell.Dock = DockStyle.Fill;
            shell.ColumnCount = 1;
            shell.RowCount = 2;
            shell.Padding = new Padding(14, 10, 14, 12);
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            card.Controls.Add(shell);

            Label h = new Label();
            h.Text = title;
            h.Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
            h.ForeColor = Accent;
            h.Dock = DockStyle.Fill;
            h.TextAlign = ContentAlignment.MiddleLeft;
            shell.Controls.Add(h, 0, 0);

            contentHost = new Panel();
            contentHost.Dock = DockStyle.Fill;
            contentHost.BackColor = Panel;
            contentHost.Margin = new Padding(0, 4, 0, 0);
            shell.Controls.Add(contentHost, 0, 1);

            return card;
        }

        Panel MakePanel(Color c)
        {
            Panel p = new Panel();
            p.BackColor = c;
            return p;
        }

        Label MakeMutedLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.ForeColor = TextMuted;
            l.AutoSize = true;
            l.BackColor = Color.Transparent;
            return l;
        }

        TextBox MakeTextBox(bool editable)
        {
            TextBox t = new TextBox();
            t.BackColor = Panel2;
            t.ForeColor = TextMain;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.ReadOnly = !editable;
            t.Font = new Font("Segoe UI", 9.5f);
            return t;
        }

        Button MakeButton(string text, Color back, Color hover)
        {
            Button b = new Button();
            b.Text = text;
            b.Height = 32;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = back;
            b.ForeColor = Color.White;
            b.Cursor = Cursors.Hand;
            b.FlatAppearance.MouseOverBackColor = hover;
            b.FlatAppearance.MouseDownBackColor = hover;
            b.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
            return b;
        }

        void ResizeListColumns()
        {
            if (list == null || list.Columns.Count < 3) return;
            int w = list.ClientSize.Width - list.Columns[0].Width - list.Columns[1].Width - 8;
            list.Columns[2].Width = Math.Max(80, w);
        }

        void UpdateCharCount()
        {
            int n = txtCompose.Text.Length;
            int parts = n == 0 ? 0 : (n <= 160 ? 1 : (int)Math.Ceiling(n / 153.0));
            lblChars.Text = n + " caratteri" + (parts > 1 ? (" · " + parts + " SMS") : (parts == 1 ? " · 1 SMS" : ""));
        }

        void SetBusy(bool value, string status)
        {
            busy = value;
            progress.Visible = value;
            btnRefresh.Enabled = !value;
            btnDelete.Enabled = !value;
            btnDeleteAll.Enabled = !value;
            btnSend.Enabled = !value;
            btnExport.Enabled = !value;
            if (!string.IsNullOrEmpty(status)) lblStatus.Text = status;
            Cursor = value ? Cursors.WaitCursor : Cursors.Default;
        }

        void SetStatus(string s)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(delegate { lblStatus.Text = s; }));
                return;
            }
            lblStatus.Text = s;
        }

        void ShowSelected()
        {
            if (list.SelectedItems.Count == 0)
            {
                txtFrom.Text = "";
                txtDate.Text = "";
                txtBody.Text = "";
                return;
            }
            SmsItem item = list.SelectedItems[0].Tag as SmsItem;
            if (item == null) return;
            txtFrom.Text = item.From + (item.PartCount > 1 ? ("  (" + item.PartCount + " parti ricomposte)") : "");
            txtDate.Text = item.Timestamp;
            txtBody.Text = item.Body;
            if (!string.IsNullOrEmpty(item.From) && string.IsNullOrEmpty(txtTo.Text))
                txtTo.Text = item.From;
        }

        void RefreshListView()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (SmsItem it in items)
            {
                ListViewItem row = new ListViewItem(it.From);
                row.SubItems.Add(it.Timestamp);
                row.SubItems.Add((it.PartCount > 1 ? "[" + it.PartCount + "p] " : "") + it.Preview);
                row.Tag = it;
                list.Items.Add(row);
            }
            list.EndUpdate();
            lblCount.Text = items.Count + " messaggi";
            ResizeListColumns();
        }

        SmsDevice OpenDevice()
        {
            SmsDevice d = AwaitOp(SmsDevice.GetDefaultAsync(), GeneralTimeoutMs);
            if (d == null) throw new Exception("Nessun modem Mobile Broadband / SMS trovato.\nInserisci una SIM e verifica Impostazioni > Rete cellulare.");
            return d;
        }

        void RefreshInboxAsync()
        {
            if (busy) return;
            cancelWork = false;
            SetBusy(true, "Lettura SMS dal modem...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = null;
                string phone = null;
                int max = 0;
                int rawCount = 0;
                List<SmsItem> loaded = new List<SmsItem>();
                try
                {
                    device = OpenDevice();
                    phone = device.AccountPhoneNumber;
                    max = (int)device.MessageStore.MaxMessages;
                    IReadOnlyList<ISmsMessage> msgs = AwaitProg(device.MessageStore.GetMessagesAsync(SmsMessageFilter.All), GeneralTimeoutMs);
                    List<SmsItem> raw = new List<SmsItem>();
                    if (msgs != null)
                    {
                        foreach (ISmsMessage m in msgs)
                        {
                            SmsItem it = ToItem(m);
                            if (it != null) raw.Add(it);
                        }
                    }
                    rawCount = raw.Count;
                    loaded = ReassembleMultipart(raw);
                    loaded.Sort(delegate(SmsItem a, SmsItem b) { return b.SortKey.CompareTo(a.SortKey); });
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }

                BeginInvoke(new Action(delegate
                {
                    if (IsDisposed) return;
                    if (err != null)
                    {
                        items.Clear();
                        RefreshListView();
                        lblPhone.Text = "Modem: non disponibile";
                        SetBusy(false, "Errore");
                        MessageBox.Show(this, err, "SMS Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    items.Clear();
                    items.AddRange(loaded);
                    RefreshListView();
                    lblPhone.Text = "SIM " + (string.IsNullOrEmpty(phone) ? "(sconosciuta)" : phone)
                        + "   ·   memoria modem " + rawCount + "/" + max;
                    SetBusy(false, "Inbox aggiornata · " + items.Count + " messaggi"
                        + (rawCount != items.Count ? (" (ricomposti da " + rawCount + " segmenti)") : ""));
                }));
            });
        }

        void DeleteSelectedAsync()
        {
            if (busy) return;
            if (list.SelectedItems.Count == 0)
            {
                MessageBox.Show(this, "Seleziona almeno un messaggio.", "SMS Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show(this, "Eliminare " + list.SelectedItems.Count + " messaggio/i selezionato/i?", "Conferma",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            List<uint> ids = new List<uint>();
            List<SmsItem> selected = new List<SmsItem>();
            foreach (ListViewItem row in list.SelectedItems)
            {
                SmsItem it = row.Tag as SmsItem;
                if (it == null) continue;
                selected.Add(it);
                foreach (uint id in it.PartIds)
                {
                    if (!ids.Contains(id)) ids.Add(id);
                }
            }
            if (ids.Count == 0)
            {
                MessageBox.Show(this, "Nessun ID messaggio valido da eliminare.", "SMS Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DeleteIdsAsync(ids, selected, "Eliminazione...");
        }

        void DeleteAllAsync()
        {
            if (busy) return;
            if (MessageBox.Show(this, "Eliminare TUTTI gli SMS dalla memoria del modem?\nOperazione irreversibile.", "Svuota inbox",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            List<uint> ids = new List<uint>();
            List<SmsItem> all = new List<SmsItem>(items);
            foreach (SmsItem it in items)
            {
                foreach (uint id in it.PartIds)
                {
                    if (!ids.Contains(id)) ids.Add(id);
                }
            }

            // Se la lista UI è vuota ma il modem ha messaggi, li scopriamo nel worker
            DeleteIdsAsync(ids, all, "Svuotamento inbox...", true);
        }

        /// <summary>
        /// Elimina per ID con timeout duro, aggiornamento UI ottimistico e refresh non bloccante.
        /// Evita il freeze tipico quando resta un solo SMS sul modem.
        /// </summary>
        void DeleteIdsAsync(List<uint> ids, List<SmsItem> removeFromUi, string status)
        {
            DeleteIdsAsync(ids, removeFromUi, status, false);
        }

        void DeleteIdsAsync(List<uint> ids, List<SmsItem> removeFromUi, string status, bool discoverIfEmpty)
        {
            cancelWork = false;
            SetBusy(true, status);

            // UI ottimistica: togli subito i messaggi dalla lista (sblocca l'interfaccia)
            if (removeFromUi != null && removeFromUi.Count > 0)
            {
                foreach (SmsItem it in removeFromUi)
                    items.Remove(it);
                RefreshListView();
                txtFrom.Text = "";
                txtDate.Text = "";
                txtBody.Text = "";
            }

            // Worker STA: alcune stack WWAN si bloccano su MTA alla DeleteMessageAsync
            Thread worker = new Thread(delegate()
            {
                string err = null;
                int ok = 0, fail = 0;
                List<uint> toDelete = new List<uint>(ids);
                try
                {
                    SmsDevice localDevice = OpenDevice();
                    device = localDevice;

                    if (discoverIfEmpty && toDelete.Count == 0)
                    {
                        SetStatus("Lettura ID dal modem...");
                        IReadOnlyList<ISmsMessage> msgs = AwaitProg(localDevice.MessageStore.GetMessagesAsync(SmsMessageFilter.All), RefreshAfterDeleteMs);
                        if (msgs != null)
                        {
                            foreach (ISmsMessage m in msgs)
                            {
                                if (!toDelete.Contains(m.Id)) toDelete.Add(m.Id);
                            }
                        }
                    }

                    for (int i = 0; i < toDelete.Count; i++)
                    {
                        if (cancelWork) break;
                        uint id = toDelete[i];
                        SetStatus("Eliminazione " + (i + 1) + "/" + toDelete.Count + " (id " + id + ")...");
                        try
                        {
                            // Timeout corto: se il modem non risponde (caso "ultimo SMS"), non restiamo bloccati
                            AwaitAction(localDevice.MessageStore.DeleteMessageAsync(id), DeleteTimeoutMs);
                            ok++;
                        }
                        catch
                        {
                            fail++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }

                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(delegate
                        {
                            FinishDelete(err, ok, fail, toDelete.Count);
                        }));
                    }
                }
                catch { }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        void FinishDelete(string err, int ok, int fail, int attempted)
        {
            if (IsDisposed) return;

            // Sblocca SEMPRE l'UI prima di qualsiasi altra cosa
            SetBusy(false, err != null
                ? ("Errore: " + err)
                : (ok > 0
                    ? ("Eliminati " + ok + (fail > 0 ? (", timeout su " + fail) : ""))
                    : (fail > 0 ? "Eliminazione non confermata dal modem (timeout)" : "Nessun messaggio eliminato")));

            if (err != null)
                MessageBox.Show(this, err, "SMS Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else if (ok == 0 && fail > 0)
                MessageBox.Show(this,
                    "Il modem non ha confermato l'eliminazione in tempo.\n" +
                    "Se il messaggio è sparito dalla lista ma riappare dopo Aggiorna, riprova.",
                    "SMS Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            // Refresh non bloccante (timeout ridotto). Se fallisce, lasciamo la lista ottimistica.
            SoftRefreshAfterDelete();
        }

        void SoftRefreshAfterDelete()
        {
            if (busy) return;
            SetBusy(true, "Aggiornamento inbox...");
            Thread worker = new Thread(delegate()
            {
                string err = null;
                string phone = null;
                int max = 0;
                int rawCount = 0;
                List<SmsItem> loaded = new List<SmsItem>();
                try
                {
                    SmsDevice d = OpenDeviceWithTimeout(RefreshAfterDeleteMs);
                    device = d;
                    phone = d.AccountPhoneNumber;
                    max = (int)d.MessageStore.MaxMessages;
                    IReadOnlyList<ISmsMessage> msgs = AwaitProg(d.MessageStore.GetMessagesAsync(SmsMessageFilter.All), RefreshAfterDeleteMs);
                    List<SmsItem> raw = new List<SmsItem>();
                    if (msgs != null)
                    {
                        foreach (ISmsMessage m in msgs)
                        {
                            SmsItem it = ToItem(m);
                            if (it != null) raw.Add(it);
                        }
                    }
                    rawCount = raw.Count;
                    loaded = ReassembleMultipart(raw);
                    loaded.Sort(delegate(SmsItem a, SmsItem b) { return b.SortKey.CompareTo(a.SortKey); });
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }

                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(delegate
                        {
                            if (IsDisposed) return;
                            if (err == null)
                            {
                                items.Clear();
                                items.AddRange(loaded);
                                RefreshListView();
                                lblPhone.Text = "SIM " + (string.IsNullOrEmpty(phone) ? "(sconosciuta)" : phone)
                                    + "   ·   memoria modem " + rawCount + "/" + max;
                                SetBusy(false, "Inbox aggiornata · " + items.Count + " messaggi");
                            }
                            else
                            {
                                // Non bloccare: tieni lo stato ottimistico già mostrato
                                SetBusy(false, "Eliminazione inviata (refresh modem non riuscito: " + err + ")");
                            }
                        }));
                    }
                }
                catch { }
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        SmsDevice OpenDeviceWithTimeout(int timeoutMs)
        {
            SmsDevice d = AwaitOp(SmsDevice.GetDefaultAsync(), timeoutMs);
            if (d == null) throw new Exception("Nessun modem Mobile Broadband / SMS trovato.");
            return d;
        }

        void SendSmsAsync()
        {
            if (busy) return;
            string to = (txtTo.Text ?? "").Trim();
            string body = txtCompose.Text ?? "";
            if (string.IsNullOrEmpty(to))
            {
                MessageBox.Show(this, "Inserisci il numero destinatario.", "SMS Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
                txtTo.Focus();
                return;
            }
            if (string.IsNullOrEmpty(body.Trim()))
            {
                MessageBox.Show(this, "Inserisci il testo del messaggio.", "SMS Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
                txtCompose.Focus();
                return;
            }
            if (MessageBox.Show(this, "Inviare SMS a:\n" + to + "\n\n" + Truncate(body, 120), "Conferma invio",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            SetBusy(true, "Invio SMS a " + to + "...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = null;
                try
                {
                    device = OpenDevice();
                    SmsTextMessage msg = new SmsTextMessage();
                    msg.To = to;
                    msg.Body = body;
                    AwaitAction(device.SendMessageAsync(msg), GeneralTimeoutMs);
                }
                catch (Exception ex) { err = ex.Message; }

                BeginInvoke(new Action(delegate
                {
                    if (IsDisposed) return;
                    if (err != null)
                    {
                        SetBusy(false, "Invio fallito");
                        MessageBox.Show(this, "Invio non riuscito:\n" + err, "SMS Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    else
                    {
                        txtCompose.Text = "";
                        UpdateCharCount();
                        SetBusy(false, "SMS inviato a " + to);
                        MessageBox.Show(this, "SMS inviato correttamente.", "SMS Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        RefreshInboxAsync();
                    }
                }));
            });
        }

        void ExportInbox()
        {
            if (items.Count == 0)
            {
                MessageBox.Show(this, "Nessun messaggio da esportare.", "SMS Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "Testo (*.txt)|*.txt|CSV (*.csv)|*.csv";
                dlg.FileName = "sms-inbox-" + DateTime.Now.ToString("yyyyMMdd-HHmm");
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (dlg.FilterIndex == 2 || dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine("Id,From,Timestamp,Parts,Body");
                        foreach (SmsItem it in items)
                            sb.AppendLine(string.Format("{0},\"{1}\",\"{2}\",{3},\"{4}\"", it.Id, Esc(it.From), Esc(it.Timestamp), it.PartCount, Esc(it.Body)));
                        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                    }
                    else
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine("SMS INBOX");
                        sb.AppendLine(lblPhone.Text);
                        sb.AppendLine(new string('=', 50));
                        foreach (SmsItem it in items)
                        {
                            sb.AppendLine();
                            sb.AppendLine("Da: " + it.From + (it.PartCount > 1 ? (" [" + it.PartCount + " parti]") : ""));
                            sb.AppendLine("Data: " + it.Timestamp);
                            sb.AppendLine(it.Body);
                            sb.AppendLine(new string('-', 40));
                        }
                        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                    }
                    SetStatus("Esportato: " + dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "SMS Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        /// <summary>
        /// Ricompone SMS multi-parte usando UDH (ref/part), non euristiche sul testo.
        /// </summary>
        static List<SmsItem> ReassembleMultipart(List<SmsItem> raw)
        {
            if (raw == null || raw.Count == 0) return new List<SmsItem>();

            // 1) Gruppi con UDH concat
            Dictionary<string, List<SmsItem>> groups = new Dictionary<string, List<SmsItem>>();
            List<SmsItem> singles = new List<SmsItem>();

            foreach (SmsItem m in raw)
            {
                if (m.HasConcat && m.TotalParts > 1 && m.ConcatRef >= 0)
                {
                    string key = NormFrom(m.From) + "|" + m.ConcatRef + "|" + m.TotalParts;
                    if (!groups.ContainsKey(key)) groups[key] = new List<SmsItem>();
                    groups[key].Add(m);
                }
                else singles.Add(m);
            }

            List<SmsItem> result = new List<SmsItem>();

            foreach (KeyValuePair<string, List<SmsItem>> kv in groups)
            {
                List<SmsItem> parts = kv.Value;
                parts.Sort(delegate(SmsItem a, SmsItem b) { return a.PartNumber.CompareTo(b.PartNumber); });

                SmsItem merged = new SmsItem();
                merged.Id = parts[0].Id;
                merged.From = parts[0].From;
                merged.SortKey = parts[0].SortKey;
                merged.Timestamp = parts[0].Timestamp;
                merged.HasConcat = true;
                merged.ConcatRef = parts[0].ConcatRef;
                merged.TotalParts = parts[0].TotalParts;
                merged.PartNumber = 1;
                merged.PartIds = new List<uint>();
                StringBuilder sb = new StringBuilder();
                foreach (SmsItem p in parts)
                {
                    sb.Append(p.Body ?? "");
                    if (!merged.PartIds.Contains(p.Id)) merged.PartIds.Add(p.Id);
                    if (p.SortKey < merged.SortKey && p.SortKey != DateTime.MinValue)
                    {
                        merged.SortKey = p.SortKey;
                        merged.Timestamp = p.Timestamp;
                    }
                }
                merged.Body = GsmPdu.StripPaddingArtifacts(sb.ToString());
                merged.PartCount = merged.PartIds.Count;
                result.Add(merged);
            }

            // 2) Singoli: fallback euristico leggero solo se testo ancora spezzato senza UDH
            singles.Sort(delegate(SmsItem a, SmsItem b)
            {
                int c = string.Compare(NormFrom(a.From), NormFrom(b.From), StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                c = a.SortKey.CompareTo(b.SortKey);
                if (c != 0) return c;
                return a.Id.CompareTo(b.Id);
            });

            SmsItem cur = null;
            for (int i = 0; i < singles.Count; i++)
            {
                SmsItem m = singles[i];
                if (cur == null) { cur = CloneStart(m); continue; }
                if (CanMergeHeuristic(cur, m))
                {
                    cur.Body = GsmPdu.StripPaddingArtifacts(cur.Body + m.Body);
                    if (!cur.PartIds.Contains(m.Id)) cur.PartIds.Add(m.Id);
                    cur.PartCount = cur.PartIds.Count;
                    cur.LastSortKey = m.SortKey;
                }
                else
                {
                    result.Add(cur);
                    cur = CloneStart(m);
                }
            }
            if (cur != null) result.Add(cur);
            return result;
        }

        static SmsItem CloneStart(SmsItem m)
        {
            SmsItem n = new SmsItem();
            n.Id = m.Id;
            n.From = m.From;
            n.Body = GsmPdu.StripPaddingArtifacts(m.Body ?? "");
            n.Timestamp = m.Timestamp;
            n.SortKey = m.SortKey;
            n.LastSortKey = m.SortKey;
            n.PartIds = new List<uint>();
            n.PartIds.Add(m.Id);
            n.PartCount = 1;
            n.HasConcat = m.HasConcat;
            n.ConcatRef = m.ConcatRef;
            n.PartNumber = m.PartNumber;
            n.TotalParts = m.TotalParts;
            return n;
        }

        static string NormFrom(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Trim();
        }

        static bool CanMergeHeuristic(SmsItem a, SmsItem b)
        {
            if (!string.Equals(NormFrom(a.From), NormFrom(b.From), StringComparison.OrdinalIgnoreCase))
                return false;
            double sec = Math.Abs((a.LastSortKey - b.SortKey).TotalSeconds);
            if (sec > 12) return false;
            string left = a.Body ?? "";
            string right = b.Body ?? "";
            if (left.Length == 0 || right.Length == 0) return true;
            char last = left[left.Length - 1];
            char first = right[0];
            bool cut = !(last == '.' || last == '!' || last == '?' || last == ' ');
            bool cont = char.IsLower(first) || first == ' ' || first == ',' || first == ';';
            return (cut || cont) && sec <= 12;
        }

        static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ");
        }

        static string Truncate(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            if (s.Length <= n) return s;
            return s.Substring(0, n) + "...";
        }

        static SmsItem ToItem(ISmsMessage m)
        {
            SmsItem row = new SmsItem();
            row.Id = m.Id;
            row.PartIds.Add(m.Id);

            SmsBinaryMessage bin = m as SmsBinaryMessage;
            if (bin != null)
            {
                byte[] data = null;
                try { data = bin.GetData(); } catch { }

                string from, body;
                DateTime time;
                bool hasConcat;
                int concatRef, partNum, totalParts;
                if (data != null && GsmPdu.TryParseDeliver(data, out from, out body, out time,
                        out hasConcat, out concatRef, out partNum, out totalParts))
                {
                    row.From = from;
                    row.Body = body;
                    row.HasConcat = hasConcat;
                    row.ConcatRef = concatRef;
                    row.PartNumber = partNum;
                    row.TotalParts = totalParts;
                    if (time != DateTime.MinValue)
                    {
                        row.SortKey = time;
                        row.Timestamp = time.ToString("yyyy-MM-dd HH:mm");
                    }
                    return row;
                }

                // fallback Windows decoder + sanifica @@@@@
                try
                {
                    SmsTextMessage decoded = SmsTextMessage.FromBinaryMessage(bin);
                    if (decoded != null)
                    {
                        row.From = decoded.From ?? "";
                        row.Body = GsmPdu.SanitizeWindowsBody(decoded.Body ?? "");
                        row.Timestamp = decoded.Timestamp.ToString("yyyy-MM-dd HH:mm");
                        row.SortKey = decoded.Timestamp.DateTime;
                        return row;
                    }
                }
                catch { }
                row.Body = "[PDU non decodificabile]";
                return row;
            }

            SmsTextMessage text = m as SmsTextMessage;
            if (text != null)
            {
                row.From = text.From ?? "";
                row.Body = GsmPdu.SanitizeWindowsBody(text.Body ?? "");
                row.Timestamp = text.Timestamp.ToString("yyyy-MM-dd HH:mm");
                row.SortKey = text.Timestamp.DateTime;
                return row;
            }

            row.Body = "[non supportato]";
            return row;
        }

        static T AwaitOp<T>(IAsyncOperation<T> op, int timeoutMs)
        {
            using (ManualResetEvent evt = new ManualResetEvent(false))
            {
                Exception err = null;
                T result = default(T);
                op.Completed = delegate(IAsyncOperation<T> info, AsyncStatus status)
                {
                    try
                    {
                        if (status == AsyncStatus.Completed) result = info.GetResults();
                        else err = new Exception("AsyncStatus=" + status + " ErrorCode=" + info.ErrorCode);
                    }
                    catch (Exception ex) { err = ex; }
                    evt.Set();
                };
                if (!evt.WaitOne(timeoutMs)) throw new TimeoutException("Timeout operazione SMS (" + timeoutMs + " ms)");
                if (err != null) throw err;
                return result;
            }
        }

        static T AwaitProg<T, P>(IAsyncOperationWithProgress<T, P> op, int timeoutMs)
        {
            using (ManualResetEvent evt = new ManualResetEvent(false))
            {
                Exception err = null;
                T result = default(T);
                op.Completed = delegate(IAsyncOperationWithProgress<T, P> info, AsyncStatus status)
                {
                    try
                    {
                        if (status == AsyncStatus.Completed) result = info.GetResults();
                        else err = new Exception("AsyncStatus=" + status + " ErrorCode=" + info.ErrorCode);
                    }
                    catch (Exception ex) { err = ex; }
                    evt.Set();
                };
                if (!evt.WaitOne(timeoutMs)) throw new TimeoutException("Timeout lettura SMS");
                if (err != null) throw err;
                return result;
            }
        }

        static void AwaitAction(IAsyncAction op, int timeoutMs)
        {
            // Non usare "using" sull'event: in caso di timeout il callback WinRT
            // può arrivare dopo e non deve toccare un handle disposed (freeze/crash).
            ManualResetEvent evt = new ManualResetEvent(false);
            Exception err = null;
            op.Completed = delegate(IAsyncAction info, AsyncStatus status)
            {
                try
                {
                    if (status != AsyncStatus.Completed)
                        err = new Exception("AsyncStatus=" + status + " ErrorCode=" + info.ErrorCode);
                    else
                        info.GetResults();
                }
                catch (Exception ex) { err = ex; }
                try { evt.Set(); } catch { }
            };
            if (!evt.WaitOne(timeoutMs))
            {
                // Lascia vivere evt fino al GC; non bloccare oltre il timeout
                throw new TimeoutException("Timeout eliminazione/invio");
            }
            try { evt.Close(); } catch { }
            if (err != null) throw err;
        }
    }
}
