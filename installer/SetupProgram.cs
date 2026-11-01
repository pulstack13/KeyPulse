using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace KeyPulseSetup
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }
    }

    internal sealed class SetupForm : Form
    {
        private const string WebViewDownload = "https://developer.microsoft.com/microsoft-edge/webview2/";
        private TextBox folderBox;
        private CheckBox desktopShortcut;
        private CheckBox launchAfter;
        private Button installButton;
        private Label statusLabel;
        private ProgressBar progress;

        public SetupForm()
        {
            Text = "KeyPulse Setup";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(520, 340);
            BackColor = Color.FromArgb(244, 246, 249);
            Font = new Font("Segoe UI", 9F);
            Icon = LoadIcon();

            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 92;
            header.BackColor = Color.FromArgb(28, 38, 52);
            Controls.Add(header);

            PictureBox logo = new PictureBox();
            logo.Size = new Size(60, 60);
            logo.Location = new Point(18, 16);
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.Image = LoadLogo();
            header.Controls.Add(logo);

            Label title = new Label();
            title.Text = "Install KeyPulse";
            title.ForeColor = Color.White;
            title.Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold);
            title.Location = new Point(92, 18);
            title.AutoSize = true;
            header.Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "Text to real keystrokes, ready from your Desktop.";
            subtitle.ForeColor = Color.FromArgb(196, 207, 220);
            subtitle.Location = new Point(95, 55);
            subtitle.AutoSize = true;
            header.Controls.Add(subtitle);

            Label folderLabel = new Label();
            folderLabel.Text = "Install folder";
            folderLabel.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
            folderLabel.Location = new Point(24, 116);
            folderLabel.AutoSize = true;
            Controls.Add(folderLabel);

            folderBox = new TextBox();
            folderBox.Location = new Point(24, 140);
            folderBox.Size = new Size(472, 26);
            folderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "KeyPulse");
            Controls.Add(folderBox);

            desktopShortcut = new CheckBox();
            desktopShortcut.Text = "Create a Desktop shortcut";
            desktopShortcut.Checked = true;
            desktopShortcut.Location = new Point(24, 184);
            desktopShortcut.AutoSize = true;
            Controls.Add(desktopShortcut);

            launchAfter = new CheckBox();
            launchAfter.Text = "Open KeyPulse after setup";
            launchAfter.Checked = true;
            launchAfter.Location = new Point(24, 210);
            launchAfter.AutoSize = true;
            Controls.Add(launchAfter);

            progress = new ProgressBar();
            progress.Location = new Point(24, 249);
            progress.Size = new Size(472, 8);
            progress.Style = ProgressBarStyle.Continuous;
            Controls.Add(progress);

            statusLabel = new Label();
            statusLabel.Text = "The app and its required files will be installed together.";
            statusLabel.ForeColor = Color.FromArgb(75, 86, 101);
            statusLabel.Location = new Point(24, 265);
            statusLabel.Size = new Size(290, 40);
            Controls.Add(statusLabel);

            installButton = new Button();
            installButton.Text = "Install";
            installButton.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
            installButton.BackColor = Color.FromArgb(35, 113, 220);
            installButton.ForeColor = Color.White;
            installButton.FlatStyle = FlatStyle.Flat;
            installButton.FlatAppearance.BorderSize = 0;
            installButton.Location = new Point(370, 278);
            installButton.Size = new Size(126, 40);
            installButton.Click += InstallClicked;
            Controls.Add(installButton);
        }

        private void InstallClicked(object sender, EventArgs e)
        {
            string installDir = folderBox.Text.Trim();
            if (installDir.Length == 0 || !Path.IsPathRooted(installDir))
            {
                MessageBox.Show(this, "Choose a valid install folder.", "KeyPulse Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            installButton.Enabled = false;
            folderBox.Enabled = false;
            statusLabel.Text = "Installing KeyPulse...";
            progress.Value = 10;

            try
            {
                Directory.CreateDirectory(installDir);
                Extract("KeyPulse.exe", installDir);
                progress.Value = 40;
                Extract("Microsoft.Web.WebView2.Core.dll", installDir);
                progress.Value = 60;
                Extract("Microsoft.Web.WebView2.WinForms.dll", installDir);
                progress.Value = 80;
                Extract("WebView2Loader.dll", installDir);
                progress.Value = 90;

                string appPath = Path.Combine(installDir, "KeyPulse.exe");
                if (desktopShortcut.Checked)
                {
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    CreateShortcut(Path.Combine(desktop, "KeyPulse.lnk"), appPath, installDir);
                }

                string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string startMenuFolder = Path.Combine(programs, "KeyPulse");
                Directory.CreateDirectory(startMenuFolder);
                CreateShortcut(Path.Combine(startMenuFolder, "KeyPulse.lnk"), appPath, installDir);

                progress.Value = 100;
                statusLabel.Text = "KeyPulse installed.";

                if (!HasWebView2Runtime())
                {
                    DialogResult result = MessageBox.Show(this,
                        "KeyPulse is installed, but Microsoft Edge WebView2 Runtime was not found.\r\n\r\nOpen Microsoft's runtime download page now?",
                        "WebView2 Runtime needed", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (result == DialogResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo(WebViewDownload) { UseShellExecute = true });
                    }
                }
                else if (launchAfter.Checked)
                {
                    Process.Start(new ProcessStartInfo(appPath) { WorkingDirectory = installDir, UseShellExecute = true });
                }

                MessageBox.Show(this, "Setup is complete. KeyPulse is available from your Desktop and Start menu.",
                    "KeyPulse Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Setup could not finish.";
                installButton.Enabled = true;
                folderBox.Enabled = true;
                MessageBox.Show(this, "KeyPulse could not be installed.\r\n\r\n" + ex.Message,
                    "KeyPulse Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void Extract(string fileName, string installDir)
        {
            string resourceName = "KeyPulseSetup.Payload." + fileName;
            using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (input == null) throw new FileNotFoundException("Installer payload is missing: " + fileName);
                using (FileStream output = new FileStream(Path.Combine(installDir, fileName), FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                }
            }
        }

        private static void CreateShortcut(string shortcutPath, string target, string workingDirectory)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("Windows Script Host is unavailable.");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { target + ",0" });
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "KeyPulse text-to-keystrokes" });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }

        private static bool HasWebView2Runtime()
        {
            string[] roots = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "EdgeWebView", "Application"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "EdgeWebView", "Application"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "EdgeWebView", "Application")
            };
            for (int i = 0; i < roots.Length; i++)
            {
                if (!Directory.Exists(roots[i])) continue;
                try
                {
                    string[] versions = Directory.GetDirectories(roots[i]);
                    for (int j = 0; j < versions.Length; j++)
                    {
                        if (File.Exists(Path.Combine(versions[j], "msedgewebview2.exe"))) return true;
                    }
                }
                catch (UnauthorizedAccessException) { }
            }
            return false;
        }

        private static Image LoadLogo()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KeyPulseSetup.Payload.keypulse-logo.png"))
            {
                if (stream == null) return null;
                using (Image image = Image.FromStream(stream)) return new Bitmap(image);
            }
        }

        private static Icon LoadIcon()
        {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KeyPulseSetup.Payload.keypulse-logo.ico");
            if (stream == null) return SystemIcons.Application;
            using (stream) return new Icon(stream);
        }
    }
}
