# Private DNS Zones and Virtual Network Links for Storage (Blob, Queue, Table).
# Resolution within the validation VNet maps storage service FQDNs to Private Endpoint IPs.
# (Functions DNS is defined in validation-vm.tf; Batch DNS is defined in batch.tf)

resource "azurerm_private_dns_zone" "storage_blob" {
  name                = "privatelink.blob.core.windows.net"
  resource_group_name = local.resource_group_name
  tags                = local.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "storage_blob" {
  name                 = "${local.prefix}-storage-blob-dns"
  private_dns_zone_id  = azurerm_private_dns_zone.storage_blob.id
  virtual_network_id   = azurerm_virtual_network.validation.id
  registration_enabled = false
  tags                 = local.tags
}

resource "azurerm_private_dns_zone" "storage_queue" {
  name                = "privatelink.queue.core.windows.net"
  resource_group_name = local.resource_group_name
  tags                = local.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "storage_queue" {
  name                 = "${local.prefix}-storage-queue-dns"
  private_dns_zone_id  = azurerm_private_dns_zone.storage_queue.id
  virtual_network_id   = azurerm_virtual_network.validation.id
  registration_enabled = false
  tags                 = local.tags
}

resource "azurerm_private_dns_zone" "storage_table" {
  name                = "privatelink.table.core.windows.net"
  resource_group_name = local.resource_group_name
  tags                = local.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "storage_table" {
  name                 = "${local.prefix}-storage-table-dns"
  private_dns_zone_id  = azurerm_private_dns_zone.storage_table.id
  virtual_network_id   = azurerm_virtual_network.validation.id
  registration_enabled = false
  tags                 = local.tags
}
