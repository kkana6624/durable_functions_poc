terraform {
  required_version = ">= 1.9.0, < 2.0.0"
  backend "azurerm" {}
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
provider "azurerm" {
  alias = "gallery"
  features {}
  subscription_id                 = try(split("/", var.gallery_image_version_id)[2], var.subscription_id)
  resource_provider_registrations = "none"
}
