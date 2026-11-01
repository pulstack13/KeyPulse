using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Web.Script.Serialization;

namespace KeyTyper
{
    public class MainForm : Form
    {
        private const int HOTKEY_ID = 0x4B54;
        private const int STOP_HOTKEY_ID = 0x4B55;
        private const int F5_HOTKEY_ID = 0x4B56;
        private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RUN_VALUE = "KeyTyper";

        private readonly bool startHidden;
        private readonly string autoTypeFile;
        private readonly bool exitAfter;
        private readonly bool suppressDraft;
        private readonly uint ownPid;

        private string dataDir;
        private string settingsPath;
        private string snippetsPath;
        private string draftPath;

        private Icon appIcon;
        private NotifyIcon tray;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem startupItem;
        private ToolStripMenuItem startupMenuItem;
        private ToolStripMenuItem stopTrayItem;
        private Button stopButton;
        private Button typeButton;
        private PictureBox logoPictureBox;
        private WebView2 htmlView;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private System.Windows.Forms.Timer draftSaveTimer;
        private int lastUiProgressPercent = -1;
        private string lastStatusText = "Ready.";
        private bool lastStatusError;

        private TextBox editor;
        private ListBox snippetList;
        private ComboBox modeBox;
        private ComboBox speedPresetBox;
        private TrackBar speedTrackBar;
        private Label speedValueLabel;
        private NumericUpDown delayInput;
        private TextBox snippetSearchBox;
        private Label snippetSearchPlaceholder;
        private Label documentStatsLabel;
        private Label statusLabel;
        private Label progressText;
        private ProgressBar typingProgress;
        private ToolTip tips;
        private bool updatingSpeedControls;

        private readonly List<string> snippetNames = new List<string>();
        private readonly List<string> snippetTexts = new List<string>();
        private readonly List<int> visibleSnippetIndices = new List<int>();

        private System.Windows.Forms.Timer trackTimer;
        private IntPtr lastExternalWindow = IntPtr.Zero;
        private bool hotkeyRegistered;
        private bool f5HotkeyRegistered;
        private bool busy;
        private volatile bool cancelRequested;
        private volatile bool stopHotkeyRegistered;
        private IntPtr escapeHookHandle = IntPtr.Zero;
        private Native.LowLevelKeyboardProc escapeHookCallback;
        private bool trayHintShown;

        private string hotkeyText = "Ctrl+Alt+Space";

        public MainForm(bool startHidden, string autoTypeFile, bool exitAfter)
        {
            this.startHidden = startHidden;
            this.autoTypeFile = autoTypeFile;
            this.exitAfter = exitAfter;
            this.suppressDraft = autoTypeFile != null;
            this.ownPid = (uint)Process.GetCurrentProcess().Id;

            SetupPaths();
            appIcon = CreateAppIcon();
            BuildWebUi();
            LoadSettings();
            if (!suppressDraft)
            {
                LoadDraft();
            }
            LoadSnippets();
            StartTracking();
        }

        private void SetupPaths()
        {
            dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "KeyTyper");
            Directory.CreateDirectory(dataDir);
            settingsPath = Path.Combine(dataDir, "settings.ini");
            snippetsPath = Path.Combine(dataDir, "snippets.txt");
            draftPath = Path.Combine(dataDir, "draft.txt");
        }

        private Icon CreateAppIcon()
        {
            Bitmap source = null;
            Bitmap bmp = null;
            Stream logoStream = typeof(MainForm).Assembly.GetManifestResourceStream("KeyPulse.logo.png");
            if (logoStream != null)
            {
                using (logoStream)
                using (Image image = Image.FromStream(logoStream))
                {
                    bmp = new Bitmap(image, 64, 64);
                }
            }
            else
            {
                bmp = CreateLogoBitmap(32);
            }

            IntPtr handle = bmp.GetHicon();
            Icon temp = Icon.FromHandle(handle);
            Icon result = (Icon)temp.Clone();
            Native.DestroyIcon(handle);
            temp.Dispose();
            bmp.Dispose();
            if (source != null) source.Dispose();
            return result;
        }

        private Bitmap CreateLogoBitmap(int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float scale = size / 64.0F;
                RectangleF box = new RectangleF(2 * scale, 2 * scale, 60 * scale, 60 * scale);
                using (GraphicsPath path = new GraphicsPath())
                {
                    float r = 13 * scale;
                    path.AddArc(box.X, box.Y, r, r, 180, 90);
                    path.AddArc(box.Right - r, box.Y, r, r, 270, 90);
                    path.AddArc(box.Right - r, box.Bottom - r, r, r, 0, 90);
                    path.AddArc(box.X, box.Bottom - r, r, r, 90, 90);
                    path.CloseFigure();
                    using (Brush bg = new SolidBrush(Color.FromArgb(18, 33, 27)))
                    using (Pen border = new Pen(Color.FromArgb(34, 197, 94), 2.2F * scale))
                    {
                        g.FillPath(bg, path);
                        g.DrawPath(border, path);
                    }
                }

                PointF[] pulse = new PointF[]
                {
                    new PointF(10 * scale, 33 * scale), new PointF(20 * scale, 33 * scale),
                    new PointF(25 * scale, 23 * scale), new PointF(31 * scale, 43 * scale),
                    new PointF(38 * scale, 17 * scale), new PointF(44 * scale, 33 * scale),
                    new PointF(54 * scale, 33 * scale)
                };
                using (Pen glow = new Pen(Color.FromArgb(55, 34, 197, 94), 6 * scale))
                using (Pen line = new Pen(Color.FromArgb(74, 222, 128), 2.7F * scale))
                {
                    glow.LineJoin = LineJoin.Round;
                    line.LineJoin = LineJoin.Round;
                    g.DrawLines(glow, pulse);
                    g.DrawLines(line, pulse);
                }
            }
            return bmp;
        }

        private void BuildWebUi()
        {
            Text = "KeyPulse";
            ClientSize = new Size(1320, 900);
            MinimumSize = new Size(1000, 720);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            Icon = appIcon;
            KeyPreview = true;
            ShowInTaskbar = true;

            editor = new TextBox();
            editor.Multiline = true;
            editor.Text = "Hello, this is a test message to be typed.\r\n\r\nBest regards,\r\n— KeyPulse Automation\r\n\r\n• Works with any app\r\n• No background permissions needed\r\n• Safe & local processing\r\n\r\nTip: Press F5 to start typing at cursor.\r\n\r\nLorem ipsum dolor sit amet, consectetur.";

            modeBox = new ComboBox();
            modeBox.Items.Add("Keystroke Emulation (VK)");
            modeBox.Items.Add("Unicode");
            modeBox.Items.Add("Paste");
            modeBox.SelectedIndex = 0;
            modeBox.SelectedIndexChanged += delegate { SaveSettings(); SendModeAndDelay(); };

            speedPresetBox = new ComboBox();
            speedPresetBox.Items.Add("Fast");
            speedPresetBox.Items.Add("Normal");
            speedPresetBox.Items.Add("Slow");
            speedPresetBox.Items.Add("Custom");
            speedPresetBox.SelectedIndex = 1;

            speedTrackBar = new TrackBar();
            speedTrackBar.Minimum = 1;
            speedTrackBar.Maximum = 100;
            speedTrackBar.Value = 63;
            speedTrackBar.ValueChanged += delegate { SpeedTrackChanged(); };
            speedPresetBox.SelectedIndexChanged += delegate { SpeedPresetChanged(); };

            delayInput = new NumericUpDown();
            delayInput.Minimum = 1;
            delayInput.Maximum = 500;
            delayInput.Increment = 5;
            delayInput.Value = 10;
            delayInput.ValueChanged += delegate
            {
                if (!updatingSpeedControls)
                {
                    SetDelayMs((int)delayInput.Value);
                    SaveSettings();
                    SendModeAndDelay();
                }
            };

            snippetList = new ListBox();
            snippetSearchBox = new TextBox();
            documentStatsLabel = null;
            statusLabel = null;
            progressText = null;
            typingProgress = null;

            draftSaveTimer = new System.Windows.Forms.Timer();
            draftSaveTimer.Interval = 450;
            draftSaveTimer.Tick += delegate { draftSaveTimer.Stop(); SaveDraft(); };

            htmlView = new WebView2();
            htmlView.Dock = DockStyle.Fill;
            Controls.Add(htmlView);
            htmlView.BringToFront();
            InitializeWebUi();
        }

        private async void InitializeWebUi()
        {
            try
            {
                string webDataPath = Path.Combine(dataDir, "WebView2");
                Directory.CreateDirectory(webDataPath);
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, webDataPath);
                await htmlView.EnsureCoreWebView2Async(environment);
                htmlView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                htmlView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                htmlView.CoreWebView2.WebMessageReceived += delegate(object sender, CoreWebView2WebMessageReceivedEventArgs args)
                {
                    try
                    {
                        HandleHtmlMessage(args.TryGetWebMessageAsString());
                    }
                    catch (Exception ex)
                    {
                        SetStatus("UI message error: " + ex.Message, true);
                    }
                };

                string html;
                using (Stream stream = typeof(MainForm).Assembly.GetManifestResourceStream("KeyTyper.ui.index.html"))
                {
                    if (stream == null)
                    {
                        throw new InvalidOperationException("Embedded UI file is missing.");
                    }
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        html = reader.ReadToEnd();
                    }
                }
                htmlView.NavigateToString(html);
            }
            catch (Exception ex)
            {
                SetStatus("Could not start the HTML interface: " + ex.Message, true);
                MessageBox.Show(this,
                    "KeyPulse could not start its embedded browser interface. Make sure Microsoft Edge WebView2 Runtime is installed.\r\n\r\n" + ex.Message,
                    "KeyPulse", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void HandleHtmlMessage(string messageText)
        {
            Dictionary<string, object> message = json.Deserialize<Dictionary<string, object>>(messageText);
            if (message == null || !message.ContainsKey("action")) return;

            string action = Convert.ToString(message["action"]);
            object value;
            switch (action)
            {
                case "ready":
                    SendUiState();
                    break;
                case "text":
                    if (message.TryGetValue("text", out value))
                    {
                        editor.Text = Convert.ToString(value);
                        if (draftSaveTimer != null) draftSaveTimer.Stop();
                        if (draftSaveTimer != null) draftSaveTimer.Start();
                        UpdateDocumentStats();
                    }
                    break;
                case "mode":
                    if (message.TryGetValue("value", out value))
                    {
                        int selected = Math.Max(0, Math.Min(2, Convert.ToInt32(value)));
                        modeBox.SelectedIndex = selected;
                        SaveSettings();
                        SendModeAndDelay();
                    }
                    break;
                case "delay":
                    if (message.TryGetValue("value", out value))
                    {
                        SetDelayMs(Convert.ToInt32(value));
                        SaveSettings();
                        SendModeAndDelay();
                    }
                    break;
                case "type":
                    if (editor.TextLength == 0) SetStatus("Enter some text first.", true);
                    else StartTyping(editor.Text);
                    break;
                case "stop":
                    StopTyping();
                    break;
                case "clear":
                    editor.Clear();
                    SaveDraft();
                    SetStatus("Input cleared.");
                    break;
                case "import":
                    LoadFromClipboard();
                    SendEditorText();
                    break;
                case "copy":
                    CopyEditorText();
                    break;
                case "saveSnippet":
                    SaveCurrentAsSnippet();
                    break;
                case "loadSnippet":
                    if (message.TryGetValue("index", out value)) LoadSnippetByIndex(Convert.ToInt32(value));
                    break;
                case "deleteSnippet":
                    if (message.TryGetValue("index", out value)) DeleteSnippetByIndex(Convert.ToInt32(value));
                    break;
                case "startup":
                    if (message.TryGetValue("enabled", out value))
                    {
                        SetStartup(Convert.ToBoolean(value));
                        SendUiState();
                        SetStatus(IsStartupEnabled() ? "Start with Windows enabled." : "Start with Windows disabled.");
                    }
                    break;
                case "admin":
                    RestartAsAdmin();
                    break;
            }
        }

        private void SendUiMessage(Dictionary<string, object> data)
        {
            if (htmlView == null || htmlView.CoreWebView2 == null) return;
            try
            {
                string script = "window.KeyPulse&&window.KeyPulse.receive(" + json.Serialize(data) + ");";
                htmlView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception)
            {
            }
        }

        private void SendUiState()
        {
            Dictionary<string, object> state = new Dictionary<string, object>();
            state["text"] = editor == null ? "" : editor.Text;
            state["mode"] = modeBox == null ? 0 : modeBox.SelectedIndex;
            state["delay"] = GetDelayMs();
            state["status"] = lastStatusText;
            state["error"] = lastStatusError;
            state["startup"] = IsStartupEnabled();
            state["busy"] = busy;
            state["snippets"] = GetSnippetState();
            SendUiMessage(state);
            SendModeAndDelay();
            UpdateDocumentStats();
        }

        private List<Dictionary<string, object>> GetSnippetState()
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            for (int i = 0; i < snippetNames.Count; i++)
            {
                Dictionary<string, object> item = new Dictionary<string, object>();
                item["index"] = i;
                item["name"] = snippetNames[i];
                item["text"] = snippetTexts[i];
                result.Add(item);
            }
            return result;
        }

        private void SendEditorText()
        {
            Dictionary<string, object> state = new Dictionary<string, object>();
            state["text"] = editor == null ? "" : editor.Text;
            SendUiMessage(state);
            UpdateDocumentStats();
        }

        private void SendModeAndDelay()
        {
            Dictionary<string, object> state = new Dictionary<string, object>();
            state["mode"] = modeBox == null ? 0 : modeBox.SelectedIndex;
            int delay = GetDelayMs();
            state["delay"] = delay;
            state["speed"] = (10.0 / Math.Max(1, delay)).ToString("0.0") + "×";
            state["speedMeta"] = Math.Max(1, (int)Math.Round(12000.0 / Math.Max(1, delay))) + " WPM";
            state["footerDelay"] = delay + "ms";
            state["footerMode"] = modeBox == null ? "VK" : (modeBox.SelectedIndex == 0 ? "VK" : (modeBox.SelectedIndex == 1 ? "Unicode" : "Paste"));
            SendUiMessage(state);
        }

        private void LoadSnippetByIndex(int index)
        {
            int visible = visibleSnippetIndices.IndexOf(index);
            if (visible < 0) return;
            snippetList.SelectedIndex = visible;
            LoadSelectedSnippet();
        }

        private void DeleteSnippetByIndex(int index)
        {
            int visible = visibleSnippetIndices.IndexOf(index);
            if (visible < 0) return;
            snippetList.SelectedIndex = visible;
            DeleteSelectedSnippet();
        }

        private void ShowSettingsDialog()
        {
            Form dialog = new Form();
            dialog.Text = "KeyPulse Settings";
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
            dialog.MinimizeBox = false;
            dialog.MaximizeBox = false;
            dialog.ClientSize = new Size(420, 150);
            CheckBox startup = new CheckBox();
            startup.Text = "Start KeyPulse with Windows";
            startup.Checked = IsStartupEnabled();
            startup.AutoSize = true;
            startup.Location = new Point(20, 20);
            dialog.Controls.Add(startup);
            Button admin = new Button();
            admin.Text = "Restart as administrator";
            admin.Location = new Point(20, 75);
            admin.Width = 190;
            admin.Click += delegate { dialog.Close(); RestartAsAdmin(); };
            dialog.Controls.Add(admin);
            Button save = new Button();
            save.Text = "Save";
            save.DialogResult = DialogResult.OK;
            save.Location = new Point(312, 75);
            save.Width = 80;
            dialog.Controls.Add(save);
            dialog.AcceptButton = save;
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                SetStartup(startup.Checked);
                SendUiState();
                SetStatus(startup.Checked ? "Start with Windows enabled." : "Start with Windows disabled.");
            }
            dialog.Dispose();
        }

        private void BuildUi()
        {
            Text = "KeyPulse";
            ClientSize = new Size(1280, 820);
            MinimumSize = new Size(1000, 680);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10F);
            Icon = appIcon;
            KeyPreview = true;
            ShowInTaskbar = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(24, 16, 24, 14);
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            Controls.Add(root);

            TableLayoutPanel header = new TableLayoutPanel();
            header.Dock = DockStyle.Fill;
            header.ColumnCount = 3;
            header.RowCount = 1;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210F));
            root.Controls.Add(header, 0, 0);

            logoPictureBox = new PictureBox();
            logoPictureBox.Dock = DockStyle.Fill;
            logoPictureBox.SizeMode = PictureBoxSizeMode.CenterImage;
            logoPictureBox.Image = CreateLogoBitmap(56);
            header.Controls.Add(logoPictureBox, 0, 0);

            Panel brandPanel = new Panel();
            brandPanel.Dock = DockStyle.Fill;
            header.Controls.Add(brandPanel, 1, 0);
            Label brand = new Label();
            brand.AutoSize = true;
            brand.Text = "KeyPulse";
            brand.Font = new Font("Segoe UI", 28F, FontStyle.Bold);
            brand.Location = new Point(8, 3);
            brandPanel.Controls.Add(brand);
            Label tagline = new Label();
            tagline.AutoSize = true;
            tagline.Text = "text  →  real keystrokes";
            tagline.Font = new Font("Segoe UI", 12F);
            tagline.ForeColor = Color.FromArgb(164, 184, 210);
            tagline.Location = new Point(12, 49);
            brandPanel.Controls.Add(tagline);

            MenuStrip appMenu = new MenuStrip();
            appMenu.Dock = DockStyle.Left;
            appMenu.AutoSize = false;
            appMenu.Size = new Size(138, 48);
            appMenu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColorTable());
            appMenu.BackColor = Color.FromArgb(48, 52, 60);
            ToolStripMenuItem appMenuItem = new ToolStripMenuItem("Settings");
            startupMenuItem = new ToolStripMenuItem("Start with Windows");
            startupMenuItem.Click += delegate
            {
                try
                {
                    SetStartup(!IsStartupEnabled());
                    startupMenuItem.Checked = IsStartupEnabled();
                    SetStatus(IsStartupEnabled() ? "KeyPulse will start with Windows." : "Start with Windows disabled.");
                }
                catch (Exception ex)
                {
                    SetStatus("Could not update startup setting: " + ex.Message, true);
                }
            };
            appMenuItem.DropDownItems.Add(startupMenuItem);
            ToolStripMenuItem adminMenuItem = new ToolStripMenuItem("Restart as administrator");
            adminMenuItem.Click += delegate { RestartAsAdmin(); };
            appMenuItem.DropDownItems.Add(adminMenuItem);
            appMenuItem.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem exitMenuItem = new ToolStripMenuItem("Exit");
            exitMenuItem.Click += delegate { ExitApp(); };
            appMenuItem.DropDownItems.Add(exitMenuItem);
            appMenu.Items.Add(appMenuItem);
            FlowLayoutPanel utilities = new FlowLayoutPanel();
            utilities.Dock = DockStyle.Fill;
            utilities.FlowDirection = FlowDirection.LeftToRight;
            utilities.WrapContents = false;
            utilities.Padding = new Padding(0, 6, 0, 0);
            utilities.Controls.Add(appMenu);
            Button helpButton = MakeButton("?", delegate { ShowHelp(); }, 40);
            helpButton.Width = 42;
            helpButton.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
            helpButton.TextAlign = ContentAlignment.MiddleCenter;
            utilities.Controls.Add(helpButton);
            header.Controls.Add(utilities, 2, 0);
            appMenuItem.DropDownOpening += delegate
            {
                try { startupMenuItem.Checked = IsStartupEnabled(); } catch (Exception) { }
                adminMenuItem.Enabled = !IsElevated();
                if (IsElevated()) adminMenuItem.Text = "Running as administrator";
            };

            TableLayoutPanel toolbar = new TableLayoutPanel();
            toolbar.Dock = DockStyle.Fill;
            toolbar.Padding = new Padding(12, 10, 12, 10);
            toolbar.ColumnCount = 3;
            toolbar.RowCount = 1;
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));
            root.Controls.Add(toolbar, 0, 1);

            TableLayoutPanel modeGroup = new TableLayoutPanel();
            modeGroup.Dock = DockStyle.Fill;
            modeGroup.ColumnCount = 2;
            modeGroup.RowCount = 1;
            modeGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70F));
            modeGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            toolbar.Controls.Add(modeGroup, 0, 0);
            Label modeLabel = new Label();
            modeLabel.Text = "Mode";
            modeLabel.Dock = DockStyle.Fill;
            modeLabel.TextAlign = ContentAlignment.MiddleLeft;
            modeGroup.Controls.Add(modeLabel, 0, 0);
            modeBox = new ComboBox();
            modeBox.Dock = DockStyle.Fill;
            modeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            modeBox.Items.Add("Type - VK keys");
            modeBox.Items.Add("Unicode");
            modeBox.Items.Add("Paste");
            modeBox.SelectedIndex = 0;
            modeBox.SelectedIndexChanged += delegate { SaveSettings(); };
            modeGroup.Controls.Add(modeBox, 1, 0);

            TableLayoutPanel speedGroup = new TableLayoutPanel();
            speedGroup.Dock = DockStyle.Fill;
            speedGroup.ColumnCount = 7;
            speedGroup.RowCount = 1;
            speedGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58F));
            speedGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74F));
            speedGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76F));
            speedGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30F));
            speedGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            speedGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30F));
            speedGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58F));
            toolbar.Controls.Add(speedGroup, 1, 0);
            Label speedLabel = new Label();
            speedLabel.Text = "Delay";
            speedLabel.Dock = DockStyle.Fill;
            speedLabel.TextAlign = ContentAlignment.MiddleLeft;
            speedGroup.Controls.Add(speedLabel, 0, 0);
            speedPresetBox = new ComboBox();
            speedPresetBox.Dock = DockStyle.Fill;
            speedPresetBox.DropDownStyle = ComboBoxStyle.DropDownList;
            speedPresetBox.Items.Add("Fast");
            speedPresetBox.Items.Add("Normal");
            speedPresetBox.Items.Add("Slow");
            speedPresetBox.Items.Add("Custom");
            speedGroup.Controls.Add(speedPresetBox, 1, 0);
            delayInput = new NumericUpDown();
            delayInput.Minimum = 1;
            delayInput.Maximum = 500;
            delayInput.Increment = 5;
            delayInput.Width = 72;
            delayInput.Dock = DockStyle.Fill;
            delayInput.TextAlign = HorizontalAlignment.Center;
            delayInput.ValueChanged += delegate
            {
                if (!updatingSpeedControls)
                {
                    SetDelayMs((int)delayInput.Value);
                    SaveSettings();
                }
            };
            speedGroup.Controls.Add(delayInput, 2, 0);
            Button slowerButton = MakeButton("−", delegate { speedTrackBar.Value = Math.Max(speedTrackBar.Minimum, speedTrackBar.Value - 5); }, 34);
            slowerButton.Dock = DockStyle.Fill;
            slowerButton.TextAlign = ContentAlignment.MiddleCenter;
            speedGroup.Controls.Add(slowerButton, 3, 0);
            speedTrackBar = new TrackBar();
            speedTrackBar.Dock = DockStyle.Fill;
            speedTrackBar.Minimum = 1;
            speedTrackBar.Maximum = 100;
            speedTrackBar.Value = 63;
            speedTrackBar.TickFrequency = 10;
            speedTrackBar.TickStyle = TickStyle.BottomRight;
            speedGroup.Controls.Add(speedTrackBar, 4, 0);
            Button fasterButton = MakeButton("+", delegate { speedTrackBar.Value = Math.Min(speedTrackBar.Maximum, speedTrackBar.Value + 5); }, 34);
            fasterButton.Dock = DockStyle.Fill;
            fasterButton.TextAlign = ContentAlignment.MiddleCenter;
            speedGroup.Controls.Add(fasterButton, 5, 0);
            speedValueLabel = new Label();
            speedValueLabel.Dock = DockStyle.Fill;
            speedValueLabel.TextAlign = ContentAlignment.MiddleRight;
            speedGroup.Controls.Add(speedValueLabel, 6, 0);
            speedPresetBox.SelectedIndexChanged += delegate { SpeedPresetChanged(); };
            speedTrackBar.ValueChanged += delegate { SpeedTrackChanged(); };
            speedPresetBox.SelectedIndex = 1;
            UpdateSpeedValueLabel();

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.RightToLeft;
            actions.WrapContents = false;
            actions.Padding = new Padding(8, 4, 0, 0);
            toolbar.Controls.Add(actions, 2, 0);
            typeButton = MakeButton("Type now  (F5)", delegate { StartTyping(editor.Text); }, 44);
            typeButton.Width = 190;
            typeButton.TextAlign = ContentAlignment.MiddleCenter;
            typeButton.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            actions.Controls.Add(typeButton);
            stopButton = MakeButton("Stop  (Esc)", delegate { StopTyping(); }, 44);
            stopButton.Width = 116;
            stopButton.TextAlign = ContentAlignment.MiddleCenter;
            stopButton.Enabled = false;
            actions.Controls.Add(stopButton);

            TableLayoutPanel workspace = new TableLayoutPanel();
            workspace.Dock = DockStyle.Fill;
            workspace.Padding = new Padding(0, 10, 0, 10);
            workspace.ColumnCount = 2;
            workspace.RowCount = 1;
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68F));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32F));
            workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(workspace, 0, 2);

            Panel editorCard = new Panel();
            editorCard.Dock = DockStyle.Fill;
            editorCard.Padding = new Padding(14);
            editorCard.Margin = new Padding(0, 0, 12, 0);
            workspace.Controls.Add(editorCard, 0, 0);
            TableLayoutPanel editorLayout = new TableLayoutPanel();
            editorLayout.Dock = DockStyle.Fill;
            editorLayout.ColumnCount = 1;
            editorLayout.RowCount = 3;
            editorLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
            editorLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            editorLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            editorCard.Controls.Add(editorLayout);

            TableLayoutPanel editorHeader = new TableLayoutPanel();
            editorHeader.Dock = DockStyle.Fill;
            editorHeader.ColumnCount = 4;
            editorHeader.RowCount = 1;
            editorHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            editorHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84F));
            editorHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112F));
            editorHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
            editorLayout.Controls.Add(editorHeader, 0, 0);
            Label inputTitle = new Label();
            inputTitle.Text = "Input text";
            inputTitle.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
            inputTitle.Dock = DockStyle.Fill;
            inputTitle.TextAlign = ContentAlignment.MiddleLeft;
            editorHeader.Controls.Add(inputTitle, 0, 0);
            Button clearButton = MakeButton("Clear", delegate { editor.Clear(); SetStatus("Input cleared."); }, 32);
            clearButton.Width = 78;
            clearButton.TextAlign = ContentAlignment.MiddleCenter;
            editorHeader.Controls.Add(clearButton, 1, 0);
            Button clipButton = MakeButton("Import", delegate { LoadFromClipboard(); }, 32);
            clipButton.Width = 104;
            clipButton.TextAlign = ContentAlignment.MiddleCenter;
            editorHeader.Controls.Add(clipButton, 2, 0);
            Button copyButton = MakeButton("Copy text", delegate { CopyEditorText(); }, 32);
            copyButton.Width = 88;
            copyButton.TextAlign = ContentAlignment.MiddleCenter;
            editorHeader.Controls.Add(copyButton, 3, 0);

            Panel editorHost = new Panel();
            editorHost.Dock = DockStyle.Fill;
            editorHost.Padding = new Padding(0, 5, 0, 5);
            editorLayout.Controls.Add(editorHost, 0, 1);
            editor = new TextBox();
            editor.Dock = DockStyle.Fill;
            editor.Multiline = true;
            editor.ScrollBars = ScrollBars.Both;
            editor.WordWrap = false;
            editor.AcceptsReturn = true;
            editor.AcceptsTab = true;
            editor.Font = new Font("Consolas", 13F);
            editor.BorderStyle = BorderStyle.FixedSingle;
            editor.HideSelection = false;
            editor.TextChanged += delegate { UpdateDocumentStats(); };
            editorHost.Controls.Add(editor);
            documentStatsLabel = new Label();
            documentStatsLabel.Dock = DockStyle.Fill;
            documentStatsLabel.Padding = new Padding(8, 0, 0, 0);
            documentStatsLabel.TextAlign = ContentAlignment.MiddleLeft;
            editorLayout.Controls.Add(documentStatsLabel, 0, 2);

            Panel snippetsCard = new Panel();
            snippetsCard.Dock = DockStyle.Fill;
            snippetsCard.Padding = new Padding(14);
            snippetsCard.Margin = new Padding(12, 0, 0, 0);
            workspace.Controls.Add(snippetsCard, 1, 0);
            TableLayoutPanel snippetsLayout = new TableLayoutPanel();
            snippetsLayout.Dock = DockStyle.Fill;
            snippetsLayout.ColumnCount = 1;
            snippetsLayout.RowCount = 4;
            snippetsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));
            snippetsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
            snippetsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            snippetsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
            snippetsCard.Controls.Add(snippetsLayout);

            TableLayoutPanel snippetsHeader = new TableLayoutPanel();
            snippetsHeader.Dock = DockStyle.Fill;
            snippetsHeader.ColumnCount = 2;
            snippetsHeader.RowCount = 1;
            snippetsHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            snippetsHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48F));
            snippetsLayout.Controls.Add(snippetsHeader, 0, 0);
            Label snippetsTitle = new Label();
            snippetsTitle.Dock = DockStyle.Fill;
            snippetsTitle.Text = "Saved snippets";
            snippetsTitle.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
            snippetsTitle.TextAlign = ContentAlignment.MiddleLeft;
            snippetsHeader.Controls.Add(snippetsTitle, 0, 0);
            Button addSnippetButton = MakeButton("+", delegate { SaveCurrentAsSnippet(); }, 36);
            addSnippetButton.Width = 40;
            addSnippetButton.TextAlign = ContentAlignment.MiddleCenter;
            addSnippetButton.Font = new Font("Segoe UI", 17F, FontStyle.Regular);
            snippetsHeader.Controls.Add(addSnippetButton, 1, 0);

            Panel searchHost = new Panel();
            searchHost.Dock = DockStyle.Fill;
            searchHost.Padding = new Padding(0, 4, 0, 4);
            snippetsLayout.Controls.Add(searchHost, 0, 1);
            snippetSearchBox = new TextBox();
            snippetSearchBox.Dock = DockStyle.Fill;
            snippetSearchBox.BorderStyle = BorderStyle.FixedSingle;
            snippetSearchBox.TextChanged += delegate
            {
                if (snippetSearchPlaceholder != null) snippetSearchPlaceholder.Visible = snippetSearchBox.TextLength == 0;
                RefreshSnippetList(-1);
            };
            searchHost.Controls.Add(snippetSearchBox);
            snippetSearchPlaceholder = new Label();
            snippetSearchPlaceholder.Text = "Search snippets...";
            snippetSearchPlaceholder.AutoSize = true;
            snippetSearchPlaceholder.ForeColor = Color.FromArgb(176, 181, 190);
            snippetSearchPlaceholder.Location = new Point(10, 10);
            snippetSearchPlaceholder.Cursor = Cursors.IBeam;
            snippetSearchPlaceholder.Click += delegate { snippetSearchBox.Focus(); };
            searchHost.Controls.Add(snippetSearchPlaceholder);
            snippetSearchPlaceholder.BringToFront();

            snippetList = new ListBox();
            snippetList.Dock = DockStyle.Fill;
            snippetList.IntegralHeight = false;
            snippetList.BorderStyle = BorderStyle.None;
            snippetList.HorizontalScrollbar = true;
            snippetList.Font = new Font("Segoe UI", 10.5F);
            snippetList.DrawMode = DrawMode.OwnerDrawFixed;
            snippetList.ItemHeight = 72;
            snippetList.DrawItem += DrawSnippetCard;
            snippetList.DoubleClick += delegate { LoadSelectedSnippet(); };
            snippetsLayout.Controls.Add(snippetList, 0, 2);
            FlowLayoutPanel snippetActions = new FlowLayoutPanel();
            snippetActions.Dock = DockStyle.Fill;
            snippetActions.WrapContents = false;
            snippetActions.Padding = new Padding(0, 5, 0, 0);
            snippetsLayout.Controls.Add(snippetActions, 0, 3);
            Button newSnippetButton = MakeButton("+  New snippet", delegate { SaveCurrentAsSnippet(); }, 36);
            newSnippetButton.Width = 138;
            newSnippetButton.TextAlign = ContentAlignment.MiddleCenter;
            newSnippetButton.Padding = new Padding(0);
            snippetActions.Controls.Add(newSnippetButton);
            Button deleteButton = MakeButton("Delete", delegate { DeleteSelectedSnippet(); }, 36);
            deleteButton.Width = 100;
            deleteButton.TextAlign = ContentAlignment.MiddleCenter;
            deleteButton.Padding = new Padding(0);
            snippetActions.Controls.Add(deleteButton);

            TableLayoutPanel footer = new TableLayoutPanel();
            footer.Dock = DockStyle.Fill;
            footer.ColumnCount = 3;
            footer.RowCount = 1;
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190F));
            root.Controls.Add(footer, 0, 3);
            statusLabel = new Label();
            statusLabel.Dock = DockStyle.Fill;
            statusLabel.Text = "Ready.";
            statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            statusLabel.Padding = new Padding(4, 0, 12, 0);
            footer.Controls.Add(statusLabel, 0, 0);
            progressText = new Label();
            progressText.Dock = DockStyle.Fill;
            progressText.TextAlign = ContentAlignment.MiddleRight;
            progressText.Padding = new Padding(0, 0, 10, 0);
            footer.Controls.Add(progressText, 1, 0);
            typingProgress = new ProgressBar();
            typingProgress.Dock = DockStyle.Fill;
            typingProgress.Minimum = 0;
            typingProgress.Maximum = 1000;
            typingProgress.Style = ProgressBarStyle.Continuous;
            typingProgress.Margin = new Padding(4, 10, 12, 10);
            footer.Controls.Add(typingProgress, 2, 0);

            tips = new ToolTip();
            tips.SetToolTip(editor, "Text is sent as keyboard input to the focused application.");
            tips.SetToolTip(typeButton, "Type this text into the app you were just using.");
            tips.SetToolTip(stopButton, "Stop the current typing run; Esc also works.");
            tips.SetToolTip(clipButton, "Import text from the clipboard.");
            tips.SetToolTip(copyButton, "Copy the editor contents to the clipboard.");
            tips.SetToolTip(clearButton, "Clear the editor contents.");
            tips.SetToolTip(addSnippetButton, "Save the current text as a snippet.");
            tips.SetToolTip(newSnippetButton, "Save the current text as a new snippet.");
            tips.SetToolTip(deleteButton, "Delete the selected snippet.");
            tips.SetToolTip(snippetList, "Double-click a snippet to load it.");
            tips.SetToolTip(modeBox, "VK types real key presses; Unicode and Paste are alternate modes.");
            tips.SetToolTip(speedTrackBar, "Adjust the delay between key presses.");
            tips.SetToolTip(delayInput, "Milliseconds between key presses (1–500).");
            tips.SetToolTip(helpButton, "Show keyboard shortcuts and usage help.");
            UpdateDocumentStats();
            ApplyDarkTheme(this);
        }
        private void ApplyDarkTheme(Control parent)
        {
            Color background = Color.FromArgb(43, 46, 53);
            Color surface = Color.FromArgb(53, 57, 65);
            Color input = Color.FromArgb(64, 69, 79);
            Color foreground = Color.FromArgb(244, 245, 247);
            Color muted = Color.FromArgb(184, 190, 201);
            Color accent = Color.FromArgb(53, 116, 207);

            parent.BackColor = parent == this ? background : surface;
            parent.ForeColor = foreground;
            foreach (Control child in parent.Controls)
            {
                child.ForeColor = foreground;
                if (child is Panel || child is TableLayoutPanel || child is FlowLayoutPanel)
                {
                    child.BackColor = surface;
                }
                else if (child is TextBox || child is ListBox || child is ComboBox || child is TrackBar || child is ProgressBar || child is NumericUpDown)
                {
                    child.BackColor = input;
                    child.ForeColor = foreground;
                }
                else if (child is Button)
                {
                    Button button = (Button)child;
                    button.BackColor = button.Text.StartsWith("Type now", StringComparison.Ordinal)
                        ? accent : (button.Text.StartsWith("Stop", StringComparison.Ordinal)
                            ? Color.FromArgb(126, 66, 71) : Color.FromArgb(69, 74, 84));
                    button.ForeColor = foreground;
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = Color.FromArgb(91, 98, 111);
                    button.FlatAppearance.MouseOverBackColor = Color.FromArgb(78, 84, 96);
                    button.UseVisualStyleBackColor = false;
                }
                else if (child is Label)
                {
                    child.BackColor = Color.Transparent;
                    if (child == snippetSearchPlaceholder) child.ForeColor = muted;
                }
                else if (child is GroupBox)
                {
                    child.BackColor = background;
                }

                if (child.HasChildren)
                {
                    ApplyDarkTheme(child);
                }
            }
        }

        private Button MakeButton(string text, EventHandler handler, int height)
        {
            Button button = new Button();
            button.Text = text;
            button.Width = 214;
            button.Height = height;
            button.Margin = new Padding(0, 0, 0, 6);
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Padding = new Padding(8, 0, 0, 0);
            button.Click += handler;
            return button;
        }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "KeyPulse types the text in the editor into the currently focused app.\r\n\r\n" +
                "F5 or Ctrl+Alt+Space: start typing\r\n" +
                "Esc: stop typing immediately\r\n" +
                "Ctrl+S: save the editor text as a snippet\r\n\r\n" +
                "VK mode sends layout-aware key presses. Unicode mode sends Unicode input. " +
                "Paste mode uses the clipboard and may not work in every target app. " +
                "For elevated apps, restart KeyPulse as administrator.",
                "KeyPulse Help", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DrawSnippetCard(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || e.Index >= visibleSnippetIndices.Count)
            {
                return;
            }

            int dataIndex = visibleSnippetIndices[e.Index];
            Rectangle card = new Rectangle(e.Bounds.X + 3, e.Bounds.Y + 3,
                Math.Max(1, e.Bounds.Width - 8), Math.Max(1, e.Bounds.Height - 7));
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color cardColor = selected ? Color.FromArgb(67, 82, 105) : Color.FromArgb(53, 59, 70);
            using (SolidBrush brush = new SolidBrush(cardColor))
            using (Pen border = new Pen(selected ? Color.FromArgb(85, 153, 235) : Color.FromArgb(75, 83, 98)))
            {
                e.Graphics.FillRectangle(brush, card);
                e.Graphics.DrawRectangle(border, card);
            }

            Rectangle textArea = new Rectangle(card.X + 12, card.Y + 8, card.Width - 22, card.Height - 16);
            using (Font titleFont = new Font("Segoe UI", 10F, FontStyle.Bold))
            using (Font previewFont = new Font("Consolas", 8.5F, FontStyle.Regular))
            using (Font metaFont = new Font("Segoe UI", 8F, FontStyle.Regular))
            using (SolidBrush titleBrush = new SolidBrush(Color.FromArgb(244, 245, 247)))
            using (SolidBrush previewBrush = new SolidBrush(Color.FromArgb(190, 198, 211)))
            using (SolidBrush metaBrush = new SolidBrush(Color.FromArgb(151, 163, 181)))
            {
                TextRenderer.DrawText(e.Graphics, snippetNames[dataIndex], titleFont,
                    new Rectangle(textArea.X, textArea.Y, textArea.Width, 20), titleBrush.Color,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

                string preview = snippetTexts[dataIndex].Replace("\r", " ").Replace("\n", "  ").Trim();
                TextRenderer.DrawText(e.Graphics, preview, previewFont,
                    new Rectangle(textArea.X, textArea.Y + 23, textArea.Width, 20), previewBrush.Color,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);

                string meta = snippetTexts[dataIndex].Length + " characters  •  Double-click to load";
                TextRenderer.DrawText(e.Graphics, meta, metaFont,
                    new Rectangle(textArea.X, textArea.Y + 43, textArea.Width, 16), metaBrush.Color,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
            }
            e.DrawFocusRectangle();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotkey();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (hotkeyRegistered)
            {
                Native.UnregisterHotKey(Handle, HOTKEY_ID);
                hotkeyRegistered = false;
            }
            if (f5HotkeyRegistered)
            {
                Native.UnregisterHotKey(Handle, F5_HOTKEY_ID);
                f5HotkeyRegistered = false;
            }
            RemoveEscapeHook();
            base.OnHandleDestroyed(e);
        }

        private void RegisterHotkey()
        {
            hotkeyRegistered = Native.RegisterHotKey(
                Handle, HOTKEY_ID,
                Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT,
                (uint)Keys.Space);

            if (!hotkeyRegistered)
            {
                hotkeyRegistered = Native.RegisterHotKey(
                    Handle, HOTKEY_ID,
                    Native.MOD_CONTROL | Native.MOD_ALT,
                    (uint)Keys.Space);
            }

            f5HotkeyRegistered = Native.RegisterHotKey(Handle, F5_HOTKEY_ID, Native.MOD_NOREPEAT, (uint)Keys.F5);
            if (!f5HotkeyRegistered)
            {
                f5HotkeyRegistered = Native.RegisterHotKey(Handle, F5_HOTKEY_ID, 0, (uint)Keys.F5);
            }

            if (hotkeyRegistered && f5HotkeyRegistered)
            {
                SetStatus("Ready. Press Ctrl+Alt+Space or F5 anywhere to type.");
            }
            else if (hotkeyRegistered)
            {
                SetStatus("Ready. Press " + hotkeyText + " anywhere to type. F5 is busy.");
            }
            else if (f5HotkeyRegistered)
            {
                SetStatus("Ctrl+Alt+Space is busy. Press F5 anywhere to type.", true);
            }
            else
            {
                SetStatus("KeyPulse hotkeys are busy. Close the other KeyPulse window and reopen this one.", true);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                StartTyping(editor.Text);
                return;
            }
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == STOP_HOTKEY_ID)
            {
                StopTyping();
                return;
            }
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == F5_HOTKEY_ID)
            {
                StartTyping(editor.Text);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            if (startHidden)
            {
                ShowInTaskbar = true;
                WindowState = FormWindowState.Minimized;
            }

            if (autoTypeFile != null)
            {
                string path = autoTypeFile;
                System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
                timer.Interval = 800;
                timer.Tick += delegate
                {
                    timer.Stop();
                    timer.Dispose();

                    string content;
                    try
                    {
                        content = File.ReadAllText(path);
                    }
                    catch (Exception ex)
                    {
                        SetStatus("Cannot read type file: " + ex.Message, true);
                        ShowBalloon("KeyTyper", "Cannot read the type file.");
                        if (exitAfter)
                        {
                            ExitApp();
                        }
                        return;
                    }

                    editor.Text = content;
                    StartTyping(content);
                };
                timer.Start();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                StartTyping(editor.Text);
                return;
            }

            if (e.Control && e.KeyCode == Keys.S)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                SaveCurrentAsSnippet();
                return;
            }

            if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                StartTyping(editor.Text);
                return;
            }

            base.OnKeyDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            if (!suppressDraft)
            {
                SaveDraft();
            }

            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }

            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (trackTimer != null)
            {
                trackTimer.Stop();
                trackTimer.Dispose();
            }
            if (appIcon != null)
            {
                appIcon.Dispose();
            }
            base.OnFormClosed(e);
        }

        private void StartTracking()
        {
            trackTimer = new System.Windows.Forms.Timer();
            trackTimer.Interval = 120;
            trackTimer.Tick += delegate
            {
                IntPtr foreground = Native.GetForegroundWindow();
                if (foreground == IntPtr.Zero || foreground == Handle)
                {
                    return;
                }

                uint pid;
                Native.GetWindowThreadProcessId(foreground, out pid);
                if (pid == ownPid)
                {
                    return;
                }

                lastExternalWindow = foreground;
            };
            trackTimer.Start();
        }

        private void StartTyping(string text)
        {
            if (text == null)
            {
                text = "";
            }

            if (text.Length == 0)
            {
                SetStatus("Nothing to type - put some text in the box first.", true);
                if (!Visible)
                {
                    ShowMain();
                }
                ShowBalloon("Nothing to type", "Put some text in the box first.");
                if (exitAfter)
                {
                    ExitApp();
                }
                return;
            }

            if (busy)
            {
                SetStatus("Already typing - wait a moment.", true);
                return;
            }

            busy = true;
            cancelRequested = false;
            SendUiFlag("busy", true);
            SaveSettings();
            if (!suppressDraft)
            {
                SaveDraft();
            }

            int mode = modeBox.SelectedIndex;
            int delay = GetDelayMs();
            bool exitWhenDone = exitAfter;

            IntPtr foreground = Native.GetForegroundWindow();
            IntPtr target = IntPtr.Zero;

            if (foreground != IntPtr.Zero && foreground != Handle)
            {
                uint foregroundPid;
                Native.GetWindowThreadProcessId(foreground, out foregroundPid);
                if (foregroundPid != ownPid)
                {
                    target = foreground;
                }
            }

            if (target == IntPtr.Zero)
            {
                target = lastExternalWindow;
            }

            if (target == IntPtr.Zero || !Native.IsWindow(target))
            {
                target = Native.FindFallbackWindow(Handle, ownPid);
            }

            if (Visible)
            {
                ShowInTaskbar = true;
                WindowState = FormWindowState.Minimized;
            }

            if (target != IntPtr.Zero)
            {
                Native.ForceForeground(target);
            }

            SetStatus("Typing " + text.Length + " characters...");
            SetTypingProgress(0, text.Length);
            if (stopButton != null)
            {
                stopButton.Enabled = true;
            }
            if (stopTrayItem != null)
            {
                stopTrayItem.Enabled = true;
            }
            stopHotkeyRegistered = Native.RegisterHotKey(
                Handle, STOP_HOTKEY_ID, Native.MOD_NOREPEAT, Native.VK_ESCAPE);
            InstallEscapeHook();

            Thread worker = new Thread((ThreadStart)delegate { DoType(text, mode, delay, exitWhenDone); });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        private void DoType(string text, int mode, int delayMs, bool exitWhenDone)
        {
            string status;
            bool error = false;
            string balloonTitle = null;
            string balloonText = null;

            try
            {
                if (!WaitOrStop(280) ||
                    !Native.WaitModifiersReleased(2000, delegate { return ShouldStop(); }) ||
                    !WaitOrStop(80))
                {
                    status = "Typing stopped before it started.";
                    goto Finish;
                }

                if (mode == 2)
                {
                    if (ShouldStop())
                    {
                        status = "Typing stopped before paste.";
                        goto Finish;
                    }
                    try
                    {
                        Clipboard.SetText(text);
                    }
                    catch (Exception ex)
                    {
                        status = "Clipboard error: " + ex.Message;
                        error = true;
                        goto Finish;
                    }

                    if (!WaitOrStop(120))
                    {
                        status = "Typing stopped before paste.";
                        goto Finish;
                    }
                    if (Typer.SendCtrlV())
                    {
                        status = "Pasted " + text.Length + " characters with Ctrl+V.";
                        ReportTypingProgress(text.Length, text.Length);
                    }
                    else
                    {
                        status = "Paste failed - the target may be elevated or blocking input.";
                        error = true;
                        balloonTitle = "Paste failed";
                        balloonText = "Try Type (VK) mode instead. For elevated apps, restart KeyTyper as administrator.";
                    }
                }
                else
                {
                    TypeResult result = Typer.TypeText(
                        text, delayMs, mode == 1,
                        delegate { return ShouldStop(); },
                        delegate(int current, int total) { ReportTypingProgress(current, total); });
                    if (result.Cancelled)
                    {
                        status = "Typing stopped after " + result.Typed + " characters.";
                    }
                    else if (result.Failed > 0)
                    {
                        status = "Typing blocked after " + result.Typed + " characters. If the target app runs as administrator, restart KeyTyper as administrator.";
                        error = true;
                        balloonTitle = "Typing blocked";
                        balloonText = "Windows rejected the injected input. Restart KeyTyper as administrator and try again.";
                    }
                    else
                    {
                        status = "Typed " + result.Typed + " characters.";
                        if (result.UnicodeFallbacks > 0)
                        {
                            status += " (" + result.UnicodeFallbacks + " via Unicode fallback)";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                status = "Error: " + ex.Message;
                error = true;
            }

        Finish:
            PostUi(delegate
            {
                if (stopHotkeyRegistered)
                {
                    Native.UnregisterHotKey(Handle, STOP_HOTKEY_ID);
                    stopHotkeyRegistered = false;
                }
                RemoveEscapeHook();
                busy = false;
                cancelRequested = false;
                SendUiFlag("busy", false);
                if (stopButton != null) stopButton.Enabled = false;
                if (stopTrayItem != null) stopTrayItem.Enabled = false;
                SetStatus(status, error);
                if (balloonTitle != null)
                {
                    ShowBalloon(balloonTitle, balloonText);
                }
                if (exitWhenDone)
                {
                    ExitApp();
                }
            });
        }

        private void StopTyping()
        {
            if (!busy)
            {
                return;
            }

            cancelRequested = true;
            SetStatus("Stopping typing...");
        }

        private bool ShouldStop()
        {
            if (cancelRequested)
            {
                return true;
            }

            return (Native.GetAsyncKeyState(Native.VK_ESCAPE) & unchecked((short)0x8000)) != 0;
        }

        private void InstallEscapeHook()
        {
            if (escapeHookHandle != IntPtr.Zero)
            {
                return;
            }

            escapeHookCallback = new Native.LowLevelKeyboardProc(EscapeHookProc);
            escapeHookHandle = Native.SetWindowsHookEx(
                Native.WH_KEYBOARD_LL, escapeHookCallback, Native.GetModuleHandle(null), 0);
        }

        private IntPtr EscapeHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && busy &&
                (wParam.ToInt32() == Native.WM_KEYDOWN || wParam.ToInt32() == Native.WM_SYSKEYDOWN))
            {
                Native.KBDLLHOOKSTRUCT key = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                    lParam, typeof(Native.KBDLLHOOKSTRUCT));
                if (key.vkCode == Native.VK_ESCAPE && (key.flags & 0x10) == 0)
                {
                    cancelRequested = true;
                    return new IntPtr(1);
                }
            }

            return Native.CallNextHookEx(escapeHookHandle, nCode, wParam, lParam);
        }

        private void RemoveEscapeHook()
        {
            if (escapeHookHandle != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(escapeHookHandle);
                escapeHookHandle = IntPtr.Zero;
            }
            escapeHookCallback = null;
        }

        private bool WaitOrStop(int milliseconds)
        {
            int waited = 0;
            while (waited < milliseconds)
            {
                if (ShouldStop())
                {
                    return false;
                }

                int slice = Math.Min(10, milliseconds - waited);
                Thread.Sleep(slice);
                waited += slice;
            }

            return !ShouldStop();
        }

        private void PostUi(MethodInvoker action)
        {
            if (IsDisposed)
            {
                return;
            }

            try
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch (Exception)
            {
            }
        }

        private void LoadFromClipboard()
        {
            try
            {
                if (!Clipboard.ContainsText())
                {
                    SetStatus("Clipboard has no text.", true);
                    return;
                }

                string text = Clipboard.GetText();
                editor.Text = text;
                editor.Focus();
                SetStatus("Loaded " + text.Length + " characters from clipboard.");
            }
            catch (Exception ex)
            {
                SetStatus("Clipboard error: " + ex.Message, true);
            }
        }

        private void CopyEditorText()
        {
            try
            {
                if (editor.TextLength == 0)
                {
                    SetStatus("There is no text to copy.", true);
                    return;
                }
                Clipboard.SetText(editor.Text);
                SetStatus("Copied " + editor.TextLength + " characters.");
            }
            catch (Exception ex)
            {
                SetStatus("Clipboard error: " + ex.Message, true);
            }
        }

        private void SendUiFlag(string key, object value)
        {
            Dictionary<string, object> state = new Dictionary<string, object>();
            state[key] = value;
            SendUiMessage(state);
        }

        private void SaveCurrentAsSnippet()
        {
            string text = editor.Text;
            if (text == null || text.Trim().Length == 0)
            {
                SetStatus("Nothing to save - the box is empty.", true);
                return;
            }

            string name = MakeSnippetName(text);
            snippetNames.Add(name);
            snippetTexts.Add(text);
            SaveSnippets();
            if (snippetSearchBox != null) snippetSearchBox.Clear();
            RefreshSnippetList(snippetNames.Count - 1);
            SendUiMessage(new Dictionary<string, object> { { "snippets", GetSnippetState() } });
            SetStatus("Saved snippet: " + name);
        }

        private void DeleteSelectedSnippet()
        {
            int selected = snippetList.SelectedIndex;
            if (selected < 0 || selected >= visibleSnippetIndices.Count)
            {
                SetStatus("Select a snippet to delete first.", true);
                return;
            }

            int index = visibleSnippetIndices[selected];

            string name = snippetNames[index];
            DialogResult answer = MessageBox.Show(
                this,
                "Delete snippet \"" + name + "\"?",
                "KeyTyper",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                return;
            }

            snippetNames.RemoveAt(index);
            snippetTexts.RemoveAt(index);
            SaveSnippets();

            int newIndex = index;
            if (newIndex >= snippetNames.Count)
            {
                newIndex = snippetNames.Count - 1;
            }
            RefreshSnippetList(newIndex);
            SendUiMessage(new Dictionary<string, object> { { "snippets", GetSnippetState() } });
            SetStatus("Deleted snippet.");
        }

        private void LoadSelectedSnippet()
        {
            int selected = snippetList.SelectedIndex;
            if (selected < 0 || selected >= visibleSnippetIndices.Count)
            {
                return;
            }

            int index = visibleSnippetIndices[selected];

            editor.Text = snippetTexts[index];
            editor.SelectionStart = editor.TextLength;
            editor.Focus();
            SendEditorText();
            SetStatus("Loaded snippet: " + snippetNames[index]);
        }

        private string MakeSnippetName(string text)
        {
            string name = "";
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length > 0)
                {
                    name = line;
                    break;
                }
            }

            if (name.Length == 0)
            {
                name = "Snippet";
            }
            if (name.Length > 46)
            {
                name = name.Substring(0, 46) + "...";
            }

            string baseName = name;
            int counter = 2;
            while (snippetNames.Contains(name))
            {
                name = baseName + " (" + counter + ")";
                counter++;
            }
            return name;
        }

        private void RefreshSnippetList(int selectedIndex)
        {
            snippetList.BeginUpdate();
            snippetList.Items.Clear();
            visibleSnippetIndices.Clear();
            string query = snippetSearchBox == null ? "" : snippetSearchBox.Text.Trim();
            for (int i = 0; i < snippetNames.Count; i++)
            {
                if (query.Length == 0 || snippetNames[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    visibleSnippetIndices.Add(i);
                    snippetList.Items.Add(snippetNames[i]);
                }
            }
            snippetList.EndUpdate();

            int visibleIndex = visibleSnippetIndices.IndexOf(selectedIndex);
            if (selectedIndex >= 0 && visibleIndex >= 0)
            {
                snippetList.SelectedIndex = visibleIndex;
            }
            SendUiMessage(new Dictionary<string, object> { { "snippets", GetSnippetState() } });
        }

        private void LoadSnippets()
        {
            snippetNames.Clear();
            snippetTexts.Clear();

            try
            {
                if (File.Exists(snippetsPath))
                {
                    string[] lines = File.ReadAllLines(snippetsPath, Encoding.UTF8);
                    foreach (string line in lines)
                    {
                        if (line.Length == 0)
                        {
                            continue;
                        }

                        int tab = line.IndexOf('\t');
                        if (tab <= 0)
                        {
                            continue;
                        }

                        try
                        {
                            string name = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(0, tab)));
                            string body = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(tab + 1)));
                            snippetNames.Add(name);
                            snippetTexts.Add(body);
                        }
                        catch (FormatException)
                        {
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SetStatus("Could not load snippets: " + ex.Message, true);
            }

            if (snippetNames.Count == 0)
            {
                snippetNames.Add("Python Hello");
                snippetTexts.Add("print(\"Hello, world!\")");
                snippetNames.Add("Email Template");
                snippetTexts.Add("Hello,\n\nJust following up on this.\n\nBest regards,");
                snippetNames.Add("SQL Select");
                snippetTexts.Add("SELECT users WHERE active = 1;");
                SaveSnippets();
            }

            RefreshSnippetList(snippetNames.Count > 0 ? 0 : -1);
        }

        private void SaveSnippets()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < snippetNames.Count; i++)
                {
                    string name = Convert.ToBase64String(Encoding.UTF8.GetBytes(snippetNames[i]));
                    string body = Convert.ToBase64String(Encoding.UTF8.GetBytes(snippetTexts[i]));
                    sb.Append(name).Append('\t').Append(body).Append('\n');
                }
                File.WriteAllText(snippetsPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                SetStatus("Could not save snippets: " + ex.Message, true);
            }
        }

        private void LoadSettings()
        {
            int mode = 0;
            int delay = 10;

            try
            {
                if (File.Exists(settingsPath))
                {
                    string[] lines = File.ReadAllLines(settingsPath);
                    foreach (string line in lines)
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0)
                        {
                            continue;
                        }

                        string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string value = line.Substring(eq + 1).Trim();

                        if (key == "mode")
                        {
                            int parsed;
                            if (int.TryParse(value, out parsed))
                            {
                                mode = parsed;
                            }
                        }
                        else if (key == "delay")
                        {
                            int parsed;
                            if (int.TryParse(value, out parsed))
                            {
                                delay = parsed;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            if (mode < 0 || mode > 2)
            {
                mode = 0;
            }
            if (delay < 1)
            {
                delay = 1;
            }
            if (delay > 500)
            {
                delay = 500;
            }

            modeBox.SelectedIndex = mode;
            SetDelayMs(delay);
            SendModeAndDelay();
        }

        private void SaveSettings()
        {
            if (modeBox == null || speedTrackBar == null)
            {
                return;
            }

            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("mode=").Append(modeBox.SelectedIndex).Append('\n');
                sb.Append("delay=").Append(GetDelayMs()).Append('\n');
                File.WriteAllText(settingsPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
        }

        private int GetDelayMs()
        {
            if (delayInput != null)
            {
                return (int)delayInput.Value;
            }
            if (speedTrackBar == null)
            {
                return 10;
            }

            double fraction = (100.0 - speedTrackBar.Value) / 99.0;
            int delay = (int)Math.Round(Math.Exp(Math.Log(500.0) * fraction));
            return Math.Max(1, Math.Min(500, delay));
        }

        private void SetDelayMs(int delay)
        {
            delay = Math.Max(1, Math.Min(500, delay));
            double fraction = Math.Log(delay) / Math.Log(500.0);
            int value = 100 - (int)Math.Round(99.0 * fraction);
            value = Math.Max(speedTrackBar.Minimum, Math.Min(speedTrackBar.Maximum, value));

            updatingSpeedControls = true;
            speedTrackBar.Value = value;
            if (delayInput != null)
            {
                delayInput.Value = delay;
            }
            if (delay == 1) speedPresetBox.SelectedIndex = 0;
            else if (delay == 10) speedPresetBox.SelectedIndex = 1;
            else if (delay == 500) speedPresetBox.SelectedIndex = 2;
            else speedPresetBox.SelectedIndex = 3;
            updatingSpeedControls = false;
            UpdateSpeedValueLabel();
        }

        private void SpeedPresetChanged()
        {
            if (updatingSpeedControls || speedPresetBox.SelectedIndex < 0)
            {
                return;
            }

            int value = -1;
            if (speedPresetBox.SelectedIndex == 0) value = 100;
            else if (speedPresetBox.SelectedIndex == 1) value = 63;
            else if (speedPresetBox.SelectedIndex == 2) value = 1;

            updatingSpeedControls = true;
            if (value >= 0) speedTrackBar.Value = value;
            updatingSpeedControls = false;
            if (value >= 0)
            {
                SetDelayMs(DelayForTrackValue(speedTrackBar.Value));
            }
            UpdateSpeedValueLabel();
            SaveSettings();
        }

        private void SpeedTrackChanged()
        {
            if (!updatingSpeedControls && delayInput != null)
            {
                updatingSpeedControls = true;
                delayInput.Value = DelayForTrackValue(speedTrackBar.Value);
                updatingSpeedControls = false;
            }
            UpdateSpeedValueLabel();
            if (!updatingSpeedControls)
            {
                updatingSpeedControls = true;
                speedPresetBox.SelectedIndex = 3;
                updatingSpeedControls = false;
                SaveSettings();
            }
        }

        private void UpdateSpeedValueLabel()
        {
            if (speedValueLabel != null)
            {
                speedValueLabel.Text = GetDelayMs() + " ms";
            }
        }

        private int DelayForTrackValue(int value)
        {
            double fraction = (100.0 - value) / 99.0;
            return Math.Max(1, Math.Min(500,
                (int)Math.Round(Math.Exp(Math.Log(500.0) * fraction))));
        }

        private void ReportTypingProgress(int current, int total)
        {
            PostUi(delegate { SetTypingProgress(current, total); });
        }

        private void SetTypingProgress(int current, int total)
        {
            int safeTotal = Math.Max(1, total);
            int safeCurrent = Math.Max(0, Math.Min(safeTotal, current));
            int percent = (int)(100L * safeCurrent / safeTotal);
            if (typingProgress != null && progressText != null)
            {
                typingProgress.Value = (int)(1000L * safeCurrent / safeTotal);
                progressText.Text = safeCurrent + " / " + total;
            }
            if (percent != lastUiProgressPercent || safeCurrent == 0 || safeCurrent == safeTotal)
            {
                lastUiProgressPercent = percent;
                Dictionary<string, object> state = new Dictionary<string, object>();
                state["progress"] = percent;
                state["progressText"] = safeCurrent + " / " + total;
                SendUiMessage(state);
            }
        }

        private void LoadDraft()
        {
            try
            {
                if (File.Exists(draftPath))
                {
                    editor.Text = File.ReadAllText(draftPath, Encoding.UTF8);
                }
            }
            catch (Exception)
            {
            }
        }

        private void SaveDraft()
        {
            try
            {
                File.WriteAllText(draftPath, editor.Text, new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
        }

        private void UpdateDocumentStats()
        {
            if (editor == null) return;
            int lines = 1;
            string text = editor.Text;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') lines++;
            }
            if (documentStatsLabel != null)
            {
                documentStatsLabel.Text = "Lines: " + lines + "  •  Characters: " + text.Length;
            }
            Dictionary<string, object> state = new Dictionary<string, object>();
            state["characters"] = text.Length;
            state["lines"] = lines;
            SendUiMessage(state);
        }

        private void CreateTray()
        {
            trayMenu = new ContextMenuStrip();

            ToolStripMenuItem openItem = new ToolStripMenuItem("Open KeyTyper");
            openItem.Font = new Font(openItem.Font, FontStyle.Bold);
            openItem.Click += delegate { ShowMain(); };
            trayMenu.Items.Add(openItem);

            ToolStripMenuItem typeItem = new ToolStripMenuItem("Type now");
            typeItem.ShortcutKeyDisplayString = hotkeyText;
            typeItem.Click += delegate { StartTyping(editor.Text); };
            trayMenu.Items.Add(typeItem);

            stopTrayItem = new ToolStripMenuItem("Stop typing (Esc)");
            stopTrayItem.Enabled = false;
            stopTrayItem.Click += delegate { StopTyping(); };
            trayMenu.Items.Add(stopTrayItem);

            ToolStripMenuItem clipItem = new ToolStripMenuItem("Type clipboard");
            clipItem.Click += delegate
            {
                string text = "";
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        text = Clipboard.GetText();
                    }
                }
                catch (Exception ex)
                {
                    SetStatus("Clipboard error: " + ex.Message, true);
                    return;
                }
                StartTyping(text);
            };
            trayMenu.Items.Add(clipItem);
            trayMenu.Items.Add(new ToolStripSeparator());

            startupItem = new ToolStripMenuItem("Start with Windows");
            startupItem.Click += delegate
            {
                try
                {
                    SetStartup(!IsStartupEnabled());
                    SetStatus(IsStartupEnabled() ? "KeyTyper will start with Windows." : "Start with Windows disabled.");
                }
                catch (Exception ex)
                {
                    SetStatus("Could not update startup setting: " + ex.Message, true);
                }
            };
            trayMenu.Items.Add(startupItem);

            ToolStripMenuItem adminItem = new ToolStripMenuItem("Restart as administrator");
            if (IsElevated())
            {
                adminItem.Text = "Running as administrator";
                adminItem.Enabled = false;
            }
            else
            {
                adminItem.Click += delegate { RestartAsAdmin(); };
            }
            trayMenu.Items.Add(adminItem);

            trayMenu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += delegate { ExitApp(); };
            trayMenu.Items.Add(exitItem);

            trayMenu.Opening += delegate
            {
                try
                {
                    startupItem.Checked = IsStartupEnabled();
                }
                catch (Exception)
                {
                    startupItem.Checked = false;
                }
            };

            tray = new NotifyIcon();
            tray.Icon = appIcon;
            tray.Text = "KeyTyper - " + hotkeyText;
            tray.ContextMenuStrip = trayMenu;
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowMain(); };
        }

        private void ShowMain()
        {
            ShowInTaskbar = true;
            if (!Visible)
            {
                Show();
            }
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }
            Activate();
            BringToFront();
            if (htmlView != null) htmlView.Focus();
            else editor.Focus();
        }

        private void HideToTray()
        {
            SaveSettings();
            if (!suppressDraft)
            {
                SaveDraft();
            }
            Hide();

            if (!trayHintShown)
            {
                trayHintShown = true;
                ShowBalloon("KeyTyper is still running", "Press " + hotkeyText + " in any app to type. Open the tray icon to edit the text.");
            }
        }

        private void ExitApp()
        {
            SaveSettings();
            if (!suppressDraft)
            {
                SaveDraft();
            }
            Close();
        }

        private bool IsStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
            {
                if (key == null)
                {
                    return false;
                }
                return key.GetValue(RUN_VALUE) != null;
            }
        }

        private void SetStartup(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RUN_KEY))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("Cannot open the Run registry key.");
                }

                if (enabled)
                {
                    string command = "\"" + Application.ExecutablePath + "\" --tray";
                    key.SetValue(RUN_VALUE, command, RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(RUN_VALUE, false);
                }
            }
        }

        private static bool IsElevated()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void RestartAsAdmin()
        {
            if (IsElevated())
            {
                MessageBox.Show(this, "KeyTyper is already running as administrator.", "KeyTyper",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                string arguments = "--allow-multi";
                if (!Visible)
                {
                    arguments += " --tray";
                }

                ProcessStartInfo info = new ProcessStartInfo(Application.ExecutablePath, arguments);
                info.UseShellExecute = true;
                info.Verb = "runas";
                Process.Start(info);

                SaveSettings();
                if (!suppressDraft)
                {
                    SaveDraft();
                }
                Close();
            }
            catch (Win32Exception)
            {
                SetStatus("Administrator restart cancelled.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not restart as administrator: " + ex.Message, "KeyTyper",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SetStatus(string text)
        {
            SetStatus(text, false);
        }

        private void SetStatus(string text, bool error)
        {
            lastStatusText = text;
            lastStatusError = error;
            if (statusLabel != null)
            {
                statusLabel.Text = text;
                statusLabel.ForeColor = error ? Color.FromArgb(248, 113, 113) : Color.FromArgb(232, 232, 238);
            }
            Dictionary<string, object> state = new Dictionary<string, object>();
            state["status"] = text;
            state["error"] = error;
            SendUiMessage(state);
        }

        private sealed class DarkMenuColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Color.FromArgb(53, 57, 65); } }
            public override Color MenuStripGradientBegin { get { return Color.FromArgb(53, 57, 65); } }
            public override Color MenuStripGradientEnd { get { return Color.FromArgb(53, 57, 65); } }
            public override Color MenuItemSelected { get { return Color.FromArgb(78, 84, 96); } }
            public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(78, 84, 96); } }
            public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(78, 84, 96); } }
            public override Color MenuItemBorder { get { return Color.FromArgb(91, 98, 111); } }
            public override Color MenuBorder { get { return Color.FromArgb(91, 98, 111); } }
            public override Color SeparatorDark { get { return Color.FromArgb(91, 98, 111); } }
        }

        private void ShowBalloon(string title, string text)
        {
            if (tray == null)
            {
                return;
            }

            try
            {
                tray.BalloonTipTitle = title;
                tray.BalloonTipText = text;
                tray.BalloonTipIcon = ToolTipIcon.Info;
                tray.ShowBalloonTip(3000);
            }
            catch (Exception)
            {
            }
        }
    }
}
