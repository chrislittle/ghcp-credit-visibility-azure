#requires -Version 7.0
<#
.SYNOPSIS
  GHCP AI Credit Visibility — one unified, interactive deploy & operations script.

  Runs the whole journey end-to-end (guided, colorized, with confirmations), or a single task:
    prereqs → preflight → configure (tfvars) → provision (terraform) → build+deploy image
            → grant SQL access → seed GitHub PAT → status/health
            → [optional] SRE agent: provision → grant SQL → sync config.
  The optional SRE agent runs LAST and is isolated — a failure there never blocks the core app.

.DESCRIPTION
  Default (-Task all) walks you through everything, making smart decisions from your choices
  (identity model, networking, WHO is admin, mock vs real data). Individual tasks let a
  different person run just one step later (e.g. a SQL admin running only the grant).

.PARAMETER Task
  all (default) | preflight | configure | provision | image | grant-sql | set-pat | status

.PARAMETER Location   Azure region for preflight + apply. Default eastus2.
.PARAMETER Sku        App Service Plan SKU (e.g. S1, P1v3, P1mv3, P1v4). Default: S1 (or existing tfvars value).
.PARAMETER SqlSku     Azure SQL DB SKU (e.g. GP_S_Gen5_1). Default: GP_S_Gen5_1 (or existing tfvars value).
.PARAMETER ImageTag   Image tag for the in-cloud build. Default: UTC timestamp.
.PARAMETER DryRun     Print actions without changing anything.
.PARAMETER Yes        Skip confirmation prompts (non-interactive where possible).
.PARAMETER Force      Overwrite existing tfvars during configure (a timestamped .bak is kept in your temp dir, outside the repo).
.PARAMETER SkipPreflight / SkipImage   Skip those phases in an 'all' run.

.EXAMPLE
  ./deploy.ps1                    # full guided deploy
.EXAMPLE
  ./deploy.ps1 -Location uksouth -Sku P1v4   # full guided deploy, different region + SKU
.EXAMPLE
  ./deploy.ps1 -Task preflight -Location uksouth,eastus2 -Sku P1v4   # scan regions for a specific SKU
.EXAMPLE
  ./deploy.ps1 -Task configure    # just (re)build terraform.tfvars interactively
.EXAMPLE
  ./deploy.ps1 -Task grant-sql    # a SQL admin grants the app identity DB access
.EXAMPLE
  ./deploy.ps1 -Task set-pat      # seed the GitHub PAT into Key Vault (real-data mode)
.EXAMPLE
  ./deploy.ps1 -Task status       # probe the health endpoints
#>
[CmdletBinding()]
param(
  [ValidateSet('all', 'preflight', 'configure', 'provision', 'image', 'grant-sql', 'set-pat', 'status', 'sre-provision', 'grant-sre-sql', 'sre-sync')]
  [string]$Task = 'all',
  [string]$Location = 'eastus2',
  [string]$Sku = '',
  [string]$SqlSku = '',
  [string]$ImageTag = (Get-Date -Format 'yyyyMMdd-HHmmss'),
  [string]$ImageName = 'ghcp-credit-visibility',
  [switch]$DryRun,
  [switch]$Yes,
  [switch]$Force,
  [switch]$SkipPreflight,
  [switch]$SkipImage,
  [switch]$Register
)

$ErrorActionPreference = 'Stop'
# Terraform emits UTF-8 box-drawing characters (e.g. the "────" divider after a plan).
# PowerShell's console defaults to the legacy OEM/CP1252 codepage, which renders those as
# garbled "ΓöÇ" sequences — purely cosmetic, but distracting. Force UTF-8 for this session.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root   = $PSScriptRoot
$infra  = Join-Path $root 'infra'
$appDir = Join-Path $root 'GhcpCreditVisibility'
$tfvars = Join-Path $infra 'terraform.tfvars'

# ── UI helpers ───────────────────────────────────────────────────
function Write-Banner($t) { Write-Host ''; Write-Host ('═' * 70) -ForegroundColor DarkCyan; Write-Host "  $t" -ForegroundColor Cyan; Write-Host ('═' * 70) -ForegroundColor DarkCyan }
function Write-Step($n, $t) { Write-Host "`n[$n] $t" -ForegroundColor Yellow }
function Write-Ok($t)   { Write-Host "  ✓ $t" -ForegroundColor Green }
function Write-Info($t) { Write-Host "  • $t" -ForegroundColor Gray }
function Write-Warn($t) { Write-Host "  ! $t" -ForegroundColor Yellow }
function Write-Err($t)  { Write-Host "  ✗ $t" -ForegroundColor Red }
function Invoke-OrEcho($cmd) {
  if ($DryRun) { Write-Host "  DRYRUN> $cmd" -ForegroundColor DarkGray; return '' }
  Write-Host "  > $cmd" -ForegroundColor DarkGray
  # Merge stderr into the captured output (2>&1) so callers can inspect the actual error text
  # afterwards (e.g. to recognize Azure's RequestDisallowedByPolicy signature — see
  # Invoke-TerraformApply) — this doesn't change what streams to the console live, only what
  # ends up available in $script:LastCommandOutput for pattern-matching after the fact.
  $out = Invoke-Expression "$cmd 2>&1"
  $script:LastCommandOutput = ($out | Out-String)
  if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Command failed (exit $LASTEXITCODE): $cmd" }
  return $out
}
function Ask([string]$p, [string]$d) { $s = if ($d) { " [$d]" } else { "" }; $a = Read-Host "  $p$s"; if ([string]::IsNullOrWhiteSpace($a)) { $d } else { $a.Trim() } }
function AskYesNo([string]$p, [bool]$d) {
  if ($Yes) { return $d }
  $h = if ($d) { 'Y/n' } else { 'y/N' }
  while ($true) {
    $a = Read-Host "  $p [$h]"
    if ([string]::IsNullOrWhiteSpace($a)) { return $d }
    if ($a -match '^[Yy]([Ee][Ss])?$') { return $true }
    if ($a -match '^[Nn][Oo]?$') { return $false }
    Write-Warn "Please answer y or n (or press Enter for the default: $(if ($d) {'y'} else {'n'}))."
  }
}
function AskChoice([string]$p, [string[]]$opts, [int]$d = 1) {
  Write-Host "  $p" -ForegroundColor White
  for ($i = 0; $i -lt $opts.Count; $i++) { Write-Host ("     {0}) {1}" -f ($i + 1), $opts[$i]) -ForegroundColor Gray }
  if ($Yes) { return $d }
  while ($true) {
    $a = Read-Host "  choose 1-$($opts.Count) [$d]"
    if ([string]::IsNullOrWhiteSpace($a)) { return $d }
    $n = 0
    if ([int]::TryParse($a, [ref]$n) -and $n -ge 1 -and $n -le $opts.Count) { return $n }
    Write-Warn "Please enter a number between 1 and $($opts.Count) (or press Enter for the default: $d)."
  }
}
function Get-TfOutput([string]$n) { try { $v = (terraform -chdir="$infra" output -raw $n 2>$null); if ($LASTEXITCODE -eq 0 -and $v) { return $v.Trim() } } catch {}; return $null }

# ── Azure Run Command helpers (private-networking path) ──────────
# Both helpers execute a small script ON the jump-box VM via `az vm run-command create`. This
# runs over the ARM control plane (no VNet reachability needed from wherever deploy.ps1 itself
# is running) but the SCRIPT executes inside the VNet — so it resolves the SQL/Key Vault private
# endpoints normally. Secrets (the SQL access token / the PAT) are passed via
# `--protected-parameters`, which Azure does NOT persist/return in the run-command resource or
# its history — unlike `--parameters`, which would leak them into `az vm run-command show`.
# The run-command resource is deleted again immediately after reading its result.
function Invoke-JumpboxRunCommand([string]$ResourceGroup, [string]$VmName, [string]$NamePrefix, [string]$ScriptBody, [string[]]$Parameters, [string[]]$ProtectedParameters, [string]$SuccessMarker) {
  $rcName = "$NamePrefix-$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
  $scriptPath = Join-Path ([System.IO.Path]::GetTempPath()) "$rcName.ps1"
  Set-Content -Path $scriptPath -Value $ScriptBody -Encoding UTF8 -NoNewline
  try {
    $argList = @('vm', 'run-command', 'create', '--resource-group', $ResourceGroup, '--vm-name', $VmName, '--name', $rcName, '--script', "@$scriptPath", '--output', 'none')
    if ($Parameters.Count -gt 0) { $argList += '--parameters'; $argList += $Parameters }
    if ($ProtectedParameters.Count -gt 0) { $argList += '--protected-parameters'; $argList += $ProtectedParameters }
    & az @argList
    if ($LASTEXITCODE -ne 0) { throw "az vm run-command create failed (exit $LASTEXITCODE) — see above for the Azure error." }

    $json = az vm run-command show --resource-group $ResourceGroup --vm-name $VmName --run-command-name $rcName --instance-view -o json
    $result = $json | ConvertFrom-Json
    $out = ($result.instanceView.output -join "`n")
    $err = ($result.instanceView.error -join "`n")
    $exitCode = $result.instanceView.exitCode

    if ($exitCode -eq 0 -and $out -match [regex]::Escape($SuccessMarker)) {
      return $out
    }
    Write-Err "Run Command on $VmName did not report success (exit code: $exitCode)."
    if ($err) { Write-Host "  $err" -ForegroundColor DarkGray }
    if ($out) { Write-Host "  $out" -ForegroundColor DarkGray }
    throw "Azure Run Command '$rcName' on $VmName did not succeed."
  } finally {
    Remove-Item $scriptPath -ErrorAction SilentlyContinue
    az vm run-command delete --resource-group $ResourceGroup --vm-name $VmName --run-command-name $rcName --yes --output none 2>$null
  }
}

function Invoke-SqlGrantViaJumpbox([string]$ResourceGroup, [string]$VmName, [string]$Server, [string]$Database, [string]$AppName) {
  Write-Info "Fetching a SQL access token for your identity ($($script:Acct.user.name))..."
  $token = (az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv)
  if (-not $token) { throw "Could not acquire a SQL access token — are you signed in as the Entra SQL admin (or in the admin group)?" }

  # Raw ADO.NET (built into Windows' .NET Framework) instead of Invoke-Sqlcmd/the SqlServer
  # module — avoids needing to install anything on a bare jump-box VM, which may have no
  # outbound internet path to the PowerShell Gallery.
  $script = @'
param([string]$SqlServer, [string]$SqlDatabase, [string]$AppName, [string]$AccessToken)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data
$safeApp = $AppName.Replace("'", "''")
$tsql = @"
DECLARE @app sysname = N'$safeApp';
DECLARE @q sysname = QUOTENAME(@app);
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @app)
    EXEC('CREATE USER ' + @q + ' FROM EXTERNAL PROVIDER;');
IF IS_ROLEMEMBER('db_datareader', @app) = 0 EXEC('ALTER ROLE db_datareader ADD MEMBER ' + @q + ';');
IF IS_ROLEMEMBER('db_datawriter', @app) = 0 EXEC('ALTER ROLE db_datawriter ADD MEMBER ' + @q + ';');
IF IS_ROLEMEMBER('db_ddladmin',  @app) = 0 EXEC('ALTER ROLE db_ddladmin  ADD MEMBER ' + @q + ';');
"@
$conn = New-Object System.Data.SqlClient.SqlConnection
$conn.ConnectionString = "Server=tcp:$SqlServer,1433;Database=$SqlDatabase;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
$conn.AccessToken = $AccessToken
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = $tsql
$cmd.ExecuteNonQuery() | Out-Null
$conn.Close()
Write-Output "GRANT_OK"
'@

  Write-Info "Running the grant on $VmName via Azure Run Command (token passed as a protected parameter — never logged or returned)..."
  Invoke-JumpboxRunCommand -ResourceGroup $ResourceGroup -VmName $VmName -NamePrefix 'grant-sql' -ScriptBody $script `
    -Parameters @("SqlServer=$Server", "SqlDatabase=$Database", "AppName=$AppName") `
    -ProtectedParameters @("AccessToken=$token") `
    -SuccessMarker 'GRANT_OK' | Out-Null
  Write-Ok 'SQL grant applied via the jump box — the app picks it up within ~30s (migrations retry). No RDP needed.'
}

function Invoke-PatSetViaJumpbox([string]$ResourceGroup, [string]$VmName, [string]$VaultName, [string]$PatValue, [string]$IdentityClientId) {
  # Uses the jump box's OWN user-assigned managed identity (via the Instance Metadata Service)
  # rather than your identity — Terraform grants it Key Vault Secrets Officer scoped to just this
  # vault. No az CLI / Az PowerShell module install needed: pure REST calls built into PowerShell.
  # client_id is required in the IMDS token request for a user-assigned identity (unlike
  # system-assigned, IMDS can't infer which identity to use without it).
  $script = @'
param([string]$VaultName, [string]$SecretValue, [string]$ClientId)
$ErrorActionPreference = 'Stop'
$idResp = Invoke-RestMethod -Method Get -Headers @{Metadata = "true" } `
  -Uri "http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=https%3A%2F%2Fvault.azure.net&client_id=$ClientId"
$body = @{ value = $SecretValue } | ConvertTo-Json
$authHeader = "Bearer " + $idResp.access_token
Invoke-RestMethod -Method Put -Headers @{Authorization = $authHeader } `
  -Uri "https://$VaultName.vault.azure.net/secrets/github-pat?api-version=7.4" `
  -Body $body -ContentType "application/json" | Out-Null
Write-Output "PAT_SET_OK"
'@

  Write-Info "Setting the PAT via $VmName's own managed identity (value passed as a protected parameter — never logged or returned)..."
  Invoke-JumpboxRunCommand -ResourceGroup $ResourceGroup -VmName $VmName -NamePrefix 'set-pat' -ScriptBody $script `
    -Parameters @("VaultName=$VaultName", "ClientId=$IdentityClientId") `
    -ProtectedParameters @("SecretValue=$PatValue") `
    -SuccessMarker 'PAT_SET_OK' | Out-Null
  Write-Ok "GitHub PAT stored in Key Vault ($VaultName/github-pat) via the jump box. No RDP needed."
}

# ── Private-networking access patterns (shared by grant-sql + set-pat) ───────
# Four ways to reach a resource whose public network access is disabled:
#   1. Direct     — you're already on the VNet somehow (VPN/ExpressRoute/peering) — zero extra infra.
#   2. JumpBox     — only offered when enable_jumpbox=true — via Azure Run Command (see above).
#   3. TempPublic  — briefly re-enable public access + an IP allow rule, do the thing, revert. No
#                    standing infra, but a real (if short) reduction in network posture — opt-in only.
#   4. Manual      — print instructions; the operator does it themselves from wherever they have access.
# $script:InAllSequence controls whether "Manual" blocks waiting for Enter (only useful when later
# phases in the SAME run depend on this one having completed) — set by the 'all' dispatcher case.
$script:InAllSequence = $false

# NOTE: an earlier version of this file had a TCP-port reachability probe here, used to decide
# whether "Direct" access would work before attempting anything. Removed: Azure SQL's gateway,
# Key Vault's front-end, and App Service's front-end all accept the TCP/TLS connection and deny
# access at a HIGHER protocol layer (SQL login handshake / HTTP 403) when public access is
# disabled — a bare port-open check gives a false "reachable" result for all three (confirmed
# live: SQL returns "Deny Public Network Access" only during login, not connection; Key Vault and
# App Service both return HTTP 403 after completing TLS). The correct approach, used everywhere
# below, is to just attempt the real operation and interpret the actual result.

# Presents the access-mode menu; returns 'Direct' | 'JumpBox' | 'TempPublic' | 'Manual'.
# The JumpBox option only appears in the list at all when a jump box actually exists.
function Select-PrivateAccessMode([bool]$JumpboxAvailable) {
  $opts = [System.Collections.Generic.List[string]]::new()
  $opts.Add('Try direct access from here (works if you are already on the VNet — VPN/ExpressRoute/peering)')
  if ($JumpboxAvailable) { $opts.Add('Use the jump box (via Azure Run Command — no RDP needed)') }
  $opts.Add('Temporarily allow public access just for this operation, then revert automatically')
  $opts.Add("Skip — I'll handle it manually")
  $choice = AskChoice 'How do you want to run this?' $opts.ToArray() 1

  $idx = 1
  if ($choice -eq $idx) { return 'Direct' }; $idx++
  if ($JumpboxAvailable) { if ($choice -eq $idx) { return 'JumpBox' }; $idx++ }
  if ($choice -eq $idx) { return 'TempPublic' }
  return 'Manual'
}

# When Direct access fails after polling, escalate: offer the jump box first (if one exists —
# it's already-paid-for standing infra, simplest to reuse), then TempPublic as the universal
# escape hatch, then fall through to Manual. Each step is opt-in — never silently escalates.
function Resolve-DirectAccessFallback([bool]$JumpboxAvailable) {
  Write-Warn "No direct path found from this workstation."
  if ($JumpboxAvailable -and (AskYesNo 'A jump box is available. Use it instead?' $true)) { return 'JumpBox' }
  if (AskYesNo 'Temporarily allow public access to complete this instead?' $false) { return 'TempPublic' }
  return 'Manual'
}

# Manual fallback: always show the instructions; only BLOCK waiting for Enter when (a) we're in
# the middle of the 'all' sequence where later phases depend on this one, AND (b) we're actually
# interactive (never hangs under -Yes or -DryRun, which both imply unattended/non-blocking use).
function Show-ManualInstructionsAndMaybePause([string]$Instructions) {
  Write-Warn 'Manual step selected.'
  Write-Host $Instructions -ForegroundColor DarkGray
  if ($Yes -or $DryRun -or -not $script:InAllSequence) {
    Write-Info 'Continuing without waiting — re-run the relevant -Task later (or -Task status) once this is done.'
    return
  }
  Write-Host ''
  Read-Host "  Press Enter once you've completed this step (or Ctrl+C to stop here and resume later)" | Out-Null
}

# Shared by both the "myworkstation is doing this directly" path (Direct) and the TempPublic
# escape hatch (which also runs directly, just after briefly opening a network path).
function Get-MyPublicIp {
  $myIp = $null
  try { $myIp = (Invoke-RestMethod -Uri 'https://api.ipify.org?format=json' -TimeoutSec 10).ip } catch {}
  if (-not $myIp) { try { $myIp = (Invoke-RestMethod -Uri 'https://ifconfig.me/ip' -TimeoutSec 10).Trim() } catch {} }
  # This is an unauthenticated third party's HTTP response, and Phase-GrantSql writes it into
  # infra/adminip.auto.tfvars — which Terraform AUTO-LOADS on every plan/apply. A quote and a
  # newline in the response would inject extra variable assignments (container_image,
  # use_private_networking, ...) into the next deployment. Accept a bare IPv4 literal or nothing:
  # both callers already handle $null.
  if ($myIp) {
    $myIp = "$myIp".Trim()
    [System.Net.IPAddress]$parsed = $null
    if (-not ($myIp -match '^\d{1,3}(\.\d{1,3}){3}$') -or -not [System.Net.IPAddress]::TryParse($myIp, [ref]$parsed)) {
      Write-Warn 'Public-IP lookup returned an unexpected value — ignoring it.'
      return $null
    }
  }
  return $myIp
}

# Used by every path that actually needs the PAT value in-hand (Direct, JumpBox, TempPublic) —
# NOT needed for Manual, since in that case the operator sets the secret themselves and this
# script never needs to hold the value at all.
function Read-PatSecurely {
  $sec = Read-Host '  Paste the GitHub PAT (input hidden)' -AsSecureString
  $pat = [System.Net.NetworkCredential]::new('', $sec).Password
  if ([string]::IsNullOrWhiteSpace($pat)) { return $null }
  return $pat
}

function Ensure-SqlServerModule {
  if (Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue) { return $true }
  if ($DryRun) { Write-Host '  DRYRUN> Install-Module SqlServer -Scope CurrentUser' -ForegroundColor DarkGray; return $true }
  if (AskYesNo 'This needs the SqlServer PowerShell module (Invoke-Sqlcmd) to run the grant locally. Install it now?' $true) {
    Install-Module SqlServer -Scope CurrentUser -Force -AllowClobber
    Import-Module SqlServer
    return $true
  }
  Write-Warn 'Without the SqlServer module, run the T-SQL from `terraform output post_deploy_sql_grant` via Portal Query editor instead.'
  return $false
}

function Invoke-SqlGrantDirect([string]$Server, [string]$Database, [string]$AppName) {
  $tsql = @"
DECLARE @app sysname = N'$($AppName.Replace("'","''"))';
DECLARE @q sysname = QUOTENAME(@app);
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @app)
    EXEC('CREATE USER ' + @q + ' FROM EXTERNAL PROVIDER;');
IF IS_ROLEMEMBER('db_datareader', @app) = 0 EXEC('ALTER ROLE db_datareader ADD MEMBER ' + @q + ';');
IF IS_ROLEMEMBER('db_datawriter', @app) = 0 EXEC('ALTER ROLE db_datawriter ADD MEMBER ' + @q + ';');
IF IS_ROLEMEMBER('db_ddladmin',  @app) = 0 EXEC('ALTER ROLE db_ddladmin  ADD MEMBER ' + @q + ';');
"@
  if ($DryRun) { Write-Host "  DRYRUN> Invoke-Sqlcmd against $Server/$Database with idempotent grant" -ForegroundColor DarkGray; Write-Host $tsql -ForegroundColor DarkGray; return }
  $token = (az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv)
  Invoke-Sqlcmd -ServerInstance $Server -Database $Database -AccessToken $token -Query $tsql -ErrorAction Stop
  Write-Ok 'SQL grant applied — the app picks it up within ~30s (migrations retry).'
}

function Invoke-PatSetDirect([string]$VaultName, [string]$PatValue) {
  if ($DryRun) { Write-Host "  DRYRUN> az keyvault secret set --vault-name $VaultName --name github-pat --file <temp file holding the PAT>" -ForegroundColor DarkGray; return }
  # NOT --value: a process argument list is readable by any other local process and is captured
  # wholesale by EDR/Sysmon command-line logging, which would defeat Read-PatSecurely reading it
  # as a SecureString in the first place. --file keeps it out of argv; the temp copy is
  # user-scoped and removed in the finally.
  $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
  try {
    # utf8NoBOM and -NoNewline are both load-bearing: a BOM or a trailing newline would be
    # stored as part of the secret and the GitHub API would reject the token.
    Set-Content -Path $tmp -Value $PatValue -NoNewline -Encoding utf8NoBOM
    $errOutput = az keyvault secret set --vault-name $VaultName --name github-pat --file $tmp --only-show-errors 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Failed to set the secret: $($errOutput.Trim())" }
  }
  finally {
    if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
  }
  Write-Ok 'PAT stored in Key Vault. The app resolves it via its managed identity on the next snapshot.'
}

# Temporarily re-enables the SQL server's public network access + an IP-scoped firewall rule,
# runs the grant directly, then reverts — regardless of success/failure (finally). Even if the
# revert step itself somehow failed, the NEXT `terraform apply` re-asserts
# public_network_access_enabled = false for private mode, so this can't silently leave the door
# open long-term — worth telling the operator, since it's a real safety net for this exact case.
function Invoke-SqlGrantViaTempPublicAccess([string]$ResourceGroup, [string]$SqlServerName, [string]$Server, [string]$Database, [string]$AppName) {
  if (-not (Ensure-SqlServerModule)) { return }
  $myIp = Get-MyPublicIp
  if (-not $myIp) { throw "Couldn't auto-detect your public IP — required to scope the temporary firewall rule." }
  $ruleName = 'TempDeployerAccess'
  Write-Info "Current state: public network access = Disabled. Will re-disable when finished."
  Write-Info "Enabling public access + adding a firewall rule for your IP ($myIp)..."
  if ($DryRun) {
    Write-Host "  DRYRUN> az sql server update -g $ResourceGroup -n $SqlServerName --enable-public-network true" -ForegroundColor DarkGray
    Write-Host "  DRYRUN> az sql server firewall-rule create -g $ResourceGroup -s $SqlServerName -n $ruleName --start-ip-address $myIp --end-ip-address $myIp" -ForegroundColor DarkGray
    Invoke-SqlGrantDirect -Server $Server -Database $Database -AppName $AppName
    return
  }
  az sql server update -g $ResourceGroup -n $SqlServerName --enable-public-network true --only-show-errors | Out-Null
  az sql server firewall-rule create -g $ResourceGroup -s $SqlServerName -n $ruleName --start-ip-address $myIp --end-ip-address $myIp --only-show-errors | Out-Null
  try {
    Write-Ok 'Public access temporarily enabled. Waiting ~20s for the rule to propagate...'
    Start-Sleep -Seconds 20
    Invoke-SqlGrantDirect -Server $Server -Database $Database -AppName $AppName
  } finally {
    Write-Info 'Reverting: removing your firewall rule and disabling public access again...'
    az sql server firewall-rule delete -g $ResourceGroup -s $SqlServerName -n $ruleName --yes --only-show-errors 2>$null | Out-Null
    az sql server update -g $ResourceGroup -n $SqlServerName --enable-public-network false --only-show-errors 2>$null | Out-Null
    $state = az sql server show -g $ResourceGroup -n $SqlServerName --query publicNetworkAccess -o tsv 2>$null
    if ($state -eq 'Disabled') { Write-Ok 'Public access confirmed back to Disabled.' }
    else { Write-Warn "Couldn't confirm public access was re-disabled (state: $state) — the next `terraform apply` will re-assert this regardless." }
  }
}

# Same idea as the SQL version, but for Key Vault: adds a scoped network-rule IP allow instead of
# flipping the whole default_action to Allow (keeps the exposure window as narrow as possible).
function Invoke-PatSetViaTempPublicAccess([string]$ResourceGroup, [string]$VaultName, [string]$PatValue) {
  $myIp = Get-MyPublicIp
  if (-not $myIp) { throw "Couldn't auto-detect your public IP — required to scope the temporary network rule." }
  Write-Info "Current state: public network access = Disabled. Will re-disable when finished."
  Write-Info "Enabling public access + adding a network rule for your IP ($myIp)..."
  if ($DryRun) {
    Write-Host "  DRYRUN> az keyvault update -n $VaultName --public-network-access Enabled" -ForegroundColor DarkGray
    Write-Host "  DRYRUN> az keyvault network-rule add -n $VaultName --ip-address $myIp" -ForegroundColor DarkGray
    Invoke-PatSetDirect -VaultName $VaultName -PatValue $PatValue
    return
  }
  az keyvault update -n $VaultName --public-network-access Enabled --only-show-errors | Out-Null
  az keyvault network-rule add -n $VaultName --ip-address $myIp --only-show-errors | Out-Null
  try {
    Write-Ok 'Public access temporarily enabled. Waiting ~20s for the rule to propagate...'
    Start-Sleep -Seconds 20
    Invoke-PatSetDirect -VaultName $VaultName -PatValue $PatValue
  } finally {
    Write-Info 'Reverting: removing your network rule and disabling public access again...'
    az keyvault network-rule remove -n $VaultName --ip-address $myIp --only-show-errors 2>$null | Out-Null
    az keyvault update -n $VaultName --public-network-access Disabled --only-show-errors 2>$null | Out-Null
    $state = az keyvault show -n $VaultName --query properties.publicNetworkAccess -o tsv 2>$null
    if ($state -eq 'Disabled') { Write-Ok 'Public access confirmed back to Disabled.' }
    else { Write-Warn "Couldn't confirm public access was re-disabled (state: $state) — the next `terraform apply` will re-assert this regardless." }
  }
}

function Get-TfVar([string]$n) {
  if (-not (Test-Path $tfvars)) { return $null }
  $line = Select-String -Path $tfvars -Pattern "^\s*$n\s*=" | Select-Object -First 1
  if (-not $line) { return $null }
  return (($line.Line -split '=', 2)[1].Trim()).Trim('"')
}
function Set-TfVar([string]$n, [string]$v) {
  # In-place update of an existing "name = value" line in terraform.tfvars (string values only).
  # No-op if the file or the var line doesn't exist yet — Phase-Configure is the source of truth for new vars.
  if (-not (Test-Path $tfvars)) { return }
  $content = Get-Content -Path $tfvars
  $updated = $false
  $content = $content | ForEach-Object {
    if ($_ -match "^\s*$n\s*=") { $updated = $true; "$n = `"$v`"" } else { $_ }
  }
  if ($updated) { Set-Content -Path $tfvars -Value $content }
}

# ── shared: az context + signed-in user ──────────────────────────
$script:Acct = $null
$script:Me = $null
$script:LastCommandOutput = ''
function Ensure-Az {
  if (-not (Get-Command az -ErrorAction SilentlyContinue)) { throw "Azure CLI (az) not found. Install it, then run: az login" }
  $script:Acct = (az account show 2>$null | ConvertFrom-Json)
  if (-not $script:Acct) { throw "Not signed in. Run: az login" }
  Write-Ok "az context: $($script:Acct.name)  (sub $($script:Acct.id), tenant $($script:Acct.tenantId))"
  try { $script:Me = (az ad signed-in-user show --query "{id:id,upn:userPrincipalName,mail:mail,name:displayName}" -o json 2>$null | ConvertFrom-Json) } catch {}
}

# B2B guest accounts get an internal UPN like "user_domain.com#EXT#@tenant.onmicrosoft.com" —
# not a real, deliverable address. Prefer the account's actual "mail" attribute; if that's
# missing, reconstruct the original external address by decoding the guest UPN encoding
# (replace the last "_" before "#EXT#" with "@"). Falls back to '' if nothing usable is found.
function Get-DisplayEmail($me) {
  if (-not $me) { return '' }
  if ($me.mail) { return $me.mail }
  if ($me.upn -match '^(?<local>.+)#EXT#@') {
    $encoded = $Matches.local
    if ($encoded -match '^(?<rest>.+)_(?<domain>[^_]+)$') { return "$($Matches.rest)@$($Matches.domain)" }
  }
  if ($me.upn -notmatch '#EXT#') { return $me.upn }
  return ''
}

# ── PHASE: prereqs ───────────────────────────────────────────────
function Phase-Prereqs {
  Write-Step 0 'Prerequisites'
  foreach ($t in 'az', 'terraform') { if (-not (Get-Command $t -ErrorAction SilentlyContinue)) { throw "$t not found on PATH." }; Write-Ok "$t present" }
  if (-not (Test-Path (Join-Path $appDir 'Dockerfile'))) { throw "App/Dockerfile not found at $appDir" }
  Ensure-Az
  if (-not $DryRun -and -not $Yes) { if (-not (AskYesNo 'Proceed against THIS subscription?' $false)) { throw 'Aborted by user.' } }
}

# ── PHASE: preflight (capacity/region) — self-contained ──────────
# Verifies, per region: resource-provider registration, App Service tier quota
# (Microsoft.Web usages; localizedValue = tier, limit>0 = deployable) and Azure SQL
# availability — BEFORE apply, so you fail fast with actionable guidance. Pass one region
# to gate a deploy, or a comma-separated list (e.g. -Location eastus2,uksouth) to scan.
function Phase-Preflight {
  param([switch]$Gate)
  if ($SkipPreflight) { Write-Info 'Preflight skipped (-SkipPreflight).'; return }
  Write-Step 1 'Capacity + region precheck'
  if (-not $script:Acct) { Ensure-Az }
  $sub = $script:Acct.id
  $regions = @($Location | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
  $providers = 'Microsoft.Web', 'Microsoft.Sql', 'Microsoft.ContainerRegistry', 'Microsoft.KeyVault', 'Microsoft.ManagedIdentity', 'Microsoft.OperationalInsights', 'Microsoft.Insights'

  # -Sku/-SqlSku (CLI) win over an already-written tfvars, which wins over the hard default.
  $resolvedSku = if ($Sku) { $Sku } else { (Get-TfVar 'app_service_sku') }; if (-not $resolvedSku) { $resolvedSku = 'S1' }
  $resolvedSqlSku = if ($SqlSku) { $SqlSku } else { (Get-TfVar 'sql_database_sku') }; if (-not $resolvedSqlSku) { $resolvedSqlSku = 'GP_S_Gen5_1' }
  Write-Info "Checking App Service SKU '$resolvedSku' + Azure SQL SKU '$resolvedSqlSku'"

  $provOk = $true
  foreach ($p in $providers) {
    $state = az provider show -n $p --query registrationState -o tsv 2>$null
    if ($state -eq 'Registered') { Write-Ok "provider $p" }
    else {
      $provOk = $false
      if ($Register) { Write-Warn "$p = $state → registering (async)"; az provider register -n $p 2>$null | Out-Null }
      else { Write-Err "$p = $state (run with -Register, or: az provider register -n $p)" }
    }
  }

  $wantTier = switch -Regex ($resolvedSku) {
    '^B' { 'Basic' } '^S' { 'Standard' } '.*mv4$' { 'Premium v4' } '.*v4$' { 'Premium v4' }
    '.*mv3$' { 'Premium v3' } '.*v3$' { 'Premium v3' } '.*v2$' { 'Premium v2' }
    '^I.*v2$' { 'Isolated v2' } '^I' { 'Isolated' } default { 'Standard' }
  }

  $results = @()
  foreach ($loc in $regions) {
    Write-Host "  ── region: $loc ──" -ForegroundColor Cyan
    $appOk = $false; $availTiers = @(); $wantLimit = 0
    $totalVmsLimit = $null; $totalVmsCurrent = 0
    try {
      $uErr = $null
      $u = az rest --method get --uri "https://management.azure.com/subscriptions/$sub/providers/Microsoft.Web/locations/$loc/usages?api-version=2023-12-01" 2>&1 | Tee-Object -Variable uRaw | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ConvertFrom-Json -ErrorAction SilentlyContinue
      if ($u.value) {
        $byTier = @{}
        foreach ($e in $u.value) {
          $t = $e.name.localizedValue; if (-not $t) { $t = $e.name.value }
          $lim = [int]$e.limit; $cur = [int]$e.currentValue
          if (-not $byTier.ContainsKey($t) -or $lim -gt $byTier[$t].Limit) { $byTier[$t] = @{ Limit = $lim; Current = $cur } }
          # "Total VMs" is a SEPARATE quota bucket from the per-tier core quota above — it caps the
          # total number of App Service Plan worker instances (VMs) across the WHOLE subscription
          # in this region, regardless of tier. A tier showing plenty of cores can still fail to
          # deploy if this bucket is exhausted (confirmed live: preflight reported "Premium v3 has
          # quota (360 cores)" while the actual apply failed with "Current Limit (Total VMs): 0").
          # Matched on the stable name.value (not the localized display string, which can vary).
          if ($e.name.value -eq 'TotalVMs') { $totalVmsLimit = $lim; $totalVmsCurrent = $cur }
        }
        $availTiers = ($byTier.GetEnumerator() | Where-Object { $_.Value.Limit -gt 0 } | ForEach-Object { $_.Key })
        $wantLimit = if ($byTier.ContainsKey($wantTier)) { $byTier[$wantTier].Limit } else { 0 }
        if ($wantLimit -gt 0) {
          Write-Ok "App Service tier '$wantTier' has quota ($wantLimit cores)"
          if ($null -ne $totalVmsLimit -and $totalVmsCurrent -ge $totalVmsLimit) {
            Write-Err "But 'Total VMs' quota is exhausted in $loc ($totalVmsCurrent of $totalVmsLimit used) — this caps App Service Plan instances subscription-wide in this region, regardless of tier. Request an increase at https://aka.ms/antquotahelp or try another region."
          }
          else { $appOk = $true }
        }
      }
      else {
        $uErrLine = ($uRaw | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | Select-Object -First 1)
      }
    }
    catch { Write-Warn "App Service: couldn't read Microsoft.Web usages for $loc." }

    if (-not $appOk) {
      # Legacy Microsoft.Web usages showed 0/no data for the tier bucket — this can lag behind, or
      # simply not surface, an approved per-SKU grant made via the newer Microsoft.Quota API (the
      # "Accepted" quota request you see in the Activity Log for .../Microsoft.Quota/quotas/<SKU>).
      # Cross-check that API directly for the exact SKU name before declaring no quota. NOTE: this
      # fallback only covers the per-tier/per-SKU quota — it can't see the "Total VMs" bucket above,
      # so if that's what's actually exhausted, this won't rescue $appOk (correctly so).
      $qLimit = 0
      try {
        $qRaw = $null
        $q = az rest --method get --uri "https://management.azure.com/subscriptions/$sub/providers/Microsoft.Web/locations/$loc/providers/Microsoft.Quota/quotas/$resolvedSku`?api-version=2023-02-01" 2>&1 | Tee-Object -Variable qRaw | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ConvertFrom-Json -ErrorAction SilentlyContinue
        if ($q.properties.limit.value) { $qLimit = [int]$q.properties.limit.value }
      } catch {}
      if ($qLimit -gt 0 -and -not ($null -ne $totalVmsLimit -and $totalVmsCurrent -ge $totalVmsLimit)) {
        $appOk = $true
        Write-Ok "App Service SKU '$resolvedSku' has quota via Microsoft.Quota ($qLimit) — legacy usages API hasn't caught up yet"
      }
      elseif ($wantLimit -le 0) {
        Write-Warn "App Service: no usages data for $loc; confirm in portal (Subscription > Usage + quotas)."
        Write-Err "App Service tier '$wantTier' has 0 quota in $loc"
        if ($availTiers) { Write-Warn "Tiers WITH quota: $($availTiers -join ', ') — set app_service_sku to one of these" }
        else { Write-Warn "No App Service tier has quota here — request at https://aka.ms/antquotahelp or try another region" }
        Write-Info "If you just approved a quota request, re-run in a few minutes — Microsoft.Quota grants can take time to propagate to the Microsoft.Web usages API."
        if ($uErrLine) { Write-Info "  az rest (usages) error: $uErrLine" }
        $qErrLine = ($qRaw | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | Select-Object -First 1)
        if ($qErrLine) { Write-Info "  az rest (Microsoft.Quota) error: $qErrLine" }
      }
    }

    $sqlOk = $false
    try {
      $ed = az sql db list-editions -l $loc -o json 2>$null | ConvertFrom-Json
      $gp = $ed | Where-Object { $_.name -eq 'GeneralPurpose' } | Select-Object -First 1
      $slos = @($gp.supportedServiceLevelObjectives.name)
      if ($slos -contains $resolvedSqlSku) { $sqlOk = $true; Write-Ok "Azure SQL '$resolvedSqlSku' offered in $loc" }
      elseif ($gp) {
        Write-Err "Azure SQL '$resolvedSqlSku' not offered in $loc"
        $alt = $slos | Where-Object { $_ -like 'GP_S_Gen5*' } | Select-Object -First 5
        if ($alt) { Write-Info "serverless SKUs available here: $($alt -join ', ')" }
      }
      else { Write-Err "Azure SQL GeneralPurpose edition not offered in $loc" }
    }
    catch { Write-Warn "Azure SQL: region query failed for $loc." }

    $results += [PSCustomObject]@{ Region = $loc; Tier = $wantTier; AppQuota = $appOk; SQL = $sqlOk; Deployable = ($appOk -and $sqlOk); AvailableTiers = ($availTiers -join ', ') }
  }

  Write-Host ''
  $results | Format-Table -AutoSize Region, Tier, AppQuota, SQL, Deployable, AvailableTiers | Out-String | Write-Host
  if (-not $provOk) { Write-Warn 'Some resource providers are not registered (see above; use -Register).' }
  $viable = $results | Where-Object { $_.Deployable }
  if ($viable) { Write-Ok ("Deployable now: " + (($viable | ForEach-Object { "$($_.Region)/$($_.Tier)" }) -join ', ')) }
  else { Write-Warn 'No checked region is deployable as-is with the configured SKU — see AvailableTiers, or request quota.' }

  if ($Gate -and $regions.Count -eq 1 -and -not $results[0].Deployable -and -not $DryRun) {
    if ($Yes) {
      throw "Preflight: '$($regions[0])' not deployable with SKU '$resolvedSku'. Change app_service_sku/region, or pass -SkipPreflight to override."
    }
    # Non-interactive callers get the hard failure above; interactively, let the operator fix it
    # right here instead of aborting the whole run — re-check without re-running the script.
    Write-Warn "Region '$($regions[0])' isn't deployable with SKU '$resolvedSku'."
    $hint = if ($results[0].AvailableTiers) { " (tiers with quota here: $($results[0].AvailableTiers))" } else { '' }
    $retryChoice = AskChoice 'What would you like to do?' @(
      'Try a different region (same SKU)',
      "Try a different App Service SKU$hint",
      'Proceed anyway (skip the gate — apply may fail on quota)',
      'Abort') 1
    switch ($retryChoice) {
      1 {
        $newLoc = Ask 'Region to try' $regions[0]
        $script:Location = $newLoc
        if (Test-Path $tfvars) { Set-TfVar 'location' $newLoc }
        Phase-Preflight -Gate
        return
      }
      2 {
        $newSku = Ask 'App Service SKU to try' $resolvedSku
        $script:Sku = $newSku
        if (Test-Path $tfvars) { Set-TfVar 'app_service_sku' $newSku }
        Phase-Preflight -Gate
        return
      }
      3 { Write-Warn 'Proceeding without a passing preflight gate — provision may fail on quota.'; return }
      default {
        Write-Warn "Aborted — no changes made. Re-run with a different -Location/-Sku, or -SkipPreflight to bypass this check."
        exit 1
      }
    }
  }
}

# ── PHASE: configure (interactive tfvars) ────────────────────────
function Phase-Configure {
  Write-Step 2 'Configure terraform.tfvars'
  if ((Test-Path $tfvars) -and -not $Force) {
    if (-not (AskYesNo "terraform.tfvars exists. Rebuild it interactively? (No = keep current)" $false)) { Write-Info 'Keeping existing terraform.tfvars.'; return }
  }
  if (-not $script:Acct) { Ensure-Az }
  if ($script:Me -and $script:Me.id) { Write-Info "You: $($script:Me.name) <$($script:Me.upn)> ($($script:Me.id))" } else { Write-Warn "Couldn't resolve your user object; 'Myself' options will ask you to paste an ID." }

  $subId  = Ask 'Subscription ID' $script:Acct.id
  $loc    = Ask 'Location' $Location
  $prefix = Ask 'Name prefix (short, a-z0-9)' 'ghcpcv'

  $skuDefault = if ($Sku) { $Sku } else { (Get-TfVar 'app_service_sku') }; if (-not $skuDefault) { $skuDefault = 'S1' }
  $appSku = Ask 'App Service Plan SKU (e.g. S1, P1v3, P1mv3, P1v4 — check ./deploy.ps1 -Task preflight for quota first)' $skuDefault

  $sqlSkuDefault = if ($SqlSku) { $SqlSku } else { (Get-TfVar 'sql_database_sku') }; if (-not $sqlSkuDefault) { $sqlSkuDefault = 'GP_S_Gen5_1' }
  $sqlModeDefault = if ($sqlSkuDefault -match '_S_') { 1 } else { 2 }
  Write-Info 'Serverless auto-scales/auto-pauses but is billed ~3.4x the per-vCore-hour rate of provisioned (eastus2) — cheaper only if the DB sits idle a meaningful fraction of the time. If it runs 24x7, provisioned is cheaper.'
  $sqlMode = AskChoice 'Azure SQL compute model' @(
    'Serverless — auto-scales + auto-pauses when idle (good for dev/test/bursty use)',
    'Provisioned — fixed vCores, always on, no pause (cheaper for steady 24x7 workloads)') $sqlModeDefault
  $sqlAutoPause = 60
  if ($sqlMode -eq 1) {
    $sqlDbSku = Ask 'Azure SQL DB serverless SKU (e.g. GP_S_Gen5_1, GP_S_Gen5_2)' $(if ($sqlSkuDefault -match '_S_') { $sqlSkuDefault } else { 'GP_S_Gen5_1' })
    $autoPauseDefault = if ($sqlSkuDefault -match '_S_') { (Get-TfVar 'sql_auto_pause_minutes') } else { '' }; if (-not $autoPauseDefault) { $autoPauseDefault = '60' }
    $sqlAutoPause = [int](Ask 'Auto-pause after idle minutes (60/90/120/240/360/720/1440, or -1 to disable/always-warm)' $autoPauseDefault)
  }
  else {
    $sqlDbSku = Ask 'Azure SQL DB provisioned SKU (e.g. GP_Gen5_2, BC_Gen5_2)' $(if ($sqlSkuDefault -notmatch '_S_') { $sqlSkuDefault } else { 'GP_Gen5_2' })
  }

  $alertEmailDefault = Get-DisplayEmail $script:Me
  $alertEmail = Ask 'Email address (or distribution list) to notify when SQL CPU/memory/storage exceeds 80% — press Enter to use your signed-in email' $alertEmailDefault
  while (-not $alertEmail) { $alertEmail = Ask 'Alert email is required (no default could be resolved for your account) — enter one' '' }

  $identityMode = @('user_assigned_selfadmin', 'system_assigned')[(AskChoice 'Identity model' @(
        'user_assigned_selfadmin  — TEST: app identity is its own SQL admin (no grant)',
        'system_assigned          — CUSTOMER/PROD: external Entra SQL admin + one-time grant') 1) - 1]

  Write-Info 'Note: public mode may be blocked by Azure Policy in governed/enterprise tenants (a Deny-effect policy disallowing public network access is common in landing zones) — if this is a managed corporate subscription, private mode is usually safer.'
  $private = AskYesNo 'Use private networking — VNet + private endpoints, not reachable from the public internet? No = public (simpler, browsable straight from your laptop)' $false
  $createZones = $false
  $customNetwork = $false
  $vnetSpace = ''; $subnetPe = ''; $subnetApp = ''
  $existingVnetRg = ''; $existingVnetName = ''; $existingSubnetPe = ''; $existingSubnetApp = ''
  if ($private) {
    $createZones = AskYesNo 'No central DNS/DINE policy in this subscription? (Yes = this stack creates its own local DNS zones)' $false
    $netMode = @('simple', 'advanced', 'byo')[(AskChoice 'VNet/subnet source' @(
          'This stack creates a VNet + subnets using sensible defaults (simplest — no IPAM dependency)',
          'This stack creates a VNet + subnets, but you choose the IP ranges (advanced — fits your IPAM plan)',
          'Bring your own existing VNet + subnets (your org controls IPAM/landing zone)') 1) - 1]
    $customNetwork = ($netMode -eq 'byo')
    if ($netMode -eq 'advanced') {
      Write-Host ''; Write-Info 'Advanced: this stack still creates the VNet/subnets — you just pick the ranges. Minimum /27 per subnet; both must fit inside the VNet range and not overlap each other.'
      $vnetSpace = Ask 'VNet address space (CIDR)' '10.60.0.0/24'
      $subnetPe = Ask 'Private-endpoint subnet prefix (CIDR, min /27)' '10.60.0.0/26'
      $subnetApp = Ask 'App Service delegated subnet prefix (CIDR, min /27)' '10.60.0.64/26'
    }
    elseif ($netMode -eq 'byo') {
      Write-Host ''; Write-Info 'Both subnets must already exist. PE subnet: empty/non-delegated. App subnet: delegated to Microsoft.Web/serverFarms.'
      $existingVnetRg = Ask 'Resource group of the existing VNet' ''
      $existingVnetName = Ask 'Existing VNet name' ''
      $existingSubnetPe = Ask 'Existing subnet name for private endpoints' ''
      $existingSubnetApp = Ask 'Existing subnet name for App Service VNet integration (already delegated)' ''
    }
  }

  # Jump box + Bastion: only offered when this stack owns the VNet (simple/advanced), since
  # bring-your-own-VNet deployments shouldn't have this script carve extra subnets out of a
  # VNet it doesn't own — matches the enable_jumpbox validation in infra/variables.tf.
  $enableJumpbox = $false; $jumpboxVmSize = ''
  if ($private -and $netMode -ne 'byo') {
    $enableJumpbox = AskYesNo 'Add a Windows jump box VM + Azure Bastion, to test the private network path end-to-end?' $false
    if ($enableJumpbox) {
      $jumpboxVmSize = Ask 'Jump box VM size' 'Standard_D2s_v6'
      Write-Info 'Admin password is auto-generated — after apply, retrieve it with: terraform output -raw jumpbox_admin_password'
    }
  }

  $mock = AskYesNo 'Use mock data (no GitHub PAT needed)?' $true
  $ghSlug = ''; if (-not $mock) { $ghSlug = Ask 'GitHub enterprise slug' 'your-enterprise' }

  $createAcr = AskYesNo 'Build the image in-cloud with ACR (needed for the test deploy)?' $true

  # admin principal
  Write-Host ''; Write-Info 'Admin = sees all data + manages the console (granted the Entra "Admin" app role at deploy).'
  $adminChoice = AskChoice 'Grant the Admin app role to:' @('Myself (this az login)', 'An Entra group (paste object ID)', 'Skip — assign later') 1
  $adminId = ''
  switch ($adminChoice) {
    1 { if ($script:Me -and $script:Me.id) { $adminId = $script:Me.id; Write-Ok "Admin = you" } else { $adminId = Ask 'Paste YOUR user object ID' '' } }
    2 { $adminId = Ask 'Paste the Entra GROUP object ID' ''; Write-Info 'Group-based app-role assignment needs Entra ID P1+.' }
    3 { Write-Warn 'No admin assigned — grant the Admin role in Entra after deploy or nobody can open the console.' }
  }

  # sql admin (system_assigned only)
  $sqlName = ''; $sqlId = ''
  if ($identityMode -eq 'system_assigned') {
    $sqlChoice = AskChoice 'Azure SQL Entra administrator (can run the DB grant):' @('Myself (this az login)', 'An Entra group') 1
    if ($sqlChoice -eq 1 -and $script:Me -and $script:Me.id) { $sqlName = $script:Me.upn; $sqlId = $script:Me.id; Write-Ok "SQL admin = you" }
    else { $sqlName = Ask 'SQL admin display name (UPN or group name)' 'SG-GHCP-SQL-Admins'; $sqlId = Ask 'SQL admin object ID' '' }
  }

  # Optional Azure SRE Agent (AI reliability agent). Off by default — it's a preview feature that
  # incurs an always-on cost (~$0.40/hr) from creation until deleted. See docs/SRE_AGENT.md.
  $sreDefault = (Get-TfVar 'enable_sre_agent') -eq 'true'
  $enableSre = AskYesNo 'Also deploy the optional Azure SRE Agent (AI reliability agent)? Preview; ~$0.40/hr always-on until deleted' $sreDefault
  $sreRegion = ''
  if ($enableSre) {
    Write-Info 'Microsoft.App/agents is NOT available in every region (notably not Germany West Central). The agent monitors the app remotely, so its own region is separate.'
    $sreRegionDefault = (Get-TfVar 'sre_agent_location'); if (-not $sreRegionDefault) { $sreRegionDefault = 'swedencentral' }
    $sreRegion = Ask 'SRE Agent region (swedencentral, francecentral, uksouth, italynorth, eastus2, canadacentral, ...)' $sreRegionDefault
  }

  $lines = [System.Collections.Generic.List[string]]::new()
  $lines.Add("subscription_id           = `"$subId`"")
  $lines.Add("location                  = `"$loc`"")
  $lines.Add("name_prefix               = `"$prefix`"")
  $lines.Add("app_service_sku           = `"$appSku`"")
  $lines.Add("sql_database_sku          = `"$sqlDbSku`"")
  if ($sqlMode -eq 1) { $lines.Add("sql_auto_pause_minutes    = $sqlAutoPause") }
  $lines.Add("sql_alert_email_addresses = [`"$alertEmail`"]")
  $lines.Add("identity_mode             = `"$identityMode`"")
  $lines.Add("use_private_networking    = $($private.ToString().ToLower())")
  if ($private -and $createZones) { $lines.Add("create_private_dns_zones  = true") }
  if ($private -and $netMode -eq 'advanced') {
    $lines.Add("vnet_address_space  = `"$vnetSpace`"")
    $lines.Add("subnet_pe_prefix    = `"$subnetPe`"")
    $lines.Add("subnet_app_prefix   = `"$subnetApp`"")
  }
  if ($private -and $customNetwork) {
    $lines.Add("custom_network_mode               = true")
    $lines.Add("existing_vnet_resource_group_name = `"$existingVnetRg`"")
    $lines.Add("existing_vnet_name                = `"$existingVnetName`"")
    $lines.Add("existing_subnet_pe_name           = `"$existingSubnetPe`"")
    $lines.Add("existing_subnet_app_name          = `"$existingSubnetApp`"")
  }
  if ($enableJumpbox) {
    $lines.Add("enable_jumpbox            = true")
    $lines.Add("jumpbox_vm_size           = `"$jumpboxVmSize`"")
  }
  if ($identityMode -eq 'system_assigned') { $lines.Add("sql_admin_group_name      = `"$sqlName`""); $lines.Add("sql_admin_object_id       = `"$sqlId`"") }
  $lines.Add("use_mock_data             = $($mock.ToString().ToLower())")
  if (-not $mock) { $lines.Add("github_enterprise_slug    = `"$ghSlug`"") }
  $lines.Add("create_acr                = $($createAcr.ToString().ToLower())")
  if ($adminId) { $lines.Add("admin_principal_object_id = `"$adminId`"") }
  if ($enableSre) {
    $lines.Add("enable_sre_agent          = true")
    $lines.Add("sre_agent_location        = `"$sreRegion`"")
  }
  $content = ($lines -join "`n") + "`n"

  Write-Host ''; Write-Info 'Review terraform.tfvars:'; Write-Host $content -ForegroundColor DarkGray
  if (-not (AskYesNo 'Write this terraform.tfvars?' $true)) { throw 'Aborted — nothing written.' }
  if ($DryRun) { Write-Host "  DRYRUN> (would write $tfvars)" -ForegroundColor DarkGray; return }
  # Back up OUTSIDE the repo: tfvars carries the PAT / jumpbox password / tenant identifiers,
  # and a ".bak" beside it is not matched by the "*.tfvars" ignore rule — one `git add -A` and
  # those land in history for good. Timestamped so successive runs don't overwrite each other.
  if (Test-Path $tfvars) {
    $backupDir = Join-Path ([System.IO.Path]::GetTempPath()) 'ghcp-credit-visibility'
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    $backup = Join-Path $backupDir ("terraform.tfvars.{0}.bak" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    Copy-Item $tfvars $backup -Force
    Write-Info "Backed up → $backup"
  }
  Set-Content -Path $tfvars -Value $content -NoNewline
  Write-Ok "Wrote $tfvars"
}

# ── PHASE: provision (terraform) ─────────────────────────────────
function Phase-Provision {
  Write-Step 3 'Provision infrastructure (terraform)'
  if (-not (Test-Path $tfvars)) { throw "terraform.tfvars not found — run: ./deploy.ps1 -Task configure" }

  Push-Location $infra
  try {
    Invoke-OrEcho 'terraform init -input=false'
    # The optional SRE agent is held out of this apply when $script:HoldSreAgent is set (see the
    # dispatcher) — Invoke-TerraformApply appends -var enable_sre_agent=false. Phase-SreProvision is
    # the ONLY apply that includes it, so an agent-API failure can never block the core app.
    Invoke-TerraformApply "-var `"location=$Location`""
  } finally { Pop-Location }
  Write-Ok 'infrastructure applied'
}

# ── PHASE: provision the optional SRE Agent (isolated, non-fatal) ──
# Runs LAST in the `all` sequence — after the app is deployed AND health-checked — so a failure in
# the preview Microsoft.App/agents API can never abort the core deploy. Re-runnable on its own:
#   ./deploy.ps1 -Task sre-provision
function Phase-SreProvision {
  if ((Get-TfVar 'enable_sre_agent') -ne 'true') { return }  # opt-in only; silent no-op otherwise
  Write-Step 8 'Provision the optional SRE Agent (isolated — cannot block the app)'
  # This is the ONE apply that includes the agent — clear the hold so it's actually created.
  $script:HoldSreAgent = $false

  # Microsoft.App is often NotRegistered; --wait blocks until it's ready so the apply doesn't race it.
  if ($DryRun) { Write-Host '  DRYRUN> az provider register -n Microsoft.App --wait' -ForegroundColor DarkGray }
  else {
    $state = az provider show -n Microsoft.App --query registrationState -o tsv 2>$null
    if ($state -ne 'Registered') {
      Write-Info 'Registering Microsoft.App provider (SRE Agent) — this can take a few minutes...'
      az provider register -n Microsoft.App --wait --only-show-errors | Out-Null
      Write-Ok 'Microsoft.App provider registered.'
    }
  }

  Push-Location $infra
  try {
    # Full apply (no enable_sre_agent override) — this is where the agent + its RBAC/alerts come up.
    Invoke-TerraformApply "-var `"location=$Location`""
    Write-Ok 'SRE Agent infrastructure applied.'
  } catch {
    Write-Warn "SRE Agent provisioning failed — the app is unaffected. $($_.Exception.Message)"
    Write-Info 'Fix and re-run just this step: ./deploy.ps1 -Task sre-provision'
  } finally { Pop-Location }
}

# Runs `terraform plan` to a saved plan file, shows it, and asks for confirmation before
# applying — unless -Yes/-DryRun is set. Caller must already be in the terraform working dir.
# $extraArgs is a string of extra -var/-var-file args appended to both plan and apply.
function Invoke-TerraformApply([string]$extraArgs = '') {
  # Hold the optional SRE agent out of core-phase applies (provision/image/grant-sql). Only
  # Phase-SreProvision clears this flag, so it's the single place the agent is applied — a failure
  # there can't abort the app. See the dispatcher where the flag is computed.
  if ($script:HoldSreAgent) { $extraArgs = ($extraArgs + ' -var "enable_sre_agent=false"').Trim() }
  if ($DryRun) { Write-Host "  DRYRUN> terraform plan -out=tfplan $extraArgs; terraform apply tfplan" -ForegroundColor DarkGray; return }
  $planFile = 'tfplan'
  Invoke-OrEcho "terraform plan -input=false -out=$planFile $extraArgs"
  if (-not $Yes) {
    Write-Host ''
    if (-not (AskYesNo 'Apply the plan shown above?' $true)) { Remove-Item -Path $planFile -ErrorAction SilentlyContinue; throw 'Aborted by user before terraform apply.' }
  }
  try {
    Invoke-OrEcho "terraform apply -input=false $planFile"
  } catch {
    # Azure's Policy engine denies a resource with a consistent, resource-agnostic signature
    # (403 RequestDisallowedByPolicy) regardless of which resource/policy triggered it — this is
    # a reliable signal that the PUBLIC networking pattern can never succeed here, because a
    # Deny-effect policy (common in governed/landing-zone tenants) blocks public network access
    # on one or more resources (SQL/Key Vault/App Service/etc.). Reacting to the actual error
    # (rather than trying to predict every possible policy assignment beforehand) means this
    # can't miss a policy we didn't know to look for, and can't false-positive on an unrelated
    # failure.
    if ($script:LastCommandOutput -match 'RequestDisallowedByPolicy') {
      Write-Err 'Azure Policy blocked this deployment (RequestDisallowedByPolicy).'
      Write-Info 'Your tenant enforces a policy that denies public network access for one or more resources (SQL/Key Vault/App Service/etc.) — common in governed/landing-zone tenants.'
      Write-Info 'The PUBLIC networking pattern (use_private_networking = false) can never succeed here.'
      Write-Info 'Fix: set use_private_networking = true in terraform.tfvars (see infra/README.md) and re-run — this satisfies the policy and is the intended pattern for governed tenants anyway.'
      throw "Azure Policy blocked deployment (RequestDisallowedByPolicy) — see guidance above. Original error: $($_.Exception.Message)"
    }
    throw
  }
  Remove-Item -Path $planFile -ErrorAction SilentlyContinue
}

# ── PHASE: image (in-cloud build + wire) ─────────────────────────
function Phase-Image {
  if ($SkipImage) { Write-Info 'Image build skipped (-SkipImage).'; return }
  $createAcr = (Get-TfVar 'create_acr')
  if ($createAcr -ne 'true') { Write-Info 'create_acr not true — skipping in-cloud build (customer supplies container_image).'; return }
  Write-Step 4 'Build image in ACR + wire the Web App'
  $acr = if ($DryRun) { 'acrXXXX.azurecr.io' } else { Get-TfOutput 'acr_login_server' }
  if (-not $acr) { throw "acr_login_server output empty — is create_acr = true and infra applied?" }
  $acrName = $acr.Split('.')[0]
  $imageRef = "$acr/${ImageName}:${ImageTag}"
  Write-Info "ACR: $acr   image: $imageRef"
  Invoke-OrEcho "az acr build -r $acrName -t ${ImageName}:${ImageTag} `"$appDir`""
  $auto = Join-Path $infra 'image.auto.tfvars'
  if ($DryRun) { Write-Host "  DRYRUN> write $auto : container_image = `"$imageRef`"" -ForegroundColor DarkGray }
  else { Set-Content -Path $auto -Value "container_image = `"$imageRef`"" -NoNewline; Write-Ok "wrote $auto" }
  Push-Location $infra
  try { Invoke-TerraformApply "-var `"location=$Location`"" } finally { Pop-Location }
  Write-Ok 'image built + Web App wired'
}

# ── PHASE: grant SQL access (system_assigned) ────────────────────
function Phase-GrantSql {
  Write-Step 5 'Grant the app permission to create/update its own database tables in SQL'
  $grantHint = Get-TfOutput 'post_deploy_sql_grant'
  if ($grantHint -and $grantHint -match 'Not required') { Write-Ok 'Self-admin mode — no grant required (app applies migrations itself).'; return }
  $server = Get-TfOutput 'sql_server_fqdn'; $db = Get-TfOutput 'sql_database_name'; $app = Get-TfOutput 'web_app_name'
  if (-not ($server -and $db -and $app)) { Write-Warn 'Could not read SQL outputs (infra not applied?). Skipping grant.'; return }
  Write-Info "Server $server · DB $db · App MI $app"
  if (-not $script:Acct) { Ensure-Az }
  Write-Info "You must be the Entra SQL admin (or in the admin group) for this to succeed ($($script:Acct.user.name))."
  if (-not (AskYesNo 'Apply the SQL grant now?' $true)) { Write-Warn "Skipped. Later: ./deploy.ps1 -Task grant-sql"; return }

  $manualInstructions = "Run this against the $db DB as the Entra SQL admin (Portal -> SQL database -> Query editor, or any host with a path to the private endpoint):`n`n$(Get-TfOutput 'post_deploy_sql_grant')"

  # PUBLIC pattern: unchanged — the SQL firewall blocks your workstation by default; open the
  # AllowDeployerIP rule (persisted so later applies don't revert it), then run the grant directly.
  if ((Get-TfVar 'use_private_networking') -ne 'true') {
    $myIp = Get-MyPublicIp
    if ($myIp) {
      Write-Info "Opening SQL firewall for your current IP ($myIp) — AllowDeployerIP rule"
      $adminIpTfvars = Join-Path $infra 'adminip.auto.tfvars'
      if ($DryRun) { Write-Host "  DRYRUN> write $adminIpTfvars : admin_client_ip = `"$myIp`"" -ForegroundColor DarkGray }
      else { Set-Content -Path $adminIpTfvars -Value "admin_client_ip = `"$myIp`"" -NoNewline; Write-Ok "wrote $adminIpTfvars" }
      Push-Location $infra
      try { Invoke-TerraformApply "-var `"location=$Location`"" } finally { Pop-Location }
      if (-not $DryRun) { Write-Ok "Firewall rule applied for $myIp"; Write-Info 'Waiting ~20s for the firewall rule to take effect...'; Start-Sleep -Seconds 20 }
    }
    else { Write-Warn "Couldn't auto-detect your public IP — if the grant fails with a firewall error, add your IP via Portal or: terraform apply -var admin_client_ip=<your.ip.here>" }

    if (-not (Ensure-SqlServerModule)) { return }
    if ($DryRun) { Invoke-SqlGrantDirect -Server $server -Database $db -AppName $app; return }
    try { Invoke-SqlGrantDirect -Server $server -Database $db -AppName $app }
    catch {
      Write-Err "Grant failed: $($_.Exception.Message)"
      if ($_.Exception.Message -match 'is not allowed to access the server') { Write-Info "Firewall rule may not have propagated yet — wait a minute and re-run: ./deploy.ps1 -Task grant-sql" }
      else { Write-Info 'Usually means the signed-in identity is not the Entra SQL admin. Sign in as that admin and retry, or use Portal Query editor.' }
      throw
    }
    return
  }

  # PRIVATE pattern: offer the four access modes.
  $rg = Get-TfOutput 'resource_group'
  $sqlServerName = Get-TfOutput 'sql_server_name'
  $jumpboxVm = Get-TfOutput 'jumpbox_vm_name'
  $mode = Select-PrivateAccessMode -JumpboxAvailable:([bool]$jumpboxVm)

  if ($mode -eq 'Direct') {
    if (-not (Ensure-SqlServerModule)) { return }
    if ($DryRun) { Invoke-SqlGrantDirect -Server $server -Database $db -AppName $app; return }
    # Don't pre-check reachability with a TCP probe: Azure SQL's gateway accepts the TCP
    # connection and denies at the login/wire-protocol layer when public access is disabled
    # (same category as Key Vault/App Service — confirmed live in this environment: "Connection
    # was denied because Deny Public Network Access is set to Yes"), so a bare port-open check
    # would give a false "reachable" result. Attempt the real thing and interpret the outcome.
    Write-Info 'Trying direct access...'
    try {
      Invoke-SqlGrantDirect -Server $server -Database $db -AppName $app
      return
    } catch {
      if ($_.Exception.Message -match 'Deny Public Network Access|network-related|instance-specific') {
        Write-Warn "Direct access didn't work: public access is disabled and there's no path from here ($($_.Exception.Message))"
      } else {
        Write-Warn "Direct access failed: $($_.Exception.Message)"
      }
      $mode = Resolve-DirectAccessFallback -JumpboxAvailable:([bool]$jumpboxVm)
    }
  }

  switch ($mode) {
    'JumpBox' {
      if (-not $jumpboxVm) { Write-Warn 'No jump box available (enable_jumpbox=false).'; Show-ManualInstructionsAndMaybePause $manualInstructions; return }
      if ($DryRun) { Write-Host "  DRYRUN> az vm run-command create ... (SQL grant via jump box $jumpboxVm)" -ForegroundColor DarkGray; return }
      Invoke-SqlGrantViaJumpbox -ResourceGroup $rg -VmName $jumpboxVm -Server $server -Database $db -AppName $app
    }
    'TempPublic' {
      Invoke-SqlGrantViaTempPublicAccess -ResourceGroup $rg -SqlServerName $sqlServerName -Server $server -Database $db -AppName $app
    }
    'Manual' {
      Show-ManualInstructionsAndMaybePause $manualInstructions
    }
  }
}

# ── SRE Agent: read-only SQL grant for the agent's managed identity ──
# Mirrors Invoke-SqlGrantDirect but grants ONLY db_datareader + VIEW DATABASE STATE (DMVs / Query
# Store) — never write or DDL. The SQL user name is the agent's identity display name, which equals
# the agent resource name (sre-ghcp-<suffix>).
function Invoke-SreSqlGrantDirect([string]$Server, [string]$Database, [string]$AgentName) {
  $tsql = @"
DECLARE @app sysname = N'$($AgentName.Replace("'","''"))';
DECLARE @q sysname = QUOTENAME(@app);
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @app)
    EXEC('CREATE USER ' + @q + ' FROM EXTERNAL PROVIDER;');
IF IS_ROLEMEMBER('db_datareader', @app) = 0 EXEC('ALTER ROLE db_datareader ADD MEMBER ' + @q + ';');
EXEC('GRANT VIEW DATABASE STATE TO ' + @q + ';');
"@
  if ($DryRun) { Write-Host "  DRYRUN> Invoke-Sqlcmd against $Server/$Database with read-only agent grant" -ForegroundColor DarkGray; Write-Host $tsql -ForegroundColor DarkGray; return }
  $token = (az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv)
  Invoke-Sqlcmd -ServerInstance $Server -Database $Database -AccessToken $token -Query $tsql -ErrorAction Stop
  Write-Ok 'Agent SQL grant applied (db_datareader + VIEW DATABASE STATE, read-only).'
}

function Phase-GrantSreSql {
  Write-Step 9 'Grant the SRE Agent read-only access to the SQL database'
  $agent = Get-TfOutput 'sre_agent_name'
  if (-not $agent) { Write-Warn 'No SRE Agent in state (enable_sre_agent=false or not applied). Skipping.'; return }
  $server = Get-TfOutput 'sql_server_fqdn'; $db = Get-TfOutput 'sql_database_name'
  if (-not ($server -and $db)) { Write-Warn 'Could not read SQL outputs (infra not applied?). Skipping.'; return }
  Write-Info "Server $server · DB $db · Agent identity $agent"
  if (-not $script:Acct) { Ensure-Az }
  Write-Info "You must be the Entra SQL admin (or in the admin group) for this to succeed ($($script:Acct.user.name))."

  $manual = "Run this against the $db DB as the Entra SQL admin (Portal -> SQL database -> Query editor):`n`n" +
            "CREATE USER [$agent] FROM EXTERNAL PROVIDER;`nALTER ROLE db_datareader ADD MEMBER [$agent];`nGRANT VIEW DATABASE STATE TO [$agent];"

  # PUBLIC pattern: the AllowDeployerIP firewall rule from `-Task grant-sql` (or a fresh apply)
  # opens the path. If it isn't open, the direct attempt fails with a clear firewall error.
  if ((Get-TfVar 'use_private_networking') -ne 'true') {
    if (-not (Ensure-SqlServerModule)) { Write-Info $manual; return }
    if (-not (AskYesNo 'Apply the read-only agent grant now?' $true)) { Write-Warn 'Skipped. Later: ./deploy.ps1 -Task grant-sre-sql'; return }
    try { Invoke-SreSqlGrantDirect -Server $server -Database $db -AgentName $agent }
    catch {
      Write-Err "Grant failed: $($_.Exception.Message)"
      if ($_.Exception.Message -match 'is not allowed to access the server') { Write-Info 'Your IP may not be open on the SQL firewall — run ./deploy.ps1 -Task grant-sql first (it opens AllowDeployerIP), then re-run this.' }
      else { Write-Info $manual }
    }
    return
  }

  # PRIVATE pattern: the four-mode access flow is owned by grant-sql; for the read-only agent grant
  # we print the T-SQL and let the operator run it via whichever private path they already use.
  Write-Warn 'Private networking: run the agent grant from a host on the VNet (jump box, or the same path you used for -Task grant-sql).'
  Write-Info $manual
}

# Ensures the powershell-yaml module (ConvertFrom-Yaml) is available for parsing sre/ config.
# Same install-on-demand pattern as Ensure-SqlServerModule.
function Ensure-YamlModule {
  if (Get-Command ConvertFrom-Yaml -ErrorAction SilentlyContinue) { return $true }
  if (AskYesNo 'Syncing SRE config needs the powershell-yaml module (ConvertFrom-Yaml). Install it now (CurrentUser scope)?' $true) {
    try {
      Install-Module powershell-yaml -Scope CurrentUser -Force -AllowClobber -ErrorAction Stop
      Import-Module powershell-yaml -ErrorAction Stop
      return $true
    } catch { Write-Warn "Couldn't install powershell-yaml: $($_.Exception.Message)"; return $false }
  }
  return $false
}

# PUT one config item to the agent's data plane. Envelope (from the official microsoft/sre-agent
# apply-extras.sh): { name, type, tags: [], properties: <spec> }. Route:
#   PUT {endpoint}/api/v2/extendedAgent/{kind}/{name}
function Invoke-SreDataPlanePut([string]$Endpoint, [string]$Token, [string]$Kind, [string]$Name, [string]$Type, $Properties) {
  $body = @{ name = $Name; type = $Type; tags = @(); properties = $Properties } | ConvertTo-Json -Depth 40
  $url = "$Endpoint/api/v2/extendedAgent/$Kind/" + [uri]::EscapeDataString($Name)
  if ($DryRun) { Write-Host "  DRYRUN> PUT $url" -ForegroundColor DarkGray; return }
  try {
    Invoke-RestMethod -Method Put -Uri $url -Headers @{ Authorization = "Bearer $Token" } -ContentType 'application/json' -Body $body -ErrorAction Stop | Out-Null
    Write-Ok "  $Kind/$Name"
  } catch {
    Write-Err "  FAILED $Kind/$Name — $($_.Exception.Message)"
  }
}

# Upload ONE knowledge file to AgentMemory (RAG-indexed) via multipart/form-data.
#   POST {endpoint}/api/v1/AgentMemory/upload?triggerIndexing={bool}   field: files=@<path>
# Only trigger indexing on the LAST file of a batch, so we don't re-index per file.
function Invoke-SreKnowledgeUpload([string]$Endpoint, [string]$Token, [string]$Path, [bool]$TriggerIndexing) {
  $name = Split-Path $Path -Leaf
  $url = "$Endpoint/api/v1/AgentMemory/upload?triggerIndexing=" + $TriggerIndexing.ToString().ToLower()
  if ($DryRun) { Write-Host "  DRYRUN> POST $url  ($name)" -ForegroundColor DarkGray; return }
  try {
    Invoke-RestMethod -Method Post -Uri $url -Headers @{ Authorization = "Bearer $Token" } -Form @{ files = Get-Item -LiteralPath $Path } -ErrorAction Stop | Out-Null
    Write-Ok "  knowledge/$name"
  } catch {
    Write-Err "  FAILED knowledge/$name — $($_.Exception.Message)"
  }
}

# ── SRE Agent: sync data-plane config (skills / custom agents / hooks) from sre/ ──
# Only the agent itself is an ARM resource; skills, custom agents, and hooks are data-plane config
# (route /api/v2/extendedAgent/*). This PUTs them directly via the data-plane API. Auth is a token
# for audience https://azuresre.dev — you need the SRE Agent Administrator role on the agent. If the
# endpoint or token isn't available, it degrades gracefully with guidance rather than failing.
function Phase-SreSync {
  Write-Step 10 'Sync SRE Agent config (skills / agents / hooks / knowledge)'
  $agentId = Get-TfOutput 'sre_agent_id'
  $agent = Get-TfOutput 'sre_agent_name'
  if (-not $agentId) { Write-Warn 'No SRE Agent in state (enable_sre_agent=false or not applied). Skipping.'; return }
  $sreDir = Join-Path $PSScriptRoot 'sre'
  if (-not (Test-Path $sreDir)) { Write-Warn "sre/ config directory not found at $sreDir."; return }

  # Resolve the data-plane endpoint (from ARM) and a data-plane token (audience azuresre.dev).
  Write-Info "Resolving data-plane endpoint for $agent ..."
  $armUrl = "https://management.azure.com" + $agentId + "?api-version=2025-05-01-preview"
  $endpoint = (az rest -m GET --url $armUrl --query properties.agentEndpoint -o tsv 2>$null)
  if ($endpoint) { $endpoint = $endpoint.Trim().TrimEnd('/') }
  if (-not $endpoint) { Write-Warn 'Could not resolve the agent data-plane endpoint (still provisioning?). Re-run in a minute: ./deploy.ps1 -Task sre-sync'; return }

  $token = (az account get-access-token --resource https://azuresre.dev --query accessToken -o tsv 2>$null)
  if ($token) { $token = $token.Trim() }
  if (-not $token) {
    Write-Warn "Couldn't get a data-plane token (audience https://azuresre.dev). You likely need the 'SRE Agent Administrator' role on the agent, or the tenant blocks this audience."
    Write-Info  "Grant it: az role assignment create --assignee <you> --role 'SRE Agent Administrator' --scope $agentId ; then re-run ./deploy.ps1 -Task sre-sync"
    return
  }

  # Preflight the data-plane RBAC: acquiring a token only proves you signed in, NOT that you can
  # write to the agent. Subscription Owner is NOT sufficient — the data plane needs the dedicated
  # 'SRE Agent Administrator' role on the agent. Check once so we fail with one actionable message
  # instead of a 403 per item.
  try {
    Invoke-RestMethod -Method Get -Uri "$endpoint/api/v2/extendedAgent/skills" -Headers @{ Authorization = "Bearer $token" } -ErrorAction Stop | Out-Null
  } catch {
    if ($_.Exception.Response -and $_.Exception.Response.StatusCode.value__ -eq 403) {
      $meId = az ad signed-in-user show --query id -o tsv 2>$null
      Write-Warn "Data-plane access denied (403). Being subscription Owner is NOT enough — the agent needs the 'SRE Agent Administrator' role."
      Write-Info "Grant it, then re-run ./deploy.ps1 -Task sre-sync :"
      Write-Host "    az role assignment create --assignee $meId --role `"SRE Agent Administrator`" --scope $agentId" -ForegroundColor DarkGray
      Write-Info 'RBAC can take up to a couple of minutes to propagate after you assign it.'
      return
    }
    # Any other error (e.g. endpoint still warming up): fall through and let per-item calls report it.
  }

  if (-not (Ensure-YamlModule)) { Write-Warn 'YAML module unavailable — cannot parse sre/ config. Skipping.'; return }

  # Skills: sre/skills/<name>/skill.yaml (name, description, tools) + SKILL.md -> skillContent.
  $skillDirs = @(Get-ChildItem -Path (Join-Path $sreDir 'skills') -Directory -ErrorAction SilentlyContinue)
  if ($skillDirs.Count -gt 0) {
    Write-Info "Skills: $($skillDirs.Count)"
    foreach ($d in $skillDirs) {
      $manifestPath = Join-Path $d.FullName 'skill.yaml'
      if (-not (Test-Path $manifestPath)) { continue }
      $m = (Get-Content $manifestPath -Raw) | ConvertFrom-Yaml
      $mdPath = Join-Path $d.FullName 'SKILL.md'
      $content = if (Test-Path $mdPath) { Get-Content $mdPath -Raw } else { '' }
      $props = @{ name = $m.name; description = $m.description; tools = @($m.tools); skillContent = $content; additionalFiles = @() }
      Invoke-SreDataPlanePut -Endpoint $endpoint -Token $token -Kind 'skills' -Name $m.name -Type 'Skill' -Properties $props
    }
  }

  # Custom agents (subagents): route is /agents/{name}, type ExtendedAgent, properties = spec.
  $agentFiles = @(Get-ChildItem -Path (Join-Path $sreDir 'agents') -Filter *.yaml -ErrorAction SilentlyContinue)
  if ($agentFiles.Count -gt 0) {
    Write-Info "Custom agents: $($agentFiles.Count)"
    foreach ($f in $agentFiles) {
      $y = (Get-Content $f.FullName -Raw) | ConvertFrom-Yaml
      Invoke-SreDataPlanePut -Endpoint $endpoint -Token $token -Kind 'agents' -Name $y.metadata.name -Type 'ExtendedAgent' -Properties $y.spec
    }
  }

  # Hooks: route /hooks/{name}, type GlobalHook, properties = spec.
  $hookFiles = @(Get-ChildItem -Path (Join-Path $sreDir 'hooks') -Filter *.yaml -ErrorAction SilentlyContinue)
  if ($hookFiles.Count -gt 0) {
    Write-Info "Hooks: $($hookFiles.Count)"
    foreach ($f in $hookFiles) {
      $y = (Get-Content $f.FullName -Raw) | ConvertFrom-Yaml
      Invoke-SreDataPlanePut -Endpoint $endpoint -Token $token -Kind 'hooks' -Name $y.metadata.name -Type 'GlobalHook' -Properties $y.spec
    }
  }

  # Knowledge files: repo docs listed in sre/knowledge/files.txt -> AgentMemory (RAG-indexed).
  # Paths are repo-relative and may be globs; indexing fires only after the final file.
  $manifest = Join-Path $sreDir 'knowledge/files.txt'
  if (Test-Path $manifest) {
    $paths = [System.Collections.Generic.List[string]]::new()
    foreach ($line in Get-Content $manifest) {
      $t = $line.Trim()
      if (-not $t -or $t.StartsWith('#')) { continue }
      foreach ($r in @(Get-ChildItem -Path (Join-Path $PSScriptRoot $t) -File -ErrorAction SilentlyContinue)) {
        if ($r.Length -gt 16MB) { Write-Warn "  skipping $($r.Name) — exceeds the 16 MB/file cap"; continue }
        $paths.Add($r.FullName)
      }
    }
    if ($paths.Count -gt 0) {
      Write-Info "Knowledge files: $($paths.Count)"
      for ($i = 0; $i -lt $paths.Count; $i++) {
        Invoke-SreKnowledgeUpload -Endpoint $endpoint -Token $token -Path $paths[$i] -TriggerIndexing:($i -eq $paths.Count - 1)
      }
      Write-Info 'Indexing runs after the final file — progress: GET {endpoint}/api/v1/agentmemory/indexer-status'
    }
  }

  Write-Ok 'SRE config sync complete (skills, agents, hooks, knowledge).'
  Write-Info "Open the agent: https://sre.azure.com"
}

# ── PHASE: seed GitHub PAT into Key Vault (real data) ────────────
function Phase-SetPat {
  Write-Step 6 'Seed the GitHub PAT into Key Vault (real-data mode)'
  $mock = Get-TfVar 'use_mock_data'
  if ($mock -eq 'true') { Write-Ok 'Mock data mode — no PAT needed. Skipping.'; return }
  $kv = Get-TfOutput 'key_vault_name'
  if (-not $kv) { Write-Warn 'key_vault_name output not available (infra not applied?). Skipping.'; return }
  Write-Info "Key Vault: $kv  (secret name: github-pat)"
  if (-not (AskYesNo 'Set the GitHub PAT secret now?' $true)) { Write-Warn "Skipped. Later: ./deploy.ps1 -Task set-pat  (or az keyvault secret set)"; return }

  $manualInstructions = "Run this from any host with a path to the Key Vault private endpoint:`n`n  az keyvault secret set --vault-name $kv --name github-pat --value <PAT>"

  # PUBLIC pattern: unchanged.
  if ((Get-TfVar 'use_private_networking') -ne 'true') {
    if ($DryRun) { Invoke-PatSetDirect -VaultName $kv -PatValue '<PAT>'; return }
    $pat = Read-PatSecurely
    if (-not $pat) { Write-Warn 'Empty PAT — skipped.'; return }
    Invoke-PatSetDirect -VaultName $kv -PatValue $pat
    return
  }

  # PRIVATE pattern: offer the four access modes BEFORE asking for the PAT value — Manual doesn't
  # need this script to hold the value at all.
  $rg = Get-TfOutput 'resource_group'
  $jumpboxVm = Get-TfOutput 'jumpbox_vm_name'
  $mode = Select-PrivateAccessMode -JumpboxAvailable:([bool]$jumpboxVm)
  $pat = $null

  if ($mode -eq 'Direct') {
    if ($DryRun) { Invoke-PatSetDirect -VaultName $kv -PatValue '<PAT>'; return }
    # Same reasoning as the SQL grant: Key Vault's shared front-end accepts the TCP/TLS
    # connection and denies at the HTTP layer (403) when public access is disabled — a bare
    # port-open check can't detect that, so attempt the real write and interpret the outcome.
    $pat = Read-PatSecurely
    if (-not $pat) { Write-Warn 'Empty PAT — skipped.'; return }
    Write-Info 'Trying direct access...'
    try {
      Invoke-PatSetDirect -VaultName $kv -PatValue $pat
      return
    } catch {
      if ($_.Exception.Message -match 'Public network access is disabled|Forbidden|403') {
        Write-Warn "Direct access didn't work: public access is disabled and there's no path from here ($($_.Exception.Message))"
      } else {
        Write-Warn "Direct access failed: $($_.Exception.Message)"
      }
      $mode = Resolve-DirectAccessFallback -JumpboxAvailable:([bool]$jumpboxVm)
    }
  }

  switch ($mode) {
    'JumpBox' {
      if (-not $jumpboxVm) { Write-Warn 'No jump box available (enable_jumpbox=false).'; Show-ManualInstructionsAndMaybePause $manualInstructions; return }
      if ($DryRun) { Write-Host "  DRYRUN> az vm run-command create ... (set PAT via jump box $jumpboxVm)" -ForegroundColor DarkGray; return }
      if (-not $pat) { $pat = Read-PatSecurely; if (-not $pat) { Write-Warn 'Empty PAT — skipped.'; return } }
      Invoke-PatSetViaJumpbox -ResourceGroup $rg -VmName $jumpboxVm -VaultName $kv -PatValue $pat -IdentityClientId (Get-TfOutput 'jumpbox_identity_client_id')
    }
    'TempPublic' {
      if ($DryRun) { Invoke-PatSetViaTempPublicAccess -ResourceGroup $rg -VaultName $kv -PatValue '<PAT>'; return }
      if (-not $pat) { $pat = Read-PatSecurely; if (-not $pat) { Write-Warn 'Empty PAT — skipped.'; return } }
      Invoke-PatSetViaTempPublicAccess -ResourceGroup $rg -VaultName $kv -PatValue $pat
    }
    'Manual' {
      Show-ManualInstructionsAndMaybePause $manualInstructions
    }
  }
}

function Invoke-HealthCheckViaJumpbox([string]$ResourceGroup, [string]$VmName, [string]$Url) {
  # Runs the health checks FROM the jump box (inside the VNet) instead of your workstation.
  # On a private deployment the web app has public_network_access_enabled = false, so hitting
  # its public hostname from outside the VNet returns a platform-level 403 (Azure denying the
  # request at the front door, before Easy Auth or the app ever see it) — not a real health
  # signal. The script always exits 0 and prints both status codes (even non-200 ones, e.g.
  # while migrations are still warming up) so Phase-Status can report them accurately.
  $script = @'
param([string]$BaseUrl)
$ErrorActionPreference = 'SilentlyContinue'
function Get-Code([string]$Path) {
  try { (Invoke-WebRequest -Uri "$BaseUrl$Path" -UseBasicParsing -TimeoutSec 20).StatusCode }
  catch { if ($_.Exception.Response) { $_.Exception.Response.StatusCode.value__ } else { "no-response" } }
}
# /health/ready can take up to a couple of minutes to come up on the very first hit after a
# deploy (cold-start DB connection/pool warm-up). Retry here (inside the single jumpbox
# run-command invocation) rather than reporting a false failure back to Phase-Status.
# Worst case: 8 * 20s timeout + 7 * 5s sleeps = ~195s (~3.3 min) of headroom.
$readyCode = 'no-response'
for ($i = 1; $i -le 8; $i++) {
  $readyCode = Get-Code '/health/ready'
  if ($readyCode -eq 200) { break }
  Start-Sleep -Seconds 5
}
Write-Output "HEALTH_LIVE=$(Get-Code '/health/live')"
Write-Output "HEALTH_READY=$readyCode"
Write-Output "HEALTH_CHECK_DONE"
'@
  $out = Invoke-JumpboxRunCommand -ResourceGroup $ResourceGroup -VmName $VmName -NamePrefix 'health-check' -ScriptBody $script `
    -Parameters @("BaseUrl=$Url") -ProtectedParameters @() -SuccessMarker 'HEALTH_CHECK_DONE'
  $live = if ($out -match 'HEALTH_LIVE=(\S+)') { $Matches[1] } else { 'unknown' }
  $ready = if ($out -match 'HEALTH_READY=(\S+)') { $Matches[1] } else { 'unknown' }
  return @{ Live = $live; Ready = $ready }
}

# ── PHASE: status / health ───────────────────────────────────────
function Phase-Status {
  Write-Step 7 'Status + health'
  $url = Get-TfOutput 'web_app_url'
  if (-not $url) { Write-Warn 'web_app_url not available (infra not applied?).'; return }
  Write-Ok "URL: $url"
  $isPrivate = (Get-TfVar 'use_private_networking') -eq 'true'

  if ($isPrivate) {
    # IMPORTANT: unlike SQL/Key Vault (which genuinely REFUSE the TCP connection when public
    # access is disabled — a raw TCP probe correctly detects that), Azure's front-end for
    # *.azurewebsites.net always accepts the TCP/TLS connection globally regardless of the app's
    # own public-access setting. The "disabled" enforcement happens one layer higher, at the HTTP
    # level — it returns a platform 403 instead of refusing the connection. A TCP-only probe would
    # therefore report "reachable" even when it isn't really usable — so for the web app
    # specifically we have to attempt the actual HTTP request and treat a 403 as NOT reachable.
    Write-Info "Checking direct reachability to $url ..."
    $directCode = $null
    try { $directCode = (Invoke-WebRequest -Uri "$url/health/live" -UseBasicParsing -TimeoutSec 10).StatusCode }
    catch { if ($_.Exception.Response) { $directCode = $_.Exception.Response.StatusCode.value__ } }

    if ($directCode -and $directCode -ne 403) {
      Write-Ok 'Reachable directly — checking from here.'
    }
    else {
      if ($directCode -eq 403) { Write-Info "Got a platform-level 403 from here (expected — public access to the app is disabled by design in private mode; this is not a real health check result)." }
      else { Write-Info 'No direct response from here.' }
      $jumpboxVm = Get-TfOutput 'jumpbox_vm_name'
      if ($jumpboxVm) {
        Write-Info "Checking health from the jump box instead."
        try {
          $health = Invoke-HealthCheckViaJumpbox -ResourceGroup (Get-TfOutput 'resource_group') -VmName $jumpboxVm -Url $url
          foreach ($check in @(@('/health/live', $health.Live), @('/health/ready', $health.Ready))) {
            if ($check[1] -eq '200') { Write-Ok "$($check[0]) → 200" }
            else { Write-Warn "$($check[0]) → $($check[1]) (still warming up: grant/migrations, or DNS if just deployed)" }
          }
        } catch {
          Write-Warn "Couldn't run the health check via the jump box: $($_.Exception.Message)"
        }
        return
      }
      Write-Warn "Can't verify health from here — no direct path and no jump box (enable_jumpbox=false)."
      Write-Info "This is NOT a health verdict — it doesn't mean anything is wrong. It just means this workstation has no network path to check (public access to the app is disabled by design in private mode, so a 403 from here is expected and tells you nothing about the app's real state)."
      Write-Info 'To actually verify: temporarily set enable_jumpbox = true and re-apply, then re-run `-Task status` (it will route through the jump box automatically) — or check from any other host that already has a path into the VNet (VPN/ExpressRoute/existing bastion).'
      return
    }
  }

  $hint = if ($isPrivate) { '503 = still warming up: grant/migrations' } else { '503 = still warming up: grant/migrations (not DNS — this is the PUBLIC pattern)' }
  foreach ($p in '/health/live', '/health/ready') {
    # /health/ready can take up to a couple of minutes to come up on the very first hit after a
    # deploy (cold-start DB connection/pool warm-up), which otherwise gets misreported as a
    # failure. Retry with a short backoff before giving up — worst case here is 8 * 20s timeout +
    # 7 * 5s sleeps = ~195s (~3.3 min) of headroom. /health/live is expected to be instant, so it
    # stays single-shot.
    $maxAttempts = if ($p -eq '/health/ready') { 8 } else { 1 }
    $delaySec = 5
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
      try {
        $r = Invoke-WebRequest "$url$p" -UseBasicParsing -TimeoutSec 20
        Write-Ok "$p → $($r.StatusCode)$(if ($attempt -gt 1) { " (after $attempt attempts)" })"
        break
      }
      catch {
        $code = $_.Exception.Response.StatusCode.value__
        $isLast = ($attempt -eq $maxAttempts)
        if (-not $isLast) {
          Write-Info "$p → $(if ($code) { $code } else { 'no response' }) (attempt $attempt/$maxAttempts, retrying in ${delaySec}s — likely still warming up)"
          Start-Sleep -Seconds $delaySec
          continue
        }
        if ($code) { Write-Warn "$p → $code ($hint)" }
        else { Write-Warn "$p → no response ($($_.Exception.Message))" }
      }
    }
  }
}

# ── dispatcher ───────────────────────────────────────────────────
Write-Banner "GHCP AI Credit Visibility — deploy  (task: $Task)"
if ($DryRun) { Write-Host '  (DryRun: no changes will be made)' -ForegroundColor Magenta }

trap {
  Write-Host "`n[failed] $($_.Exception.Message)" -ForegroundColor Red
  Write-Host "Common constrained-subscription causes:" -ForegroundColor Yellow
  Write-Host "  - App Service quota = 0     → https://aka.ms/antquotahelp, or ./deploy.ps1 -Task preflight -Location <region> -Sku <sku>"
  Write-Host "  - Azure SQL disabled in region → re-run with -Location <other-region>"
  Write-Host "  - Easy Auth SP/secret 403  → deploy where you're tenant admin, or set enable_easy_auth=false"
  Write-Host "  - Policy blocks public network access (RequestDisallowedByPolicy) → set use_private_networking = true and re-run (common in governed/landing-zone tenants)"
  break
}

# Tasks that operate on ALREADY-provisioned infra sync $Location from the existing
# terraform.tfvars first, so we never pass a stale/default -var "location=..." that could
# force Terraform to REPLACE the resource group (and everything in it) into the wrong region.
# 'configure' sets the region itself (interactively); 'all' syncs it below after Phase-Configure;
# every other task must sync here. 'provision' is included: Phase-Provision requires an existing
# terraform.tfvars, so tfvars — not the -Location parameter default (eastus2) — is the source of
# truth for its region. (Standalone -Task provision/image/grant-sql/set-pat/status previously did
# not sync, so `deploy.ps1 -Task provision` on a non-default region planned a destroy/recreate into
# eastus2 — bug fix.)
if ($Task -in @('provision', 'image', 'grant-sql', 'set-pat', 'status', 'sre-provision', 'grant-sre-sql', 'sre-sync')) {
  $existingLoc = Get-TfVar 'location'
  if ($existingLoc) { $Location = $existingLoc }
}

# Decide whether to hold the OPTIONAL SRE agent out of core-phase terraform applies. We hold it out
# only when it's enabled AND not yet created (fresh deploy) — so a failure in the preview agent API
# can never block the app phases (provision/image/grant-sql). Once the agent exists in state we stop
# holding it, so there's no destroy/recreate churn. Phase-SreProvision (task 'sre-provision', run
# last in 'all') clears this flag and is the only apply that includes the agent.
$script:HoldSreAgent = $false
if ($Task -in @('all', 'provision', 'image', 'grant-sql', 'set-pat') -and (Get-TfVar 'enable_sre_agent') -eq 'true') {
  $agentInState = $null
  Push-Location $infra
  try { $agentInState = terraform state list 2>$null | Select-String -Pattern 'azapi_resource\.sre_agent' } catch {}
  finally { Pop-Location }
  if (-not $agentInState) { $script:HoldSreAgent = $true }
}

switch ($Task) {
  'preflight' { Ensure-Az; Phase-Preflight }
  'configure' { Phase-Configure }
  'provision' { Phase-Prereqs; Phase-Provision }
  'image'     { Ensure-Az; Phase-Image }
  'grant-sql'     { Ensure-Az; Phase-GrantSql }
  'set-pat'       { Ensure-Az; Phase-SetPat }
  'status'        { Phase-Status }
  'sre-provision' { Ensure-Az; Phase-SreProvision }
  'grant-sre-sql' { Ensure-Az; Phase-GrantSreSql }
  'sre-sync'      { Ensure-Az; Phase-SreSync }
  'all' {
    $script:InAllSequence = $true
    Phase-Prereqs
    # Configure FIRST — collects the real region/SKU/settings choices — so the gating preflight
    # check below reflects what was actually chosen, not a stale/default param on a fresh machine.
    Phase-Configure
    $tfLoc = Get-TfVar 'location'; if ($tfLoc) { $Location = $tfLoc }
    Phase-Preflight -Gate
    # ── Core app: everything the app needs, in order. The optional SRE agent is deliberately NOT
    #    here — it runs LAST (below), so a failure in the preview agent API can never abort these.
    Phase-Provision
    Phase-Image
    Phase-GrantSql
    Phase-SetPat
    Phase-Status
    # ── Optional SRE Agent add-on — LAST, after the app is deployed and health-checked. All three
    #    self-skip when enable_sre_agent != true, and Phase-SreProvision is non-fatal on failure.
    Phase-SreProvision
    Phase-GrantSreSql
    Phase-SreSync
    Write-Banner 'Done'
    Write-Info 'Sign in with your Microsoft account when redirected. Admin console appears if you were granted the Admin role.'
    Write-Info 'Tear down when finished:  cd infra ; terraform destroy'
  }
}
