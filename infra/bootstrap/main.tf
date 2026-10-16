# Independent, local-state bootstrap root. Never include it in an environment destroy.
terraform {
  required_version = ">= 1.9.0, < 2.0.0"
  backend "local" {}
  required_providers {
    azurerm = { source = "hashicorp/azurerm", version = "~> 5.10.0" }
  }
}

provider "azurerm" {
  features {}
  subscription_id                 = var.subscription_id
  resource_provider_registrations = "none"
  storage_use_azuread             = true
}

variable "subscription_id" { type = string }
variable "resource_group_name" {
  type = string
  validation {
    condition     = startswith(var.resource_group_name, "poc-")
    error_message = "Bootstrap RG name must start with poc-."
  }
}
variable "location" { type = string }
variable "storage_account_name" {
  type = string
  validation {
    condition     = can(regex("^poc[a-z0-9]{3,21}$", var.storage_account_name))
    error_message = "Use a unique 6-24 character lowercase alphanumeric name starting with poc."
  }
}
variable "container_name" {
  type    = string
  default = "tfstate"
}
variable "deployer_principal_id" {
  type        = string
  description = "Object ID of the identity running Terraform backend operations."
}

resource "azurerm_resource_group" "state" {
  name     = var.resource_group_name
  location = var.location
  tags     = { purpose = "poc-terraform-state" }
  lifecycle { prevent_destroy = true }
}

resource "azurerm_storage_account" "state" {
  name                            = var.storage_account_name
  resource_group_name             = azurerm_resource_group.state.name
  location                        = azurerm_resource_group.state.location
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  shared_access_key_enabled       = false
  default_to_oauth_authentication = true
  allow_nested_items_to_be_public = false
  blob_properties {
    versioning_enabled = true
    delete_retention_policy { days = 7 }
    container_delete_retention_policy { days = 7 }
  }
  tags = { purpose = "poc-terraform-state" }
  lifecycle { prevent_destroy = true }
}

resource "azurerm_storage_container" "state" {
  name                  = var.container_name
  storage_account_id    = azurerm_storage_account.state.id
  container_access_type = "private"
  lifecycle { prevent_destroy = true }
}

resource "azurerm_role_assignment" "backend" {
  scope                = "${azurerm_storage_account.state.id}/blobServices/default/containers/${azurerm_storage_container.state.name}"
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = var.deployer_principal_id
}

output "backend_config" {
  description = "Append an environment-specific key when initializing the application root. No access keys or SAS."
  value = {
    subscription_id      = var.subscription_id
    resource_group_name  = azurerm_resource_group.state.name
    storage_account_name = azurerm_storage_account.state.name
    container_name       = azurerm_storage_container.state.name
    use_azuread_auth     = true
  }
}
