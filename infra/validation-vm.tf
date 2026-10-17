# Only Functions ingress is private in this PoC. Storage and Batch retain their
# existing network configuration; their identities and RBAC remain enforced.
resource "azurerm_virtual_network" "validation" {
  name                = "${local.prefix}-vnet"
  location            = local.location
  resource_group_name = local.resource_group_name
  address_space       = [var.validation_vnet_address_space]
  tags                = local.tags
}

resource "azurerm_subnet" "validation_vm" {
  name                            = "validation-vm"
  resource_group_name             = local.resource_group_name
  virtual_network_name            = azurerm_virtual_network.validation.name
  address_prefixes                = [var.validation_vm_subnet_prefix]
  default_outbound_access_enabled = false
}

resource "azurerm_subnet" "private_endpoints" {
  name                 = "private-endpoints"
  resource_group_name  = local.resource_group_name
  virtual_network_name = azurerm_virtual_network.validation.name
  address_prefixes     = [var.validation_private_endpoint_subnet_prefix]
}

resource "azurerm_network_security_group" "validation_vm" {
  name                = "${local.prefix}-vm-nsg"
  location            = local.location
  resource_group_name = local.resource_group_name
  tags                = local.tags
  security_rule {
    name                       = "DenyAllInbound"
    priority                   = 100
    direction                  = "Inbound"
    access                     = "Deny"
    protocol                   = "*"
    source_port_range          = "*"
    destination_port_range     = "*"
    source_address_prefix      = "*"
    destination_address_prefix = "*"
  }
}

resource "azurerm_subnet_network_security_group_association" "validation_vm" {
  subnet_id                 = azurerm_subnet.validation_vm.id
  network_security_group_id = azurerm_network_security_group.validation_vm.id
}

# Explicit outbound is required for package installation, Entra/ARM, and the
# existing Storage/Batch endpoints. This public IP is outbound-only, not VM ingress.
resource "azurerm_public_ip" "validation_egress" {
  name                = "${local.prefix}-egress-ip"
  location            = local.location
  resource_group_name = local.resource_group_name
  allocation_method   = "Static"
  sku                 = "Standard"
  tags                = local.tags
}

resource "azurerm_nat_gateway" "validation" {
  name                = "${local.prefix}-nat"
  location            = local.location
  resource_group_name = local.resource_group_name
  sku_name            = "Standard"
  tags                = local.tags
}

resource "azurerm_nat_gateway_public_ip_association" "validation" {
  nat_gateway_id       = azurerm_nat_gateway.validation.id
  public_ip_address_id = azurerm_public_ip.validation_egress.id
}

resource "azurerm_subnet_nat_gateway_association" "validation" {
  subnet_id      = azurerm_subnet.validation_vm.id
  nat_gateway_id = azurerm_nat_gateway.validation.id
}

resource "azurerm_private_dns_zone" "functions" {
  name                = "privatelink.azurewebsites.net"
  resource_group_name = local.resource_group_name
  tags                = local.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "functions" {
  name                 = "${local.prefix}-functions-dns"
  private_dns_zone_id  = azurerm_private_dns_zone.functions.id
  virtual_network_id   = azurerm_virtual_network.validation.id
  registration_enabled = false
  tags                 = local.tags
}

resource "azurerm_private_endpoint" "functions" {
  name                = "${local.prefix}-functions-pe"
  location            = local.location
  resource_group_name = local.resource_group_name
  subnet_id           = azurerm_subnet.private_endpoints.id
  tags                = local.tags
  private_service_connection {
    name                           = "functions"
    private_connection_resource_id = azurerm_linux_function_app.poc.id
    subresource_names              = ["sites"]
    is_manual_connection           = false
  }
  # Azure populates both app and SCM records in this zone group.
  private_dns_zone_group {
    name                 = "functions"
    private_dns_zone_ids = [azurerm_private_dns_zone.functions.id]
  }
}

resource "azurerm_network_interface" "validation_vm" {
  name                = "${local.prefix}-vm-nic"
  location            = local.location
  resource_group_name = local.resource_group_name
  tags                = local.tags
  ip_configuration {
    name                          = "private"
    subnet_id                     = azurerm_subnet.validation_vm.id
    private_ip_address_allocation = "Dynamic"
  }
}

resource "azurerm_linux_virtual_machine" "validation" {
  name                            = "${local.prefix}-validation"
  computer_name                   = "poc-validation"
  location                        = local.location
  resource_group_name             = local.resource_group_name
  size                            = var.validation_vm_size
  disk_controller_type            = var.validation_vm_disk_controller_type
  admin_username                  = var.validation_vm_admin_username
  disable_password_authentication = true
  network_interface_ids           = [azurerm_network_interface.validation_vm.id]
  custom_data                     = base64encode(file("${path.module}/templates/validation-vm-cloud-init.yaml"))
  tags                            = local.tags
  admin_ssh_key {
    username   = var.validation_vm_admin_username
    public_key = trimspace(var.validation_vm_ssh_public_key)
  }
  identity { type = "SystemAssigned" }
  os_disk {
    caching              = "ReadWrite"
    storage_account_type = "Standard_LRS"
  }
  source_image_reference {
    publisher = "RedHat"
    offer     = "RHEL"
    sku       = "9-lvm-gen2"
    version   = "latest"
  }
  depends_on = [
    azurerm_nat_gateway_public_ip_association.validation,
    azurerm_subnet_nat_gateway_association.validation,
    azurerm_subnet_network_security_group_association.validation_vm,
  ]
}

# Deployment and validation execute through Azure VM Run Command; no SSH ingress.
# The role may be assigned only inside the PoC RG, and is granted at Function scope.
resource "azurerm_role_definition" "validation_function" {
  name        = "${local.prefix}-validation-function"
  scope       = "/subscriptions/${var.subscription_id}/resourceGroups/${local.resource_group_name}"
  description = "Read Function readiness/settings, obtain starter key and publish the PoC package."
  permissions {
    actions = [
      "Microsoft.Web/sites/read",
      "Microsoft.Web/sites/functions/read",
      "Microsoft.Web/sites/functions/listkeys/action",
      "Microsoft.Web/sites/config/list/action",
      "Microsoft.Web/sites/publish/action",
    ]
  }
  assignable_scopes = ["/subscriptions/${var.subscription_id}/resourceGroups/${local.resource_group_name}"]
  depends_on        = [azurerm_resource_group.poc]
}

resource "azurerm_role_assignment" "validation_function" {
  scope              = azurerm_linux_function_app.poc.id
  role_definition_id = azurerm_role_definition.validation_function.role_definition_resource_id
  principal_id       = azurerm_linux_virtual_machine.validation.identity[0].principal_id
}

resource "azurerm_role_assignment" "validation_blob" {
  scope                = azurerm_storage_account.poc.id
  role_definition_name = "Storage Blob Data Reader"
  principal_id         = azurerm_linux_virtual_machine.validation.identity[0].principal_id
}

# Includes Batch data-plane read operations; generic ARM Reader is insufficient.
# https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/compute#azure-batch-account-reader
resource "azurerm_role_assignment" "validation_batch" {
  scope                = local.batch_account_id
  role_definition_name = "Azure Batch Account Reader"
  principal_id         = azurerm_linux_virtual_machine.validation.identity[0].principal_id
}

resource "azurerm_storage_container" "vm_control" {
  name                  = "poc-vm-control"
  storage_account_id    = azurerm_storage_account.poc.id
  container_access_type = "private"
}

resource "azurerm_role_assignment" "validation_control" {
  scope                = "${azurerm_storage_account.poc.id}/blobServices/default/containers/${azurerm_storage_container.vm_control.name}"
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_linux_virtual_machine.validation.identity[0].principal_id
}

resource "azurerm_role_assignment" "validation_operator_control" {
  count                = var.validation_operator_principal_id == null ? 0 : 1
  scope                = "${azurerm_storage_account.poc.id}/blobServices/default/containers/${azurerm_storage_container.vm_control.name}"
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = var.validation_operator_principal_id
}
