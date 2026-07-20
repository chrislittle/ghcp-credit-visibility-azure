#requires -Version 7.0
<#
.SYNOPSIS
  GHCP AI Credit Visibility — one unified, interactive deploy & operations script.

  Runs the whole journey end-to-end (guided, colorized, with confirmations), or a single task:
    prereqs → preflight → configure (tfvars) → provision (terraform) → build+deploy image
            → grant SQL access → seed GitHub PAT → status/health.

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
.PARAMETER Force      Overwrite existing tfvars during configure (a .bak is kept).
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
  [ValidateSet('all', 'preflight', 'configure', 'provision', 'image', 'grant-sql', 'set-pat', 'status')]
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
  $out = Invoke-Expression $cmd
  if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "Command failed (exit $LASTEXITCODE): $cmd" }
  return $out
}
function Ask([string]$p, [string]$d) { $s = if ($d) { " [$d]" } else { "" }; $a = Read-Host "  $p$s"; if ([string]::IsNullOrWhiteSpace($a)) { $d } else { $a.Trim() } }
function AskYesNo([string]$p, [bool]$d) { if ($Yes) { return $d }; $h = if ($d) { 'Y/n' } else { 'y/N' }; $a = Read-Host "  $p [$h]"; if ([string]::IsNullOrWhiteSpace($a)) { $d } else { $a -match '^[Yy]' } }
function AskChoice([string]$p, [string[]]$opts, [int]$d = 1) {
  Write-Host "  $p" -ForegroundColor White
  for ($i = 0; $i -lt $opts.Count; $i++) { Write-Host ("     {0}) {1}" -f ($i + 1), $opts[$i]) -ForegroundColor Gray }
  if ($Yes) { return $d }
  $a = Read-Host "  choose 1-$($opts.Count) [$d]"; if ([string]::IsNullOrWhiteSpace($a)) { return $d }
  $n = 0; if ([int]::TryParse($a, [ref]$n) -and $n -ge 1 -and $n -le $opts.Count) { $n } else { $d }
}
function Get-TfOutput([string]$n) { try { $v = (terraform -chdir="$infra" output -raw $n 2>$null); if ($LASTEXITCODE -eq 0 -and $v) { return $v.Trim() } } catch {}; return $null }
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
function Ensure-Az {
  if (-not (Get-Command az -ErrorAction SilentlyContinue)) { throw "Azure CLI (az) not found. Install it, then run: az login" }
  $script:Acct = (az account show 2>$null | ConvertFrom-Json)
  if (-not $script:Acct) { throw "Not signed in. Run: az login" }
  Write-Ok "az context: $($script:Acct.name)  (sub $($script:Acct.id), tenant $($script:Acct.tenantId))"
  try { $script:Me = (az ad signed-in-user show --query "{id:id,upn:userPrincipalName,name:displayName}" -o json 2>$null | ConvertFrom-Json) } catch {}
}

# ── PHASE: prereqs ───────────────────────────────────────────────
function Phase-Prereqs {
  Write-Step 0 'Prerequisites'
  foreach ($t in 'az', 'terraform') { if (-not (Get-Command $t -ErrorAction SilentlyContinue)) { throw "$t not found on PATH." }; Write-Ok "$t present" }
  if (-not (Test-Path (Join-Path $appDir 'Dockerfile'))) { throw "App/Dockerfile not found at $appDir" }
  Ensure-Az
  if (-not $DryRun -and -not $Yes) { if ((Read-Host "  Proceed against THIS subscription? (y/N)") -ne 'y') { throw 'Aborted by user.' } }
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
    try {
      $uErr = $null
      $u = az rest --method get --uri "https://management.azure.com/subscriptions/$sub/providers/Microsoft.Web/locations/$loc/usages?api-version=2023-12-01" 2>&1 | Tee-Object -Variable uRaw | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ConvertFrom-Json -ErrorAction SilentlyContinue
      if ($u.value) {
        $byTier = @{}
        foreach ($e in $u.value) {
          $t = $e.name.localizedValue; if (-not $t) { $t = $e.name.value }
          $lim = [int]$e.limit; $cur = [int]$e.currentValue
          if (-not $byTier.ContainsKey($t) -or $lim -gt $byTier[$t].Limit) { $byTier[$t] = @{ Limit = $lim; Current = $cur } }
        }
        $availTiers = ($byTier.GetEnumerator() | Where-Object { $_.Value.Limit -gt 0 } | ForEach-Object { $_.Key })
        $wantLimit = if ($byTier.ContainsKey($wantTier)) { $byTier[$wantTier].Limit } else { 0 }
        if ($wantLimit -gt 0) { $appOk = $true; Write-Ok "App Service tier '$wantTier' has quota ($wantLimit cores)" }
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
      # Cross-check that API directly for the exact SKU name before declaring no quota.
      $qLimit = 0
      try {
        $qRaw = $null
        $q = az rest --method get --uri "https://management.azure.com/subscriptions/$sub/providers/Microsoft.Web/locations/$loc/providers/Microsoft.Quota/quotas/$resolvedSku`?api-version=2023-02-01" 2>&1 | Tee-Object -Variable qRaw | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ConvertFrom-Json -ErrorAction SilentlyContinue
        if ($q.properties.limit.value) { $qLimit = [int]$q.properties.limit.value }
      } catch {}
      if ($qLimit -gt 0) {
        $appOk = $true
        Write-Ok "App Service SKU '$resolvedSku' has quota via Microsoft.Quota ($qLimit) — legacy usages API hasn't caught up yet"
      }
      else {
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
  $sqlDbSku = Ask 'Azure SQL DB SKU (e.g. GP_S_Gen5_1 serverless, GP_Gen5_2 provisioned)' $sqlSkuDefault

  $identityMode = @('user_assigned_selfadmin', 'system_assigned')[(AskChoice 'Identity model' @(
        'user_assigned_selfadmin  — TEST: app identity is its own SQL admin (no grant)',
        'system_assigned          — CUSTOMER/PROD: external Entra SQL admin + one-time grant') 1) - 1]

  $private = AskYesNo 'Private networking (VNet + private endpoints)? No = public (browsable)' $false
  $createZones = $false
  $customNetwork = $false
  $vnetSpace = ''; $subnetPe = ''; $subnetApp = ''
  $existingVnetRg = ''; $existingVnetName = ''; $existingSubnetPe = ''; $existingSubnetApp = ''
  if ($private) {
    $createZones = AskYesNo 'Isolated demo sub with NO central DNS policy? (Yes = create local DNS zones)' $false
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

  $lines = [System.Collections.Generic.List[string]]::new()
  $lines.Add("subscription_id           = `"$subId`"")
  $lines.Add("location                  = `"$loc`"")
  $lines.Add("name_prefix               = `"$prefix`"")
  $lines.Add("app_service_sku           = `"$appSku`"")
  $lines.Add("sql_database_sku          = `"$sqlDbSku`"")
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
  $content = ($lines -join "`n") + "`n"

  Write-Host ''; Write-Info 'Review terraform.tfvars:'; Write-Host $content -ForegroundColor DarkGray
  if (-not (AskYesNo 'Write this terraform.tfvars?' $true)) { throw 'Aborted — nothing written.' }
  if ($DryRun) { Write-Host "  DRYRUN> (would write $tfvars)" -ForegroundColor DarkGray; return }
  if (Test-Path $tfvars) { Copy-Item $tfvars "$tfvars.bak" -Force; Write-Info 'Backed up → terraform.tfvars.bak' }
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
    Invoke-TerraformApply "-var `"location=$Location`""
  } finally { Pop-Location }
  Write-Ok 'infrastructure applied'
}

# Runs `terraform plan` to a saved plan file, shows it, and asks for confirmation before
# applying — unless -Yes/-DryRun is set. Caller must already be in the terraform working dir.
# $extraArgs is a string of extra -var/-var-file args appended to both plan and apply.
function Invoke-TerraformApply([string]$extraArgs = '') {
  if ($DryRun) { Write-Host "  DRYRUN> terraform plan -out=tfplan $extraArgs; terraform apply tfplan" -ForegroundColor DarkGray; return }
  $planFile = 'tfplan'
  Invoke-OrEcho "terraform plan -input=false -out=$planFile $extraArgs"
  if (-not $Yes) {
    Write-Host ''
    if (-not (AskYesNo 'Apply the plan shown above?' $true)) { Remove-Item -Path $planFile -ErrorAction SilentlyContinue; throw 'Aborted by user before terraform apply.' }
  }
  Invoke-OrEcho "terraform apply -input=false $planFile"
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
  Write-Step 5 'Grant the app identity access to SQL (for EF migrations)'
  $grantHint = Get-TfOutput 'post_deploy_sql_grant'
  if ($grantHint -and $grantHint -match 'Not required') { Write-Ok 'Self-admin mode — no grant required (app applies migrations itself).'; return }
  $server = Get-TfOutput 'sql_server_fqdn'; $db = Get-TfOutput 'sql_database_name'; $app = Get-TfOutput 'web_app_name'
  if (-not ($server -and $db -and $app)) { Write-Warn 'Could not read SQL outputs (infra not applied?). Skipping grant.'; return }
  Write-Info "Server $server · DB $db · App MI $app"
  if (-not $script:Acct) { Ensure-Az }
  Write-Info "You must be the Entra SQL admin (or in the admin group) for this to succeed ($($script:Acct.user.name))."
  if (-not (AskYesNo 'Apply the SQL grant now?' $true)) { Write-Warn "Skipped. Later: ./deploy.ps1 -Task grant-sql"; return }

  # PUBLIC pattern only: the SQL server firewall blocks your workstation by default. Detect your
  # current public IP and apply the AllowDeployerIP rule (infra/sql.tf) before running T-SQL.
  if ((Get-TfVar 'use_private_networking') -ne 'true') {
    $myIp = $null
    try { $myIp = (Invoke-RestMethod -Uri 'https://api.ipify.org?format=json' -TimeoutSec 10).ip } catch {}
    if (-not $myIp) { try { $myIp = (Invoke-RestMethod -Uri 'https://ifconfig.me/ip' -TimeoutSec 10).Trim() } catch {} }
    if ($myIp) {
      Write-Info "Opening SQL firewall for your current IP ($myIp) — AllowDeployerIP rule"
      # Persist to an .auto.tfvars file (same pattern as image.auto.tfvars) so this rule
      # SURVIVES any later `terraform apply` run by other tasks (image/provision/all).
      # Passing it only as an ephemeral -var here meant the very next unrelated apply
      # (e.g. `-Task image`) would silently revert admin_client_ip to its default ("")
      # and destroy the firewall rule out from under you.
      $adminIpTfvars = Join-Path $infra 'adminip.auto.tfvars'
      if ($DryRun) { Write-Host "  DRYRUN> write $adminIpTfvars : admin_client_ip = `"$myIp`"" -ForegroundColor DarkGray }
      else { Set-Content -Path $adminIpTfvars -Value "admin_client_ip = `"$myIp`"" -NoNewline; Write-Ok "wrote $adminIpTfvars" }
      Push-Location $infra
      try { Invoke-TerraformApply "-var `"location=$Location`"" } finally { Pop-Location }
      if (-not $DryRun) {
        Write-Ok "Firewall rule applied for $myIp"
        Write-Info 'Waiting ~20s for the firewall rule to take effect...'
        Start-Sleep -Seconds 20
      }
    }
    else { Write-Warn "Couldn't auto-detect your public IP — if the grant fails with a firewall error, add your IP via Portal or: terraform apply -var admin_client_ip=<your.ip.here>" }
  }


  if (-not (Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue)) {
    if ($DryRun) { Write-Host '  DRYRUN> Install-Module SqlServer -Scope CurrentUser' -ForegroundColor DarkGray }
    elseif (AskYesNo 'Install the SqlServer PowerShell module (needed to run the grant)?' $true) { Install-Module SqlServer -Scope CurrentUser -Force -AllowClobber; Import-Module SqlServer }
    else { Write-Warn 'Without SqlServer module, run the T-SQL from `terraform output post_deploy_sql_grant` via Portal Query editor.'; return }
  }
  $tsql = @"
DECLARE @app sysname = N'$($app.Replace("'","''"))';
DECLARE @q sysname = QUOTENAME(@app);
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @app)
    EXEC('CREATE USER ' + @q + ' FROM EXTERNAL PROVIDER;');
IF IS_ROLEMEMBER('db_datareader', @app) = 0 EXEC('ALTER ROLE db_datareader ADD MEMBER ' + @q + ';');
IF IS_ROLEMEMBER('db_datawriter', @app) = 0 EXEC('ALTER ROLE db_datawriter ADD MEMBER ' + @q + ';');
IF IS_ROLEMEMBER('db_ddladmin',  @app) = 0 EXEC('ALTER ROLE db_ddladmin  ADD MEMBER ' + @q + ';');
"@
  if ($DryRun) { Write-Host "  DRYRUN> Invoke-Sqlcmd against $server/$db with idempotent grant" -ForegroundColor DarkGray; Write-Host $tsql -ForegroundColor DarkGray; return }
  $token = (az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv)
  try {
    Invoke-Sqlcmd -ServerInstance $server -Database $db -AccessToken $token -Query $tsql -ErrorAction Stop
    Write-Ok 'SQL grant applied — the app picks it up within ~30s (migrations retry). No restart needed.'
  } catch {
    Write-Err "Grant failed: $($_.Exception.Message)"
    if ($_.Exception.Message -match 'is not allowed to access the server') {
      Write-Info "Firewall rule may not have propagated yet — wait a minute and re-run: ./deploy.ps1 -Task grant-sql"
    }
    else { Write-Info 'Usually means the signed-in identity is not the Entra SQL admin. Sign in as that admin and retry, or use Portal Query editor.' }
    throw
  }
}

# ── PHASE: seed GitHub PAT into Key Vault (real data) ────────────
function Phase-SetPat {
  Write-Step 6 'Seed the GitHub PAT into Key Vault (real-data mode)'
  $mock = Get-TfVar 'use_mock_data'
  if ($mock -eq 'true') { Write-Ok 'Mock data mode — no PAT needed. Skipping.'; return }
  $kv = Get-TfOutput 'key_vault_name'
  if (-not $kv) { Write-Warn 'key_vault_name output not available (infra not applied?). Skipping.'; return }
  Write-Info "Key Vault: $kv  (secret name: github-pat)"
  Write-Info 'Note: writing a secret is a data-plane op — for a PRIVATE vault, run this from a host on the VNet.'
  if (-not (AskYesNo 'Set the GitHub PAT secret now?' $true)) { Write-Warn "Skipped. Later: ./deploy.ps1 -Task set-pat  (or az keyvault secret set)"; return }
  if ($DryRun) { Write-Host "  DRYRUN> az keyvault secret set --vault-name $kv --name github-pat --value <PAT>" -ForegroundColor DarkGray; return }
  $sec = Read-Host '  Paste the GitHub PAT (input hidden)' -AsSecureString
  $pat = [System.Net.NetworkCredential]::new('', $sec).Password
  if ([string]::IsNullOrWhiteSpace($pat)) { Write-Warn 'Empty PAT — skipped.'; return }
  az keyvault secret set --vault-name $kv --name github-pat --value $pat --only-show-errors | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "Failed to set the secret (permissions or private-network reachability?)." }
  Write-Ok 'PAT stored in Key Vault. The app resolves it via its managed identity on the next snapshot.'
}

# ── PHASE: status / health ───────────────────────────────────────
function Phase-Status {
  Write-Step 7 'Status + health'
  $url = Get-TfOutput 'web_app_url'
  if (-not $url) { Write-Warn 'web_app_url not available (infra not applied?).'; return }
  Write-Ok "URL: $url"
  $isPrivate = (Get-TfVar 'use_private_networking') -eq 'true'
  $hint = if ($isPrivate) { '503 = still warming up: DNS/grant/migrations' } else { '503 = still warming up: grant/migrations (not DNS — this is the PUBLIC pattern)' }
  foreach ($p in '/health/live', '/health/ready') {
    try { $r = Invoke-WebRequest "$url$p" -UseBasicParsing -TimeoutSec 20; Write-Ok "$p → $($r.StatusCode)" }
    catch {
      $code = $_.Exception.Response.StatusCode.value__
      if ($code) { Write-Warn "$p → $code ($hint)" }
      else { Write-Warn "$p → no response ($($_.Exception.Message))" }
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
  break
}

# Standalone tasks (other than 'configure'/'provision', which set the region themselves)
# operate on ALREADY-provisioned infra — always sync $Location from the existing
# terraform.tfvars first so we never pass a stale/default -var "location=..." that could
# force Terraform to replace resources into the wrong region. (The 'all' path below did
# this already; standalone -Task image/grant-sql/set-pat/status did not — bug fix.)
if ($Task -in @('image', 'grant-sql', 'set-pat', 'status')) {
  $existingLoc = Get-TfVar 'location'
  if ($existingLoc) { $Location = $existingLoc }
}

switch ($Task) {
  'preflight' { Ensure-Az; Phase-Preflight }
  'configure' { Phase-Configure }
  'provision' { Phase-Prereqs; Phase-Provision }
  'image'     { Ensure-Az; Phase-Image }
  'grant-sql' { Ensure-Az; Phase-GrantSql }
  'set-pat'   { Ensure-Az; Phase-SetPat }
  'status'    { Phase-Status }
  'all' {
    Phase-Prereqs
    # Configure FIRST — collects the real region/SKU/settings choices — so the gating preflight
    # check below reflects what was actually chosen, not a stale/default param on a fresh machine.
    Phase-Configure
    $tfLoc = Get-TfVar 'location'; if ($tfLoc) { $Location = $tfLoc }
    Phase-Preflight -Gate
    Phase-Provision
    Phase-Image
    Phase-GrantSql
    Phase-SetPat
    Phase-Status
    Write-Banner 'Done'
    Write-Info 'Sign in with your Microsoft account when redirected. Admin console appears if you were granted the Admin role.'
    Write-Info 'Tear down when finished:  cd infra ; terraform destroy'
  }
}
