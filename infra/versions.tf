terraform {
  required_version = ">= 1.9.0, < 2.0.0"
  backend "azurerm" {}
  required_providers {
    azurerm = { source = "hashicorp/azurerm", version = "~> 5.10.0" }
    azapi   = { source = "Azure/azapi", version = "= 2.12.0" }
  }
}
provider "azapi" {
  subscription_id            = var.subscription_id
  skip_provider_registration = true
}
provider "azurerm" {
  features {
    resource_group {
      prevent_deletion_if_contains_resources = false
    }
  }
  subscription_id                 = var.subscription_id
  resource_provider_registrations = "none"
  storage_use_azuread             = true
}
provider "azurerm" {
  alias = "gallery"
  features {}
  subscription_id                 = try(split("/", var.gallery_image_version_id)[2], var.subscription_id)
  resource_provider_registrations = "none"
}
