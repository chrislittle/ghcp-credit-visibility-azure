terraform {
  required_version = ">= 1.9"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.20"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
    time = {
      source  = "hashicorp/time"
      version = "~> 0.12"
    }
  }
}

provider "azurerm" {
  # Target subscription. Set via ARM_SUBSCRIPTION_ID env var or -var subscription_id=...
  subscription_id = var.subscription_id
  features {
    key_vault {
      purge_soft_delete_on_destroy = false
    }
    resource_group {
      # Application Insights auto-creates a "Failure Anomalies" smart detector rule +
      # action group outside Terraform's state on every deploy. Without this flag,
      # `terraform destroy` fails at the resource-group-deletion step because it finds
      # these untracked nested resources. This tells Terraform to delete the RG via the
      # Azure API directly (which cascades to any nested resources) instead of erroring.
      prevent_deletion_if_contains_resources = false
    }
  }
}

provider "azuread" {
  # Uses the same `az login` context as azurerm (the subscription's tenant).
}
