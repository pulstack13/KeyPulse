using System;
using System.Threading;
using System.Windows.Forms;

namespace KeyTyper
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool startHidden = false;
            bool allowMulti = false;
            bool exitAfter = false;
            string typeFile = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == "--tray")
                {
                    startHidden = true;
                }
                else if (arg == "--allow-multi")
                {
                    allowMulti = true;
                }
                else if (arg == "--exit-after")
                {
                    exitAfter = true;
                }
                else if (arg == "--type-file" && i + 1 < args.Length)
                {
                    typeFile = args[i + 1];
                    i++;
                }
            }

            if (typeFile != null)
            {
                startHidden = true;
            }

            Mutex mutex = null;
            if (!allowMulti)
            {
                bool created;
                mutex = new Mutex(true, "Local\\KeyTyper.SingleInstance", out created);
                if (!created)
                {
                    MessageBox.Show(
                        "KeyTyper is already running.\n\nLook for the blue K icon in the notification area (system tray).",
                        "KeyTyper",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(startHidden, typeFile, exitAfter));
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "KeyPulse startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (mutex != null)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch (Exception)
                    {
                    }
                    mutex.Dispose();
                }
            }
        }

    }
}
