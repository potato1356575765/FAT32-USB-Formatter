// Raw volume access (lock, dismount, geometry) and USB drive discovery.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Fat32Usb
{
    public class UsbDrive
    {
        public string Letter;      // "E:"
        public string Label;
        public string FileSystem;
        public long Size;
        public string Model;

        public override string ToString()
        {
            string lbl = string.IsNullOrEmpty(Label) ? "No label" : Label;
            return string.Format("{0}  {1}  ({2}, {3})", Letter, lbl, FormatSize(Size),
                string.IsNullOrEmpty(FileSystem) ? "RAW" : FileSystem);
        }

        public static string FormatSize(long bytes)
        {
            double gb = bytes / 1e9;
            if (gb >= 1) return gb.ToString("0.0") + " GB";
            return (bytes / 1e6).ToString("0") + " MB";
        }
    }

    public static class DriveFinder
    {
        /// <summary>Lists volumes on USB disks plus any removable-media volumes. Never returns the system drive.</summary>
        public static List<UsbDrive> Find()
        {
            var result = new List<UsbDrive>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string sysDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');

            try
            {
                using (var disks = new ManagementObjectSearcher(
                    "SELECT DeviceID, Model, InterfaceType, MediaType FROM Win32_DiskDrive"))
                {
                    foreach (ManagementObject disk in disks.Get())
                    {
                        string iface = Convert.ToString(disk["InterfaceType"]) ?? "";
                        string media = Convert.ToString(disk["MediaType"]) ?? "";
                        bool usb = iface.Equals("USB", StringComparison.OrdinalIgnoreCase)
                                   || media.StartsWith("Removable", StringComparison.OrdinalIgnoreCase);
                        if (!usb) continue;
                        string model = Convert.ToString(disk["Model"]);
                        string devId = Convert.ToString(disk["DeviceID"]).Replace("\\", "\\\\");

                        using (var parts = new ManagementObjectSearcher("ASSOCIATORS OF {Win32_DiskDrive.DeviceID='" + devId +
                            "'} WHERE AssocClass=Win32_DiskDriveToDiskPartition"))
                        {
                            foreach (ManagementObject part in parts.Get())
                            {
                                using (var lds = new ManagementObjectSearcher("ASSOCIATORS OF {Win32_DiskPartition.DeviceID='" +
                                    part["DeviceID"] + "'} WHERE AssocClass=Win32_LogicalDiskToPartition"))
                                {
                                    foreach (ManagementObject ld in lds.Get())
                                    {
                                        string letter = Convert.ToString(ld["DeviceID"]);
                                        if (string.IsNullOrEmpty(letter) || letter.Equals(sysDrive, StringComparison.OrdinalIgnoreCase)) continue;
                                        if (!seen.Add(letter)) continue;
                                        result.Add(new UsbDrive
                                        {
                                            Letter = letter,
                                            Label = Convert.ToString(ld["VolumeName"]),
                                            FileSystem = Convert.ToString(ld["FileSystem"]),
                                            Size = ld["Size"] == null ? 0 : Convert.ToInt64(ld["Size"]),
                                            Model = model
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (ManagementException) { /* fall through to DriveInfo */ }

            // Catch card readers etc. that WMI didn't map.
            foreach (var di in DriveInfo.GetDrives())
            {
                if (di.DriveType != DriveType.Removable) continue;
                string letter = di.Name.TrimEnd('\\');
                if (letter.Equals(sysDrive, StringComparison.OrdinalIgnoreCase) || seen.Contains(letter)) continue;
                if (!di.IsReady) continue;
                seen.Add(letter);
                result.Add(new UsbDrive
                {
                    Letter = letter, Label = di.VolumeLabel, FileSystem = di.DriveFormat,
                    Size = di.TotalSize, Model = "Removable drive"
                });
            }

            result.Sort(delegate(UsbDrive a, UsbDrive b) { return string.Compare(a.Letter, b.Letter, StringComparison.OrdinalIgnoreCase); });
            return result;
        }
    }

    /// <summary>An open, locked, dismounted volume ready for raw writes.</summary>
    public sealed class RawVolume : IDisposable
    {
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        const uint FSCTL_LOCK_VOLUME = 0x00090018, FSCTL_UNLOCK_VOLUME = 0x0009001C, FSCTL_DISMOUNT_VOLUME = 0x00090020;
        const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;
        const uint IOCTL_DISK_GET_PARTITION_INFO_EX = 0x00070048;
        const uint IOCTL_DISK_SET_PARTITION_INFO = 0x0007C008;
        const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize,
            byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

        readonly SafeFileHandle handle;
        bool locked;

        public int BytesPerSector { get; private set; }
        public long TotalSectors { get; private set; }
        public uint HiddenSectors { get; private set; }
        public ushort SectorsPerTrack { get; private set; }
        public ushort Heads { get; private set; }
        public bool IsMbr { get; private set; }
        public FileStream Stream { get; private set; }

        public RawVolume(string letter)
        {
            string path = @"\\.\" + letter.TrimEnd('\\');
            handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't open " + letter + ". Run the app as administrator.");

            try
            {
                ReadGeometry();
                Lock();
                Ioctl(FSCTL_DISMOUNT_VOLUME, null, null, "dismount the volume");
                Stream = new FileStream(handle, FileAccess.ReadWrite, 1); // bufferSize 1 = unbuffered pass-through
            }
            catch { Dispose(); throw; }
        }

        void ReadGeometry()
        {
            byte[] geo = new byte[24];
            Ioctl(IOCTL_DISK_GET_DRIVE_GEOMETRY, null, geo, "read the drive geometry");
            SectorsPerTrack = (ushort)Math.Min(BitConverter.ToUInt32(geo, 16), 0xFFFF);
            uint tracksPerCyl = BitConverter.ToUInt32(geo, 12);
            Heads = (ushort)Math.Min(tracksPerCyl, 0xFFFF);
            BytesPerSector = (int)BitConverter.ToUInt32(geo, 20);
            if (SectorsPerTrack == 0) SectorsPerTrack = 63;
            if (Heads == 0) Heads = 255;

            long length = 0;
            byte[] pi = new byte[144];
            int ret;
            if (DeviceIoControl(handle, IOCTL_DISK_GET_PARTITION_INFO_EX, null, 0, pi, pi.Length, out ret, IntPtr.Zero))
            {
                IsMbr = BitConverter.ToInt32(pi, 0) == 0;
                HiddenSectors = (uint)(BitConverter.ToInt64(pi, 8) / BytesPerSector);
                length = BitConverter.ToInt64(pi, 16);
            }
            if (length <= 0)
            {
                byte[] li = new byte[8];
                Ioctl(IOCTL_DISK_GET_LENGTH_INFO, null, li, "read the volume size");
                length = BitConverter.ToInt64(li, 0);
            }
            TotalSectors = length / BytesPerSector;
        }

        void Lock()
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                int ret;
                if (DeviceIoControl(handle, FSCTL_LOCK_VOLUME, null, 0, null, 0, out ret, IntPtr.Zero)) { locked = true; return; }
                Thread.Sleep(300);
            }
            throw new IOException("The drive is in use. Close any windows or programs using it (including Explorer windows) and try again.");
        }

        /// <summary>Sets the MBR partition type byte to 0x0C (FAT32 LBA). Best-effort; GPT disks are skipped.</summary>
        public void SetFat32PartitionType()
        {
            if (!IsMbr) return;
            int ret;
            DeviceIoControl(handle, IOCTL_DISK_SET_PARTITION_INFO, new byte[] { 0x0C }, 1, null, 0, out ret, IntPtr.Zero);
        }

        void Ioctl(uint code, byte[] input, byte[] output, string what)
        {
            int ret;
            if (!DeviceIoControl(handle, code, input, input == null ? 0 : input.Length,
                    output, output == null ? 0 : output.Length, out ret, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't " + what + ".");
        }

        public void Dispose()
        {
            try { if (Stream != null) Stream.Flush(); } catch { }
            if (locked && !handle.IsInvalid && !handle.IsClosed)
            {
                int ret;
                DeviceIoControl(handle, FSCTL_DISMOUNT_VOLUME, null, 0, null, 0, out ret, IntPtr.Zero); // force remount with new FS
                DeviceIoControl(handle, FSCTL_UNLOCK_VOLUME, null, 0, null, 0, out ret, IntPtr.Zero);
                locked = false;
            }
            if (Stream != null) Stream.Dispose(); else handle.Dispose();
        }
    }
}
