#Requires -Version 5.1
<#
.SYNOPSIS
  编译 AirPlay 并输出到 publish/<配置>，可选中打包成本地 zip 分发包。

.DESCRIPTION
  无参数运行（双击或人工执行）时显示菜单，执行完一项任务后回到主菜单重选，菜单选 0 才退出；
  带参数运行时不显示菜单、结束后直接返回，便于脚本或 agent 调用。

.EXAMPLE
  .\build-release-debug.ps1
.EXAMPLE
  .\build-release-debug.ps1 -Configuration Release
.EXAMPLE
  .\build-release-debug.ps1 -Configuration Release -SelfContained -Package -Version v1.0.1
.EXAMPLE
  .\build-release-debug.ps1 -CiTrigger   # 触发 GitHub Actions 在远端打包
.EXAMPLE
  .\build-release-debug.ps1 -Release     # 按 csproj 版本打 v 标签并推送，触发 GitHub Release
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    [switch] $SelfContained,

    [switch] $Package,

    [switch] $CiArtifact,

    [switch] $BuildOnly,

    [switch] $CiTrigger,

    [switch] $Release,

    [switch] $Watch,

    [string] $Version = '',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = '',

    [string] $OutputDir = '',

    [string] $FFmpegDir = '',

    [string] $FdkAacDll = '',

    [switch] $NoPause
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 任何未处理异常都先打印原因并暂停，避免双击运行时窗口一闪而过、看不到失败原因
trap {
    Write-Host ''
    Write-Host "执行失败: $($_.Exception.Message)" -ForegroundColor Red
    Wait-Interactive
    exit 1
}

$repoRoot = $PSScriptRoot
$project = Join-Path $repoRoot 'AirPlay\AirPlay.csproj'

# 分发包必须剔除：调试符号、非 Windows 配置、附带的多语言资源、Linux 原生库、日志
$excludeFiles = @('*.pdb', 'appsettings_linux.json', 'appsettings_osx.json', '*.log')
$excludeDirs = @('cs', 'de', 'es', 'fr', 'it', 'ja', 'ko', 'pl', 'pt-BR', 'ru', 'tr', 'zh-Hans', 'zh-Hant', 'runtimes', 'Logs')

# 无参数调用（双击/人工运行）弹菜单、任务完成后回到主菜单；带参数调用执行完直接结束
$menuMode = ($PSBoundParameters.Count -eq 0) -and (-not $NoPause)
$interactive = $menuMode
# 仅菜单 3 会在打包前询问是否递增版本号；提交推送（可选递增版本号）是独立菜单 4，其余路径默认不递增
$BumpVersion = $false

function Wait-Interactive {
    if ($interactive) {
        Write-Host ''
        $prompt = if ($menuMode) { '按回车键返回主菜单' } else { '按回车键退出' }
        Read-Host $prompt | Out-Null
    }
}

function Read-VersionBump {
    # 询问是否递增版本号；直接回车视为 y
    $current = (Resolve-Version) -replace '^v', ''
    $answer = Read-Host "是否递增版本号（当前 $current，直接回车默认 y）? (y/n)"
    return ($answer -notmatch '^(n|no)$')
}

function Invoke-VersionBump {
    # 递增 csproj <Version> 最后一位（如 1.0.0.4 -> 1.0.0.5），重编译后新版本打进 exe 文件版本
    $csproj = Join-Path $repoRoot 'AirPlay\AirPlay.csproj'
    $content = [IO.File]::ReadAllText($csproj)
    $m = [regex]::Match($content, '<Version>([^<]+)</Version>')
    if (-not $m.Success) { throw 'AirPlay\AirPlay.csproj 中找不到 <Version>，无法递增版本号' }
    $oldVersion = $m.Groups[1].Value.Trim()
    $parts = $oldVersion -split '\.'
    if ($parts.Count -eq 0 -or -not ($parts[-1] -match '^\d+$')) {
        throw "版本号最后一位不是数字，无法自动递增: $oldVersion"
    }
    $parts[-1] = [string]([int]$parts[-1] + 1)
    $newVersion = $parts -join '.'
    # 只替换 Version 标签本身，其余内容（含原换行符）原样保留
    $newContent = $content.Replace($m.Value, "<Version>$newVersion</Version>")
    [IO.File]::WriteAllText($csproj, $newContent, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "版本号已递增: $oldVersion -> $newVersion" -ForegroundColor Green
    return $newVersion
}

function Invoke-CommitPush {
    # 菜单 4：询问是否递增 csproj <Version> 末位，然后提交推送，供菜单 5/6 的远端构建/Release 使用
    if (Read-VersionBump) {
        $newVersion = Invoke-VersionBump
        # 远端构建/Release 编的是推送后的代码，版本号必须先提交推送，否则 exe 文件版本还是旧的
        Write-Host '==> 提交并推送版本号变更' -ForegroundColor Cyan
        & git add -- 'AirPlay\AirPlay.csproj'
        if ($LASTEXITCODE -ne 0) { throw 'git add AirPlay\AirPlay.csproj 失败' }
        & git commit -m "chore: 版本号提升到 $newVersion"
        if ($LASTEXITCODE -ne 0) { throw 'git commit 失败' }
    }
    else {
        # 不递增版本号也一样提交推送，只是提交当前工作区的其他改动；commit message 由用户输入
        $dirty = & git status --porcelain
        if ([string]::IsNullOrWhiteSpace($dirty)) {
            Write-Host '工作区没有未提交的改动，仅执行推送'
        }
        else {
            Write-Host '==> 提交并推送当前改动' -ForegroundColor Cyan
            $commitMsg = Read-Host '请输入 commit message（直接回车则跳过提交，仅推送）'
            if ([string]::IsNullOrWhiteSpace($commitMsg)) {
                Write-Host '已跳过提交，仅推送'
            }
            else {
                & git add -A
                if ($LASTEXITCODE -ne 0) { throw 'git add 失败' }
                & git commit -m $commitMsg
                if ($LASTEXITCODE -ne 0) { throw 'git commit 失败' }
            }
        }
    }
    & git push
    if ($LASTEXITCODE -ne 0) { throw 'git push 失败：请先处理远端冲突（如 git pull --rebase）后重试' }
}

function Select-Task {
    # 重定向/无控制台环境（CI、agent）下 Clear-Host 会报句柄无效，忽略即可
    try { Clear-Host } catch { }
    Write-Host 'AirPlay 编译 / 分发' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '1. Debug 编译（仅编译，输出到 bin\Debug\net10.0）'
    Write-Host '2. Release 编译（仅编译，输出到 bin\Release\net10.0）'
    Write-Host '3. Release 发布 + 本地 zip 分发包（自包含 win-x64）'
    Write-Host '4. 提交推送(版本号处理)'
    Write-Host '5. 触发 GitHub Actions 构建（在 GitHub 上打包，不在本地打包）'
    Write-Host '6. 发布 Release（按 csproj 版本打 v 标签并推送，触发 GitHub Release）'
    Write-Host '0. 退出'
    Write-Host ''
    $choice = Read-Host '请选择'
    switch ($choice) {
        '1' { return @{ Configuration = 'Debug'; SelfContained = $false; Package = $false; CiArtifact = $false; BuildOnly = $true; CiTrigger = $false; Release = $false; Watch = $false; BumpVersion = $false } }
        '2' { return @{ Configuration = 'Release'; SelfContained = $false; Package = $false; CiArtifact = $false; BuildOnly = $true; CiTrigger = $false; Release = $false; Watch = $false; BumpVersion = $false } }
        '3' { return @{ Configuration = 'Release'; SelfContained = $true; Package = $true; CiArtifact = $false; BuildOnly = $false; CiTrigger = $false; Release = $false; Watch = $false; BumpVersion = (Read-VersionBump) } }
        '4' { return @{ CommitPush = $true } }
        '5' { return @{ Configuration = 'Release'; SelfContained = $true; Package = $false; CiArtifact = $false; BuildOnly = $false; CiTrigger = $true; Release = $false; Watch = $true; BumpVersion = $false } }
        '6' { return @{ Configuration = 'Release'; SelfContained = $true; Package = $false; CiArtifact = $false; BuildOnly = $false; CiTrigger = $false; Release = $true; Watch = $true; BumpVersion = $false } }
        '0' { exit 0 }
        default {
            Write-Host '无效输入' -ForegroundColor Red
            Start-Sleep -Seconds 1
            return (Select-Task)
        }
    }
}

function Resolve-FfmpegDir {
    if (-not [string]::IsNullOrWhiteSpace($FFmpegDir)) {
        $dir = (Resolve-Path -LiteralPath $FFmpegDir).Path
    }
    else {
        $cmd = Get-Command ffplay.exe -ErrorAction SilentlyContinue
        if ($null -eq $cmd) {
            throw '未找到 ffplay.exe：请把 FFmpeg 加入 PATH，或用 -FFmpegDir 指定其 bin 目录'
        }
        $dir = Split-Path -Parent $cmd.Source
    }
    if (-not (Test-Path -LiteralPath (Join-Path $dir 'ffplay.exe'))) {
        throw "FFmpeg 目录缺少 ffplay.exe: $dir"
    }
    return $dir
}

function Resolve-FdkAacDll {
    if (-not [string]::IsNullOrWhiteSpace($FdkAacDll)) {
        if (-not (Test-Path -LiteralPath $FdkAacDll)) { throw "找不到 libfdk-aac-2.dll: $FdkAacDll" }
        return (Resolve-Path -LiteralPath $FdkAacDll).Path
    }
    $hit = Get-ChildItem -LiteralPath $repoRoot -Filter 'libfdk-aac-2.dll' -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notlike '*\publish\*' } | Select-Object -First 1
    if ($null -eq $hit) {
        throw '找不到 libfdk-aac-2.dll：请用 -FdkAacDll 指定，或先编译 fdk-aac 放到 AirPlay\bin 下'
    }
    return $hit.FullName
}

function Resolve-Version {
    if (-not [string]::IsNullOrWhiteSpace($Version)) { return $Version }
    # 以 csproj 里的 <Version> 为准，保证分发包名和 exe 的文件版本一致
    $csproj = Join-Path $repoRoot 'AirPlay\AirPlay.csproj'
    if (Test-Path -LiteralPath $csproj) {
        $m = Select-String -LiteralPath $csproj -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
        if ($m) { return 'v' + $m.Matches[0].Groups[1].Value.Trim() }
    }
    # 版本号只认源码，读不到就报错，避免打包出与 exe 版本不一致的包
    throw '无法从 AirPlay\AirPlay.csproj 读取 <Version>，请用 -Version 显式指定'
}

function Get-OriginSlug {
    # 本仓库 origin 指向 fork，upstream 指向上游；gh 默认可能选到 upstream，
    # 所以所有 gh 命令都显式 --repo 指定 origin 对应的仓库
    $url = (& git remote get-url origin).Trim()
    if ($url -match 'github\.com[:/](?<slug>[^/]+/[^/]+?)(\.git)?$') {
        return $Matches['slug']
    }
    throw "无法从 origin 地址解析 GitHub 仓库: $url"
}

function Assert-GhAvailable {
    if ($null -eq (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw '未找到 gh 命令：请先安装 GitHub CLI (https://cli.github.com/) 并执行 gh auth login'
    }
    & gh auth status *> $null
    if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI 未登录，请先执行: gh auth login' }
}

function Get-LatestRunId {
    param([string] $Repo)
    $raw = & gh run list --repo $Repo --workflow build.yml --limit 1 --json databaseId 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($raw)) {
        $latest = ($raw | ConvertFrom-Json) | Select-Object -First 1
        if ($null -ne $latest) { return [long]$latest.databaseId }
    }
    return 0
}

function Wait-NewRun {
    # 上一次构建可能还在跑，靠 run id 变大认出新触发的运行
    param([string] $Repo, [long] $BeforeId)
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 2
        $raw = & gh run list --repo $Repo --workflow build.yml --limit 5 --json databaseId,url 2>$null
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($raw)) { continue }
        $newest = @($raw | ConvertFrom-Json) | Sort-Object databaseId -Descending | Select-Object -First 1
        if ($null -ne $newest -and [long]$newest.databaseId -gt $BeforeId) { return $newest }
    }
    return $null
}

function Show-RunResult {
    param([string] $Repo, $Run, [string] $VersionTag, [switch] $Track)
    Write-Host ''
    Write-Host "构建已提交: $($Run.url)" -ForegroundColor Green
    if (-not $Track) {
        Write-Host "  查看进度: gh run watch $($Run.databaseId) --repo $Repo"
        Write-Host "  完成后下载: gh run download $($Run.databaseId) --repo $Repo -n AirPlay-win-x64-$VersionTag"
        return
    }
    Write-Host '==> 跟踪构建中（Ctrl+C 只中断跟踪，不影响 GitHub 上的构建）' -ForegroundColor Cyan
    & gh run watch $Run.databaseId --repo $Repo --interval 10
    $view = (& gh run view $Run.databaseId --repo $Repo --json status,conclusion 2>$null) | ConvertFrom-Json
    if ($view.conclusion -ne 'success') {
        throw "构建未成功（$($view.conclusion)），失败日志: gh run view $($Run.databaseId) --repo $Repo --log-failed"
    }
    Write-Host ''
    Write-Host '构建成功' -ForegroundColor Green
}

function Invoke-GitHubCi {
    param([switch] $Watch)
    Assert-GhAvailable

    $repo = Get-OriginSlug
    $branch = (& git rev-parse --abbrev-ref HEAD).Trim()
    # 工作流只能构建远端已有分支上的代码
    if ([string]::IsNullOrWhiteSpace((& git ls-remote --heads origin $branch 2>$null))) {
        throw "分支 $branch 尚未推送到 origin，请先执行: git push -u origin $branch"
    }
    if (-not [string]::IsNullOrWhiteSpace((& git status --porcelain))) {
        Write-Host '  注意：工作区有未提交的改动，GitHub 上构建的是远端已有代码' -ForegroundColor Yellow
    }

    $beforeId = Get-LatestRunId -Repo $repo
    $versionTag = Resolve-Version
    Write-Host "==> 触发 GitHub Actions: $repo build.yml @ $branch (version=$versionTag)" -ForegroundColor Cyan
    & gh workflow run build.yml --repo $repo --ref $branch -f "version=$versionTag"
    if ($LASTEXITCODE -ne 0) { throw "触发工作流失败，退出码 $LASTEXITCODE（若提示权限不足，执行 gh auth refresh -s workflow 后重试）" }

    $run = Wait-NewRun -Repo $repo -BeforeId $beforeId
    if ($null -eq $run) {
        Write-Host '  已触发，但暂时查不到运行记录，请到仓库 Actions 页面查看' -ForegroundColor Yellow
        return
    }
    Show-RunResult -Repo $repo -Run $run -VersionTag $versionTag -Track:$Watch
}

function Invoke-ReleaseTag {
    # 打 v<版本> 标签（已存在则跳过）并触发工作流创建 GitHub Release
    param([switch] $Watch, [switch] $Confirm)
    Assert-GhAvailable

    $repo = Get-OriginSlug
    $tag = Resolve-Version
    if (-not $tag.StartsWith('v')) { $tag = "v$tag" }
    $branch = (& git rev-parse --abbrev-ref HEAD).Trim()
    if ([string]::IsNullOrWhiteSpace((& git ls-remote --heads origin $branch 2>$null))) {
        throw "分支 $branch 尚未推送到 origin，请先执行: git push -u origin $branch"
    }

    # 已有 Release 就不再重复发布
    & gh release view $tag --repo $repo *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "Release $tag 已存在: https://github.com/$repo/releases/tag/$tag（如需重发请提升 AirPlay.csproj 版本号）"
    }

    if (-not [string]::IsNullOrWhiteSpace((& git status --porcelain))) {
        Write-Host '  注意：工作区有未提交的改动，Release 只包含最后一次提交的内容' -ForegroundColor Yellow
    }

    $localTag = -not [string]::IsNullOrWhiteSpace((& git rev-parse -q --verify "refs/tags/$tag" 2>$null))
    $remoteTag = -not [string]::IsNullOrWhiteSpace((& git ls-remote --tags origin $tag 2>$null))
    $head = (& git rev-parse --short HEAD).Trim()

    Write-Host "==> 发布 $tag 到 $repo（分支 $branch，提交 $head）" -ForegroundColor Cyan
    if (-not $localTag) { Write-Host "    将新建标签 $tag" }
    elseif (-not $remoteTag) { Write-Host "    标签 $tag 已存在（本地），将推送到 origin" }
    else { Write-Host "    标签 $tag 已存在于 origin，直接复用" }

    if ($Confirm) {
        $answer = Read-Host '将创建并推送标签并触发 GitHub Release，继续? (Y/n，直接回车继续)'
        if ($answer -match '^(n|no)$') {
            Write-Host '已取消'
            return
        }
    }

    $beforeId = Get-LatestRunId -Repo $repo

    if (-not $localTag) {
        & git tag $tag
        if ($LASTEXITCODE -ne 0) { throw "创建标签失败: $tag" }
    }
    if (-not $remoteTag) {
        & git push origin $tag
        if ($LASTEXITCODE -ne 0) {
            # 推送失败时回收刚建的本地标签，避免下次被「标签已存在」挡住
            if (-not $localTag) { & git tag -d $tag *> $null }
            throw "推送标签失败: $tag"
        }
        Write-Host "标签已推送: $tag" -ForegroundColor Green
    }

    # 远端仓库默认分支不是当前分支，tag push 事件不会触发工作流，改用 workflow_dispatch 显式发布
    & gh workflow run build.yml --repo $repo --ref $branch -f "version=$tag" -f 'release=true'
    if ($LASTEXITCODE -ne 0) { throw "触发发布工作流失败，退出码 $LASTEXITCODE（若提示权限不足，执行 gh auth refresh -s workflow 后重试）" }
    Write-Host "Release 页面: https://github.com/$repo/releases/tag/$tag" -ForegroundColor Green

    $run = Wait-NewRun -Repo $repo -BeforeId $beforeId
    if ($null -eq $run) {
        Write-Host '  已触发，但暂时查不到运行记录，请到 Actions 页面查看' -ForegroundColor Yellow
        return
    }
    Show-RunResult -Repo $repo -Run $run -VersionTag $tag -Track:$Watch
}

function New-DistributionPackage {
    param(
        [string] $SourceDir,
        [string] $StageDir,
        [string] $ZipPath,
        [string] $VersionTag,
        [switch] $Flat
    )

    Write-Host ""
    Write-Host "==> 准备分发目录: $StageDir" -ForegroundColor Cyan
    if (Test-Path -LiteralPath $StageDir) {
        Remove-Item -LiteralPath $StageDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $StageDir -Force | Out-Null

    $robocopyArgs = @($SourceDir, $StageDir, '/E', '/NFL', '/NDL', '/NJH', '/NJS')
    foreach ($pattern in $excludeFiles) { $robocopyArgs += @('/XF', $pattern) }
    foreach ($name in $excludeDirs) { $robocopyArgs += @('/XD', $name) }
    # robocopy 用 0~7 表示成功；用 Start-Process 取退出码，避免污染脚本外的 $LASTEXITCODE
    $copy = Start-Process -FilePath 'robocopy' -ArgumentList $robocopyArgs -NoNewWindow -Wait -PassThru
    if ($copy.ExitCode -ge 8) { throw "复制发布产物失败，robocopy 退出码 $($copy.ExitCode)" }

    Write-Host "==> 拷入 FFmpeg / libfdk-aac" -ForegroundColor Cyan
    $ffDir = Resolve-FfmpegDir
    $ffFiles = @('ffplay.exe', 'ffmpeg.exe')
    $ffFiles += Get-ChildItem -LiteralPath $ffDir -Filter '*.dll' -File |
        Where-Object { $_.Name -match '^(av|sw|postproc)' } | ForEach-Object { $_.Name }
    foreach ($name in $ffFiles) {
        $src = Join-Path $ffDir $name
        if (Test-Path -LiteralPath $src) {
            Copy-Item -LiteralPath $src -Destination $StageDir -Force
        }
    }
    Copy-Item -LiteralPath (Resolve-FdkAacDll) -Destination (Join-Path $StageDir 'libfdk-aac-2.dll') -Force
    foreach ($doc in @('README.md', 'LICENSE')) {
        $docPath = Join-Path $repoRoot $doc
        if (Test-Path -LiteralPath $docPath) {
            Copy-Item -LiteralPath $docPath -Destination $StageDir -Force
        }
    }
    # 与 CI 对齐：包内写入版本号，便于核对下载到的制品
    [IO.File]::WriteAllText((Join-Path $StageDir 'version.txt'), $VersionTag, (New-Object System.Text.UTF8Encoding($false)))

    # 缺任意一项都会导致开箱即用失败，压缩前先自检
    foreach ($name in @('AirPlay.exe', 'AirPlay.runtimeconfig.json', 'appsettings_win.json', 'libfdk-aac-2.dll', 'ffplay.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $StageDir $name))) {
            throw "分发包缺少 $name，已中止"
        }
    }

    $zipDir = Split-Path -Parent $ZipPath
    if (-not (Test-Path -LiteralPath $zipDir)) {
        New-Item -ItemType Directory -Path $zipDir -Force | Out-Null
    }
    Write-Host "==> 压缩: $ZipPath" -ForegroundColor Cyan
    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }
    # GitHub artifact 的 zip 内没有顶层文件夹，-Flat 时把内容直接平铺到 zip 根
    if ($Flat) {
        Compress-Archive -Path (Join-Path $StageDir '*') -DestinationPath $ZipPath -CompressionLevel Optimal
    }
    else {
        Compress-Archive -Path $StageDir -DestinationPath $ZipPath -CompressionLevel Optimal
    }

    $zipMB = [math]::Round((Get-Item -LiteralPath $ZipPath).Length / 1MB, 1)
    $fileCount = (Get-ChildItem -LiteralPath $StageDir -Recurse -File).Count
    Write-Host ""
    Write-Host "分发包: $ZipPath ($zipMB MB, $fileCount 个文件)" -ForegroundColor Green

    # 打包成功后清理发布输出与 staging，只保留 zip，避免留下两套几百 MB 的中间目录
    foreach ($dir in @($SourceDir, $StageDir)) {
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        try {
            Remove-Item -LiteralPath $dir -Recurse -Force
            Write-Host "  已清理: $dir"
        }
        catch {
            Write-Host "  清理失败（可能被程序占用）: $dir" -ForegroundColor Yellow
        }
    }
}

$runTask = {
# 菜单模式下每次循环重选任务；带参数模式只进一次
if ([string]::IsNullOrWhiteSpace($Configuration)) {
    # 只触发 CI 或只发 Release 时都不需要选本地配置，也就不能弹菜单
    if ($CiTrigger -or $Release) {
        $Configuration = 'Release'
    }
    # 无任何参数运行视为人工双击，弹菜单；其余情况（agent/CI）直接报错给出用法
    elseif ($PSBoundParameters.Count -eq 0 -and -not $NoPause) {
        $task = Select-Task
        # 菜单 4：提交推送（可选递增版本号），完成后直接结束，不再走编译流程
        # 菜单 1/2/3/5/6 返回的哈希表没有 CommitPush 键，StrictMode 下要用 Contains 判断，不能直接点属性
        if ($task.Contains('CommitPush') -and $task.CommitPush) {
            Invoke-CommitPush
            Wait-Interactive
            return
        }
        $Configuration = $task.Configuration
        $SelfContained = $task.SelfContained
        $Package = $task.Package
        $CiArtifact = $task.CiArtifact
        $BuildOnly = $task.BuildOnly
        $CiTrigger = $task.CiTrigger
        $Release = $task.Release
        $Watch = $task.Watch
        $BumpVersion = $task.BumpVersion
    }
    else {
        throw '非交互环境：请用 -Configuration Debug|Release 指定配置，打包分发包再加 -Package -SelfContained'
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    # CI 制品沿用工作流的输出目录 publish/win-x64，便于与 Actions 产物对照
    if ($CiArtifact) {
        $OutputDir = Join-Path $repoRoot 'publish\win-x64'
    }
    else {
        $OutputDir = Join-Path $repoRoot "publish\$Configuration"
    }
}

if (-not (Test-Path -LiteralPath $project)) {
    throw "找不到项目: $project"
}

# 菜单 3 选择递增时先改版本号，重编译后新版本会打进 exe 文件版本与制品名；提交推送见菜单 4
if ($BumpVersion) {
    Invoke-VersionBump | Out-Null
}

if ($Release) {
    Invoke-ReleaseTag -Watch:$Watch -Confirm:($PSBoundParameters.Count -eq 0)
    Wait-Interactive
    return
}

if ($CiTrigger) {
    Invoke-GitHubCi -Watch:$Watch
    Wait-Interactive
    return
}

if ($BuildOnly) {
    Write-Host "==> Build ($Configuration)" -ForegroundColor Cyan
    & dotnet build $project -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败，退出码 $LASTEXITCODE" }

    # 仅编译：产物留在 bin\<配置>\net10.0，不写 publish
    $binExe = Join-Path $repoRoot "AirPlay\bin\$Configuration\net10.0\AirPlay.exe"
    Write-Host ""
    Write-Host "完成（仅编译，未发布）: $binExe" -ForegroundColor Green
    if (Test-Path -LiteralPath $binExe) {
        $binInfo = Get-Item -LiteralPath $binExe
        Write-Host ("  体积: {0:N0} 字节    时间: {1}" -f $binInfo.Length, $binInfo.LastWriteTime)
    }
    Wait-Interactive
    return
}

# 每次发布前清空输出目录，避免上次自包含/框架依赖发布的残留混进这次的分发包
if (Test-Path -LiteralPath $OutputDir) {
    Write-Host "==> 清理旧输出目录: $OutputDir" -ForegroundColor Cyan
    try {
        Remove-Item -LiteralPath $OutputDir -Recurse -Force
    }
    catch {
        throw "无法清空输出目录（可能 AirPlay.exe 正在运行）: $OutputDir"
    }
}

Write-Host "==> Restore" -ForegroundColor Cyan
# 自包含发布必须按 RID 还原，否则 publish --no-restore 找不到 net10.0/<rid> 资产
$restoreArgs = @('restore', $project)
if ($SelfContained) { $restoreArgs += @('-r', $Runtime) }
& dotnet @restoreArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败，退出码 $LASTEXITCODE" }

$publishArgs = @(
    'publish', $project
    '-c', $Configuration
    '-o', $OutputDir
    '--no-restore'
)

if ($SelfContained) {
    $publishArgs += @('--self-contained', 'true', '-r', $Runtime)
}
else {
    $publishArgs += @('--self-contained', 'false')
}

Write-Host "==> Publish ($Configuration)" -ForegroundColor Cyan
Write-Host ("dotnet " + ($publishArgs -join ' '))
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

$exe = Join-Path $OutputDir 'AirPlay.exe'
Write-Host ""
Write-Host "完成: $exe" -ForegroundColor Green
if (Test-Path -LiteralPath $exe) {
    # 用 Write-Host 输出，避免格式化对象污染调用方的管道
    $info = Get-Item -LiteralPath $exe
    Write-Host ("  体积: {0:N0} 字节    时间: {1}" -f $info.Length, $info.LastWriteTime)
}

if ($Package) {
    $versionTag = Resolve-Version
    if ($CiArtifact) {
        # -CiArtifact 参数路径：模拟 GitHub Actions 的 win-x64 制品，zip 根目录平铺，可直接上传 Release
        New-DistributionPackage -SourceDir $OutputDir `
            -StageDir (Join-Path $repoRoot 'publish\ci-win-x64') `
            -ZipPath (Join-Path $repoRoot "publish\ci\AirPlay-ci-$Runtime-$versionTag.zip") `
            -VersionTag $versionTag -Flat
    }
    else {
        # 框架依赖版单独加 -fdd 后缀，避免和自包含版共用同名 staging/zip 后互相覆盖
        $suffix = '-fdd'
        if ($SelfContained) { $suffix = '' }
        New-DistributionPackage -SourceDir $OutputDir `
            -StageDir (Join-Path $repoRoot "publish\AirPlay-$Runtime$suffix") `
            -ZipPath (Join-Path $repoRoot "publish\AirPlay-$Runtime$suffix-$versionTag.zip") `
            -VersionTag $versionTag
    }
}

Wait-Interactive
}  # $runTask 结束

# 人工双击运行：每完成一项任务回到主菜单重选，直到菜单选 0 退出；带参数运行只执行一次
if ($menuMode) {
    while ($true) { & $runTask }
}
else {
    & $runTask
}
