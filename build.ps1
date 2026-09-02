param(
    [Parameter(Position = 0)]
    [ValidateSet('check', 'test', 'benchmark', 'ci', 'package', 'release')]
    [string]$Command = 'check',

    [string]$Version = '1.0.4'
)

chcp 65001 > $null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::InputEncoding  = [System.Text.Encoding]::UTF8
$OutputEncoding           = [System.Text.Encoding]::UTF8
$PSDefaultParameterValues['*:Encoding'] = 'UTF8'

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$script:RepoRoot = $PSScriptRoot
$script:ArtifactsRoot = Join-Path $script:RepoRoot 'artifacts'
$script:PublishRoot = Join-Path $script:ArtifactsRoot 'publish'
$script:PackageRoot = Join-Path $script:ArtifactsRoot 'package'

function Resolve-DotNet {
    $localDotNet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'
    if (Test-Path -LiteralPath $localDotNet) {
        return $localDotNet
    }

    $commandInfo = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $commandInfo) {
        throw '.NET SDK 10.0.400 未安装。'
    }

    return $commandInfo.Source
}

function Resolve-InnoCompiler {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw 'Inno Setup 7.1.0 x64 未安装。'
}

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    & $script:DotNet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet 命令失败，退出码 $LASTEXITCODE。"
    }
}

function Invoke-Check {
    Write-Host '检查工具链与锁定依赖…'
    $sdkVersion = (& $script:DotNet --version).Trim()
    if ($sdkVersion -ne '10.0.400') {
        throw "需要 .NET SDK 10.0.400，实际为 $sdkVersion。"
    }

    Invoke-DotNet restore (Join-Path $script:RepoRoot 'InputAtlas.sln') '--locked-mode'
    Invoke-DotNet format (Join-Path $script:RepoRoot 'InputAtlas.sln') '--verify-no-changes' '--no-restore'
    Invoke-DotNet build (Join-Path $script:RepoRoot 'InputAtlas.sln') '-c' 'Release' '--no-restore' ('-p:Version=' + $Version)
    Write-Host '检查完成。'
}

function Invoke-Tests {
    Write-Host '运行自动化测试…'
    Invoke-DotNet test (Join-Path $script:RepoRoot 'InputAtlas.sln') '-c' 'Release' '--no-restore' '--logger' 'console;verbosity=minimal'
    Write-Host '测试完成。'
}

function Invoke-Benchmarks {
    Write-Host '运行输入热路径基准…'
    # BenchmarkDotNet 会在生成的子进程中按名称查找 dotnet；确保本地锁定 SDK
    # （包括 dotnet-install.ps1 安装的位置）对这些子进程可见。
    $dotnetDirectory = Split-Path -Parent $script:DotNet
    $env:DOTNET_ROOT = $dotnetDirectory
    if (-not (($env:PATH -split ';') -contains $dotnetDirectory)) {
        $env:PATH = "$dotnetDirectory;$env:PATH"
    }
    Invoke-DotNet run '--project' (Join-Path $script:RepoRoot 'benchmarks\InputAtlas.Benchmarks\InputAtlas.Benchmarks.csproj') '-c' 'Release' '--' '--filter' '*'
}

function Invoke-Publish {
    Write-Host '[发布] 准备 win-x64 自包含发布目录…'
    if (Test-Path -LiteralPath $script:PublishRoot) {
        Remove-Item -LiteralPath $script:PublishRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $script:PublishRoot -Force | Out-Null

    Write-Host '[发布] 编译 Release 自包含应用；目标机器无需预装 .NET 10。'
    Invoke-DotNet publish (Join-Path $script:RepoRoot 'src\InputAtlas.App\InputAtlas.App.csproj') '-c' 'Release' '--no-restore' '-r' 'win-x64' '--self-contained' 'true' '-o' $script:PublishRoot ('-p:Version=' + $Version) '-p:DebugType=None' '-p:DebugSymbols=false' '-p:PublishSingleFile=false'
    Get-ChildItem -LiteralPath $script:PublishRoot -Filter '*.pdb' -File | Remove-Item -Force

    $requiredFiles = @(
        'InputAtlas.exe',
        'coreclr.dll',
        'hostfxr.dll',
        'hostpolicy.dll',
        'PresentationFramework.dll'
    )
    foreach ($requiredFile in $requiredFiles) {
        $requiredPath = Join-Path $script:PublishRoot $requiredFile
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "自包含发布校验失败：缺少 $requiredFile。"
        }
        Write-Host "[发布] 已验证运行时文件：$requiredFile"
    }

    $size = (Get-ChildItem -LiteralPath $script:PublishRoot -File -Recurse | Measure-Object -Property Length -Sum).Sum
    Write-Host ("[发布] 自包含发布目录大小：{0:N2} MB" -f ($size / 1MB))
    Write-Host '[发布] 自包含运行时校验完成，安装器无需检查或下载 .NET。'
}

function Invoke-Ci {
    Invoke-Check
    Invoke-Tests
    Invoke-Publish
    Write-Host '本地 CI 完成。'
}

function Invoke-Package {
    Invoke-Ci
    $innoCompiler = Resolve-InnoCompiler
    if (Test-Path -LiteralPath $script:PackageRoot) {
        Remove-Item -LiteralPath $script:PackageRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $script:PackageRoot -Force | Out-Null
    & $innoCompiler "/DMyAppVersion=$Version" "/DSourceDir=$script:PublishRoot" "/DOutputDir=$script:PackageRoot" (Join-Path $script:RepoRoot 'installer\InputAtlas.iss')
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup 编译失败，退出码 $LASTEXITCODE。"
    }
    Write-ReleaseMetadata
    Write-Host "Release 安装包已生成：$script:PackageRoot"
}

function Write-ReleaseMetadata {
    $files = Get-ChildItem -LiteralPath $script:PackageRoot -File
    foreach ($file in $files) {
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        Set-Content -LiteralPath ($file.FullName + '.sha256') -Value "$hash  $($file.Name)" -Encoding UTF8
    }

    $commit = (& git -C $script:RepoRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { $commit = 'uncommitted' }
    $manifest = [ordered]@{
        product = 'InputAtlas'
        version = $Version
        commit = $commit
        built_utc = [DateTimeOffset]::UtcNow.ToString('O')
        dotnet_sdk = (& $script:DotNet --version).Trim()
        powershell = $PSVersionTable.PSVersion.ToString()
        windows = [Environment]::OSVersion.VersionString
        inno_setup = '7.1.0'
        deployment = 'self-contained win-x64'
        requires_dotnet_runtime = $false
        installer_network_downloads = $false
        files = @($files | ForEach-Object {
            [ordered]@{
                name = $_.Name
                size = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        })
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $script:PackageRoot 'build-manifest.json') -Encoding UTF8
}

function Invoke-Release {
    if ($Version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') {
        throw 'release 版本必须是有效的 SemVer。'
    }

    $status = (& git -C $script:RepoRoot status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw 'release 无法读取 Git 工作树状态。'
    }
    if ($status) {
        throw 'release 要求工作树干净；请先提交源码、文档和锁文件。'
    }
    $head = (& git -C $script:RepoRoot rev-parse --verify HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head)) {
        throw 'release 要求存在 Git 提交。'
    }

    Invoke-Ci
    Invoke-Benchmarks
    $innoCompiler = Resolve-InnoCompiler
    if (Test-Path -LiteralPath $script:PackageRoot) {
        Remove-Item -LiteralPath $script:PackageRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $script:PackageRoot -Force | Out-Null
    & $innoCompiler "/DMyAppVersion=$Version" "/DSourceDir=$script:PublishRoot" "/DOutputDir=$script:PackageRoot" (Join-Path $script:RepoRoot 'installer\InputAtlas.iss')
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup 编译失败，退出码 $LASTEXITCODE。"
    }
    Write-ReleaseMetadata
    Write-Host "发布制品已生成：$script:PackageRoot"
}

$script:DotNet = Resolve-DotNet
switch ($Command) {
    'check' { Invoke-Check }
    'test' { Invoke-Tests }
    'benchmark' { Invoke-Benchmarks }
    'ci' { Invoke-Ci }
    'package' { Invoke-Package }
    'release' { Invoke-Release }
}
