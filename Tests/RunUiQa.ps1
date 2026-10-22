param([Parameter(Mandatory)][string]$Application,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
# Run on a separate desktop without switching to it or taking foreground focus.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class QaDesktop {
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] public struct Startup { public int cb;public string reserved,desktop,title;public int x,y,width,height,xchars,ychars,fill,flags;public short show,reserved2;public IntPtr reservedPointer,input,output,error; }
 [StructLayout(LayoutKind.Sequential)] public struct ProcessInfo { public IntPtr process,thread;public int pid,tid; }
 [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] public static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,int flags,uint access,IntPtr security);
 [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] public static extern bool CreateProcess(string application,string command,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr environment,string directory,ref Startup startup,out ProcessInfo process);
 [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
 [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr process,out uint code);
 [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
}
'@
$Application=(Resolve-Path -LiteralPath $Application).Path
$Output=[IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$name='PHRTF-QA-'+[guid]::NewGuid().ToString('N')
$desktop=[QaDesktop]::CreateDesktop($name,[IntPtr]::Zero,[IntPtr]::Zero,0,0x10000000,[IntPtr]::Zero)
if($desktop-eq[IntPtr]::Zero){throw 'Isolated QA desktop creation failed'}
$startup=[QaDesktop+Startup]::new();$startup.cb=[Runtime.InteropServices.Marshal]::SizeOf($startup);$startup.desktop='WinSta0\'+$name
$process=[QaDesktop+ProcessInfo]::new()
try {
 if(-not[QaDesktop]::CreateProcess($Application,'"'+$Application+'" --ui-qa "'+$Output+'"',[IntPtr]::Zero,[IntPtr]::Zero,$false,0x08000000,[IntPtr]::Zero,[IO.Path]::GetDirectoryName($Application),[ref]$startup,[ref]$process)){throw ('QA launch failed: '+[Runtime.InteropServices.Marshal]::GetLastWin32Error())}
 while([QaDesktop]::WaitForSingleObject($process.process,1000)-eq258){}
 $code=0;[void][QaDesktop]::GetExitCodeProcess($process.process,[ref]$code)
 if($code-ne0){throw ('QA process failed: '+$code+'; see '+$Output)}
 Get-Content -LiteralPath (Join-Path $Output 'ui-results.json')
} finally {
 if($process.thread-ne[IntPtr]::Zero){[void][QaDesktop]::CloseHandle($process.thread)}
 if($process.process-ne[IntPtr]::Zero){[void][QaDesktop]::CloseHandle($process.process)}
 [void][QaDesktop]::CloseDesktop($desktop)
}
