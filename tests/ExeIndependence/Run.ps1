# Regression test: once a published NotepadRedo.exe is running, it reads nothing more from the exe.
# That is what keeps an instance working when a build on another computer replaces the exe on a
# shared drive under it. See "A running instance reads nothing from its exe" in design.md.
#
#   powershell -File tests\ExeIndependence\Run.ps1               publish as build.bat does, then check
#   powershell -File tests\ExeIndependence\Run.ps1 -Exe <path>   check an existing exe instead
#
# Checks the exe's header for the run-from-swap flags, then starts it and lists the files it has
# mapped: it must hold no data view of its exe, and must map NotepadRedo.dll from an extracted copy.
# The instance runs under its own name and with a scratch data folder, so no other NotepadRedo takes
# it for a sibling, and the .txt registration that every launch rewrites is put back afterwards.
# Exit code 0 when every check passes.
param([string]$Exe)
$ErrorActionPreference = 'Stop'

Add-Type @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class MappedFiles {
    [StructLayout(LayoutKind.Sequential)]
    struct MBI { public IntPtr Base, AllocBase; public uint AllocProtect; public ushort PartitionId; public IntPtr Size; public uint State, Protect, Type; }
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MBI mbi, IntPtr len);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode)] static extern uint GetMappedFileNameW(IntPtr h, IntPtr addr, StringBuilder name, uint size);
    const uint MEM_COMMIT = 0x1000, MEM_IMAGE = 0x1000000, MEM_MAPPED = 0x40000;

    /// <summary>Every file mapped into the process, as "image <path>" or "data <path>".</summary>
    public static SortedSet<string> Of(int pid) {
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
}
'@

$name = 'NRExeTest'   # not "NotepadRedo...": other instances find their siblings by that prefix
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("nr-exeindep-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force "$scratch\bin", "$scratch\data" | Out-Null
$failures = 0
function Check([bool]$ok, [string]$what) {
    Write-Output ("{0}  {1}" -f ($(if ($ok) { 'ok  ' } else { 'FAIL' }), $what))
    if (-not $ok) { $script:failures++ }
}

# Every launch registers its own exe as a .txt handler (FileAssociation.cs). Afterwards, put back the
# paths of a registration that existed, and remove the parts of one that didn't.
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

$p = $null
try {
    if (-not $Exe) {
        # GetFullPath, not Resolve-Path: on a UNC path that returns a provider-qualified name
        # ("Microsoft.PowerShell.Core\FileSystem::\\server\...") that dotnet can't open.
        $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
        Write-Output "publishing $repo\NotepadRedo.csproj as build.bat does..."
        # 'Stop' would make Windows PowerShell throw at the first line dotnet writes to stderr, a mere
        # notice included; the exit code is what says whether it worked.
        $ErrorActionPreference = 'Continue'
        $log = & dotnet publish "$repo\NotepadRedo.csproj" -c Release -r win-x64 --self-contained false `
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

    # Just the header: a self-contained exe given as -Exe is ~160 MB.
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
    # Only for the instance: run in an interactive shell, the script must not leave it set there.
    $dataBefore = $env:NOTEPADREDO_DATA_DIR
    $env:NOTEPADREDO_DATA_DIR = "$scratch\data"
    try { $p = Start-Process -FilePath $exeCopy -ArgumentList '--detached', "`"$doc`"" -PassThru }
    finally { $env:NOTEPADREDO_DATA_DIR = $dataBefore }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($p.MainWindowHandle -eq 0 -and -not $p.HasExited -and $sw.ElapsedMilliseconds -lt 30000) {
        Start-Sleep -Milliseconds 200
        $p.Refresh()
    }
    if ($p.HasExited -or $p.MainWindowHandle -eq 0) { throw 'the instance never showed a window' }
    Start-Sleep -Milliseconds 1500   # let startup finish loading what it loads

    $maps = [MappedFiles]::Of($p.Id)
    $dataViews = @($maps | Where-Object { $_ -like "data *\$name.exe" })
    $appCode = @($maps | Where-Object { $_ -like 'image *\NotepadRedo.dll' })
    Check ($dataViews.Count -eq 0) "no data view of its exe (the bundle) is mapped$(if ($dataViews) { ': ' + ($dataViews -join '; ') })"
    Check ($appCode.Count -gt 0) "NotepadRedo.dll is mapped from an extracted copy$(if ($appCode) { ': ' + ($appCode -join '; ') })"
}
catch {
    Write-Output "FAIL  $($_.Exception.Message)"
    $failures++
}
finally {
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force; $p.WaitForExit(10000) | Out-Null }
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
    $extracted = Join-Path ([IO.Path]::GetTempPath()) ".net\$name"
    Remove-Item -LiteralPath $extracted -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output $(if ($failures -eq 0) { 'PASS' } else { "FAIL: $failures check(s)" })
exit $(if ($failures -eq 0) { 0 } else { 1 })
