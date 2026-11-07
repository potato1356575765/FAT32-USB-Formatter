# FAT32 USB Formatter

A tiny Windows app that formats USB drives and SD cards as **FAT32**, including drives **larger than 32 GB**, which Windows' built-in Format dialog refuses to do.

Handy for game consoles, car stereos, 3D printers, cameras, retro handhelds, and anything else that only reads FAT32.

## Features

- Formats any USB / removable drive up to 2 TB as FAT32
- Picks the right allocation unit size automatically (or choose 4–64 KB yourself)
- Sets the volume label
- Lists only USB and removable drives, and never shows your system drive
- Asks you to confirm before erasing, with **No** as the default
- Refuses to run if another program is using the drive
- Single small `.exe`, nothing to install, no extra runtime needed (uses the .NET Framework built into Windows 10/11)

## Download

Grab `Fat32UsbFormatter.exe` from the [latest release](../../releases/latest).

## Usage

1. Plug in your USB drive.
2. Run `Fat32UsbFormatter.exe` and accept the administrator prompt. Writing directly to a drive needs admin rights.
3. Pick the drive, type a label, and leave **Allocation unit size** on *Default* unless you need something specific.
4. Click **Format as FAT32** and confirm.

> ⚠️ Formatting **erases everything** on the selected drive. Double-check the drive letter.

## Limitations

- **4 GB max file size.** That's a FAT32 limit, not this app's. Use exFAT or NTFS if you need bigger files.
- The drive needs a **drive letter**. A brand-new or fully wiped drive with no partition won't appear. Create a partition in Disk Management first.
- Volumes over 2 TB can't be FAT32.

## Antivirus warnings

Some antivirus tools (Norton, for example) may flag the app. It's unsigned, new, asks for admin rights, and writes directly to disks, and wipers and ransomware do those things too. It's a false positive. The full source is in [`src/`](src) if you want to check it or build it yourself.

## Building from source

No Visual Studio or SDK needed. It compiles with the C# compiler that ships with Windows:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

This produces `Fat32UsbFormatter.exe` in the repo folder.

| File | What it does |
| --- | --- |
| `src/Fat32Writer.cs` | Works out the FAT32 layout and writes the boot sector, FSInfo, FATs and root directory |
| `src/Volume.cs` | Finds USB drives, and locks, dismounts and writes to the raw volume |
| `src/MainForm.cs` | The Windows Forms UI |
| `src/app.manifest` | Requests admin rights and DPI awareness |

## How it works

The app locks and dismounts the volume, then writes a fresh FAT32 file system directly to it:

- boot sector with backup copy
- FSInfo sector
- two FAT tables
- root directory holding the volume label

The data area is aligned to cluster boundaries. On MBR disks the partition type is set to `0x0C` (FAT32 LBA). It's a quick format: data clusters aren't wiped, only the file system structures.
