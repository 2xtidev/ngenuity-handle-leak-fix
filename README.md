# NGenuityLeakFix

A small workaround for a handle leak in **HyperX NGENUITY**: its background
process `NGenuity2Helper.exe` opens a handle to every running process every few
seconds and never closes them. Left running for a day or two, the PC ends up at
99% memory and 100% disk.

This tool closes the leaked handles and nothing else. It needs no administrator
rights, and ships as readable source plus an executable built by GitHub Actions.

> Not affiliated with HyperX or HP. Use at your own risk; see [Risks](#risks).

## Symptoms

- Memory at 95–99% while Task Manager shows no process using much of it.
- Disk at 100% (heavy paging), everything slow, sometimes apps failing with
  "Thread failed to start" or out-of-memory errors.
- `pagefile.sys` grows to tens of GB and eats space on `C:`.
- Rebooting fixes it for a day or two.

## Check whether you have it

In PowerShell:

```powershell
(Get-Process NGenuity2Helper).HandleCount
```

A normal process holds a few thousand handles. If this number is in the
hundreds of thousands or millions and keeps growing, you are affected.

## What we measured

Windows 11 25H2 (build 26200), 32 GB RAM, NGENUITY 5.38.0.0 from the Microsoft
Store (the latest at the time of writing).

**After about 36 hours of uptime:**

| | |
|---|---|
| `NGenuity2Helper.exe` handles | **6,423,670** (next highest process: 14,675) |
| Committed memory | 114.3 GB of a 114.6 GB limit |
| Free RAM | 0.7 GB of 31 GB |
| Paging | ~22,000 pages/s, disk queue 11.5 |
| Sum of all processes' private memory | only 17.6 GB |

Killing `NGenuity2Helper.exe` dropped committed memory from 114 GB to 31.5 GB
immediately and brought the disk queue back to 0.1.

**After restarting NGENUITY, measured every 15 seconds:**

- The count grows linearly from launch, whether the app is used or idle:
  ~62 handles/s in the first 10 minutes, ~88 handles/s over the next 4.5 hours
  (about 5–7 million per day).
- **99.93%** of the handles are of type *Process* (1,428,911 of 1,429,911).
- Sampling 3,000 of them: they point at **every running process** on the
  machine (415 distinct targets: browsers, Discord, Telegram, `svchost`,
  `conhost`, ...), a few thousand handles per target. About **7%** point at
  processes that had already exited.

So the helper enumerates the running processes (probably to detect games),
opens each one, and never calls `CloseHandle`.

**Why memory is hit so hard.** A handle to a process that has exited keeps that
process object alive as a "zombie" until the handle is closed. The cost is not
linear in the handle count: at 1.47 million handles (4.5 hours) closing them
freed about 1.2 GB, while at 6.4 million (36 hours) killing the helper freed
over 80 GB of commit. Our reading is that the cost is dominated by exited
processes, which accumulate with uptime; we have not proven this.

## How the fix works

Every 5 seconds `NGenuityLeakFix`:

1. Lists the handles held by `NGenuity2Helper.exe`
   (`NtQuerySystemInformation(SystemExtendedHandleInformation)`), keeping only
   those of type *Process*.
2. For each new handle, finds out which process it points to and whether that
   process is still running (a short-lived duplicate, `GetProcessId`,
   `GetExitCodeProcess`, closed right away).
3. Closes, inside the helper (`DuplicateHandle` with `DUPLICATE_CLOSE_SOURCE`),
   every handle older than 30 seconds that either
   - points at a process that has exited, or
   - points at a process the helper holds a **newer** handle to.

The newest handle to every live process is always left open, so if the helper
really uses a handle, it still has one. The tool does nothing while the helper
holds fewer than 500 process handles, so it goes quiet by itself if HyperX
fixes the bug.

It never touches another process, never touches other handle types, and makes
no network connections. It writes one log file:
`%LOCALAPPDATA%\NGenuityLeakFix\NGenuityLeakFix.log`.

The first pass on our machine:

```
pid 64300: 1475025 handles, 1474051 of type Process;
closed 1473745 (1384922 duplicate, 88823 to exited processes)
```

It took under 5 seconds. The helper and the NGENUITY app kept running, and the
helper then stayed at ~4,400 handles.

## Install

No administrator rights are needed: the helper runs as your user.

1. Download `NGenuityLeakFix.zip` from
   [Releases](../../releases/latest) and extract it.
2. In that folder, run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```

This copies the files to `%LOCALAPPDATA%\NGenuityLeakFix`, adds a shortcut to
your Startup folder and starts the tool.

### Script mode (no binary)

If you would rather not run an `.exe` at all:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Mode script
```

This runs `run.ps1`, which compiles `src\NGenuityLeakFix.cs` in memory with
`Add-Type` at every start. What you read is exactly what runs.

### Try it first

See what it would do without closing anything:

```powershell
powershell -ExecutionPolicy Bypass -File .\run.ps1 -DryRun -Once
```

### Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

## Verify the executable

The `.exe` is not code-signed, so Windows SmartScreen may warn about it. It is
built by [the workflow](.github/workflows/build.yml) from the source in this
repository, with the `csc.exe` that ships with Windows. Every release lists its
SHA-256, and GitHub records a signed build attestation you can check:

```powershell
gh attestation verify NGenuityLeakFix.exe --repo 2xtidev/ngenuity-handle-leak-fix
```

To build it yourself, no SDK is needed:

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /target:winexe /platform:x64 /optimize+ /out:NGenuityLeakFix.exe src\NGenuityLeakFix.cs
```

## Options

```
NGenuityLeakFix.exe [--interval 5] [--min-age 30] [--threshold 500]
                    [--dry-run] [--once] [--console] [--verbose] [--log path]
```

| Option | Default | Meaning |
|---|---|---|
| `--interval` | 5 | Seconds between passes |
| `--min-age` | 30 | A handle must be at least this many seconds old before it can be closed |
| `--threshold` | 500 | Do nothing while the helper holds fewer process handles than this |
| `--dry-run` | | Report what would be closed, close nothing |
| `--once` | | One pass, then exit |
| `--console` | | Print to a console as well as the log |
| `--verbose` | | Log every pass instead of a summary every 10 minutes |

## Risks

- Closing another program's handles is intrusive by nature. If the helper did
  need one of the handles closed here, the worst expected outcome is that the
  helper errors or restarts, which NGENUITY does on its own. We have not seen
  this happen.
- Handle values can be reused. If the helper closed a handle itself and got the
  same value back between two passes, the tool would treat the new handle as
  old. The 30-second minimum age and the "newest handle per process is kept"
  rule make this harmless in practice.
- Tested on one machine (Windows 11 25H2, NGENUITY 5.38.0.0). 64-bit Windows
  only.

## Alternatives

- Remove NGENUITY from startup (Task Manager → Startup apps) and open it only
  when you change settings. Most HyperX devices keep their settings onboard.
- Restart `NGenuity2Helper.exe` periodically, for example from Task Scheduler.
- [withmorten/NGenuity2Helper_leakfix](https://github.com/withmorten/NGenuity2Helper_leakfix)
  takes the same approach more aggressively: it closes every process handle
  still present one second later. It confirmed the approach works; this
  repository is an independent implementation.

## Reporting to HyperX

If you are affected, report it to HyperX support with your NGENUITY version and
the output of the handle check above. The more reports, the sooner a real fix.

## License

[MIT](LICENSE)
