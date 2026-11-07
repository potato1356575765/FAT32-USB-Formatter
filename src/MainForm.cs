using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace Fat32Usb
{
    public class MainForm : Form
    {
        ComboBox driveBox, clusterBox;
        Button refreshBtn, formatBtn;
        TextBox labelBox;
        Label infoLabel, statusLabel;
        ProgressBar progress;
        BackgroundWorker worker;

        static readonly string[] ClusterNames = { "Default", "4 KB", "8 KB", "16 KB", "32 KB", "64 KB" };
        static readonly int[] ClusterValues = { 0, 4096, 8192, 16384, 32768, 65536 };

        public MainForm()
        {
            Text = "FAT32 USB Formatter";
            Font = new Font("Segoe UI", 9.5f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(480, 330);
            AutoScaleMode = AutoScaleMode.Dpi;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            int x = 18, w = 444;
            Controls.Add(new Label { Text = "USB drive", Left = x, Top = 16, AutoSize = true });
            driveBox = new ComboBox { Left = x, Top = 38, Width = w - 96, DropDownStyle = ComboBoxStyle.DropDownList };
            driveBox.SelectedIndexChanged += delegate { UpdateInfo(); };
            refreshBtn = new Button { Text = "Refresh", Left = x + w - 88, Top = 37, Width = 88, Height = 27 };
            refreshBtn.Click += delegate { LoadDrives(); };
            infoLabel = new Label { Left = x, Top = 70, Width = w, Height = 36, ForeColor = SystemColors.GrayText };

            Controls.Add(new Label { Text = "Volume label", Left = x, Top = 112, AutoSize = true });
            labelBox = new TextBox { Left = x, Top = 134, Width = 210, MaxLength = 11, CharacterCasing = CharacterCasing.Upper, Text = "USB" };

            Controls.Add(new Label { Text = "Allocation unit size", Left = x + 234, Top = 112, AutoSize = true });
            clusterBox = new ComboBox { Left = x + 234, Top = 134, Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
            clusterBox.Items.AddRange(ClusterNames);
            clusterBox.SelectedIndex = 0;

            var warn = new Label
            {
                Left = x, Top = 176, Width = w, Height = 38,
                Text = "Formatting erases everything on the selected drive. Works on drives larger than 32 GB, which Windows' own Format dialog won't do for FAT32.",
                ForeColor = Color.FromArgb(160, 60, 0)
            };

            formatBtn = new Button { Text = "Format as FAT32", Left = x, Top = 222, Width = w, Height = 36 };
            formatBtn.Font = new Font(Font, FontStyle.Bold);
            formatBtn.Click += delegate { StartFormat(); };

            progress = new ProgressBar { Left = x, Top = 268, Width = w, Height = 16 };
            statusLabel = new Label { Left = x, Top = 292, Width = w, Height = 24, Text = "Ready." };

            Controls.AddRange(new Control[] { driveBox, refreshBtn, infoLabel, labelBox, clusterBox, warn, formatBtn, progress, statusLabel });

            worker = new BackgroundWorker { WorkerReportsProgress = true };
            worker.DoWork += DoFormat;
            worker.ProgressChanged += delegate(object s, ProgressChangedEventArgs e) { progress.Value = e.ProgressPercentage; };
            worker.RunWorkerCompleted += FormatDone;

            Shown += delegate { LoadDrives(); };
        }

        void LoadDrives()
        {
            Cursor = Cursors.WaitCursor;
            driveBox.Items.Clear();
            try
            {
                foreach (var d in DriveFinder.Find()) driveBox.Items.Add(d);
            }
            catch (Exception ex) { statusLabel.Text = "Couldn't list drives: " + ex.Message; }
            Cursor = Cursors.Default;
            if (driveBox.Items.Count > 0) driveBox.SelectedIndex = 0;
            else statusLabel.Text = "No USB drives found. Plug one in and click Refresh.";
            UpdateInfo();
        }

        void UpdateInfo()
        {
            var d = driveBox.SelectedItem as UsbDrive;
            formatBtn.Enabled = d != null && !worker.IsBusy;
            if (d == null) { infoLabel.Text = ""; return; }
            infoLabel.Text = string.Format("{0}\r\nCurrent file system: {1}   Capacity: {2}",
                d.Model, string.IsNullOrEmpty(d.FileSystem) ? "RAW / unknown" : d.FileSystem, UsbDrive.FormatSize(d.Size));
            if (!string.IsNullOrEmpty(d.Label)) labelBox.Text = Fat32Writer.NormalizeLabel(d.Label).TrimEnd();
            statusLabel.Text = "Ready.";
        }

        class Job { public UsbDrive Drive; public string Label; public int Cluster; public string Summary; }

        void StartFormat()
        {
            var d = driveBox.SelectedItem as UsbDrive;
            if (d == null) return;

            string msg = string.Format(
                "ALL DATA on {0} will be permanently erased.\r\n\r\n{1}\r\n{2}, currently {3}\r\n\r\nFormat it as FAT32?",
                d.Letter, string.IsNullOrEmpty(d.Label) ? "(no label)" : d.Label, UsbDrive.FormatSize(d.Size),
                string.IsNullOrEmpty(d.FileSystem) ? "RAW" : d.FileSystem);
            if (MessageBox.Show(this, msg, "Confirm format", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            SetBusy(true);
            progress.Value = 0;
            statusLabel.Text = "Locking " + d.Letter + "...";
            worker.RunWorkerAsync(new Job { Drive = d, Label = labelBox.Text, Cluster = ClusterValues[clusterBox.SelectedIndex] });
        }

        void DoFormat(object sender, DoWorkEventArgs e)
        {
            var job = (Job)e.Argument;
            using (var vol = new RawVolume(job.Drive.Letter))
            {
                var layout = Fat32Writer.Plan(vol.BytesPerSector, vol.TotalSectors, vol.HiddenSectors, job.Cluster);
                layout.SectorsPerTrack = vol.SectorsPerTrack;
                layout.Heads = vol.Heads;
                vol.SetFat32PartitionType();
                Fat32Writer.Write(vol.Stream, layout, job.Label, delegate(int p) { worker.ReportProgress(p); });
                job.Summary = string.Format("Done: {0} is now FAT32: {1} clusters of {2} KB, {3} free.",
                    job.Drive.Letter, layout.ClusterCount.ToString("N0"), layout.ClusterBytes / 1024,
                    UsbDrive.FormatSize((long)(layout.ClusterCount - 1) * layout.ClusterBytes));
            }
            e.Result = job;
        }

        void FormatDone(object sender, RunWorkerCompletedEventArgs e)
        {
            SetBusy(false);
            if (e.Error != null)
            {
                progress.Value = 0;
                statusLabel.Text = "Format failed.";
                MessageBox.Show(this, e.Error.Message, "Format failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            var job = (Job)e.Result;
            statusLabel.Text = job.Summary;
            // Give Windows a moment to remount, then refresh the list (keeps the summary visible).
            var t = new Timer { Interval = 1500 };
            t.Tick += delegate
            {
                t.Stop(); t.Dispose();
                string keep = statusLabel.Text;
                LoadDrives();
                statusLabel.Text = keep;
            };
            t.Start();
        }

        void SetBusy(bool busy)
        {
            driveBox.Enabled = refreshBtn.Enabled = labelBox.Enabled = clusterBox.Enabled = !busy;
            formatBtn.Enabled = !busy && driveBox.SelectedItem != null;
            Cursor = busy ? Cursors.AppStarting : Cursors.Default;
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
