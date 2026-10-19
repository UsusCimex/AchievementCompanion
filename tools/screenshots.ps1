<#
.SYNOPSIS
    Снимки окон приложения для проверки интерфейса и README.
.DESCRIPTION
    Работает на копиях: сборка копируется без папки bridge (помощник не запустится и не покажет тебя в игре),
    папка данных - во временную (оригинал не меняется). Нужна папка данных с settings.json и games\<appid>.json;
    открывается игра из LastAppId. Снимает главное окно, вкладку плана и оверлей.
.EXAMPLE
    dotnet build -c Release
    .\tools\screenshots.ps1 -DataDir D:\ac-demo -OutDir docs\images -Theme Dark
#>
param(
    [Parameter(Mandatory)] [string] $DataDir,
    [Parameter(Mandatory)] [string] $OutDir,
    [ValidateSet("Light", "Dark")] [string] $Theme = "Dark",
    [string] $Build,
    [int] $SelectIndex = 1
)
$ErrorActionPreference = "Stop"
# Не в param: там Windows PowerShell 5.1 при запуске через -File оставляет $PSScriptRoot пустым.
if (-not $Build) { $Build = Join-Path $PSScriptRoot "..\src\AC.App\bin\Release\net10.0-windows" }

$running = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -Name RunningAppID -ErrorAction SilentlyContinue).RunningAppID
if ($running -and $running -ne 0) { throw "Запущена игра (AppID $running): окна поверх неё не открываю" }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class Shot {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
    public static RECT Bounds(IntPtr h) { RECT v; DwmGetWindowAttribute(h, 9, out v, Marshal.SizeOf(typeof(RECT))); return v; }
    public static void TopMost(IntPtr h) { SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); }
    public static void Move(IntPtr h, int x, int y) { SetWindowPos(h, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010); }
    // С экрана, а не PrintWindow: тот не видит картинки, догруженные после первой отрисовки.
    // CAPTUREBLT - чтобы попал оверлей (layered-окно).
    public static void Grab(IntPtr h, string path) {
        var v = Bounds(h);
        using (var bmp = new Bitmap(v.R - v.L, v.B - v.T, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(bmp)) {
                IntPtr dst = g.GetHdc(); IntPtr src = GetDC(IntPtr.Zero);
                BitBlt(dst, 0, 0, bmp.Width, bmp.Height, src, v.L, v.T, 0x00CC0020 | 0x40000000);
                ReleaseDC(IntPtr.Zero, src); g.ReleaseHdc(dst);
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }
}
"@
[void][Shot]::SetThreadDpiAwarenessContext([IntPtr](-4))
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
function Both($a, $b) { New-Object System.Windows.Automation.AndCondition($a, $b) }
function WaitFor([scriptblock] $check, [int] $seconds, [string] $what) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $seconds) { $r = & $check; if ($r) { return $r }; Start-Sleep -Milliseconds 300 }
    throw "Не дождался: $what"
}
function Named($root, $type, $name) { $root.FindFirst($TS::Descendants, (Both (Cond $AE::ControlTypeProperty $type) (Cond $AE::NameProperty $name))) }
function ButtonWithText($root, $text) {
    $node = Named $root $CT::Text $text
    while ($node -and $node.Current.ControlType -ne $CT::Button) { $node = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($node) }
    $node
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ac-shots-" + [Guid]::NewGuid().ToString("N"))
$app = Join-Path $tmp "app"
$data = Join-Path $tmp "data"
# Полный путь: Bitmap.Save считает относительный путь от папки процесса, а не от текущей папки PowerShell.
$OutDir = (New-Item -ItemType Directory -Force $OutDir).FullName
Copy-Item (Resolve-Path $Build) $app -Recurse
Remove-Item (Join-Path $app "bridge") -Recurse -Force -ErrorAction SilentlyContinue
# Сборка без AC_DATA_DIR открыла бы настоящую папку данных пользователя.
$latin = [Text.Encoding]::GetEncoding(28591)
$infra = $latin.GetString([IO.File]::ReadAllBytes((Join-Path $app "AC.Infrastructure.dll")))
if (-not $infra.Contains($latin.GetString([Text.Encoding]::Unicode.GetBytes("AC_DATA_DIR")))) { throw "Сборка не поддерживает AC_DATA_DIR - не запускаю" }
Copy-Item (Resolve-Path $DataDir) $data -Recurse
$settingsPath = Join-Path $data "settings.json"
$settings = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$settings | Add-Member -NotePropertyName Theme -NotePropertyValue $Theme -Force
[IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 5))

$psi = New-Object Diagnostics.ProcessStartInfo (Join-Path $app "AchievementCompanion.exe")
$psi.UseShellExecute = $false
$psi.EnvironmentVariables["AC_DATA_DIR"] = $data
$p = [Diagnostics.Process]::Start($psi)
$suffix = $Theme.ToLowerInvariant()
try {
    $hwnd = WaitFor { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $p.MainWindowHandle } } 30 "главное окно"
    [Shot]::TopMost($hwnd)
    $win = $AE::FromHandle($hwnd)
    $list = WaitFor { $win.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty "AchievementList")) } 30 "список"
    $items = WaitFor { $i = $list.FindAll($TS::Children, (Cond $AE::ControlTypeProperty $CT::ListItem)); if ($i.Count -gt $SelectIndex) { , $i } } 60 "достижения"
    $items[$SelectIndex].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 4
    [Shot]::Grab($hwnd, (Join-Path $OutDir "main-$suffix.png"))

    (Named $win $CT::TabItem "План до 100%").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 3
    [Shot]::Grab($hwnd, (Join-Path $OutDir "plan-$suffix.png"))

    (Named $win $CT::TabItem "Достижения").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    (ButtonWithText $win "Оверлей").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $overlay = WaitFor { $AE::RootElement.FindFirst($TS::Children, (Both (Cond $AE::ProcessIdProperty $p.Id) (Cond $AE::NameProperty "Achievement Companion - оверлей"))) } 15 "оверлей"
    $b = [Shot]::Bounds($hwnd)
    # Оверлей кладём поверх главного окна, чтобы в кадр не попало чужое.
    [Shot]::Move([IntPtr]$overlay.Current.NativeWindowHandle, $b.L + 330, $b.T + 330)
    Start-Sleep -Seconds 3
    [Shot]::Grab($hwnd, (Join-Path $OutDir "overlay-$suffix.png"))
    "Готово: $OutDir"
}
finally {
    if (-not $p.HasExited) { [void]$p.CloseMainWindow(); if (-not $p.WaitForExit(8000)) { $p.Kill() } }
    Start-Sleep -Milliseconds 500
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
