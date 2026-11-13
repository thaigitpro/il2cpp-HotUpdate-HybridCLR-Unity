<#
  HotPatchKit — lệnh dòng lệnh (gọi Unity -batchmode). Hướng dẫn đầy đủ: README.md cạnh thư mục Tools.

    .\hotpatch.ps1 -Project D:\MyGame -Install                                  # một lần mỗi máy (và sau khi nâng gói HybridCLR)
    .\hotpatch.ps1 -Project D:\MyGame -Build -Out D:\MyGame\Build\win           # dựng bản cài Windows vá được (thêm -Dev: bản dev)
    .\hotpatch.ps1 -Project D:\MyGame -Build -Target android -Out D:\MyGame\Build\game.apk
    .\hotpatch.ps1 -Project D:\MyGame -Build -Target ios -Out D:\MyGame\Build\ios           # ra project Xcode
    .\hotpatch.ps1 -Project D:\MyGame -Publish                                  # bản vá cho các bản cài Windows mới nhất trong kho
    .\hotpatch.ps1 -Project D:\MyGame -Publish -Target all -Base a1b2c3d4e5f6,0f9e8d7c6b5a   # chỉ các bản cài này, mỗi mã về đúng nền tảng
    .\hotpatch.ps1 -Project D:\MyGame -List -Target all                         # các bản cài trong kho bản gốc (mã cho -Base)

  Unity: -Unity <đường dẫn Unity.exe>, hoặc biến môi trường HOTPATCHKIT_UNITY; không có thì tìm bản ghi trong
  ProjectSettings\ProjectVersion.txt ở thư mục cài mặc định của Unity Hub.
  Bản vá ra thư mục cdnDir (cấu hình, mặc định <project>\HotPatchCDN) — đẩy nội dung thư mục đó lên CDN (giữ nguyên cấu trúc
  <nền tảng>\<mã bản cài>\{patch.bytes, version.json}). Lệnh không tự đẩy.
#>
param([string]$Project = '.', [string]$Unity = '', [switch]$Install, [switch]$Build, [switch]$Publish, [switch]$List,
  [ValidateSet('win64', 'android', 'ios', 'all')][string]$Target = 'win64', [string]$Out = '', [switch]$Dev,
  [string[]]$Base = @(), [int]$Bases = 0, [string]$Cdn = '')
$ErrorActionPreference = 'Stop'
$proj = (Resolve-Path $Project).Path
$cfgFile = Join-Path $proj 'Assets\HotPatchKit\Resources\HotPatchKit.json'
$unityTarget = @{ win64 = 'Win64'; android = 'Android'; ios = 'iOS' }
$platforms = if ($Target -eq 'all') { @('android', 'ios', 'win64') } else { @($Target) }
$codes = @($Base | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

function KeyDir {
  $d = $env:HOTPATCHKIT_KEYDIR
  if (-not $d -and (Test-Path $cfgFile)) { $d = (Get-Content $cfgFile -Raw -Encoding utf8 | ConvertFrom-Json).keyDir }
  if (-not $d) { throw "chưa biết thư mục khoá: tạo $cfgFile (chạy -Install) hoặc đặt HOTPATCHKIT_KEYDIR" }
  [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($d))
}

function CdnDir {
  if ($Cdn) { return [IO.Path]::GetFullPath($Cdn) }
  $d = 'HotPatchCDN'
  if (Test-Path $cfgFile) { $c = (Get-Content $cfgFile -Raw -Encoding utf8 | ConvertFrom-Json).cdnDir; if ($c) { $d = $c } }
  $d = [Environment]::ExpandEnvironmentVariables($d)
  if ([IO.Path]::IsPathRooted($d)) { $d } else { [IO.Path]::GetFullPath((Join-Path $proj $d)) }
}

function Archived([string]$t) {
  $d = Join-Path (Join-Path (KeyDir) 'base') $t
  if (Test-Path $d) { @(Get-ChildItem $d -Directory | ForEach-Object { $_.Name }) } else { @() }
}

if ($List) {
  $cdnDir = CdnDir
  foreach ($t in $platforms) {
    "== $t"
    $dir = Join-Path (Join-Path (KeyDir) 'base') $t
    if (-not (Archived $t)) { "  (chưa có bản cài $t nào trong kho)"; ''; continue }
    $rows = foreach ($d in Get-ChildItem $dir -Directory) {
      $i = $null
      $f = Join-Path $d.FullName 'info.json'
      if (Test-Path $f) { $i = Get-Content $f -Raw -Encoding utf8 | ConvertFrom-Json }
      $v = Join-Path $cdnDir "$t\$($d.Name)\version.json"
      [pscustomobject]@{
        'Mã bản cài' = $d.Name
        'Dựng lúc'   = if ($i) { $i.built } else { '?' }
        'Loại'       = if ($i -and $i.development -eq 'False') { 'phát hành' } else { 'dev' }
        'Commit'     = if ($i) { $i.commit } else { '' }
        'Runtime'    = if ($i) { $i.runtime } else { '' }
        'Bản vá'     = if (Test-Path $v) { (Get-Content $v -Raw | ConvertFrom-Json).version } else { '-' }
        'Ra'         = if ($i) { $i.out } else { '' }
      }
    }
    ($rows | Sort-Object 'Dựng lúc' -Descending | Format-Table -AutoSize | Out-String -Width 220).TrimEnd()
    ''
  }
  'Bản cài cùng runtime với mã hiện tại mới vá được (Publish báo rõ bản nào không vá được).'
  exit 0
}

if (-not ($Install -or $Build -or $Publish)) { Get-Help $PSCommandPath -Detailed; exit 1 }

if (-not $Unity) { $Unity = $env:HOTPATCHKIT_UNITY }
if (-not $Unity) {
  $pv = Join-Path $proj 'ProjectSettings\ProjectVersion.txt'
  $ver = if (Test-Path $pv) { [regex]::Match((Get-Content $pv -Raw), 'm_EditorVersion:\s*(\S+)').Groups[1].Value } else { '' }
  foreach ($c in @("$env:ProgramFiles\Unity\Hub\Editor\$ver\Editor\Unity.exe", "C:\Program Files\Unity\Hub\Editor\$ver\Editor\Unity.exe")) {
    if ($ver -and (Test-Path $c)) { $Unity = $c; break }
  }
  if (-not $Unity) { throw "không tìm thấy Unity $ver — truyền -Unity <Unity.exe> hoặc đặt HOTPATCHKIT_UNITY" }
}
$logs = Join-Path $proj 'Logs'
New-Item -ItemType Directory -Force $logs | Out-Null

function RunUnity([string]$t, [string]$method, [string[]]$extra) {
  $log = Join-Path $logs "hotpatch-$method-$t.log"
  $argv = @('-batchmode', '-nographics', '-projectPath', $proj, '-buildTarget', $unityTarget[$t],
    '-executeMethod', "HotPatchKit.Editor.HotPatchCli.$method", '-logFile', $log) + $extra
  Write-Host "=== $method $t … log: $log"
  & $Unity @argv | Out-Null
  $code = $LASTEXITCODE
  if (Test-Path $log) { Select-String -Path $log -Pattern '\[hotpatch\]|error CS' -Encoding utf8 | ForEach-Object { Write-Host $_.Line } }
  return $code
}

if ($Install) {
  $t = if ($Target -eq 'all') { 'win64' } else { $Target }
  exit (RunUnity $t 'Install' @())
}

if ($Build) {
  if ($Target -eq 'all') { throw '-Build: chọn một nền tảng (-Target win64|android|ios) và -Out cho nó' }
  if (-not $Out) { throw '-Build cần -Out <đường dẫn bản dựng>' }
  $extra = @('-hpOut', [IO.Path]::GetFullPath($Out))
  if ($Dev) { $extra += '-hpDev' }
  exit (RunUnity $Target 'Build' $extra)
}

# -Publish
$results = [ordered]@{}
foreach ($t in $platforms) {
  $have = Archived $t
  if ($have.Count -eq 0) { $results[$t] = 'bỏ qua (chưa có bản cài nào trong kho)'; continue }
  $mine = @($codes | Where-Object { $have -contains $_ })
  if ($codes.Count -gt 0 -and $mine.Count -eq 0) { $results[$t] = 'bỏ qua (không có mã -Base nào thuộc nền tảng này)'; continue }
  $extra = @('-hpCdn', (CdnDir))
  if ($mine.Count -gt 0) { $extra += @('-hpBase', ($mine -join ',')) }
  if ($Bases -gt 0) { $extra += @('-hpBases', "$Bases") }
  $code = RunUnity $t 'Publish' $extra
  $results[$t] = if ($code -eq 0) { 'xong' } else { "LỖI (mã $code) — xem dòng [hotpatch] ở trên" }
}
if ($codes.Count -gt 0) {
  $known = @($platforms | ForEach-Object { Archived $_ })
  $missing = @($codes | Where-Object { $known -notcontains $_ })
  if ($missing.Count -gt 0) { Write-Warning ("không có trong kho bản gốc: " + ($missing -join ', ')) }
}
''
'Kết quả:'
foreach ($k in $results.Keys) { '  {0,-8} {1}' -f $k, $results[$k] }
"Bản vá ở $(CdnDir) — đẩy lên CDN (giữ cấu trúc <nền tảng>\<mã bản cài>\)."
if (@($results.Values | Where-Object { $_ -like 'LỖI*' }).Count -gt 0) { exit 1 }
