# Regression test: once a published NotepadRedo.exe is running, it reads nothing more from the exe.
# That is what keeps an instance working when a build on another computer replaces the exe on a
# shared drive under it. See "A running instance reads nothing from its exe" in design.md.
#
#   powershell -File tests\ExeIndependence\Run.ps1                  publish as build.bat does, check
#   powershell -File tests\ExeIndependence\Run.ps1 -SelfContained   publish as the release is, check
#   ... -Exe <path>                                                 check an existing exe instead
#
# Both check the exe's header for the run-from-swap flags, then start it and list the files the
# instance has mapped.
#   Framework-dependent: started in place, it must hold no data view of its exe and must map
#     NotepadRedo.dll from an extracted copy.
#   Self-contained: its code stays in the exe, so started from a share (\\localhost\<drive>$, which
#     needs admin shares) it must run from a local copy, map nothing from the share, and give its
#     window the taskbar identity of the exe it was started from.
# The instance runs under its own name and with a scratch data folder, so no other NotepadRedo takes
# it for a sibling. A .txt registration it makes is undone afterwards, and its copies are deleted.
# Exit code 0 when every check passes.
param([string]$Exe, [switch]$SelfContained)
$ErrorActionPreference = 'Stop'

Add-Type @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class Probe {
    [StructLayout(LayoutKind.Sequential)]
    struct MBI { public IntPtr Base, AllocBase; public uint AllocProtect; public ushort PartitionId; public IntPtr Size; public uint State, Protect, Type; }
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MBI mbi, IntPtr len);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode)] static extern uint GetMappedFileNameW(IntPtr h, IntPtr addr, StringBuilder name, uint size);
    const uint MEM_COMMIT = 0x1000, MEM_IMAGE = 0x1000000, MEM_MAPPED = 0x40000;

    /// <summary>Every file mapped into the process, as "image <path>" or "data <path>".</summary>
    public static SortedSet<string> Mapped(int pid) {
        var h = OpenProcess(0x0400 | 0x0010, false, pid);   // QUERY_INFORMATION | VM_READ
        if (h == IntPtr.Zero) throw new Exception("OpenProcess failed for pid " + pid);
        var found = new SortedSet<string>();
        try {
            long addr = 0;
            MBI m;
            while (VirtualQueryEx(h, new IntPtr(addr), out m, new IntPtr(Marshal.SizeOf(typeof(MBI)))) != IntPtr.Zero) {
                if (m.State == MEM_COMMIT && (m.Type == MEM_IMAGE || m.Type == MEM_MAPPED)) {
                    var name = new StringBuilder(1024);
                    if (GetMappedFileNameW(h, m.Base, name, 1024) > 0)
                        found.Add((m.Type == MEM_IMAGE ? "image " : "data ") + name);
                }
                long next = m.Base.ToInt64() + m.Size.ToInt64();
                if (next <= addr) break;
                addr = next;
            }
        } finally { CloseHandle(h); }
        return found;
    }

    [StructLayout(LayoutKind.Sequential)] struct PKEY { public Guid FormatId; public uint PropertyId; }
    [StructLayout(LayoutKind.Sequential)] struct PV { public ushort Type; ushort r1, r2, r3; public IntPtr Pointer; IntPtr rest; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore {
        [PreserveSig] int GetCount(out uint count); [PreserveSig] int GetAt(uint index, out PKEY key);
        [PreserveSig] int GetValue(ref PKEY key, out PV value); [PreserveSig] int SetValue(ref PKEY key, ref PV value);
        [PreserveSig] int Commit();
    }
    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    /// <summary>A window's System.AppUserModel.* string property (2 = RelaunchCommand, 5 = ID).</summary>
    public static string TaskbarProperty(IntPtr hwnd, uint id) {
        var iid = typeof(IPropertyStore).GUID;
        IPropertyStore store;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0) return null;
        try {
            var key = new PKEY { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = id };
            PV value;
            if (store.GetValue(ref key, out value) != 0 || value.Type != 31) return null;   // VT_LPWSTR
            return Marshal.PtrToStringUni(value.Pointer);
        } finally { Marshal.ReleaseComObject(store); }
    }
}
'@

$name = 'NRExeTest'   # not "NotepadRedo...": other instances find their siblings by that prefix
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("nr-exeindep-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$copies = Join-Path ([IO.Path]::GetTempPath()) 'NotepadRedo\run'   # LocalCopy.Root
New-Item -ItemType Directory -Force "$scratch\bin", "$scratch\data" | Out-Null
$failures = 0
function Check([bool]$ok, [string]$what) {
    Write-Output ("{0}  {1}" -f ($(if ($ok) { 'ok  ' } else { 'FAIL' }), $what))
    if (-not $ok) { $script:failures++ }
}
function Instances() { @(Get-CimInstance Win32_Process -Filter "Name = '$name.exe'") }

# Every launch may register its own exe as a .txt handler (FileAssociation.cs). Afterwards, put back
# the paths of a registration that existed, and remove the parts of one that didn't.
$regPaths = 'HKCU:\Software\Classes\NotepadRedo.txt\shell\open\command',
            'HKCU:\Software\Classes\NotepadRedo.txt\DefaultIcon',
            'HKCU:\Software\Classes\Applications\NotepadRedo.exe\shell\open\command'
$regSaved = @{}
foreach ($k in $regPaths) {
    if (Test-Path -LiteralPath $k) { $regSaved[$k] = (Get-ItemProperty -LiteralPath $k).'(default)' }
}
# Keys the launch may create: any that don't exist yet are removed afterwards, along with what it
# put in them.
$regKeys = $regPaths + 'HKCU:\Software\Classes\NotepadRedo.txt',
                       'HKCU:\Software\Classes\Applications\NotepadRedo.exe',
                       'HKCU:\Software\NotepadRedo'
$regKeysNew = @($regKeys | Where-Object { -not (Test-Path -LiteralPath $_) })
$regValues = @{ 'HKCU:\Software\Classes\.txt\OpenWithProgids' = 'NotepadRedo.txt'
                'HKCU:\Software\RegisteredApplications' = 'NotepadRedo' }
$regValuesNew = @($regValues.Keys | Where-Object {
    $null -eq (Get-ItemProperty -LiteralPath $_ -Name $regValues[$_] -ErrorAction SilentlyContinue) })

try {
    if (-not $Exe) {
        # GetFullPath, not Resolve-Path: on a UNC path that returns a provider-qualified name
        # ("Microsoft.PowerShell.Core\FileSystem::\\server\...") that dotnet can't open.
        $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
        $kind = if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }
        Write-Output "publishing $repo\NotepadRedo.csproj, $kind..."
        # 'Stop' would make Windows PowerShell throw at the first line dotnet writes to stderr, a mere
        # notice included; the exit code is what says whether it worked.
        $ErrorActionPreference = 'Continue'
        $log = & dotnet publish "$repo\NotepadRedo.csproj" -c Release -r win-x64 `
            --self-contained $(if ($SelfContained) { 'true' } else { 'false' }) `
            -p:PublishSingleFile=true -o "$scratch\publish" 2>&1
        $ErrorActionPreference = 'Stop'
        if ($LASTEXITCODE -ne 0) {
            $log | Write-Output
            throw "dotnet publish failed ($LASTEXITCODE)"
        }
        $Exe = "$scratch\publish\NotepadRedo.exe"
    }
    $exeCopy = "$scratch\bin\$name.exe"
    Copy-Item -LiteralPath $Exe -Destination $exeCopy

    # Just the header: a self-contained exe is ~160 MB.
    $stream = [IO.File]::OpenRead($exeCopy)
    try {
        $reader = New-Object IO.BinaryReader($stream)
        $stream.Position = 0x3C                      # IMAGE_DOS_HEADER.e_lfanew
        $stream.Position = $reader.ReadInt32() + 4 + 18   # IMAGE_FILE_HEADER.Characteristics
        $characteristics = $reader.ReadUInt16()
    } finally { $stream.Dispose() }
    Check (($characteristics -band 0x0800) -ne 0) 'exe is marked IMAGE_FILE_NET_RUN_FROM_SWAP'
    Check (($characteristics -band 0x0400) -ne 0) 'exe is marked IMAGE_FILE_REMOVABLE_RUN_FROM_SWAP'

    # A document of its own, and settings that keep it from handing the file to a running instance
    # (new-instance mode) or asking about a session (RestoreSession 2 is Never).
    '{ "OpenInNewInstance": true, "RestoreSession": 2 }' |
        Set-Content -LiteralPath "$scratch\data\settings.json" -Encoding ascii
    $doc = "$scratch\t.txt"
    [IO.File]::WriteAllText($doc, "text`r`n")

    if ($SelfContained) {
        # Through the drive's admin share, the way another computer's share is reached, and without
        # --detached, so that the launcher decides where the instance runs from.
        $started = '\\localhost\' + $exeCopy[0] + '$' + $exeCopy.Substring(2)
        if (-not (Test-Path -LiteralPath $started)) { throw "$started is not reachable (admin shares needed)" }
        $arguments = @("`"$doc`"")
    } else {
        $started = $exeCopy
        $arguments = @('--detached', "`"$doc`"")
    }
    # Only for the instance: run in an interactive shell, the script must not leave it set there.
    $dataBefore = $env:NOTEPADREDO_DATA_DIR
    $env:NOTEPADREDO_DATA_DIR = "$scratch\data"
    try { Start-Process -FilePath $started -ArgumentList $arguments | Out-Null }
    finally { $env:NOTEPADREDO_DATA_DIR = $dataBefore }

    # The instance: the process of that name with a window (the launcher has none, and exits).
    $p = $null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not $p -and $sw.ElapsedMilliseconds -lt 180000) {   # a first copy reads ~160 MB
        Start-Sleep -Milliseconds 300
        $p = Instances | ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue } |
            Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like '*t.txt*' } | Select-Object -First 1
    }
    if (-not $p) { throw 'the instance never showed a window' }
    Start-Sleep -Milliseconds 1500   # let startup finish loading what it loads
    $maps = [Probe]::Mapped($p.Id)

    if ($SelfContained) {
        $path = (Get-CimInstance Win32_Process -Filter "ProcessId = $($p.Id)").ExecutablePath
        Check ($path -like "$copies\*") "it runs from a local copy: $path"
        $fromShare = @($maps | Where-Object { $_ -like '*\Device\Mup\*' })
        Check ($fromShare.Count -eq 0) "it maps nothing from the share$(if ($fromShare) { ': ' + ($fromShare -join '; ') })"
        $relaunch = [Probe]::TaskbarProperty($p.MainWindowHandle, 2)
        Check ($relaunch -eq "`"$started`"") "its window is pinned as the exe it was started from: $relaunch"
    } else {
        $dataViews = @($maps | Where-Object { $_ -like "data *\$name.exe" })
        $appCode = @($maps | Where-Object { $_ -like 'image *\NotepadRedo.dll' })
        Check ($dataViews.Count -eq 0) "no data view of its exe (the bundle) is mapped$(if ($dataViews) { ': ' + ($dataViews -join '; ') })"
        Check ($appCode.Count -gt 0) "NotepadRedo.dll is mapped from an extracted copy$(if ($appCode) { ': ' + ($appCode -join '; ') })"
    }
}
catch {
    Write-Output "FAIL  $($_.Exception.Message)"
    $failures++
}
finally {
    foreach ($x in Instances) { Stop-Process -Id $x.ProcessId -Force -ErrorAction SilentlyContinue }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ((Instances).Count -gt 0 -and $sw.ElapsedMilliseconds -lt 10000) { Start-Sleep -Milliseconds 200 }
    foreach ($k in $regSaved.Keys) {
        if ((Get-ItemProperty -LiteralPath $k).'(default)' -ne $regSaved[$k]) {
            Set-ItemProperty -LiteralPath $k -Name '(default)' -Value $regSaved[$k]
        }
    }
    foreach ($k in $regKeysNew) { Remove-Item -LiteralPath $k -Recurse -ErrorAction SilentlyContinue }
    foreach ($k in $regValuesNew) {
        Remove-ItemProperty -LiteralPath $k -Name $regValues[$k] -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) ".net\$name") -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $copies -Directory -Filter "$name-*" -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Output $(if ($failures -eq 0) { 'PASS' } else { "FAIL: $failures check(s)" })
exit $(if ($failures -eq 0) { 0 } else { 1 })
