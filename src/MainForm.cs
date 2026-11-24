// Main window: search bar + filter chips on top, rounded cards for collections / details / patients /
// series, and a collapsible downloads card. Visual language follows Linear (dark surfaces, thin borders,
// tag badges) and Airbnb (pill search, filter chips, one bold call to action).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TciaDownloader
{
    class SeriesRow
    {
        public bool Group;
        public string StudyUID, Date = "", Desc = "";
        public SeriesInfo S;
        public int Count;
        public long Bytes;
    }

    class MainForm : Form
    {
        const int MaxWorkers = 2;
        const string CacheVersion = "2";
        static readonly string[] ChipOrder = { "CT", "MR", "PT", "RTSTRUCT", "RTDOSE", "RTPLAN", "SEG" };

        readonly string[] startArgs;
        readonly string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TCIA Downloader");
        string saveRoot;

        // ---- catalog & browsing state
        List<CollectionInfo> collections = new List<CollectionInfo>();
        List<CollectionInfo> collShown = new List<CollectionInfo>();
        bool webLoaded, indexing, catalogBusy, patLoading, linkHover, suppressQuery;
        int indexDone, indexTotal, patToken, seriesToken, sortMode;
        readonly Dictionary<string, List<string>> patientCache = new Dictionary<string, List<string>>();
        readonly Dictionary<string, List<SeriesInfo>> seriesCache = new Dictionary<string, List<SeriesInfo>>();
        CollectionInfo curCollection;
        string curPatient, pendingPatient, modality;
        string collQuery = "", patientQuery = "";
        List<SeriesInfo> curSeries = new List<SeriesInfo>();
        List<SeriesRow> rows = new List<SeriesRow>();
        readonly HashSet<string> ticked = new HashSet<string>();
        readonly Dictionary<string, bool> onDisk = new Dictionary<string, bool>();
        List<string> patientsAll = new List<string>(), patientsShown = new List<string>();
        Dictionary<string, int> modCounts = new Dictionary<string, int>();
        Rectangle linkRect;
        bool queueOpen;

        // ---- download queue
        readonly List<QueueItem> queue = new List<QueueItem>();
        readonly object qlock = new object();
        readonly Dictionary<string, QueueItem> byUid = new Dictionary<string, QueueItem>();
        QueueItem[] qView = new QueueItem[0];
        CancellationTokenSource runCts = new CancellationTokenSource();
        int activeWorkers;
        long lastRecv;
        DateTime lastTick = DateTime.UtcNow;
        double speed, queueFrac;
        bool wasBusy, sleepBlocked, queueBusy;
        string queueSummary = "Nothing queued yet";
        Color queueSummaryColor;
        ITaskbarList3 taskbar;

        // ---- controls
        Card topBar, chipBar, cardColl, cardDetail, cardPat, cardSeries, cardQueue, footer;
        SearchBox search, patFilter;
        PillButton btnManifest, btnSave, btnRefresh, btnSort, btnPatAll, btnPatDownload, btnTickAll, btnTickNone,
            btnDownload, btnRetry, btnClear, btnCancelAll, btnOpenFolder, btnQueueToggle;
        ModernList listColl, listPat, listSeries, listQueue;
        readonly List<Chip> chips = new List<Chip>();
        Chip chipMore;
        ContextMenuStrip menuQueue, menuSave, menuSort, menuMore;
        ToolStripMenuItem miSavePath;
        string statusText = "";
        bool statusError;
        Bitmap logo;
        readonly ToolTip tips = new ToolTip();
        System.Windows.Forms.Timer uiTimer, collFilterTimer, patFilterTimer, patSelectTimer;

        static int Px(float v) { return Theme.Px(v); }
        static string Esc(string s) { return Uri.EscapeDataString(s ?? ""); }
        static void Try(Action a) { try { a(); } catch (Exception) { } }

        public MainForm(string[] args)
        {
            startArgs = args ?? new string[0];
            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            Theme.Init(scale);
            queueSummaryColor = Theme.Text3;

            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.Base;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Text = "TCIA Downloader";
            try { Icon = AppIcon.MakeIcon(); } catch (Exception) { }
            StartPosition = FormStartPosition.CenterScreen;
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            var want = new Size(Px(1440), Px(900));
            MinimumSize = new Size(Math.Min(Px(1080), wa.Width), Math.Min(Px(660), wa.Height));
            if (want.Width > wa.Width - 40 || want.Height > wa.Height - 40)
            {
                Size = new Size(Math.Min(want.Width, wa.Width), Math.Min(want.Height, wa.Height));
                WindowState = FormWindowState.Maximized;
            }
            else Size = want;
            AllowDrop = true;
            KeyPreview = true;
            tips.AutoPopDelay = 15000;
            tips.InitialDelay = 500;

            LoadSettings();
            SuspendLayout();
            BuildUi();
            WireEvents();
            ResumeLayout(false);
            UpdateSavePathLabel();
            ClearSeries("No patient selected", "Pick a collection, then a patient. Their scans appear here, grouped by study date.");
            UpdatePatEmpty();
            UpdatePatientButtons();
            ApplyCollectionFilter();
            LayoutAll();
        }

        protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(Theme.Bg); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Try(() =>
            {
                int dark = Theme.Dark ? 1 : 0;
                Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                int cap = ColorTranslator.ToWin32(Theme.Bg);
                Native.DwmSetWindowAttribute(Handle, 35, ref cap, 4);
                int txt = ColorTranslator.ToWin32(Theme.Text);
                Native.DwmSetWindowAttribute(Handle, 36, ref txt, 4);
                int border = ColorTranslator.ToWin32(Theme.Border);
                Native.DwmSetWindowAttribute(Handle, 34, ref border, 4);
            });
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (topBar != null && WindowState != FormWindowState.Minimized) LayoutAll();
        }

        // ============================================================ building the UI

        Card NewCard()
        {
            var c = new Card();
            c.Fill = Theme.Surface; c.Outer = Theme.Bg; c.Radius = Px(12); c.Stroke = Theme.Border;
            Controls.Add(c);
            return c;
        }

        PillButton NewButton(Card parent, string text, BtnKind kind, string icon, bool compact)
        {
            var b = new PillButton(text, kind, icon);
            b.Compact = compact;
            if (compact) b.Height = Px(30);
            b.BackColor = parent.Fill;
            parent.Controls.Add(b);
            b.Fit();
            return b;
        }

        ModernList NewList(Card parent, int rowH, int headerH)
        {
            var l = new ModernList();
            l.BackColor = parent.Fill;
            l.RowHeight = Px(rowH);
            l.HeaderHeight = headerH > 0 ? Px(headerH) : 0;
            parent.Controls.Add(l);
            return l;
        }

        static ContextMenuStrip NewMenu()
        {
            var m = new ContextMenuStrip();
            m.Renderer = new MenuRenderer();
            m.Font = Theme.Base;
            m.ShowImageMargin = false;
            m.BackColor = Theme.Surface2;
            m.ForeColor = Theme.Text;
            return m;
        }

        static ToolStripMenuItem MI(string text, Action a)
        {
            var i = new ToolStripMenuItem(text);
            i.Padding = new Padding(Px(4), Px(4), Px(4), Px(4));
            if (a != null) i.Click += (s, e) => a();
            return i;
        }

        void BuildUi()
        {
            topBar = new Card(); topBar.Fill = Theme.Bg; topBar.LineBottom = true;
            Controls.Add(topBar);
            search = new SearchBox(true, new[] { "Collections", "Patient ID" }, new[] {
                "Search collections — cancer type, body site, scan type…",
                "Type an exact patient ID, e.g. LUNG1-001, and press Enter" });
            search.BackColor = Theme.Bg;
            topBar.Controls.Add(search);
            btnManifest = NewButton(topBar, "Open manifest", BtnKind.Secondary, "file", false);
            btnSave = NewButton(topBar, "Downloads", BtnKind.Secondary, "folder", false);
            btnSave.TrailIcon = "chevron";

            chipBar = new Card(); chipBar.Fill = Theme.Bg;
            Controls.Add(chipBar);
            btnRefresh = NewButton(chipBar, "", BtnKind.Ghost, "refresh", true);
            btnRefresh.IconOnly = true; btnRefresh.Width = btnRefresh.Height;

            cardColl = NewCard();
            btnSort = NewButton(cardColl, "A–Z", BtnKind.Ghost, null, true);
            btnSort.TrailIcon = "chevron";
            listColl = NewList(cardColl, 60, 0);
            listColl.DrawRow = DrawCollRow;

            cardDetail = NewCard();

            cardPat = NewCard();
            btnPatAll = NewButton(cardPat, "Select all", BtnKind.Ghost, null, true);
            patFilter = new SearchBox(false, null, new[] { "Filter patient IDs" });
            patFilter.BackColor = Theme.Surface;
            cardPat.Controls.Add(patFilter);
            listPat = NewList(cardPat, 36, 0);
            listPat.MultiSelect = true;
            listPat.DrawRow = DrawPatRow;
            listPat.EmptyIcon = "user";
            btnPatDownload = NewButton(cardPat, "Download patient", BtnKind.Secondary, "download", false);

            cardSeries = NewCard();
            btnTickAll = NewButton(cardSeries, "Tick all", BtnKind.Ghost, null, true);
            btnTickNone = NewButton(cardSeries, "Tick none", BtnKind.Ghost, null, true);
            listSeries = NewList(cardSeries, 42, 30);
            listSeries.DrawRow = DrawSeriesRow;
            listSeries.DrawHeader = DrawSeriesHeader;
            listSeries.HitCheck = (i, p) => true;
            listSeries.IsGroup = i => i >= 0 && i < rows.Count && rows[i].Group;
            listSeries.EmptyIcon = "layers";
            btnDownload = NewButton(cardSeries, "Download", BtnKind.Primary, "download", false);
            btnDownload.Height = Px(42);

            cardQueue = NewCard();
            btnQueueToggle = NewButton(cardQueue, "", BtnKind.Ghost, "chevronUp", true);
            btnQueueToggle.IconOnly = true; btnQueueToggle.Width = btnQueueToggle.Height;
            btnRetry = NewButton(cardQueue, "Retry failed", BtnKind.Ghost, "refresh", true);
            btnClear = NewButton(cardQueue, "Clear finished", BtnKind.Ghost, "check", true);
            btnCancelAll = NewButton(cardQueue, "Cancel all", BtnKind.Ghost, "close", true);
            btnOpenFolder = NewButton(cardQueue, "Open folder", BtnKind.Secondary, "folder", true);
            listQueue = NewList(cardQueue, 40, 30);
            listQueue.MultiSelect = true;
            listQueue.DrawRow = DrawQueueRow;
            listQueue.DrawHeader = DrawQueueHeader;
            listQueue.EmptyIcon = "inbox";
            listQueue.EmptyTitle = "No downloads yet";
            listQueue.EmptyText = "Tick series and press Download. Files go to your Downloads folder.";

            footer = new Card(); footer.Fill = Theme.Bg;
            Controls.Add(footer);

            topBar.Paint += PaintTopBar;
            chipBar.Paint += PaintChipBar;
            cardColl.Paint += PaintCollCard;
            cardDetail.Paint += PaintDetail;
            cardPat.Paint += PaintPatCard;
            cardSeries.Paint += PaintSeriesCard;
            cardQueue.Paint += PaintQueueCard;
            footer.Paint += PaintFooter;

            // menus
            menuSort = NewMenu();
            menuSort.ShowCheckMargin = true;
            string[] sortNames = { "Name (A–Z)", "Most subjects", "Cancer type" };
            string[] sortShort = { "A–Z", "Subjects", "Cancer type" };
            for (int k = 0; k < 3; k++)
            {
                int kk = k;
                var it = MI(sortNames[k], null);
                it.Checked = k == 0;
                it.Click += (s, e) =>
                {
                    sortMode = kk;
                    btnSort.Text = sortShort[kk];
                    foreach (ToolStripMenuItem x in menuSort.Items) x.Checked = x == it;
                    LayoutCards();
                    ApplyCollectionFilter();
                };
                menuSort.Items.Add(it);
            }

            menuSave = NewMenu();
            miSavePath = MI("", null);
            miSavePath.Enabled = false;
            menuSave.Items.AddRange(new ToolStripItem[] {
                MI("Open download folder", () => OpenFolder(saveRoot)),
                MI("Change folder…", ChangeFolder),
                new ToolStripSeparator(), miSavePath });
            menuSave.Opening += (s, e) => miSavePath.Text = saveRoot;

            menuMore = NewMenu();
            menuMore.ShowCheckMargin = true;

            menuQueue = NewMenu();
            var miOpen = MI("Open folder", () => { var l = SelectedQueueItems(); if (l.Count > 0) OpenFolder(l[0].Folder); });
            var miRetry = MI("Retry", () => RetryItems(SelectedQueueItems()));
            var miCancel = MI("Cancel", () => CancelItems(SelectedQueueItems()));
            var miRemove = MI("Remove from list", () => RemoveItems(SelectedQueueItems()));
            var miCopy = MI("Copy series UID", () =>
            {
                var l = SelectedQueueItems();
                if (l.Count > 0) Clipboard.SetText(string.Join("\r\n", l.Select(q => q.S.SeriesUID)));
            });
            menuQueue.Items.AddRange(new ToolStripItem[] { miOpen, miRetry, miCancel, miRemove, new ToolStripSeparator(), miCopy });
            menuQueue.Opening += (s, e) =>
            {
                var l = SelectedQueueItems();
                if (l.Count == 0) { e.Cancel = true; return; }
                miRetry.Enabled = l.Any(q => q.State == QState.Failed || q.State == QState.Canceled);
                miCancel.Enabled = l.Any(q => q.State == QState.Pending || q.State == QState.Active);
                miRemove.Enabled = l.Any(q => q.State != QState.Active);
            };
        }

        void WireEvents()
        {
            uiTimer = new System.Windows.Forms.Timer(); uiTimer.Interval = 400;
            uiTimer.Tick += (s, e) => UiTick();
            collFilterTimer = new System.Windows.Forms.Timer(); collFilterTimer.Interval = 160;
            collFilterTimer.Tick += (s, e) => { collFilterTimer.Stop(); ApplyCollectionFilter(); listColl.ScrollToTop(); };
            patFilterTimer = new System.Windows.Forms.Timer(); patFilterTimer.Interval = 160;
            patFilterTimer.Tick += (s, e) => { patFilterTimer.Stop(); ApplyPatientFilter(); };
            patSelectTimer = new System.Windows.Forms.Timer(); patSelectTimer.Interval = 200;
            patSelectTimer.Tick += (s, e) => { patSelectTimer.Stop(); OnPatientSelectionSettled(); };

            search.QueryChanged += (s, e) =>
            {
                if (suppressQuery) return;
                if (search.Mode == 0) { collQuery = search.Query; collFilterTimer.Stop(); collFilterTimer.Start(); }
                else patientQuery = search.Query;
            };
            search.ModeChanged += (s, e) =>
            {
                suppressQuery = true;
                search.Query = search.Mode == 0 ? collQuery : patientQuery;
                suppressQuery = false;
            };
            search.Submitted += (s, e) =>
            {
                if (search.Mode == 1) FindPatient(search.Query);
                else if (collShown.Count > 0) { listColl.SelectOnly(0); listColl.EnsureVisible(0); }
            };
            btnManifest.Click += (s, e) => PickManifest();
            btnSave.Click += (s, e) => menuSave.Show(btnSave, new Point(0, btnSave.Height + Px(4)));
            btnRefresh.Click += async (s, e) => await LoadCatalog(true);
            btnSort.Click += (s, e) => menuSort.Show(btnSort, new Point(0, btnSort.Height + Px(4)));

            listColl.SelectionChanged += (s, e) => OnCollectionSelected();
            listColl.HoverChanged += i =>
            {
                string t = "";
                if (i >= 0 && i < collShown.Count)
                {
                    var c = collShown[i];
                    t = Util.JoinNonEmpty("\r\n", c.Title != "" && Util.Norm(c.Title) != Util.Norm(c.Name) ? c.Title : c.Name,
                        c.ModalitiesLoaded ? string.Join(", ", c.Modalities) : "");
                }
                tips.SetToolTip(listColl, t);
            };

            cardDetail.MouseMove += (s, e) =>
            {
                bool h = !linkRect.IsEmpty && linkRect.Contains(e.Location);
                if (h != linkHover) { linkHover = h; cardDetail.Cursor = h ? Cursors.Hand : Cursors.Default; cardDetail.Invalidate(); }
            };
            cardDetail.MouseLeave += (s, e) => { if (linkHover) { linkHover = false; cardDetail.Invalidate(); } };
            cardDetail.MouseClick += (s, e) =>
            {
                if (curCollection == null || linkRect.IsEmpty || !linkRect.Contains(e.Location) || curCollection.Link == "") return;
                try { Process.Start(curCollection.Link); } catch (Exception ex) { SetStatus("Couldn't open the browser: " + ex.Message, true); }
            };

            patFilter.QueryChanged += (s, e) => { patFilterTimer.Stop(); patFilterTimer.Start(); };
            listPat.SelectionChanged += (s, e) => { UpdatePatientButtons(); patSelectTimer.Stop(); patSelectTimer.Start(); };
            btnPatAll.Click += (s, e) => { listPat.SelectAll(); listPat.Focus(); };
            btnPatDownload.Click += (s, e) => DownloadSelectedPatients();

            listSeries.CheckClicked += i => ToggleSeriesRow(i);
            btnTickAll.Click += (s, e) => SetAllTicks(true);
            btnTickNone.Click += (s, e) => SetAllTicks(false);
            btnDownload.Click += (s, e) => DownloadTicked();

            btnQueueToggle.Click += (s, e) => { queueOpen = !queueOpen; LayoutAll(); };
            cardQueue.MouseClick += (s, e) => { if (e.Y < Px(52)) { queueOpen = !queueOpen; LayoutAll(); } };
            btnRetry.Click += (s, e) => RetryItems(null);
            btnClear.Click += (s, e) => ClearFinished();
            btnCancelAll.Click += (s, e) => CancelAll(true);
            btnOpenFolder.Click += (s, e) => OpenFolder(saveRoot);
            listQueue.RowActivated += i => { if (i >= 0 && i < qView.Length) OpenFolder(qView[i].Folder); };
            listQueue.RowRightClick += (i, p) => { if (i >= 0) menuQueue.Show(listQueue, p); };
            listQueue.HoverChanged += i => UpdateQueueTip();

            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null) foreach (var f in files) LoadManifest(f);
            };

            tips.SetToolTip(btnManifest, "Open a .tcia manifest from the TCIA website and download every series in it (Ctrl+O). You can also drag one onto the window.");
            tips.SetToolTip(btnSave, "Where downloaded DICOM files are saved");
            tips.SetToolTip(btnRefresh, "Download the latest list of collections from TCIA");
            tips.SetToolTip(btnPatDownload, "Download every series for the selected patient(s). The scan-type chip above limits it to one kind of scan.");
            tips.SetToolTip(btnQueueToggle, "Show or hide the download list");
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.F) { search.Box.Focus(); search.Box.SelectAll(); e.Handled = e.SuppressKeyPress = true; return; }
            if (e.Control && e.KeyCode == Keys.O) { PickManifest(); e.Handled = e.SuppressKeyPress = true; return; }
            base.OnKeyDown(e);
        }

        // ============================================================ layout

        void LayoutAll()
        {
            if (topBar == null) return;
            int W = ClientSize.Width, H = ClientSize.Height;
            if (W <= 0 || H <= 0) return;
            int m = Px(16), gap = Px(12), topH = Px(62), chipH = Px(52), footH = Px(28);

            topBar.SetBounds(0, 0, W, topH);
            btnSave.Fit(); btnManifest.Fit();
            int by = (topH - btnSave.Height) / 2;
            int bx = W - m - btnSave.Width;
            btnSave.Location = new Point(bx, by);
            bx -= Px(8) + btnManifest.Width;
            btnManifest.Location = new Point(bx, by);
            int brandRight = m + Px(196);
            int sw = Math.Max(Px(260), Math.Min(Px(660), bx - Px(24) - brandRight));
            int sx = Math.Max(brandRight, (W - sw) / 2);
            if (sx + sw > bx - Px(24)) sx = Math.Max(brandRight, bx - Px(24) - sw);
            search.SetBounds(sx, (topH - Px(42)) / 2, sw, Px(42));

            chipBar.SetBounds(0, topH, W, chipH);
            btnRefresh.Location = new Point(W - m - btnRefresh.Width, (chipH - btnRefresh.Height) / 2);
            LayoutChips();

            footer.SetBounds(0, H - footH, W, footH);
            int body = H - topH - chipH - footH;
            int qh = queueOpen ? Math.Max(Px(150), Math.Min(Px(214), (int)(body * 0.29))) : Px(52);
            cardQueue.SetBounds(m, footer.Top - qh, W - 2 * m, qh);

            int top = chipBar.Bottom, bottom = cardQueue.Top - gap;
            int collW = Math.Max(Px(320), Math.Min(Px(420), (int)(W * 0.27)));
            cardColl.SetBounds(m, top, collW, Math.Max(Px(120), bottom - top));
            int rx = cardColl.Right + gap, rw = W - m - rx;
            bool compact = bottom - top < Px(560);   // short screens: details card shows title + tags only
            cardDetail.SetBounds(rx, top, rw, compact ? Px(88) : Px(128));
            int y2 = cardDetail.Bottom + gap;
            int patW = Math.Max(Px(210), Math.Min(Px(260), (int)(rw * 0.26)));
            cardPat.SetBounds(rx, y2, patW, Math.Max(Px(120), bottom - y2));
            cardSeries.SetBounds(rx + patW + gap, y2, rw - patW - gap, Math.Max(Px(120), bottom - y2));
            LayoutCards();
            Invalidate();
        }

        void LayoutCards()
        {
            int head = Px(52);
            btnSort.Fit();
            btnSort.Location = new Point(cardColl.Width - Px(10) - btnSort.Width, (head - btnSort.Height) / 2);
            listColl.SetBounds(Px(4), head, cardColl.Width - Px(8), cardColl.Height - head - Px(6));

            btnPatAll.Fit();
            btnPatAll.Location = new Point(cardPat.Width - Px(10) - btnPatAll.Width, (head - btnPatAll.Height) / 2);
            patFilter.SetBounds(Px(12), head - Px(2), cardPat.Width - Px(24), Px(34));
            int patFoot = Px(60);
            listPat.SetBounds(Px(4), patFilter.Bottom + Px(8), cardPat.Width - Px(8), Math.Max(0, cardPat.Height - patFilter.Bottom - Px(8) - patFoot));
            btnPatDownload.SetBounds(Px(12), cardPat.Height - Px(12) - Px(36), cardPat.Width - Px(24), Px(36));

            btnTickNone.Fit(); btnTickAll.Fit();
            btnTickNone.Location = new Point(cardSeries.Width - Px(10) - btnTickNone.Width, (head - btnTickNone.Height) / 2);
            btnTickAll.Location = new Point(btnTickNone.Left - Px(2) - btnTickAll.Width, btnTickNone.Top);
            listSeries.SetBounds(Px(4), head, cardSeries.Width - Px(8), Math.Max(0, cardSeries.Height - head - Px(68)));
            PlaceDownloadButton();

            btnQueueToggle.Icon = queueOpen ? "chevron" : "chevronUp";
            btnQueueToggle.Location = new Point(Px(10), (head - btnQueueToggle.Height) / 2);
            int x = cardQueue.Width - Px(10);
            foreach (var b in new[] { btnOpenFolder, btnCancelAll, btnClear, btnRetry })
            {
                b.Fit();
                x -= b.Width;
                b.Location = new Point(x, (head - b.Height) / 2);
                x -= Px(4);
            }
            listQueue.Visible = queueOpen;
            listQueue.SetBounds(Px(4), head + Px(4), cardQueue.Width - Px(8), Math.Max(0, cardQueue.Height - head - Px(10)));
            foreach (Control c in new Control[] { cardColl, cardPat, cardSeries, cardQueue, cardDetail, btnQueueToggle }) c.Invalidate();
        }

        void PlaceDownloadButton()
        {
            btnDownload.Fit();
            btnDownload.Width = Math.Max(btnDownload.Width, Px(190));
            btnDownload.Location = new Point(cardSeries.Width - Px(14) - btnDownload.Width, cardSeries.Height - Px(13) - btnDownload.Height);
            cardSeries.Invalidate();
        }

        void LayoutChips()
        {
            int x = Px(16), h = Px(32), y = (Px(52) - h) / 2;
            int limit = btnRefresh.Left - Px(250);
            for (int i = 0; i < chips.Count; i++)
            {
                var c = chips[i];
                c.Height = h;
                c.Width = c.Natural();
                c.Location = new Point(x, y);
                c.Visible = i == 0 || x + c.Width < limit;
                x += c.Width + Px(8);
            }
            chipBar.Invalidate();
        }

        // ============================================================ painting: chrome

        void PaintTopBar(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int sz = Px(28);
            if (logo == null) logo = AppIcon.Render(sz);
            g.DrawImage(logo, Px(16), (topBar.Height - sz) / 2, sz, sz);
            Gfx.Text(g, "TCIA Downloader", Theme.Brand, new Rectangle(Px(16) + sz + Px(10), 0, Px(170), topBar.Height), Theme.Text);
        }

        void PaintChipBar(object sender, PaintEventArgs e)
        {
            string t;
            if (collections.Count == 0) t = catalogBusy ? "Loading catalog…" : "";
            else
            {
                t = collections.Count + " public collections";
                if (indexing) t += "  ·  reading scan types " + indexDone + "/" + indexTotal;
            }
            int right = btnRefresh.Left - Px(6);
            Gfx.TextRight(e.Graphics, t, Theme.Small, new Rectangle(right - Px(300), 0, Px(300), chipBar.Height), Theme.Text3);
        }

        void PaintFooter(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int m = Px(18);
            string note = "Public collections only  ·  data from cancerimagingarchive.net  ·  Ctrl+F search  ·  Ctrl+O manifest";
            int nw = Gfx.W(note, Theme.Small);
            Gfx.TextRight(g, note, Theme.Small, new Rectangle(footer.Width - m - nw - 2, 0, nw + 2, footer.Height), Theme.Text3);
            Gfx.Text(g, statusText, Theme.Small, new Rectangle(m, 0, Math.Max(0, footer.Width - nw - 3 * m), footer.Height), statusError ? Theme.Danger : Theme.Text2);
        }

        void SetStatus(string msg, bool error = false)
        {
            statusText = msg ?? "";
            statusError = error;
            footer.Invalidate();
        }

        void PaintCardTitle(Graphics g, string title, string count, int h)
        {
            int x = Px(16);
            int tw = Gfx.W(title, Theme.Title);
            Gfx.Text(g, title, Theme.Title, new Rectangle(x, 0, tw + 2, h), Theme.Text);
            if (!string.IsNullOrEmpty(count)) Gfx.Pill(g, count, x + tw + Px(8), h / 2, Theme.Surface2, Theme.Text2, Theme.Small, Px(20));
        }

        void PaintCollCard(object sender, PaintEventArgs e)
        {
            string count = collections.Count == 0 ? "" : collShown.Count == collections.Count
                ? collections.Count.ToString() : collShown.Count + " of " + collections.Count;
            PaintCardTitle(e.Graphics, "Collections", count, Px(52));
        }

        void PaintPatCard(object sender, PaintEventArgs e)
        {
            string count = curCollection == null ? "" : patLoading ? "…"
                : patientsShown.Count == patientsAll.Count ? patientsAll.Count.ToString("N0")
                : patientsShown.Count.ToString("N0") + " of " + patientsAll.Count.ToString("N0");
            PaintCardTitle(e.Graphics, "Patients", count, Px(52));
        }

        void PaintSeriesCard(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int head = Px(52);
            int tw = Gfx.W("Series", Theme.Title);
            Gfx.Text(g, "Series", Theme.Title, new Rectangle(Px(16), 0, tw + 2, head), Theme.Text);
            if (curPatient != null)
            {
                int maxW = btnTickAll.Left - Px(16) - (Px(16) + tw + Px(10));
                if (maxW > Px(60))
                    Gfx.Pill(g, curPatient, Px(16) + tw + Px(10), head / 2, Theme.BadgeBg(Theme.Accent, Theme.Surface), Theme.AccentText, Theme.SmallBold, Px(22), "user");
            }
            int fy = cardSeries.Height - Px(68);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, Px(1), fy, cardSeries.Width - Px(2), fy);
            if (curSeries.Count > 0)
            {
                int n, imgs, saved; long bytes;
                Summary(out n, out imgs, out bytes, out saved);
                int w = btnDownload.Left - Px(30);
                string l1 = n + " of " + curSeries.Count + " series ticked" + (n > 0 ? "  ·  " + imgs.ToString("N0") + " images" : "");
                Gfx.Text(g, l1, Theme.Base, new Rectangle(Px(18), fy + Px(14), w, Px(20)), Theme.Text2);
                if (saved > 0)
                    Gfx.Text(g, "✓  " + saved + " already saved on this computer", Theme.Small, new Rectangle(Px(18), fy + Px(36), w, Px(18)), Theme.Success);
            }
        }

        void PaintDetail(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int W = cardDetail.Width, pad = Px(22);
            bool compact = cardDetail.Height < Px(110);
            linkRect = Rectangle.Empty;
            var c = curCollection;
            if (c == null)
            {
                Gfx.Text(g, "Find the scans you need", Theme.Hero, new Rectangle(pad, Px(14), W - 2 * pad, Px(34)), Theme.Text);
                string intro = "Search " + (collections.Count > 0 ? collections.Count + " " : "") +
                    "public collections from The Cancer Imaging Archive by cancer type, body site or scan type. Pick one to browse its patients, " +
                    "tick the series you want, then download. Files are saved as standard .dcm files that open in any DICOM viewer.";
                if (compact) { Gfx.Text(g, intro, Theme.Small, new Rectangle(pad, Px(52), W - 2 * pad, Px(20)), Theme.Text2); return; }
                Gfx.Wrap(g, intro, Theme.Base, new Rectangle(pad, Px(56), W - 2 * pad, Px(42)), Theme.Text2);
                Gfx.Text(g, "Limited-access collections (for example HNSCC) need a TCIA account and aren't listed.", Theme.Small,
                    new Rectangle(pad, Px(100), W - 2 * pad, Px(18)), Theme.Text3);
                return;
            }
            if (c.Link != "")
            {
                string lt = "Open on TCIA";
                int lw = Gfx.W(lt, Theme.Small) + Px(20);
                linkRect = new Rectangle(W - pad - lw, Px(16), lw, Px(26));
                Color lc = linkHover ? Theme.Text : Theme.AccentText;
                Gfx.Text(g, lt, Theme.Small, new Rectangle(linkRect.X, linkRect.Y, lw - Px(17), linkRect.Height), lc);
                Icons.Draw(g, "external", new RectangleF(linkRect.Right - Px(13), linkRect.Y + (linkRect.Height - Px(12)) / 2f, Px(12), Px(12)), lc);
            }
            int right = linkRect.IsEmpty ? W - pad : linkRect.Left - Px(16);
            Gfx.Text(g, c.Name, Theme.Hero, new Rectangle(pad, Px(12), right - pad, Px(36)), Theme.Text);

            int x = pad, mid = Px(62);
            var facts = new List<string>();
            if (c.CancerTypes != "") facts.Add(c.CancerTypes);
            if (c.Locations != "" && c.Locations != c.CancerTypes) facts.Add(c.Locations);
            if (c.SubjectsText != "") facts.Add(c.SubjectsText + " subjects");
            foreach (var f in facts)
            {
                string t = f.Length > 56 ? f.Substring(0, 53) + "…" : f;
                if (x + Gfx.W(t, Theme.Small) + Px(30) > W - pad) break;
                x += Gfx.Pill(g, t, x, mid, Theme.Surface2, Theme.Text2, Theme.Small, Px(22)) + Px(6);
            }
            if (c.ModalitiesLoaded)
                foreach (var m in c.Modalities)
                {
                    if (x + Gfx.W(m, Theme.Badge) + Px(20) > W - pad) break;
                    x += Gfx.Badge(g, m, x, mid, Theme.ModColor(m), Theme.Surface, false) + Px(4);
                }
            if (compact) return;
            string desc = c.Abstract.Replace("\r\n", " ");
            if (c.Title != "" && Util.Norm(c.Title) != Util.Norm(c.Name)) desc = c.Title + (desc != "" ? "  —  " + desc : "");
            if (desc == "") desc = "No description available for this collection.";
            Gfx.Wrap(g, desc, Theme.Small, new Rectangle(pad, Px(82), W - 2 * pad, Px(36)), Theme.Text2);
        }

        void PaintQueueCard(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            int head = Px(52);
            int x = btnQueueToggle.Right + Px(6);
            int tw = Gfx.W("Downloads", Theme.Title);
            Gfx.Text(g, "Downloads", Theme.Title, new Rectangle(x, 0, tw + 2, head), Theme.Text);
            x += tw + Px(8);
            if (qView.Length > 0) x += Gfx.Pill(g, qView.Length.ToString(), x, head / 2, Theme.Surface2, Theme.Text2, Theme.Small, Px(20)) + Px(12);
            else x += Px(6);
            Gfx.Text(g, queueSummary, Theme.Base, new Rectangle(x, 0, Math.Max(0, btnRetry.Left - Px(12) - x), head), queueSummaryColor);
            if (queueBusy)
                Gfx.Bar(g, new Rectangle(Px(16), head - Px(3), cardQueue.Width - Px(32), Px(3)), queueFrac, Theme.Accent, Theme.Surface3);
            else if (queueOpen)
                using (var p = new Pen(Theme.Border)) g.DrawLine(p, Px(1), head, cardQueue.Width - Px(2), head);
        }

        // ============================================================ painting: rows

        Rectangle RowBox(Rectangle r) { return new Rectangle(r.X + Px(6), r.Y + Px(2), r.Width - Px(12), r.Height - Px(4)); }

        void DrawCollRow(Graphics g, Rectangle r, int i, RowState st)
        {
            if (i >= collShown.Count) return;
            var c = collShown[i];
            var box = RowBox(r);
            Color bg = st.Selected ? Theme.Surface3 : st.Hover ? Theme.Surface2 : Theme.Surface;
            if (bg != Theme.Surface) Gfx.Fill(g, bg, box, Px(9));
            if (st.Selected) Gfx.Fill(g, Theme.Accent, new Rectangle(box.X + Px(1), box.Y + Px(12), Px(3), box.Height - Px(24)), Px(2));
            int pad = Px(14), x = box.X + pad, right = box.Right - pad;
            int lh = Px(20), y1 = box.Y + Px(9), y2 = y1 + lh + Px(2);

            string subj = c.SubjectsText;
            int sw = 0;
            if (subj != "")
            {
                sw = Gfx.W(subj, Theme.Small);
                Gfx.TextRight(g, subj, Theme.Small, new Rectangle(right - sw - 2, y1, sw + 2, lh), Theme.Text3);
                Icons.Draw(g, "user", new RectangleF(right - sw - Px(16), y1 + (lh - Px(12)) / 2f, Px(12), Px(12)), Theme.Text3);
                sw += Px(22);
            }
            Gfx.Text(g, c.Name, Theme.Semi, new Rectangle(x, y1, right - x - sw - Px(6), lh), Theme.Text);

            int bx = right, mid = y2 + lh / 2;
            if (c.ModalitiesLoaded)
            {
                var mods = c.Modalities.Take(3).ToList();
                if (c.Modalities.Count > 3) bx -= Gfx.Badge(g, "+" + (c.Modalities.Count - 3), bx, mid, Theme.Text3, bg, true) + Px(4);
                for (int k = mods.Count - 1; k >= 0; k--) bx -= Gfx.Badge(g, mods[k], bx, mid, Theme.ModColor(mods[k]), bg, true) + Px(4);
            }
            Gfx.Text(g, c.TypeText == "" ? "—" : c.TypeText, Theme.Small, new Rectangle(x, y2, bx - x - Px(6), lh), Theme.Text2);
        }

        void DrawPatRow(Graphics g, Rectangle r, int i, RowState st)
        {
            if (i >= patientsShown.Count) return;
            var box = RowBox(r);
            Color bg = st.Selected ? Theme.Surface3 : st.Hover ? Theme.Surface2 : Theme.Surface;
            if (bg != Theme.Surface) Gfx.Fill(g, bg, box, Px(8));
            Gfx.Text(g, patientsShown[i], st.Selected ? Theme.Semi : Theme.Base, new Rectangle(box.X + Px(12), box.Y, box.Width - Px(40), box.Height),
                st.Selected ? Theme.Text : Theme.Text2);
            if (st.Selected && listPat.Selected.Count > 1)
                Icons.Draw(g, "check", new RectangleF(box.Right - Px(24), box.Y + (box.Height - Px(14)) / 2f, Px(14), Px(14)), Theme.AccentText);
        }

        void SeriesColumns(int width, out int xCheck, out int xBadge, out int xDesc, out int xNum, out int xImg, out int xSize, out int xStatus, out int right)
        {
            right = width - Px(18);
            xStatus = right - Px(96);
            xSize = xStatus - Px(12) - Px(62);
            xImg = xSize - Px(60);
            xNum = xImg - Px(44);
            xCheck = Px(18);
            xBadge = Px(46);
            xDesc = xBadge + Px(88);
            if (xNum - xDesc < Px(80)) xNum = xImg;   // narrow: hide the series-number column
        }

        void DrawSeriesHeader(Graphics g, Rectangle r)
        {
            int xc, xb, xd, xn, xi, xs, xst, right;
            SeriesColumns(r.Width, out xc, out xb, out xd, out xn, out xi, out xs, out xst, out right);
            Color c = Theme.Text3;
            var f = Theme.Mono;
            Gfx.Text(g, "SCAN", f, new Rectangle(xb, 0, Px(80), r.Height), c);
            Gfx.Text(g, "DESCRIPTION", f, new Rectangle(xd, 0, Px(120), r.Height), c);
            if (xn < xi) Gfx.TextRight(g, "NO.", f, new Rectangle(xn, 0, xi - xn - Px(10), r.Height), c);
            Gfx.TextRight(g, "IMAGES", f, new Rectangle(xi, 0, xs - xi - Px(10), r.Height), c);
            Gfx.TextRight(g, "SIZE", f, new Rectangle(xs, 0, xst - xs - Px(14), r.Height), c);
            Gfx.Text(g, "STATUS", f, new Rectangle(xst, 0, right - xst, r.Height), c);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, Px(10), r.Bottom - 1, r.Right - Px(10), r.Bottom - 1);
        }

        int GroupTickState(string study)
        {
            int n = 0, t = 0;
            foreach (var row in rows)
                if (!row.Group && row.StudyUID == study) { n++; if (ticked.Contains(row.S.SeriesUID)) t++; }
            return t == 0 ? 0 : t == n ? 1 : 2;
        }

        void SeriesStatus(SeriesInfo s, out string text, out Color color)
        {
            QueueItem q;
            if (byUid.TryGetValue(s.SeriesUID, out q))
            {
                switch (q.State)
                {
                    case QState.Pending: text = "In queue"; color = Theme.Accent; return;
                    case QState.Active: text = "Downloading"; color = Theme.Accent; return;
                    case QState.Failed: text = "Failed"; color = Theme.Danger; return;
                    case QState.Done: case QState.Skipped: text = "Saved"; color = Theme.Success; return;
                }
            }
            bool d;
            if (onDisk.TryGetValue(s.SeriesUID, out d) && d) { text = "Saved"; color = Theme.Success; return; }
            text = ""; color = Theme.Text3;
        }

        void DrawSeriesRow(Graphics g, Rectangle r, int i, RowState st)
        {
            if (i >= rows.Count) return;
            var row = rows[i];
            var box = RowBox(r);
            int mid = r.Y + r.Height / 2, cs = Px(16);
            if (row.Group)
            {
                Color gb = st.Hover ? Theme.Surface3 : Theme.Surface2;
                Gfx.Fill(g, gb, box, Px(9));
                var cb = new Rectangle(r.X + Px(18), mid - cs / 2, cs, cs);
                Gfx.Check(g, cb, GroupTickState(row.StudyUID), st.Hover, gb);
                int x = cb.Right + Px(12);
                string date = row.Date == "" ? "Unknown date" : row.Date;
                int dw = Gfx.W(date, Theme.Semi);
                Gfx.Text(g, date, Theme.Semi, new Rectangle(x, box.Y, dw + 2, box.Height), Theme.Text);
                string info = row.Count + " series" + (row.Bytes > 0 ? "  ·  " + Util.Size(row.Bytes) : "");
                int iw = Gfx.W(info, Theme.Small);
                Gfx.TextRight(g, info, Theme.Small, new Rectangle(box.Right - Px(14) - iw - 2, box.Y, iw + 2, box.Height), Theme.Text3);
                if (row.Desc != "")
                {
                    int dx = x + dw + Px(10);
                    Gfx.Text(g, row.Desc, Theme.Base, new Rectangle(dx, box.Y, box.Right - Px(28) - iw - dx, box.Height), Theme.Text2);
                }
                return;
            }
            var s = row.S;
            Color bg = st.Hover ? Theme.Surface2 : Theme.Surface;
            if (st.Hover) Gfx.Fill(g, bg, box, Px(9));
            if (st.Focus) Gfx.Stroke(g, Theme.BorderStrong, box, Px(9), 1);
            int xc, xb, xd, xn, xi, xs, xst, right;
            SeriesColumns(r.Width, out xc, out xb, out xd, out xn, out xi, out xs, out xst, out right);
            Gfx.Check(g, new Rectangle(r.X + xc, mid - cs / 2, cs, cs), ticked.Contains(s.SeriesUID) ? 1 : 0, st.Hover, bg);
            Gfx.Badge(g, s.Modality == "" ? "?" : s.Modality, r.X + xb, mid, Theme.ModColor(s.Modality), bg, false);
            Gfx.Text(g, Util.SeriesLabel(s), Theme.Base, new Rectangle(r.X + xd, r.Y, xn - xd - Px(10), r.Height), s.SeriesDesc == "" ? Theme.Text3 : Theme.Text);
            if (xn < xi) Gfx.TextRight(g, s.SeriesNumber, Theme.Small, new Rectangle(r.X + xn, r.Y, xi - xn - Px(10), r.Height), Theme.Text3);
            Gfx.TextRight(g, s.ImageCount > 0 ? s.ImageCount.ToString("N0") : "", Theme.Small, new Rectangle(r.X + xi, r.Y, xs - xi - Px(10), r.Height), Theme.Text2);
            Gfx.TextRight(g, Util.Size(s.FileSize), Theme.Small, new Rectangle(r.X + xs, r.Y, xst - xs - Px(14), r.Height), Theme.Text2);
            string stt; Color sc;
            SeriesStatus(s, out stt, out sc);
            if (stt != "")
                Gfx.Pill(g, stt, r.X + xst, mid, Theme.BadgeBg(sc, bg), Theme.BadgeFg(sc), Theme.Badge, Px(20),
                    sc == Theme.Success ? "check" : sc == Theme.Danger ? "alert" : "dot");
        }

        void QueueColumns(int width, out int xPat, out int xMod, out int xSeries, out int xSize, out int xStatus, out int right)
        {
            right = width - Px(18);
            xPat = Px(18);
            xMod = xPat + Px(150);
            xSeries = xMod + Px(96);
            xStatus = right - Math.Min(Px(320), Math.Max(Px(220), width / 4));
            xSize = xStatus - Px(14) - Px(66);
        }

        void DrawQueueHeader(Graphics g, Rectangle r)
        {
            int xp, xm, xse, xsz, xst, right;
            QueueColumns(r.Width, out xp, out xm, out xse, out xsz, out xst, out right);
            var f = Theme.Mono; Color c = Theme.Text3;
            Gfx.Text(g, "PATIENT", f, new Rectangle(xp, 0, Px(120), r.Height), c);
            Gfx.Text(g, "SCAN", f, new Rectangle(xm, 0, Px(80), r.Height), c);
            Gfx.Text(g, "SERIES", f, new Rectangle(xse, 0, Px(100), r.Height), c);
            Gfx.TextRight(g, "SIZE", f, new Rectangle(xsz, 0, xst - xsz - Px(14), r.Height), c);
            Gfx.Text(g, "PROGRESS", f, new Rectangle(xst, 0, right - xst, r.Height), c);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, Px(10), r.Bottom - 1, r.Right - Px(10), r.Bottom - 1);
        }

        void DrawQueueRow(Graphics g, Rectangle r, int i, RowState st)
        {
            if (i >= qView.Length) return;
            var q = qView[i];
            var box = RowBox(r);
            Color bg = st.Selected ? Theme.Surface3 : st.Hover ? Theme.Surface2 : Theme.Surface;
            if (bg != Theme.Surface) Gfx.Fill(g, bg, box, Px(9));
            int xp, xm, xse, xsz, xst, right;
            QueueColumns(r.Width, out xp, out xm, out xse, out xsz, out xst, out right);
            int mid = r.Y + r.Height / 2;
            Gfx.Text(g, q.S.PatientID, Theme.Base, new Rectangle(r.X + xp, r.Y, xm - xp - Px(10), r.Height), Theme.Text2);
            if (q.S.Modality != "") Gfx.Badge(g, q.S.Modality, r.X + xm, mid, Theme.ModColor(q.S.Modality), bg, false);
            Gfx.Text(g, Util.SeriesLabel(q.S), Theme.Base, new Rectangle(r.X + xse, r.Y, xsz - xse - Px(10), r.Height), Theme.Text);
            Gfx.TextRight(g, Util.Size(q.S.FileSize), Theme.Small, new Rectangle(r.X + xsz, r.Y, xst - xsz - Px(14), r.Height), Theme.Text3);

            var cell = new Rectangle(r.X + xst, r.Y, right - xst, r.Height);
            int isz = Px(13);
            string status = q.Status;
            switch (q.State)
            {
                case QState.Active:
                    {
                        long rec = Interlocked.Read(ref q.Received);
                        bool unpack = status == Downloader.StUnpacking;
                        bool dl = status == Downloader.StDownloading;
                        double frac = unpack ? 1 : Math.Min(0.97, rec / Math.Max(1.0, q.S.FileSize * Downloader.ZipRatio));
                        int bw = Math.Min(Px(110), cell.Width / 2);
                        if (dl || unpack) Gfx.Bar(g, new Rectangle(cell.X, mid - Px(2), bw, Px(4)), frac, Theme.Accent, Theme.Surface3);
                        string t = unpack ? "Unpacking…" : dl ? (q.S.FileSize > 0 ? (int)(frac * 100) + "%  ·  " : "") + Util.Size(rec) : status;
                        int tx = dl || unpack ? cell.X + bw + Px(10) : cell.X;
                        Gfx.Text(g, t, Theme.Small, new Rectangle(tx, r.Y, cell.Right - tx, r.Height), Theme.AccentText);
                        break;
                    }
                case QState.Pending:
                    Gfx.Text(g, "Waiting", Theme.Small, cell, Theme.Text3);
                    break;
                case QState.Done:
                case QState.Skipped:
                    Icons.Draw(g, "check", new RectangleF(cell.X, mid - isz / 2f, isz, isz), Theme.Success);
                    Gfx.Text(g, q.State == QState.Skipped ? "Already on your computer" : status, Theme.Small,
                        new Rectangle(cell.X + isz + Px(6), r.Y, cell.Width - isz - Px(6), r.Height), Theme.Success);
                    break;
                case QState.Failed:
                    Icons.Draw(g, "alert", new RectangleF(cell.X, mid - isz / 2f, isz, isz), Theme.Danger);
                    Gfx.Text(g, status.StartsWith("Failed: ") ? status.Substring(8) : status, Theme.Small,
                        new Rectangle(cell.X + isz + Px(6), r.Y, cell.Width - isz - Px(6), r.Height), Theme.Danger);
                    break;
                default:
                    Gfx.Text(g, "Canceled", Theme.Small, cell, Theme.Text3);
                    break;
            }
        }

        // ============================================================ startup & shutdown

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LayoutAll();
            try { taskbar = (ITaskbarList3)new TaskbarInstance(); taskbar.HrInit(); } catch (Exception) { taskbar = null; }
            Util.TryDeleteDir(Path.Combine(saveRoot, ".partial"));
            uiTimer.Start();
            UiTick();
            foreach (var a in startArgs) if (File.Exists(a)) LoadManifest(a);
            await LoadCatalog(false);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            bool busy;
            lock (qlock) busy = queue.Any(q => q.State == QState.Active || q.State == QState.Pending);
            if (busy && e.CloseReason == CloseReason.UserClosing)
            {
                var r = MessageBox.Show(this,
                    "Downloads are still running. Stop them and quit?\n\nSeries that already finished stay on your computer. Unfinished ones can be downloaded again later.",
                    "Quit TCIA Downloader", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
            }
            runCts.Cancel();
            if (sleepBlocked) Native.SetThreadExecutionState(0x80000000);
            base.OnFormClosing(e);
        }

        // ============================================================ settings & catalog cache

        void LoadSettings()
        {
            saveRoot = Path.Combine(Native.Downloads(), "TCIA");
            try
            {
                string f = Path.Combine(appDir, "settings.txt");
                if (File.Exists(f))
                    foreach (var line in File.ReadAllLines(f))
                        if (line.StartsWith("saveRoot=") && line.Substring(9).Trim() != "") saveRoot = line.Substring(9).Trim();
            }
            catch (Exception) { }
        }

        void SaveSettings()
        {
            try { Directory.CreateDirectory(appDir); File.WriteAllText(Path.Combine(appDir, "settings.txt"), "saveRoot=" + saveRoot + "\r\n"); }
            catch (Exception) { }
        }

        void UpdateSavePathLabel()
        {
            string dl = Native.Downloads().TrimEnd('\\');
            string label = saveRoot.StartsWith(dl + "\\", StringComparison.OrdinalIgnoreCase)
                ? "Downloads\\" + saveRoot.Substring(dl.Length + 1) : Util.ShortPath(saveRoot, 30);
            btnSave.Text = label;
            tips.SetToolTip(btnSave, "Saving to " + saveRoot);
            LayoutAll();
        }

        string CacheFile { get { return Path.Combine(appDir, "catalog.json"); } }

        bool LoadCache(out DateTime savedUtc)
        {
            savedUtc = DateTime.MinValue;
            try
            {
                if (!File.Exists(CacheFile)) return false;
                var ser = new JavaScriptSerializer(); ser.MaxJsonLength = int.MaxValue;
                var root = ser.DeserializeObject(File.ReadAllText(CacheFile, Encoding.UTF8)) as Dictionary<string, object>;
                if (root == null || !root.ContainsKey("collections") || J.S(root, "v") != CacheVersion) return false;
                DateTime.TryParse(J.S(root, "saved"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out savedUtc);
                webLoaded = J.S(root, "web") == "True";
                var list = new List<CollectionInfo>();
                foreach (var o in (IEnumerable)root["collections"])
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    var c = new CollectionInfo();
                    c.Name = J.S(d, "name");
                    if (c.Name == "") continue;
                    c.Title = J.S(d, "title"); c.CancerTypes = J.S(d, "cancer"); c.Locations = J.S(d, "loc");
                    c.Subjects = J.S(d, "subjects"); c.Species = J.S(d, "species"); c.Link = J.S(d, "link");
                    c.Abstract = J.S(d, "abstract"); c.Doi = J.S(d, "doi");
                    object mods;
                    if (d.TryGetValue("mods", out mods) && mods is IEnumerable && !(mods is string))
                    {
                        c.Modalities = ((IEnumerable)mods).Cast<object>().Select(x => Convert.ToString(x)).Where(x => !string.IsNullOrEmpty(x)).ToList();
                        c.ModalitiesLoaded = true;
                    }
                    c.UpdateSearch();
                    list.Add(c);
                }
                if (list.Count == 0) return false;
                collections = list;
                return true;
            }
            catch (Exception) { return false; }
        }

        void SaveCache()
        {
            try
            {
                var list = collections.Select(c => (object)new Dictionary<string, object> {
                    { "name", c.Name }, { "title", c.Title }, { "cancer", c.CancerTypes }, { "loc", c.Locations },
                    { "subjects", c.Subjects }, { "species", c.Species }, { "link", c.Link }, { "abstract", c.Abstract },
                    { "doi", c.Doi }, { "mods", c.ModalitiesLoaded ? (object)c.Modalities.ToArray() : null } }).ToArray();
                var root = new Dictionary<string, object> {
                    { "v", CacheVersion }, { "saved", DateTime.UtcNow.ToString("o") }, { "web", webLoaded }, { "collections", list } };
                var ser = new JavaScriptSerializer(); ser.MaxJsonLength = int.MaxValue;
                Directory.CreateDirectory(appDir);
                string tmp = CacheFile + ".tmp";
                File.WriteAllText(tmp, ser.Serialize(root), Encoding.UTF8);
                if (File.Exists(CacheFile)) File.Delete(CacheFile);
                File.Move(tmp, CacheFile);
            }
            catch (Exception) { }
        }

        async Task LoadCatalog(bool force)
        {
            if (catalogBusy) return;
            catalogBusy = true;
            btnRefresh.Enabled = false;
            chipBar.Invalidate();
            try
            {
                DateTime saved;
                if (!force && collections.Count == 0 && LoadCache(out saved))
                {
                    BuildChips();
                    ApplyCollectionFilter();
                    cardDetail.Invalidate();
                    bool fresh = (DateTime.UtcNow - saved).TotalDays < 7 && webLoaded && collections.All(c => c.ModalitiesLoaded);
                    SetStatus("Ready · " + collections.Count + " public collections.");
                    if (fresh) return;
                }

                SetStatus(collections.Count > 0 ? "Checking TCIA for catalog updates…" : "Connecting to The Cancer Imaging Archive…");
                ApplyCollectionFilter();
                var tWeb = Tcia.GetWebCollections(CancellationToken.None);
                List<Dictionary<string, object>> nb;
                try { nb = await Tcia.GetList("getCollectionValues", CancellationToken.None); }
                catch (Exception ex)
                {
                    SetStatus("Couldn't reach TCIA (" + Util.Inner(ex) + "). Check your internet connection, then press refresh.", true);
                    listColl.EmptyTitle = "Not connected";
                    listColl.EmptyText = "Couldn't reach The Cancer Imaging Archive. Check your connection and press the refresh button.";
                    listColl.Invalidate();
                    return;
                }

                var old = new Dictionary<string, CollectionInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in collections) old[c.Name] = c;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var list = new List<CollectionInfo>();
                foreach (var r in nb)
                {
                    string n = J.S(r, "Collection");
                    if (n == "" || !seen.Add(n)) continue;
                    CollectionInfo c;
                    if (!old.TryGetValue(n, out c)) { c = new CollectionInfo(); c.Name = n; }
                    if (force) c.ModalitiesLoaded = false;
                    c.UpdateSearch();
                    list.Add(c);
                }
                collections = list;
                ApplyCollectionFilter();

                try { MergeWeb(await tWeb); webLoaded = true; }
                catch (Exception) { /* descriptions are optional */ }
                ApplyCollectionFilter();
                cardDetail.Invalidate();
                SaveCache();

                await IndexModalities();
                SaveCache();
                BuildChips();
                ApplyCollectionFilter();
                SetStatus("Ready · " + collections.Count + " public collections.");
            }
            finally
            {
                catalogBusy = false;
                btnRefresh.Enabled = true;
                chipBar.Invalidate();
            }
        }

        void MergeWeb(List<Dictionary<string, object>> web)
        {
            var map = new Dictionary<string, Dictionary<string, object>>();
            foreach (var r in web)
                foreach (var key in new[] { J.S(r, "collection_short_title"), J.S(r, "title"), J.S(r, "slug") })
                {
                    string k = Util.Norm(WebUtility.HtmlDecode(key));
                    if (k != "" && !map.ContainsKey(k)) map[k] = r;
                }
            foreach (var c in collections)
            {
                Dictionary<string, object> r;
                if (!map.TryGetValue(Util.Norm(c.Name), out r)) continue;
                string title = WebUtility.HtmlDecode(J.S(r, "collection_title"));
                if (title == "") title = WebUtility.HtmlDecode(J.S(r, "title"));
                c.Title = title;
                c.CancerTypes = J.S(r, "cancer_types");
                c.Locations = J.S(r, "cancer_locations");
                c.Subjects = J.S(r, "subjects");
                c.Species = J.S(r, "species");
                c.Link = J.S(r, "link");
                c.Doi = J.S(r, "collection_doi");
                string text = "";
                foreach (var f in new[] { "collection_abstract", "collection_summary", "collection_introduction" })
                {
                    text = Util.StripHtml(J.S(r, f));
                    if (text != "") break;
                }
                c.Abstract = text;
                c.UpdateSearch();
            }
        }

        async Task IndexModalities()
        {
            var todo = collections.Where(c => !c.ModalitiesLoaded).ToList();
            if (todo.Count == 0) return;
            indexing = true; indexDone = 0; indexTotal = todo.Count;
            chipBar.Invalidate();
            var sem = new SemaphoreSlim(8);
            var tasks = todo.Select(async c =>
            {
                await sem.WaitAsync();
                try
                {
                    var rs = await Tcia.GetList("getModalityValues?Collection=" + Esc(c.Name), CancellationToken.None);
                    c.Modalities = Util.SortModalities(rs.Select(r => J.S(r, "Modality")).Where(m => m != ""));
                    c.ModalitiesLoaded = true;
                    c.UpdateSearch();
                    listColl.Invalidate();
                    if (c == curCollection) cardDetail.Invalidate();
                }
                catch (Exception) { }
                finally
                {
                    sem.Release();
                    indexDone++;
                    chipBar.Invalidate();
                }
            }).ToList();
            await Task.WhenAll(tasks);
            indexing = false;
            chipBar.Invalidate();
        }

        // ============================================================ scan-type chips

        static string ChipLabel(string m) { return m == "PT" ? "PET" : m; }

        void BuildChips()
        {
            modCounts = new Dictionary<string, int>();
            foreach (var c in collections)
                foreach (var m in c.Modalities) { int n; modCounts.TryGetValue(m, out n); modCounts[m] = n + 1; }
            foreach (var c in chips) { chipBar.Controls.Remove(c); c.Dispose(); }
            chips.Clear();
            AddChip(null, "All scans");
            foreach (var m in ChipOrder) if (modCounts.ContainsKey(m)) AddChip(m, ChipLabel(m));
            var more = Util.SortModalities(modCounts.Keys).Where(m => !ChipOrder.Contains(m)).ToList();
            menuMore.Items.Clear();
            chipMore = null;
            if (more.Count > 0)
            {
                chipMore = AddChip("*more", "More");
                chipMore.TrailIcon = "chevron";
                foreach (var m in more)
                {
                    string mm = m;
                    var it = MI(Util.ModalityLabel(m) + "   ·  " + modCounts[m] + (modCounts[m] == 1 ? " collection" : " collections"), () => SetModality(mm));
                    it.Tag = m;
                    menuMore.Items.Add(it);
                }
            }
            if (modality != null && !modCounts.ContainsKey(modality)) modality = null;
            UpdateChipStates();
        }

        Chip AddChip(string code, string text)
        {
            var c = new Chip();
            c.Code = code; c.Text = text; c.BackColor = Theme.Bg;
            c.Click += (s, e) => OnChip(c);
            chipBar.Controls.Add(c);
            chips.Add(c);
            int n;
            if (code != null && modCounts.TryGetValue(code, out n))
                tips.SetToolTip(c, Util.ModalityLabel(code) + " · in " + n + (n == 1 ? " collection" : " collections"));
            else if (code == null) tips.SetToolTip(c, "Show every collection");
            return c;
        }

        void OnChip(Chip c)
        {
            if (c.Code == "*more")
            {
                foreach (ToolStripMenuItem it in menuMore.Items) it.Checked = (string)it.Tag == modality;
                menuMore.Show(c, new Point(0, c.Height + Px(4)));
                return;
            }
            SetModality(c.Code);
        }

        void SetModality(string m)
        {
            if (m == modality) return;
            modality = m;
            UpdateChipStates();
            OnModalityChanged();
        }

        void UpdateChipStates()
        {
            foreach (var c in chips)
            {
                if (c == chipMore) continue;
                c.Selected = c.Code == modality;
                c.Invalidate();
            }
            if (chipMore != null)
            {
                bool sel = modality != null && !ChipOrder.Contains(modality);
                chipMore.Selected = sel;
                chipMore.Text = sel ? ChipLabel(modality) : "More";
                chipMore.Invalidate();
            }
            LayoutChips();
        }

        void OnModalityChanged()
        {
            ApplyCollectionFilter();
            listColl.ScrollToTop();
            if (curCollection != null) { var _ = LoadPatients(); }
            UpdatePatientButtons();
        }

        // ============================================================ collections

        void ApplyCollectionFilter()
        {
            string raw = collQuery.Trim().ToLowerInvariant();
            string[] terms = Regex.Split(raw, "[^a-z0-9]+").Where(x => x != "").ToArray();
            var shown = collections.Where(c => c.Matches(terms, raw) && (modality == null || c.Modalities.Contains(modality))).ToList();
            Comparison<CollectionInfo> cmp;
            switch (sortMode)
            {
                case 1: cmp = (a, b) => b.SubjectsNum.CompareTo(a.SubjectsNum); break;
                case 2: cmp = (a, b) => CompareText(a.TypeText, b.TypeText); break;
                default: cmp = (a, b) => 0; break;
            }
            shown.Sort((a, b) => { int r = cmp(a, b); return r != 0 ? r : Native.StrCmpLogicalW(a.Name, b.Name); });
            collShown = shown;
            listColl.Count = shown.Count;
            listColl.SilentSelect(curCollection == null ? -1 : shown.IndexOf(curCollection));
            if (collections.Count == 0)
            {
                listColl.EmptyIcon = "search";
                listColl.EmptyTitle = catalogBusy ? "Loading collections…" : "No collections yet";
                listColl.EmptyText = catalogBusy ? "Connecting to The Cancer Imaging Archive" : "Press the refresh button to load the catalog.";
            }
            else
            {
                listColl.EmptyIcon = "search";
                listColl.EmptyTitle = "No matching collections";
                listColl.EmptyText = "Try another word, or choose “All scans”.";
            }
            listColl.Invalidate();
            cardColl.Invalidate();
            cardDetail.Invalidate();
        }

        static int CompareText(string a, string b)
        {
            if (a == "" && b != "") return 1;
            if (b == "" && a != "") return -1;
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        void OnCollectionSelected()
        {
            var l = listColl.SelectedList();
            if (l.Count != 1 || l[0] >= collShown.Count) return;
            var c = collShown[l[0]];
            if (c == curCollection) { TrySelectPendingPatient(); return; }
            curCollection = c;
            linkHover = false;
            cardDetail.Invalidate();
            tips.SetToolTip(cardDetail, TipText(Util.JoinNonEmpty("\r\n\r\n", c.Title, c.Abstract)));
            var _ = LoadPatients();
        }

        // Tooltips don't wrap long lines on their own.
        static string TipText(string s)
        {
            s = (s ?? "").Replace("\r\n", "\n");
            if (s.Length > 900) s = s.Substring(0, 900).TrimEnd() + "…";
            var sb = new StringBuilder();
            foreach (var para in s.Split('\n'))
            {
                int col = 0;
                foreach (var word in para.Split(' '))
                {
                    if (col > 0 && col + word.Length > 90) { sb.Append("\r\n"); col = 0; }
                    else if (col > 0) { sb.Append(' '); col++; }
                    sb.Append(word);
                    col += word.Length;
                }
                sb.Append("\r\n");
            }
            return sb.ToString().TrimEnd();
        }

        void SelectCollection(CollectionInfo c)
        {
            int i = collShown.IndexOf(c);
            if (i >= 0) { listColl.SelectOnly(i); listColl.EnsureVisible(i); }
            else { curCollection = c; cardDetail.Invalidate(); var _ = LoadPatients(); }
        }

        // ============================================================ patients

        void UpdatePatEmpty()
        {
            if (curCollection == null)
            {
                listPat.EmptyTitle = "No collection selected";
                listPat.EmptyText = "Choose a collection on the left to list its patients.";
            }
            else if (patLoading)
            {
                listPat.EmptyTitle = "Loading patients…";
                listPat.EmptyText = curCollection.Name;
            }
            else if (patientsAll.Count > 0)
            {
                listPat.EmptyTitle = "No matching patients";
                listPat.EmptyText = "Nothing contains “" + patFilter.Query.Trim() + "”.";
            }
            else if (modality != null)
            {
                listPat.EmptyTitle = "No " + ChipLabel(modality) + " scans here";
                listPat.EmptyText = "Choose another scan type, or “All scans”.";
            }
            listPat.Invalidate();
        }

        async Task LoadPatients()
        {
            var c = curCollection;
            if (c == null) return;
            string mod = modality;
            string key = c.Name + "|" + (mod ?? "*");
            int token = ++patToken;
            patientsAll = new List<string>();
            patLoading = !patientCache.ContainsKey(key);
            ApplyPatientFilter();
            ClearSeries("No patient selected", "Pick a patient to see their scans, grouped by study date.");

            List<string> list;
            if (!patientCache.TryGetValue(key, out list))
            {
                SetStatus("Loading patients in " + c.Name + "…");
                try
                {
                    string q = mod == null
                        ? "getPatient?Collection=" + Esc(c.Name)
                        : "getPatientByCollectionAndModality?Collection=" + Esc(c.Name) + "&Modality=" + Esc(mod);
                    var rs = await Tcia.GetList(q, CancellationToken.None);
                    list = rs.Select(r => { string id = J.S(r, "PatientId"); return id != "" ? id : J.S(r, "PatientID"); })
                        .Where(x => x != "").Distinct().ToList();
                    list.Sort((a, b) => Native.StrCmpLogicalW(a, b));
                    patientCache[key] = list;
                }
                catch (Exception ex)
                {
                    if (token == patToken)
                    {
                        patLoading = false;
                        listPat.EmptyTitle = "Couldn't load patients";
                        listPat.EmptyText = Util.Inner(ex);
                        listPat.Invalidate();
                        cardPat.Invalidate();
                        SetStatus("Couldn't load patients: " + Util.Inner(ex), true);
                    }
                    return;
                }
            }
            if (token != patToken) return;
            patLoading = false;
            patientsAll = list;
            ApplyPatientFilter();
            SetStatus(list.Count.ToString("N0") + " patients in " + c.Name + (mod != null ? " with " + mod + " scans" : "") + ".");
            TrySelectPendingPatient();
        }

        void ApplyPatientFilter()
        {
            string f = patFilter.Query.Trim();
            patientsShown = f == "" ? patientsAll : patientsAll.Where(p => p.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            listPat.Selected.Clear();
            listPat.Count = patientsShown.Count;
            listPat.ScrollToTop();
            UpdatePatEmpty();
            UpdatePatientButtons();
            cardPat.Invalidate();
        }

        void TrySelectPendingPatient()
        {
            if (pendingPatient == null || patientsAll.Count == 0) return;
            string want = pendingPatient;
            pendingPatient = null;
            if (patFilter.Query != "") { patFilter.Query = ""; patFilterTimer.Stop(); ApplyPatientFilter(); }
            int idx = patientsShown.FindIndex(p => string.Equals(p, want, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return;
            listPat.SelectOnly(idx);
            listPat.EnsureVisible(idx);
            listPat.Focus();
        }

        void UpdatePatientButtons()
        {
            if (btnPatDownload == null) return;
            int n = listPat.Selected.Count;
            string what = modality == null ? "" : " · " + ChipLabel(modality);
            btnPatDownload.Enabled = n > 0 && curCollection != null;
            btnPatDownload.Text = (n <= 1 ? "Download patient" : "Download " + n.ToString("N0") + " patients") + what;
            btnPatAll.Enabled = patientsShown.Count > 0;
        }

        void OnPatientSelectionSettled()
        {
            var sel = listPat.SelectedList();
            if (sel.Count == 1)
            {
                int i = sel[0];
                if (i < 0 || i >= patientsShown.Count) return;
                string pid = patientsShown[i];
                if (pid != curPatient || curSeries.Count == 0) { var _ = LoadSeries(pid); }
            }
            else if (sel.Count > 1)
            {
                ClearSeries(sel.Count.ToString("N0") + " patients selected",
                    "Press “Download " + sel.Count.ToString("N0") + " patients” to get all their scans, or pick one patient to choose individual series.");
            }
        }

        async void DownloadSelectedPatients()
        {
            var c = curCollection;
            if (c == null) return;
            var pids = listPat.SelectedList().Where(i => i >= 0 && i < patientsShown.Count).Select(i => patientsShown[i]).ToList();
            if (pids.Count == 0) return;
            string mod = modality;
            if (pids.Count > 10)
            {
                var r = MessageBox.Show(this,
                    "Download " + (mod == null ? "every series" : "every " + mod + " series") + " for " + pids.Count.ToString("N0") +
                    " patients in " + c.Name + "?\n\nThis can be a lot of data. Each patient's scans are added to the download list " +
                    "as they're found, and you can cancel at any time.",
                    "Download " + pids.Count.ToString("N0") + " patients", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
            }
            btnPatDownload.Enabled = false;
            int done = 0, added = 0, failed = 0;
            long bytes = 0;
            var sem = new SemaphoreSlim(4);
            var tasks = pids.Select(async pid =>
            {
                await sem.WaitAsync();
                try
                {
                    var list = await GetPatientSeries(c.Name, pid);
                    var sel = list.Where(s => mod == null || s.Modality == mod).ToList();
                    added += Enqueue(sel);
                    bytes += sel.Sum(s => s.FileSize);
                }
                catch (Exception) { failed++; }
                finally
                {
                    sem.Release();
                    done++;
                    if (pids.Count > 1) SetStatus("Adding patients to Downloads… " + done + " of " + pids.Count);
                }
            }).ToList();
            await Task.WhenAll(tasks);
            UpdatePatientButtons();
            if (curPatient != null && pids.Contains(curPatient) && curSeries.Count > 0) FillSeries();
            SetStatus("Added " + added + " series" + (bytes > 0 ? " (" + Util.Size(bytes) + ")" : "") + " from " + pids.Count +
                (pids.Count == 1 ? " patient" : " patients") + " to Downloads" +
                (failed > 0 ? "; " + failed + " patient(s) couldn't be read - try again" : "") + ".", failed > 0);
        }

        // ============================================================ series

        async Task<List<SeriesInfo>> GetPatientSeries(string coll, string pid)
        {
            string key = coll + "|" + pid;
            List<SeriesInfo> list;
            if (seriesCache.TryGetValue(key, out list)) return list;
            var tSeries = Tcia.GetList("getSeries?Collection=" + Esc(coll) + "&PatientID=" + Esc(pid), CancellationToken.None);
            var tStudies = Tcia.GetList("getPatientStudy?Collection=" + Esc(coll) + "&PatientID=" + Esc(pid), CancellationToken.None);
            var rs = await tSeries;
            var studies = new Dictionary<string, Dictionary<string, object>>();
            try
            {
                foreach (var r in await tStudies)
                {
                    string u = J.S(r, "StudyInstanceUID");
                    if (u != "") studies[u] = r;
                }
            }
            catch (Exception) { }
            list = rs.Select(SeriesInfo.From).Where(s => s.SeriesUID != "").ToList();
            foreach (var s in list)
            {
                Dictionary<string, object> st;
                if (!studies.TryGetValue(s.StudyUID, out st)) continue;
                if (s.StudyDesc == "") s.StudyDesc = J.S(st, "StudyDescription");
                if (s.StudyDate == "") s.StudyDate = Util.DateOnly(J.S(st, "StudyDate"));
            }
            list.Sort(SeriesInfo.Compare);
            seriesCache[key] = list;
            return list;
        }

        async Task LoadSeries(string pid)
        {
            var c = curCollection;
            if (c == null) return;
            int token = ++seriesToken;
            curPatient = pid;
            curSeries = new List<SeriesInfo>();
            rows = new List<SeriesRow>();
            listSeries.Count = 0;
            listSeries.EmptyTitle = "Loading series…";
            listSeries.EmptyText = pid;
            UpdateSeriesSummary();
            List<SeriesInfo> list;
            try { list = await GetPatientSeries(c.Name, pid); }
            catch (Exception ex)
            {
                if (token == seriesToken)
                {
                    listSeries.EmptyTitle = "Couldn't load series";
                    listSeries.EmptyText = Util.Inner(ex);
                    listSeries.Invalidate();
                }
                return;
            }
            if (token != seriesToken) return;
            curSeries = list;
            FillSeries();
        }

        bool InQueue(SeriesInfo s)
        {
            QueueItem q;
            return byUid.TryGetValue(s.SeriesUID, out q) && (q.State == QState.Pending || q.State == QState.Active);
        }

        void FillSeries()
        {
            string mod = modality;
            var newRows = new List<SeriesRow>();
            ticked.Clear();
            onDisk.Clear();
            foreach (var grp in curSeries.GroupBy(x => x.StudyUID))
            {
                var first = grp.First();
                var gr = new SeriesRow();
                gr.Group = true; gr.StudyUID = grp.Key; gr.Date = first.StudyDate; gr.Desc = first.StudyDesc;
                gr.Count = grp.Count(); gr.Bytes = grp.Sum(x => x.FileSize);
                newRows.Add(gr);
                foreach (var s in grp)
                {
                    bool disk = Util.IsComplete(Downloader.SeriesFolder(saveRoot, s), s.ImageCount);
                    onDisk[s.SeriesUID] = disk;
                    var r = new SeriesRow();
                    r.StudyUID = grp.Key; r.S = s;
                    newRows.Add(r);
                    if (!disk && !InQueue(s) && (mod == null || s.Modality == mod)) ticked.Add(s.SeriesUID);
                }
            }
            rows = newRows;
            listSeries.EmptyTitle = "No series";
            listSeries.EmptyText = "This patient has no public series.";
            listSeries.Count = rows.Count;
            listSeries.ScrollToTop();
            UpdateSeriesSummary();
        }

        void ClearSeries(string title, string text)
        {
            seriesToken++;
            curPatient = null;
            curSeries = new List<SeriesInfo>();
            rows = new List<SeriesRow>();
            ticked.Clear();
            listSeries.Count = 0;
            listSeries.EmptyIcon = "layers";
            listSeries.EmptyTitle = title;
            listSeries.EmptyText = text;
            UpdateSeriesSummary();
        }

        void Summary(out int n, out int imgs, out long bytes, out int saved)
        {
            n = 0; imgs = 0; bytes = 0; saved = 0;
            foreach (var r in rows)
            {
                if (r.Group) continue;
                if (ticked.Contains(r.S.SeriesUID)) { n++; imgs += r.S.ImageCount; bytes += r.S.FileSize; }
                bool d;
                if (onDisk.TryGetValue(r.S.SeriesUID, out d) && d) saved++;
            }
        }

        void UpdateSeriesSummary()
        {
            int n, imgs, saved; long bytes;
            Summary(out n, out imgs, out bytes, out saved);
            btnDownload.Enabled = n > 0;
            btnDownload.Text = n > 0 ? "Download " + n + " series" + (bytes > 0 ? "  ·  " + Util.Size(bytes) : "") : "Download";
            PlaceDownloadButton();
            listSeries.Invalidate();
        }

        void ToggleSeriesRow(int i)
        {
            if (i < 0 || i >= rows.Count) return;
            var row = rows[i];
            if (row.Group)
            {
                var uids = rows.Where(x => !x.Group && x.StudyUID == row.StudyUID).Select(x => x.S.SeriesUID).ToList();
                bool all = uids.All(u => ticked.Contains(u));
                foreach (var u in uids) { if (all) ticked.Remove(u); else ticked.Add(u); }
            }
            else if (!ticked.Remove(row.S.SeriesUID)) ticked.Add(row.S.SeriesUID);
            UpdateSeriesSummary();
        }

        void SetAllTicks(bool v)
        {
            ticked.Clear();
            if (v) foreach (var r in rows) if (!r.Group) ticked.Add(r.S.SeriesUID);
            UpdateSeriesSummary();
        }

        void DownloadTicked()
        {
            var list = rows.Where(r => !r.Group && ticked.Contains(r.S.SeriesUID)).Select(r => r.S).ToList();
            if (list.Count == 0) return;
            int added = Enqueue(list);
            ticked.Clear();
            UpdateSeriesSummary();
            SetStatus(added > 0 ? "Added " + added + " series to Downloads. Saving to " + saveRoot : "Those series are already in the download list.");
        }

        // ============================================================ find patient & manifests

        async void FindPatient(string id)
        {
            id = (id ?? "").Trim();
            if (id == "") { search.Box.Focus(); return; }
            SetStatus("Looking for patient “" + id + "” in all public collections…");
            try
            {
                var rs = await Tcia.GetList("getSeries?PatientID=" + Esc(id), CancellationToken.None);
                if (rs.Count == 0)
                {
                    SetStatus("No public scans found for patient ID “" + id + "”.", true);
                    MessageBox.Show(this,
                        "No public scans were found for patient ID “" + id + "”.\n\nThe ID must match exactly (for example LUNG1-001 or PANCREAS_0001). " +
                        "To search by part of an ID, pick a collection first and type in its patient filter box.",
                        "Patient not found", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var colls = rs.Select(r => J.S(r, "Collection")).Where(x => x != "").Distinct().ToList();
                string pid = J.S(rs[0], "PatientID");
                if (pid == "") pid = id;
                string coll = colls.Count > 0 ? colls[0] : "";
                var target = collections.FirstOrDefault(c => string.Equals(c.Name, coll, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                {
                    target = new CollectionInfo(); target.Name = coll; target.UpdateSearch();
                    collections.Add(target);
                }
                pendingPatient = pid;
                collQuery = "";
                if (modality != null) { modality = null; UpdateChipStates(); }
                ApplyCollectionFilter();
                if (target == curCollection)
                {
                    if (patientsAll.Count > 0 && patientCache.ContainsKey(target.Name + "|*")) TrySelectPendingPatient();
                    else { var _ = LoadPatients(); }
                }
                else SelectCollection(target);
                SetStatus("Found " + pid + " in " + string.Join(", ", colls) + ".");
            }
            catch (Exception ex) { SetStatus("Search failed: " + Util.Inner(ex), true); }
        }

        void PickManifest()
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = "Open a TCIA manifest";
                d.Filter = "TCIA manifest (*.tcia)|*.tcia|All files (*.*)|*.*";
                d.Multiselect = true;
                d.InitialDirectory = Native.Downloads();
                if (d.ShowDialog(this) == DialogResult.OK) foreach (var f in d.FileNames) LoadManifest(f);
            }
        }

        async void LoadManifest(string path)
        {
            List<string> uids;
            try { uids = Util.ParseManifest(File.ReadAllLines(path)); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't read " + Path.GetFileName(path) + ":\n\n" + ex.Message, "Open manifest", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string name = Path.GetFileName(path);
            if (uids.Count == 0)
            {
                MessageBox.Show(this, "No series were found in " + name + ".\n\nMake sure it's a .tcia manifest downloaded from the TCIA website.",
                    "Open manifest", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            int done = 0, added = 0, missing = 0;
            SetStatus("Reading manifest " + name + ": 0 of " + uids.Count + " series…");
            var sem = new SemaphoreSlim(8);
            var tasks = uids.Select(async uid =>
            {
                await sem.WaitAsync();
                SeriesInfo s = null;
                try
                {
                    var rs = await Tcia.GetList("getSeries?SeriesInstanceUID=" + Esc(uid), CancellationToken.None);
                    if (rs.Count > 0) s = SeriesInfo.From(rs[0]);
                }
                catch (Exception) { }
                finally { sem.Release(); }
                if (s == null || s.SeriesUID == "")
                {
                    s = new SeriesInfo();
                    s.SeriesUID = uid;
                    s.Collection = "From manifest " + Path.GetFileNameWithoutExtension(name);
                    s.PatientID = "Unknown patient";
                    missing++;
                }
                added += Enqueue(new[] { s });
                done++;
                if (done % 5 == 0 || done == uids.Count) SetStatus("Reading manifest " + name + ": " + done + " of " + uids.Count + " series…");
            }).ToList();
            await Task.WhenAll(tasks);
            SetStatus("Manifest " + name + ": added " + added + " series to Downloads" +
                (missing > 0 ? " (" + missing + " weren't found in the public archive)" : "") + ".", missing > 0);
        }

        // ============================================================ folders

        void ChangeFolder()
        {
            string picked = FolderPicker.Pick(this, "Choose where to save DICOM files", Directory.Exists(saveRoot) ? saveRoot : Native.Downloads());
            if (string.IsNullOrEmpty(picked)) return;
            saveRoot = picked;
            SaveSettings();
            UpdateSavePathLabel();
            if (curSeries.Count > 0) FillSeries();
            SetStatus("New downloads will be saved to " + saveRoot);
        }

        void OpenFolder(string path)
        {
            try
            {
                string p = path;
                if (p == saveRoot) Directory.CreateDirectory(p);
                while (!string.IsNullOrEmpty(p) && !Directory.Exists(p)) p = Path.GetDirectoryName(p);
                if (string.IsNullOrEmpty(p)) return;
                Process.Start("explorer.exe", "\"" + p + "\"");
            }
            catch (Exception ex) { SetStatus("Couldn't open the folder: " + ex.Message, true); }
        }

        // ============================================================ download queue

        void RefreshQueueView()
        {
            lock (qlock) qView = queue.ToArray();
            listQueue.Count = qView.Length;
            listQueue.Invalidate();
            cardQueue.Invalidate();
        }

        int Enqueue(IEnumerable<SeriesInfo> items)
        {
            int added = 0;
            foreach (var s in items)
            {
                if (s == null || s.SeriesUID == "") continue;
                QueueItem existing;
                if (byUid.TryGetValue(s.SeriesUID, out existing))
                {
                    if (existing.State == QState.Failed || existing.State == QState.Canceled)
                    {
                        existing.Status = "Waiting"; existing.Seen = false; existing.State = QState.Pending; added++;
                    }
                    continue;
                }
                var q = new QueueItem();
                q.S = s;
                q.Root = saveRoot;
                q.CollectionDir = Downloader.CollectionDir(saveRoot, s);
                q.Folder = Downloader.SeriesFolder(saveRoot, s);
                lock (qlock) queue.Add(q);
                byUid[s.SeriesUID] = q;
                added++;
            }
            if (added > 0)
            {
                RefreshQueueView();
                if (!queueOpen) { queueOpen = true; LayoutAll(); }
                PumpQueue();
                UiTick();
            }
            return added;
        }

        void PumpQueue()
        {
            int pending;
            lock (qlock) pending = queue.Count(x => x.State == QState.Pending);
            var token = runCts.Token;
            while (activeWorkers < MaxWorkers && pending > 0)
            {
                activeWorkers++;
                pending--;
                Task.Run(() => WorkerLoop(token)).ContinueWith(t => { activeWorkers--; PumpQueue(); },
                    TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        async Task WorkerLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                QueueItem q = null;
                lock (qlock)
                {
                    foreach (var x in queue) if (x.State == QState.Pending) { q = x; break; }
                    if (q != null) q.State = QState.Active;
                }
                if (q == null) return;
                using (var itemCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    q.Cts = itemCts;
                    try { await Downloader.DownloadOne(q, itemCts.Token); }
                    catch (OperationCanceledException) { q.Status = "Canceled"; q.State = QState.Canceled; }
                    catch (Exception ex) { q.Status = "Failed: " + Util.Inner(ex); q.State = QState.Failed; }
                    finally { q.Cts = null; }
                }
            }
        }

        void UiTick()
        {
            QueueItem[] items;
            lock (qlock) items = queue.ToArray();
            double ratio = Downloader.ZipRatio;
            long total = 0;
            double done = 0;
            int nAct = 0, nPend = 0, nDone = 0, nSkip = 0, nFail = 0, nCanc = 0;
            bool seriesChanged = false;
            foreach (var q in items)
            {
                long fs = Math.Max(1, q.S.FileSize);
                switch (q.State)
                {
                    case QState.Pending: nPend++; total += fs; break;
                    case QState.Active:
                        {
                            nAct++; total += fs;
                            long rec = Interlocked.Read(ref q.Received);
                            double frac = q.Status == Downloader.StUnpacking ? 0.99 : Math.Min(0.97, rec / Math.Max(1.0, q.S.FileSize * ratio));
                            done += fs * frac;
                            break;
                        }
                    case QState.Done: nDone++; total += fs; done += fs; break;
                    case QState.Skipped: nSkip++; total += fs; done += fs; break;
                    case QState.Failed: nFail++; total += fs; done += fs; break;
                    default: nCanc++; break;
                }
                bool final = q.State == QState.Done || q.State == QState.Skipped || q.State == QState.Failed || q.State == QState.Canceled;
                if (final && !q.Seen)
                {
                    q.Seen = true;
                    if (onDisk.ContainsKey(q.S.SeriesUID))
                    {
                        onDisk[q.S.SeriesUID] = q.State == QState.Done || q.State == QState.Skipped;
                        seriesChanged = true;
                    }
                }
            }

            var now = DateTime.UtcNow;
            double dt = (now - lastTick).TotalSeconds;
            long recv = Interlocked.Read(ref Downloader.TotalReceived);
            if (dt >= 0.3)
            {
                double inst = (recv - lastRecv) / dt;
                speed = speed <= 0 ? inst : speed * 0.75 + inst * 0.25;
                lastRecv = recv; lastTick = now;
            }

            bool busy = nAct + nPend > 0;
            queueBusy = busy;
            queueFrac = total > 0 ? done / total : 0;
            if (busy)
            {
                string eta = "";
                if (speed > 20 * 1024)
                {
                    string d = Util.Duration(Math.Max(0, total - done) * ratio / speed);
                    if (d != "") eta = "  ·  about " + d + " left";
                }
                queueSummary = (nDone + nSkip + nFail) + " of " + (items.Length - nCanc) + " finished  ·  " +
                    (speed >= 1024 ? Util.Size((long)speed) + "/s" : "starting…") + eta;
                queueSummaryColor = Theme.AccentText;
            }
            else if (items.Length > 0)
            {
                queueSummary = "All done  ·  " + nDone + " downloaded" +
                    (nSkip > 0 ? ", " + nSkip + " already on your computer" : "") +
                    (nFail > 0 ? ", " + nFail + " failed" : "") +
                    (nCanc > 0 ? ", " + nCanc + " canceled" : "");
                queueSummaryColor = nFail > 0 ? Theme.Danger : Theme.Success;
            }
            else
            {
                queueSummary = "Nothing queued yet";
                queueSummaryColor = Theme.Text3;
            }

            btnCancelAll.Enabled = busy;
            btnRetry.Enabled = nFail + nCanc > 0;
            btnClear.Enabled = nDone + nSkip + nCanc + nFail > 0;
            if (busy || wasBusy) { listQueue.Invalidate(); UpdateQueueTip(); }
            cardQueue.Invalidate();
            if (busy || seriesChanged) listSeries.Invalidate();
            if (seriesChanged) UpdateSeriesSummary();

            if (taskbar != null)
            {
                try
                {
                    if (busy) { taskbar.SetProgressState(Handle, 2); taskbar.SetProgressValue(Handle, (ulong)(queueFrac * 1000), 1000); }
                    else taskbar.SetProgressState(Handle, 0);
                }
                catch (Exception) { taskbar = null; }
            }
            if (busy != sleepBlocked)
            {
                Native.SetThreadExecutionState(busy ? 0x80000001u : 0x80000000u);   // keep the PC awake while downloading
                sleepBlocked = busy;
            }
            if (wasBusy && !busy)
            {
                foreach (var root in items.Select(q => q.Root).Distinct())
                {
                    string part = Path.Combine(root, ".partial");
                    try { if (Directory.Exists(part) && !Directory.EnumerateFileSystemEntries(part).Any()) Directory.Delete(part); } catch (Exception) { }
                }
                if (Form.ActiveForm != this) Native.Flash(Handle);
                SetStatus("Downloads finished. Your DICOM files are in " + saveRoot, nFail > 0);
            }
            wasBusy = busy;
        }

        string lastQueueTip = "";

        void UpdateQueueTip()
        {
            int i = listQueue.HoverIndex;
            string t = "";
            if (i >= 0 && i < qView.Length)
            {
                var q = qView[i];
                string st = q.State == QState.Pending ? "Waiting" : q.State == QState.Skipped ? "Already on your computer" : q.Status;
                t = st + "\r\n" + q.Folder + "\r\nDouble-click to open the folder";
            }
            if (t == lastQueueTip) return;
            lastQueueTip = t;
            tips.SetToolTip(listQueue, t);
        }

        List<QueueItem> SelectedQueueItems()
        {
            return listQueue.SelectedList().Where(i => i >= 0 && i < qView.Length).Select(i => qView[i]).ToList();
        }

        void RetryItems(List<QueueItem> list)
        {
            if (list == null) lock (qlock) list = queue.ToList();
            foreach (var q in list)
                if (q.State == QState.Failed || q.State == QState.Canceled)
                {
                    q.Status = "Waiting"; q.Seen = false;
                    Interlocked.Exchange(ref q.Received, 0);
                    q.State = QState.Pending;
                }
            PumpQueue();
            UiTick();
        }

        void CancelItems(List<QueueItem> list)
        {
            foreach (var q in list)
            {
                lock (qlock) if (q.State == QState.Pending) { q.Status = "Canceled"; q.State = QState.Canceled; continue; }
                var cts = q.Cts;
                if (q.State == QState.Active && cts != null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
            }
            UiTick();
        }

        void RemoveItems(List<QueueItem> list)
        {
            foreach (var q in list)
            {
                lock (qlock)
                {
                    if (q.State == QState.Active) continue;
                    queue.Remove(q);
                }
                byUid.Remove(q.S.SeriesUID);
            }
            listQueue.Selected.Clear();
            RefreshQueueView();
            listSeries.Invalidate();
            UiTick();
        }

        void ClearFinished()
        {
            List<QueueItem> fin;
            lock (qlock) fin = queue.Where(q => q.State == QState.Done || q.State == QState.Skipped || q.State == QState.Canceled || q.State == QState.Failed).ToList();
            RemoveItems(fin);
        }

        void CancelAll(bool ask)
        {
            bool busy;
            lock (qlock) busy = queue.Any(q => q.State == QState.Active || q.State == QState.Pending);
            if (!busy) return;
            if (ask && MessageBox.Show(this, "Stop all downloads?\n\nSeries that already finished stay on your computer.", "Cancel downloads",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            lock (qlock) foreach (var q in queue) if (q.State == QState.Pending) { q.Status = "Canceled"; q.State = QState.Canceled; }
            runCts.Cancel();
            runCts = new CancellationTokenSource();
            UiTick();
        }
    }
}
