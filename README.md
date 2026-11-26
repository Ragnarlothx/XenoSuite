# XenoSuite

A Windows system toolkit by **Xeno**: a live hardware/system monitor, a full disk manager with health,
recovery and imaging tools, and a built-in security audit, in one app.
C# .NET 8 WPF host with a WebView2 UI.

![Monitor overview](docs/screenshots/monitor-overview.png)

> Screenshots are from a real PC. Serial numbers, machine name, IP/MAC addresses and similar identifiers
> are replaced with `REDACTED` / `x.x.x.x`.

---

## Modules at a glance

| Module | What it's for |
|---|---|
| [**Monitor**](#monitor) | Live CPU, memory, GPUs, disks, network, temperatures, fans; processes, services, startup apps; system report |
| [**Disk**](#disk) | Partitions and volumes, SMART health, surface scans, file finder, duplicates, space analysis, recovery, USB tools, imaging, benchmarks |
| [**Audit**](#audit) | Built-in security audit: 33 read-only checks, graded A–F, with fixes |

Switch modules with the tabs at the top. Everything runs locally; nothing is sent anywhere.

---

## Monitor

![Performance](docs/screenshots/monitor-performance.png)

| Page | Functions |
|---|---|
| **Overview** | One-screen dashboard: CPU, memory, each GPU, network and disk I/O cards with sparklines; per-thread CPU load; hottest-first thermal list; fans and pumps |
| **Performance** | Task-Manager-style detail per device (CPU, memory, every GPU, every disk, every network adapter): history charts, clocks, power, temperature, utilization, handles/threads, interrupts, DPC time, run queue, all raw CPU sensors |
| **Processes** | Full process list: CPU, memory, commit, GPU and VRAM, disk read/write, threads, handles, priority, user, session, start time, path; **end, suspend, resume**, change **priority** and **CPU affinity**, loaded modules, open file location |
| **Sensors** | Every hardware sensor (via LibreHardwareMonitor): temperatures, voltages, fan RPM and duty, clocks, loads, power; min/max tracking, favorites, filters by type, full hardware report, **CSV logging** |
| **Network** | Adapters with live throughput, active **TCP connections** per process, **latency targets** (gateway + your own hosts, pinged every 2 s), DNS lookup |
| **Disks** | Per-volume free space, per-disk live read/write charts, latency, queue depth, IOPS, drive life, total data written, temperature |
| **Services** | All Windows services with status, startup type, PID, account and description; **start, stop, restart, pause, continue** |
| **Startup** | Programs that start with Windows (registry Run keys and Startup folders) with **enable/disable** |
| **System** | Hardware and OS report: board, BIOS, CPU, memory modules, GPUs, drives, OS build |
| **Health** | Quick system health summary |
| **Mini mode** | A compact always-on-top bar with the key numbers |
| **Settings** | Sample interval, always-on-top, theme, title bar, logging |

![Sensors](docs/screenshots/monitor-sensors.png)

![Disks](docs/screenshots/monitor-disks.png)

![Services](docs/screenshots/monitor-services.png)

---

## Disk

![Disk manager](docs/screenshots/disk-manager.png)

| Page | Functions |
|---|---|
| **Disks** | Every physical disk with bus, media type, partition style, health and a partition map. **Create, delete, resize and format** partitions; set **label**, **drive letter** and partition **attributes** (active/hidden/read-only); **initialize**, **clear** and **convert MBR ⇄ GPT**; take a disk **offline/online** or make it **read-only** |
| **Volumes** | Volume list with **repair** (online scan / spot fix / full), **optimize** (TRIM for SSDs, defrag for HDDs) and **BitLocker** status |
| **Health** | SMART failure prediction per drive and a non-destructive **surface scan** for bad sectors |
| **Finder** | Fast file search by name, size, date and type across a folder or drive; send results to the **Recycle Bin**, **move** or **copy** them |
| **Space** | Folder-size analyzer: what's using the space, drill-down tree and breakdown by file type |
| **Duplicates** | Finds duplicate files by content hash |
| **Recover** | Restore from the **Recycle Bin**, browse and restore from **Volume Shadow Copies** (and create a new snapshot), list a file's previous versions |
| **USB** | Safely **eject**, toggle the system-wide **USB write-protect** policy, and run a **capacity test** that catches fake-capacity flash drives |
| **Tools** | **chkdsk**, optimize/defrag, sequential read/write **benchmark**, sector-by-sector **disk imaging** to and from `.img` with verification, **file hashing** (MD5/SHA), **temp-file cleanup** (with a dry run first) |

![Disk health](docs/screenshots/disk-health.png)

![Disk tools](docs/screenshots/disk-tools.png)

Long operations (scans, imaging, benchmarks, searches) run as background jobs with progress and can be cancelled.

> ⚠️ Partition, format, clear, convert and image-write operations change disks for real. They are confirmed
> before running; double-check the disk number.

---

## Audit

A **built-in security audit**: nothing else to download or install. 33 read-only checks of Windows
security settings, each with a pass/warn/fail result, evidence and the exact place to change it:

| Area | Checks |
|---|---|
| **Antivirus** | Active antivirus, Microsoft Defender settings, antivirus exclusions |
| **Updates** | Whether this Windows version still gets security updates, recent Windows Update activity, pending restart |
| **Firewall & network** | Firewall profiles, network profile, ports exposed to the network, hosts file, proxy settings |
| **Hardening** | UAC, SMBv1, BitLocker, Secure Boot, TPM, core isolation (memory integrity), LSA protection, SmartScreen, Smart App Control, vulnerable driver blocklist, WDigest password caching, PowerShell 2.0, PowerShell script policy, AutoRun/AutoPlay |
| **Remote access** | Remote Desktop, Remote Assistance, remote-access services |
| **Accounts** | Built-in accounts (Administrator, Guest), members of Administrators |
| **Persistence** | Startup programs and scheduled tasks with unsigned or missing files (signatures checked with Windows' own verifier, catalog-signed system files included), unquoted service paths |

You get a score out of 100 with a grade from **A** to **F**. Each problem shows its evidence and the fix,
and **Open settings** jumps to the matching Windows Settings or Windows Security page. The audit only
**reads** the system and never changes a setting. Run XenoSuite as administrator for the complete set of
checks.

The last report is saved in `%APPDATA%\XenoSuite\audit\` (`report.json` and a standalone `report.html`).
The older external VulnAudit tool still works as an optional extra for one-click fixes; the tab doesn't need it.

---

## Requirements

- Windows 10 or 11 (x64) with the Microsoft Edge WebView2 Runtime
- For the portable build, extract the entire ZIP and run `XenoSuite.exe`. The package includes .NET 8 and the Audit module.
- Approve the Windows administrator prompt, then select **Audit → Run full scan** to run the built-in checks.

## Build

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o dist\XenoSuite
```

Package everything in `dist\XenoSuite`, including the `web` folder. Building requires a .NET SDK that supports .NET 8. Hardware sensors come from
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor).

## Project layout

| Path | Contents |
|---|---|
| `MainWindow.xaml.cs`, `Api.cs` | WebView2 host and the bridge between the UI and the C# back end |
| `Sampler.cs`, `Counters.cs`, `HwSensors.cs`, `Proc.cs` | Live monitoring: performance counters, sensors, processes |
| `Storage.cs`, `Health.cs`, `DiskTools.cs`, `Usb.cs`, `Recovery.cs` | Disk management, SMART, imaging, USB and recovery |
| `Finder.cs`, `Space.cs`, `Scan.cs` | File search, duplicates, space analysis |
| `SysHealth.cs`, `Tools.cs`, `Wmi.cs`, `Native.cs`, `Audit.cs` | System report, misc tools, WMI/Win32 helpers, audit integration |
| `web/mon`, `web/disk`, `web/audit` | The three module UIs |

App data (window placement, settings, logs) lives in `%APPDATA%\XenoSuite`.
