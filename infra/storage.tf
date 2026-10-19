resource "azurerm_storage_account" "poc" {
  name                            = "poc${var.name_suffix}"
  resource_group_name             = local.resource_group_name
  location                        = local.location
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  shared_access_key_enabled       = false
  default_to_oauth_authentication = true
  allow_nested_items_to_be_public = false
  public_network_access_enabled   = var.storage_public_network_access_enabled
  tags                            = local.tags
}

resource "azurerm_storage_container" "results" {
  name                  = "poc-results"
  storage_account_id    = azurerm_storage_account.poc.id
  container_access_type = "private"
}

resource "azurerm_storage_container" "workflow_output" {
  name                  = "workflow-output"
  storage_account_id    = azurerm_storage_account.poc.id
  container_access_type = "private"
}

resource "azurerm_private_endpoint" "storage_blob" {
  name                = "${local.prefix}-pe-storage-blob"
  location            = local.location
  resource_group_name = local.resource_group_name
  subnet_id           = azurerm_subnet.private_endpoints.id
  tags                = local.tags

  private_service_connection {
    name                           = "${local.prefix}-psc-storage-blob"
    private_connection_resource_id = azurerm_storage_account.poc.id
    subresource_names              = ["blob"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "storage-blob-dns-zone-group"
    private_dns_zone_ids = [azurerm_private_dns_zone.storage_blob.id]
  }
}

resource "azurerm_private_endpoint" "storage_queue" {
  name                = "${local.prefix}-pe-storage-queue"
  location            = local.location
  resource_group_name = local.resource_group_name
  subnet_id           = azurerm_subnet.private_endpoints.id
  tags                = local.tags

  private_service_connection {
    name                           = "${local.prefix}-psc-storage-queue"
    private_connection_resource_id = azurerm_storage_account.poc.id
    subresource_names              = ["queue"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "storage-queue-dns-zone-group"
    private_dns_zone_ids = [azurerm_private_dns_zone.storage_queue.id]
  }
}

resource "azurerm_private_endpoint" "storage_table" {
  name                = "${local.prefix}-pe-storage-table"
  location            = local.location
  resource_group_name = local.resource_group_name
  subnet_id           = azurerm_subnet.private_endpoints.id
  tags                = local.tags

  private_service_connection {
    name                           = "${local.prefix}-psc-storage-table"
    private_connection_resource_id = azurerm_storage_account.poc.id
    subresource_names              = ["table"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "storage-table-dns-zone-group"
    private_dns_zone_ids = [azurerm_private_dns_zone.storage_table.id]
  }
}
