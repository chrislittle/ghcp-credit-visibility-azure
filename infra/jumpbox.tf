# ── Jump box + Azure Bastion (advanced networking, self-created VNet only) ─────
# Optional resources so a tester can reach the app/Key Vault/SQL PRIVATE endpoints
# end-to-end without any public access: RDP to a small Windows VM over Bastion
# (no VM public IP; no NSG hole open to the internet). Created only when
# enable_jumpbox = true, which itself requires custom_network_mode = false (this
# stack must own the VNet to carve out the two extra subnets Bastion/the VM need).
locals {
  create_jumpbox = local.create_network && var.enable_jumpbox

  # Same "normalize to network base address, compare as integers" approach used for
  # the pe/app subnets in network.tf — reused here for the bastion/jumpbox subnets.
  jb_addr_num = {
    for k, cidr in { vnet = var.vnet_address_space, bastion = var.subnet_bastion_prefix, jumpbox = var.subnet_jumpbox_prefix } :
    k => sum([for i, o in split(".", cidrhost(cidr, 0)) : tonumber(o) * pow(256, 3 - i)])
  }
  jb_size_num = {
    for k, cidr in { vnet = var.vnet_address_space, bastion = var.subnet_bastion_prefix, jumpbox = var.subnet_jumpbox_prefix } :
    k => pow(2, 32 - tonumber(split("/", cidr)[1]))
  }
  bastion_within_vnet = (
    local.jb_addr_num.bastion >= local.jb_addr_num.vnet &&
    (local.jb_addr_num.bastion + local.jb_size_num.bastion) <= (local.jb_addr_num.vnet + local.jb_size_num.vnet)
  )
  jumpbox_within_vnet = (
    local.jb_addr_num.jumpbox >= local.jb_addr_num.vnet &&
    (local.jb_addr_num.jumpbox + local.jb_size_num.jumpbox) <= (local.jb_addr_num.vnet + local.jb_size_num.vnet)
  )
  # Overlap checks against ALL FOUR subnets (pe, app, bastion, jumpbox) — pairwise.
  bastion_overlaps_pe = !(
    (local.jb_addr_num.bastion + local.jb_size_num.bastion) <= local.net_addr_num.pe ||
    (local.net_addr_num.pe + local.net_size_num.pe) <= local.jb_addr_num.bastion
  )
  bastion_overlaps_app = !(
    (local.jb_addr_num.bastion + local.jb_size_num.bastion) <= local.net_addr_num.app ||
    (local.net_addr_num.app + local.net_size_num.app) <= local.jb_addr_num.bastion
  )
  bastion_overlaps_jumpbox = !(
    (local.jb_addr_num.bastion + local.jb_size_num.bastion) <= local.jb_addr_num.jumpbox ||
    (local.jb_addr_num.jumpbox + local.jb_size_num.jumpbox) <= local.jb_addr_num.bastion
  )
  jumpbox_overlaps_pe = !(
    (local.jb_addr_num.jumpbox + local.jb_size_num.jumpbox) <= local.net_addr_num.pe ||
    (local.net_addr_num.pe + local.net_size_num.pe) <= local.jb_addr_num.jumpbox
  )
  jumpbox_overlaps_app = !(
    (local.jb_addr_num.jumpbox + local.jb_size_num.jumpbox) <= local.net_addr_num.app ||
    (local.net_addr_num.app + local.net_size_num.app) <= local.jb_addr_num.jumpbox
  )
}

resource "azurerm_subnet" "bastion" {
  count                = local.create_jumpbox ? 1 : 0
  name                 = "AzureBastionSubnet" # exact name required by Azure Bastion
  resource_group_name  = azurerm_resource_group.rg.name
  virtual_network_name = azurerm_virtual_network.vnet[0].name
  address_prefixes     = [var.subnet_bastion_prefix]

  lifecycle {
    precondition {
      condition     = local.bastion_within_vnet
      error_message = "subnet_bastion_prefix (${var.subnet_bastion_prefix}) must fall entirely within vnet_address_space (${var.vnet_address_space})."
    }
    precondition {
      condition     = !local.bastion_overlaps_pe && !local.bastion_overlaps_app && !local.bastion_overlaps_jumpbox
      error_message = "subnet_bastion_prefix (${var.subnet_bastion_prefix}) overlaps another subnet (snet-pe, snet-app, or snet-jumpbox)."
    }
  }
}

resource "azurerm_subnet" "jumpbox" {
  count                = local.create_jumpbox ? 1 : 0
  name                 = "snet-jumpbox"
  resource_group_name  = azurerm_resource_group.rg.name
  virtual_network_name = azurerm_virtual_network.vnet[0].name
  address_prefixes     = [var.subnet_jumpbox_prefix]

  lifecycle {
    precondition {
      condition     = local.jumpbox_within_vnet
      error_message = "subnet_jumpbox_prefix (${var.subnet_jumpbox_prefix}) must fall entirely within vnet_address_space (${var.vnet_address_space})."
    }
    precondition {
      condition     = !local.jumpbox_overlaps_pe && !local.jumpbox_overlaps_app && !local.bastion_overlaps_jumpbox
      error_message = "subnet_jumpbox_prefix (${var.subnet_jumpbox_prefix}) overlaps another subnet (snet-pe, snet-app, or AzureBastionSubnet)."
    }
  }
}

# Jump box's NIC gets only a private IP — Bastion is the sole path in, so no NSG rule
# needs to allow RDP from the internet.
resource "azurerm_network_security_group" "jumpbox" {
  count               = local.create_jumpbox ? 1 : 0
  name                = "nsg-jumpbox-${local.base}"
  location            = azurerm_resource_group.rg.location
  resource_group_name = azurerm_resource_group.rg.name
  tags                = var.tags

  security_rule {
    name                       = "AllowRdpFromBastion"
    priority                   = 100
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "3389"
    source_address_prefix      = var.subnet_bastion_prefix
    destination_address_prefix = "*"
  }
}

resource "azurerm_subnet_network_security_group_association" "jumpbox" {
  count                     = local.create_jumpbox ? 1 : 0
  subnet_id                 = azurerm_subnet.jumpbox[0].id
  network_security_group_id = azurerm_network_security_group.jumpbox[0].id
}

resource "azurerm_public_ip" "bastion" {
  count               = local.create_jumpbox ? 1 : 0
  name                = "pip-bastion-${local.base}"
  location            = azurerm_resource_group.rg.location
  resource_group_name = azurerm_resource_group.rg.name
  allocation_method   = "Static"
  sku                 = "Standard"
  tags                = var.tags
}

resource "azurerm_bastion_host" "bastion" {
  count               = local.create_jumpbox ? 1 : 0
  name                = "bas-${local.base}"
  location            = azurerm_resource_group.rg.location
  resource_group_name = azurerm_resource_group.rg.name
  sku                 = var.bastion_sku
  tags                = var.tags

  ip_configuration {
    name                 = "bastion-ipconfig"
    subnet_id            = azurerm_subnet.bastion[0].id
    public_ip_address_id = azurerm_public_ip.bastion[0].id
  }
}

# Auto-generate a strong local admin password when one isn't supplied, so `terraform
# apply` never fails on a missing secret and no plaintext password needs to live in
# terraform.tfvars. Retrieve it afterwards with: terraform output -raw jumpbox_admin_password
resource "random_password" "jumpbox" {
  count            = local.create_jumpbox && var.jumpbox_admin_password == "" ? 1 : 0
  length           = 20
  special          = true
  override_special = "_%@!#"
}

locals {
  jumpbox_admin_password = var.jumpbox_admin_password != "" ? var.jumpbox_admin_password : try(random_password.jumpbox[0].result, "")
}

resource "azurerm_network_interface" "jumpbox" {
  count               = local.create_jumpbox ? 1 : 0
  name                = "nic-jumpbox-${local.base}"
  location            = azurerm_resource_group.rg.location
  resource_group_name = azurerm_resource_group.rg.name
  tags                = var.tags

  ip_configuration {
    name                          = "internal"
    subnet_id                     = azurerm_subnet.jumpbox[0].id
    private_ip_address_allocation = "Dynamic"
  }
}

resource "azurerm_windows_virtual_machine" "jumpbox" {
  count               = local.create_jumpbox ? 1 : 0
  name                = "vm-jumpbox"
  computer_name       = "jumpbox"
  location            = azurerm_resource_group.rg.location
  resource_group_name = azurerm_resource_group.rg.name
  size                = var.jumpbox_vm_size
  admin_username      = var.jumpbox_admin_username
  admin_password      = local.jumpbox_admin_password
  network_interface_ids = [
    azurerm_network_interface.jumpbox[0].id
  ]
  tags = var.tags

  # System-assigned identity so the jump box can authenticate to Key Vault itself (via the
  # Instance Metadata Service) when deploy.ps1 seeds the GitHub PAT through Azure Run Command —
  # avoids needing an interactive RDP session for that step on a private deployment.
  identity {
    type = "SystemAssigned"
  }

  os_disk {
    caching              = "ReadWrite"
    storage_account_type = "StandardSSD_LRS"
  }

  # Windows Server 2022 Datacenter Azure Edition (Core) — small footprint, patched by
  # Microsoft, includes enough of a desktop (via RDP) to run Edge and test the app.
  source_image_reference {
    publisher = "MicrosoftWindowsServer"
    offer     = "WindowsServer"
    sku       = "2022-datacenter-azure-edition"
    version   = "latest"
  }
}

# Lets the jump box write the GitHub PAT into Key Vault using its OWN identity (via
# deploy.ps1's Azure Run Command path) — scoped to just this vault's secrets, nothing else.
# Splat + one() (not a direct [0][0] double-index) — the established pattern this codebase
# already uses for this same resource in outputs.tf. Directly indexing
# azurerm_windows_virtual_machine.jumpbox[0].identity[0].principal_id fails at plan time with
# "Missing required argument" because Terraform can't safely resolve a nested computed block
# (identity[0]) through a direct index into a resource that itself has a conditional count —
# even when that count resolves to 1. Splatting first avoids the problem entirely.
locals {
  jumpbox_principal_id = one(azurerm_windows_virtual_machine.jumpbox[*].identity[0].principal_id)
}

resource "azurerm_role_assignment" "jumpbox_kv_secrets_officer" {
  count                = local.create_jumpbox ? 1 : 0
  scope                = azurerm_key_vault.kv.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = local.jumpbox_principal_id
}
