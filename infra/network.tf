# ── Networking (PRIVATE pattern only) ─────────────────────────
# All resources here are created only when use_private_networking = true.
# Two paths, selected by custom_network_mode:
#   false (default) — this stack creates its own VNet + two subnets.
#   true             — bring your own existing VNet/subnets (enterprise IPAM/landing-zone mode).
locals {
  pne            = var.use_private_networking ? 1 : 0
  byo_network    = var.use_private_networking && var.custom_network_mode
  create_network = var.use_private_networking && !var.custom_network_mode
}

# ── Validate the "advanced" custom CIDR inputs (create_network path only) ──
# Per-field format/size checks live on the variables themselves (variables.tf);
# containment/overlap needs arithmetic across multiple variables, which variable
# validation blocks can't do — so it's computed here and enforced as hard
# lifecycle.precondition checks on the subnet resources below (a top-level `check`
# block would only warn, not block apply — preconditions actually fail the run).
locals {
  # IPv4-as-integer for each relevant prefix, normalized to its network base address
  # (cidrhost(cidr, 0)) so misaligned host bits in user input don't skew the math.
  net_addr_num = {
    for k, cidr in { vnet = var.vnet_address_space, pe = var.subnet_pe_prefix, app = var.subnet_app_prefix } :
    k => sum([for i, o in split(".", cidrhost(cidr, 0)) : tonumber(o) * pow(256, 3 - i)])
  }
  net_size_num = {
    for k, cidr in { vnet = var.vnet_address_space, pe = var.subnet_pe_prefix, app = var.subnet_app_prefix } :
    k => pow(2, 32 - tonumber(split("/", cidr)[1]))
  }
  pe_within_vnet = (
    local.net_addr_num.pe >= local.net_addr_num.vnet &&
    (local.net_addr_num.pe + local.net_size_num.pe) <= (local.net_addr_num.vnet + local.net_size_num.vnet)
  )
  app_within_vnet = (
    local.net_addr_num.app >= local.net_addr_num.vnet &&
    (local.net_addr_num.app + local.net_size_num.app) <= (local.net_addr_num.vnet + local.net_size_num.vnet)
  )
  subnets_overlap = !(
    (local.net_addr_num.pe + local.net_size_num.pe) <= local.net_addr_num.app ||
    (local.net_addr_num.app + local.net_size_num.app) <= local.net_addr_num.pe
  )
}

# ── Path A (default): this stack creates its own VNet + subnets ──
resource "azurerm_virtual_network" "vnet" {
  count               = local.create_network ? 1 : 0
  name                = "vnet-${local.base}"
  resource_group_name = azurerm_resource_group.rg.name
  location            = azurerm_resource_group.rg.location
  address_space       = [var.vnet_address_space]
  tags                = var.tags
}

# Subnet for all private endpoints (Key Vault, SQL, Web App inbound)
resource "azurerm_subnet" "pe" {
  count                = local.create_network ? 1 : 0
  name                 = "snet-pe"
  resource_group_name  = azurerm_resource_group.rg.name
  virtual_network_name = azurerm_virtual_network.vnet[0].name
  address_prefixes     = [var.subnet_pe_prefix]

  lifecycle {
    precondition {
      condition     = local.pe_within_vnet
      error_message = "subnet_pe_prefix (${var.subnet_pe_prefix}) must fall entirely within vnet_address_space (${var.vnet_address_space})."
    }
    precondition {
      condition     = !local.subnets_overlap
      error_message = "subnet_pe_prefix (${var.subnet_pe_prefix}) and subnet_app_prefix (${var.subnet_app_prefix}) must not overlap."
    }
  }
}

# Delegated subnet for App Service regional VNet integration (outbound)
resource "azurerm_subnet" "app" {
  count                = local.create_network ? 1 : 0
  name                 = "snet-app"
  resource_group_name  = azurerm_resource_group.rg.name
  virtual_network_name = azurerm_virtual_network.vnet[0].name
  address_prefixes     = [var.subnet_app_prefix]

  delegation {
    name = "appservice-delegation"
    service_delegation {
      name    = "Microsoft.Web/serverFarms"
      actions = ["Microsoft.Network/virtualNetworks/subnets/action"]
    }
  }

  lifecycle {
    precondition {
      condition     = local.app_within_vnet
      error_message = "subnet_app_prefix (${var.subnet_app_prefix}) must fall entirely within vnet_address_space (${var.vnet_address_space})."
    }
    precondition {
      condition     = !local.subnets_overlap
      error_message = "subnet_pe_prefix (${var.subnet_pe_prefix}) and subnet_app_prefix (${var.subnet_app_prefix}) must not overlap."
    }
  }
}

# ── Path B: bring-your-own VNet + subnets (customer-owned IPAM) ──
# These subnets must already exist: snet-pe empty/non-delegated, snet-app delegated
# to Microsoft.Web/serverFarms. This stack does not create or modify them.
data "azurerm_virtual_network" "existing" {
  count               = local.byo_network ? 1 : 0
  name                = var.existing_vnet_name
  resource_group_name = var.existing_vnet_resource_group_name
}

data "azurerm_subnet" "pe_existing" {
  count                = local.byo_network ? 1 : 0
  name                 = var.existing_subnet_pe_name
  virtual_network_name = var.existing_vnet_name
  resource_group_name  = var.existing_vnet_resource_group_name
}

data "azurerm_subnet" "app_existing" {
  count                = local.byo_network ? 1 : 0
  name                 = var.existing_subnet_app_name
  virtual_network_name = var.existing_vnet_name
  resource_group_name  = var.existing_vnet_resource_group_name
}

# Note: the azurerm_subnet data source does not expose delegation details, so we can't
# assert the customer-supplied app subnet is delegated to Microsoft.Web/serverFarms here.
# If it isn't, Azure will fail the App Service VNet-integration step with a clear error —
# see the existing_subnet_app_name variable description for the requirement.

# Resolved network locals used by every other resource in infra/ — safe for either path.
locals {
  vnet_id = var.use_private_networking ? (
    local.byo_network ? data.azurerm_virtual_network.existing[0].id : azurerm_virtual_network.vnet[0].id
  ) : null
  subnet_pe_id = var.use_private_networking ? (
    local.byo_network ? data.azurerm_subnet.pe_existing[0].id : azurerm_subnet.pe[0].id
  ) : null
  subnet_app_id = var.use_private_networking ? (
    local.byo_network ? data.azurerm_subnet.app_existing[0].id : azurerm_subnet.app[0].id
  ) : null
}

# ── Private DNS zones (created here unless reusing a centralized hub) ──
locals {
  dns_zone_names = {
    vault = "privatelink.vaultcore.azure.net"
    sql   = "privatelink.database.windows.net"
    sites = "privatelink.azurewebsites.net"
  }
  create_zones = var.use_private_networking && var.create_private_dns_zones
}

resource "azurerm_private_dns_zone" "zone" {
  for_each            = local.create_zones ? local.dns_zone_names : {}
  name                = each.value
  resource_group_name = azurerm_resource_group.rg.name
  tags                = var.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "link" {
  for_each              = local.create_zones ? local.dns_zone_names : {}
  name                  = "link-${each.key}"
  resource_group_name   = azurerm_resource_group.rg.name
  private_dns_zone_name = azurerm_private_dns_zone.zone[each.key].name
  virtual_network_id    = local.vnet_id
  registration_enabled  = false
  tags                  = var.tags
}

# Resolve the zone IDs used by private endpoints (created vs. existing centralized).
# Always exposes the three keys (null when not applicable) so lookups are safe.
locals {
  private_dns_zone_ids = var.use_private_networking ? {
    for k in keys(local.dns_zone_names) :
    k => var.create_private_dns_zones ? azurerm_private_dns_zone.zone[k].id : lookup(var.existing_private_dns_zone_ids, k, null)
    } : {
    vault = null
    sql   = null
    sites = null
  }
}
