param(
    [Parameter(Mandatory = $true)]
    [string]$Message
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$credPath = Join-Path $root "Config\DeployCredentials.local.json"

if (-not (Test-Path $credPath)) {
    Write-Error "Missing $credPath"
}

$cred = Get-Content $credPath -Raw | ConvertFrom-Json
$token = $cred.github.token
$remoteUrl = $cred.github.remoteUrl

if ([string]::IsNullOrWhiteSpace($token)) {
    Write-Error "github.token is empty"
}

Push-Location $root
try {
    if (-not (Test-Path ".git")) {
        git init
        git branch -M main
    }

    $ownerRepo = $remoteUrl -replace '^https://github.com/','' -replace '\.git$',''
    if ($ownerRepo -match 'REPLACE_WITH_OWNER') {
        Write-Host "Creating GitHub repo LiveAssistant..."
        $create = gh repo create LiveAssistant --private --source=. --remote=origin 2>&1
        Write-Host $create
        $remoteUrl = git remote get-url origin 2>$null
        if (-not $remoteUrl) {
            $remoteUrl = "https://github.com/$(gh api user -q .login)/LiveAssistant.git"
            git remote add origin $remoteUrl
        }
        $cred.github.remoteUrl = $remoteUrl
        $cred | ConvertTo-Json | Set-Content $credPath -Encoding UTF8
        $ownerRepo = $remoteUrl -replace '^https://github.com/','' -replace '\.git$',''
    }

    # Fine-grained PAT (github_pat_*) 与 classic PAT 均可用 x-access-token
    $authUrl = "https://x-access-token:$token@github.com/$ownerRepo.git"
    git remote remove origin 2>$null
    git remote add origin $authUrl

    git add -A
    git status
    git -c user.email="liveassistant@local" -c user.name="LiveAssistant" commit -m $Message
    # 若工作区无变更，commit 可能失败；仍继续 push 已有提交
    if ($LASTEXITCODE -ne 0) {
        Write-Host "commit skipped or failed; continuing push of existing commits"
    }
    git push -u origin main
    if ($LASTEXITCODE -ne 0) {
        throw "git push failed (403 多为 PAT 未授予该仓库 Contents: Read and write)"
    }

    git remote set-url origin "https://github.com/$ownerRepo.git"
    Write-Host "Pushed to https://github.com/$ownerRepo"
}
finally {
    Pop-Location
}
