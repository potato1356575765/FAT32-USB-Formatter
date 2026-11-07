// FAT32 file-system writer. Works on any seekable Stream (a raw volume or an image file).
// Written in C# 5 so it builds with the csc.exe that ships with Windows (.NET Framework 4).
using System;
using System.IO;
using System.Text;

namespace Fat32Usb
{
    public class Fat32Layout
    {
        public int BytesPerSector;
        public long TotalSectors;
        public uint HiddenSectors;
        public ushort SectorsPerTrack = 63;
        public ushort Heads = 255;

        // Computed
        public int SectorsPerCluster;
        public int ReservedSectors;
        public uint FatSizeSectors;
        public uint ClusterCount;

        public int ClusterBytes { get { return SectorsPerCluster * BytesPerSector; } }
        public long SystemAreaSectors { get { return ReservedSectors + 2L * FatSizeSectors; } }
    }

    public static class Fat32Writer
    {
        const int NumFats = 2;
        const uint MinClusters = 65525;
        const uint MaxClusters = 0x0FFFFFF5;

        /// <summary>Default cluster size in bytes for a volume of the given size (same table as fat32format).</summary>
        public static int DefaultClusterBytes(long volumeBytes)
        {
            long mb = volumeBytes / (1024 * 1024);
            if (mb > 512L * 1024) return 65536;
            if (mb > 32L * 1024) return 32768;
            if (mb > 16L * 1024) return 16384;
            if (mb > 8L * 1024) return 8192;
            if (mb > 260) return 4096;
            return 512;
        }

        /// <summary>Works out the FAT32 layout. clusterBytes = 0 picks a default. Throws if the volume can't hold FAT32.</summary>
        public static Fat32Layout Plan(int bytesPerSector, long totalSectors, uint hiddenSectors, int clusterBytes)
        {
            if (bytesPerSector < 512 || (bytesPerSector & (bytesPerSector - 1)) != 0)
                throw new InvalidOperationException("Unsupported sector size: " + bytesPerSector);
            if (totalSectors > uint.MaxValue)
                throw new InvalidOperationException("This volume is larger than FAT32's 2 TB limit (with 512-byte sectors).");

            long volumeBytes = totalSectors * bytesPerSector;
            bool auto = clusterBytes <= 0;
            if (auto) clusterBytes = DefaultClusterBytes(volumeBytes);
            if (clusterBytes < bytesPerSector) clusterBytes = bytesPerSector;

            while (true)
            {
                var l = TryPlan(bytesPerSector, totalSectors, hiddenSectors, clusterBytes / bytesPerSector);
                if (l.ClusterCount > MaxClusters)
                    throw new InvalidOperationException("Cluster size is too small for this volume. Pick a larger allocation unit.");
                if (l.ClusterCount >= MinClusters) return l;

                // Too few clusters to be FAT32: try smaller clusters if we're picking automatically.
                if (auto && clusterBytes > bytesPerSector) { clusterBytes /= 2; continue; }
                throw new InvalidOperationException(auto
                    ? "This volume is too small for FAT32 (needs roughly 33 MB or more)."
                    : "Allocation unit is too large for this volume (FAT32 needs at least 65,525 clusters). Pick a smaller one or use Default.");
            }
        }

        static Fat32Layout TryPlan(int bps, long totalSectors, uint hidden, int spc)
        {
            var l = new Fat32Layout();
            l.BytesPerSector = bps;
            l.TotalSectors = totalSectors;
            l.HiddenSectors = hidden;
            l.SectorsPerCluster = spc;
            l.ReservedSectors = 32;

            // Microsoft's FAT size formula (from fatgen103), slightly conservative.
            ulong numerator = 4UL * (ulong)(totalSectors - l.ReservedSectors);
            ulong denominator = (ulong)(spc * bps) + 4UL * NumFats;
            l.FatSizeSectors = (uint)(numerator / denominator + 1);

            while (true)
            {
                // Grow the reserved area so the data region starts on a cluster boundary.
                l.ReservedSectors = 32;
                long rem = (l.ReservedSectors + 2L * l.FatSizeSectors) % spc;
                if (rem != 0) l.ReservedSectors += (int)(spc - rem);

                long dataSectors = totalSectors - l.ReservedSectors - 2L * l.FatSizeSectors;
                l.ClusterCount = dataSectors <= 0 ? 0 : (uint)Math.Min(dataSectors / spc, uint.MaxValue);

                // The FAT must hold an entry for every cluster plus the two reserved entries.
                long neededFatSectors = ((l.ClusterCount + 2L) * 4 + bps - 1) / bps;
                if (neededFatSectors <= l.FatSizeSectors) return l;
                l.FatSizeSectors = (uint)neededFatSectors;
            }
        }

        /// <summary>Writes a fresh FAT32 file system. progress receives 0..100.</summary>
        public static void Write(Stream s, Fat32Layout l, string label, Action<int> progress)
        {
            int bps = l.BytesPerSector;
            label = NormalizeLabel(label);
            uint volumeId = MakeVolumeId();

            // 1. Zero the reserved area, both FATs and the root directory cluster.
            long zeroSectors = l.SystemAreaSectors + l.SectorsPerCluster;
            int chunkSectors = Math.Max(1, (1024 * 1024) / bps); // 1 MB writes
            byte[] zero = new byte[chunkSectors * bps];
            s.Seek(0, SeekOrigin.Begin);
            long done = 0;
            while (done < zeroSectors)
            {
                int n = (int)Math.Min(chunkSectors, zeroSectors - done);
                s.Write(zero, 0, n * bps);
                done += n;
                if (progress != null) progress((int)(done * 90 / zeroSectors));
            }

            // 2. Boot sector + FSInfo, and their backups at sector 6/7.
            byte[] boot = BuildBootSector(l, label, volumeId);
            byte[] fsinfo = BuildFsInfo(l);
            byte[] sector2 = new byte[bps];
            sector2[510] = 0x55; sector2[511] = 0xAA;
            WriteSector(s, bps, 0, boot);
            WriteSector(s, bps, 1, fsinfo);
            WriteSector(s, bps, 2, sector2);
            WriteSector(s, bps, 6, boot);
            WriteSector(s, bps, 7, fsinfo);
            WriteSector(s, bps, 8, sector2);

            // 3. First sector of each FAT: media/EOC markers + root dir end-of-chain.
            byte[] fat0 = new byte[bps];
            PutU32(fat0, 0, 0x0FFFFFF8);
            PutU32(fat0, 4, 0x0FFFFFFF);
            PutU32(fat0, 8, 0x0FFFFFFF);
            for (int i = 0; i < NumFats; i++)
                WriteSector(s, bps, l.ReservedSectors + (long)i * l.FatSizeSectors, fat0);

            // 4. Root directory (cluster 2) with the volume label entry.
            if (label != "NO NAME    ")
            {
                byte[] root = new byte[bps];
                Encoding.ASCII.GetBytes(label, 0, 11, root, 0);
                root[11] = 0x08; // ATTR_VOLUME_ID
                WriteDosTime(root, 22, DateTime.Now);
                WriteSector(s, bps, l.SystemAreaSectors, root);
            }

            s.Flush();
            if (progress != null) progress(100);
        }

        static byte[] BuildBootSector(Fat32Layout l, string label, uint volumeId)
        {
            byte[] b = new byte[l.BytesPerSector];
            b[0] = 0xEB; b[1] = 0x58; b[2] = 0x90;
            Encoding.ASCII.GetBytes("MSWIN4.1", 0, 8, b, 3);
            PutU16(b, 11, (ushort)l.BytesPerSector);
            b[13] = (byte)l.SectorsPerCluster;
            PutU16(b, 14, (ushort)l.ReservedSectors);
            b[16] = NumFats;
            // 17 RootEntCnt = 0, 19 TotSec16 = 0
            b[21] = 0xF8; // fixed media
            // 22 FATSz16 = 0
            PutU16(b, 24, l.SectorsPerTrack);
            PutU16(b, 26, l.Heads);
            PutU32(b, 28, l.HiddenSectors);
            PutU32(b, 32, (uint)l.TotalSectors);
            PutU32(b, 36, l.FatSizeSectors);
            // 40 ExtFlags = 0 (mirror FATs), 42 FSVer = 0
            PutU32(b, 44, 2);  // root cluster
            PutU16(b, 48, 1);  // FSInfo sector
            PutU16(b, 50, 6);  // backup boot sector
            b[64] = 0x80;      // drive number
            b[66] = 0x29;      // extended boot signature
            PutU32(b, 67, volumeId);
            Encoding.ASCII.GetBytes(label, 0, 11, b, 71);
            Encoding.ASCII.GetBytes("FAT32   ", 0, 8, b, 82);
            // Tiny boot code at 0x5A: INT 18h ("no bootable disk") then halt loop.
            b[0x5A] = 0xCD; b[0x5B] = 0x18; b[0x5C] = 0xEB; b[0x5D] = 0xFE;
            b[510] = 0x55; b[511] = 0xAA;
            return b;
        }

        static byte[] BuildFsInfo(Fat32Layout l)
        {
            byte[] b = new byte[l.BytesPerSector];
            PutU32(b, 0, 0x41615252);
            PutU32(b, 484, 0x61417272);
            PutU32(b, 488, l.ClusterCount - 1); // root dir uses one cluster
            PutU32(b, 492, 3);                  // next free cluster hint
            PutU32(b, 508, 0xAA550000);
            return b;
        }

        public static string NormalizeLabel(string label)
        {
            if (label == null) label = "";
            var sb = new StringBuilder();
            foreach (char c in label.ToUpperInvariant())
            {
                if (c < 0x20 || c > 0x7E) continue;
                if ("\"*+,./:;<=>?[\\]|".IndexOf(c) >= 0) continue;
                sb.Append(c);
                if (sb.Length == 11) break;
            }
            string s = sb.ToString().TrimEnd();
            if (s.Length == 0) s = "NO NAME";
            return s.PadRight(11);
        }

        static uint MakeVolumeId()
        {
            DateTime n = DateTime.Now;
            ushort lo = (ushort)((n.Day + (n.Month << 8)) + ((n.Millisecond / 10) + (n.Second << 8)));
            ushort hi = (ushort)((n.Minute + (n.Hour << 8)) + n.Year);
            return ((uint)hi << 16) | lo;
        }

        static void WriteDosTime(byte[] b, int off, DateTime t)
        {
            ushort time = (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
            ushort date = (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day);
            PutU16(b, off, time);
            PutU16(b, off + 2, date);
        }

        static void WriteSector(Stream s, int bps, long sector, byte[] data)
        {
            s.Seek(sector * bps, SeekOrigin.Begin);
            s.Write(data, 0, bps);
        }

        static void PutU16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        static void PutU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }
    }
}
