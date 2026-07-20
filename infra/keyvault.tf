data "azurerm_client_config" "current" {}

resource "azurerm_key_vault" "kv" {
  name                          = "kv-${substr(local.base_st, 0, 21)}"
  resource_group_name           = azurerm_resource_group.rg.name
  location                      = azurerm_resource_group.rg.location
  tenant_id                     = data.azurerm_client_config.current.tenant_id
  sku_name                      = "standard"
  rbac_authorization_enabled    = true
  purge_protection_enabled      = true
  soft_delete_retention_days    = 7
  public_network_access_enabled = !var.use_private_networking

  network_acls {
    bypass = "AzureServices"
    # Private: default-deny (reached via private endpoint). Public: allow (RBAC still gates data).
    default_action = var.use_private_networking ? "Deny" : "Allow"
  }

  tags = var.tags
}

# Deployer needs Secrets Officer to (optionally) seed the PAT during apply.
resource "azurerm_role_assignment" "kv_deployer_secrets_officer" {
  scope                = azurerm_key_vault.kv.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

# Optional: seed the GitHub PAT at deploy time (turnkey). This is a Key Vault DATA-PLANE write, so
# with a PRIVATE Key Vault (use_private_networking=true) the `terraform apply` host must have network
# line-of-sight to the vault's private endpoint (run from a jumpbox / self-hosted agent in the VNet,
# or over VPN/ExpressRoute/Bastion). For private deployments prefer leaving this empty and setting the
# secret out-of-band from inside the network — the app setting references the secret either way.
resource "azurerm_key_vault_secret" "github_pat" {
  count        = var.github_pat_secret_value != "" ? 1 : 0
  name         = "github-pat"
  value        = var.github_pat_secret_value
  key_vault_id = azurerm_key_vault.kv.id
  depends_on   = [azurerm_role_assignment.kv_deployer_secrets_officer]
}

# Private endpoint for Key Vault (private pattern only).
resource "azurerm_private_endpoint" "kv" {
  count               = local.pne
  name                = "pe-kv-${local.base}"
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location
  subnet_id           = local.subnet_pe_id
  tags                = var.tags

  private_service_connection {
    name                           = "psc-kv"
    private_connection_resource_id = azurerm_key_vault.kv.id
    subresource_names              = ["vault"]
    is_manual_connection           = false
  }

  dynamic "private_dns_zone_group" {
    for_each = lookup(local.private_dns_zone_ids, "vault", null) != null ? [1] : []
    content {
      name                 = "vault"
      private_dns_zone_ids = [local.private_dns_zone_ids["vault"]]
    }
  }
}
