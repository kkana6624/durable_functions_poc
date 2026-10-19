# Optional business App Service (ASP.NET Core) infrastructure definition.
# When var.enable_app_service is false (default), no resources are created,
# ensuring zero disruption to existing deployments and costs.

resource "azurerm_service_plan" "app_service" {
  count               = var.enable_app_service ? 1 : 0
  name                = "${local.prefix}-app-plan"
  resource_group_name = local.resource_group_name
  location            = local.location
  os_type             = "Linux"
  sku_name            = var.app_service_sku
  tags                = local.tags
}

resource "azurerm_subnet" "app_service_outbound" {
  count                = var.enable_app_service ? 1 : 0
  name                 = "app-service-outbound"
  resource_group_name  = local.resource_group_name
  virtual_network_name = azurerm_virtual_network.validation.name
  address_prefixes     = [var.app_service_outbound_subnet_prefix]

  delegation {
    name = "app-service-delegation"
    service_delegation {
      name    = "Microsoft.Web/serverFarms"
      actions = ["Microsoft.Network/virtualNetworks/subnets/action"]
    }
  }
}

resource "azurerm_subnet_nat_gateway_association" "app_service_outbound" {
  count          = var.enable_app_service ? 1 : 0
  subnet_id      = azurerm_subnet.app_service_outbound[0].id
  nat_gateway_id = azurerm_nat_gateway.validation.id
}

resource "azurerm_user_assigned_identity" "app_service" {
  count               = var.enable_app_service ? 1 : 0
  name                = "${local.prefix}-app-uami"
  resource_group_name = local.resource_group_name
  location            = local.location
  tags                = local.tags
}

resource "azurerm_linux_web_app" "app_service" {
  count                           = var.enable_app_service ? 1 : 0
  name                            = "${local.prefix}-app"
  resource_group_name             = local.resource_group_name
  location                        = local.location
  service_plan_id                 = azurerm_service_plan.app_service[0].id
  https_only                      = true
  public_network_access_enabled   = false
  key_vault_reference_identity_id = azurerm_user_assigned_identity.app_service[0].id
  tags                            = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.app_service[0].id]
  }

  site_config {
    always_on              = true
    ftps_state             = "Disabled"
    minimum_tls_version    = "1.2"
    vnet_route_all_enabled = true

    application_stack {
      dotnet_version = "10.0"
    }
  }

  # App Service communicates with Function App and Storage via private endpoints.
  # Secrets, Function keys, and SAS tokens are strictly omitted from settings and state;
  # authentication uses Managed Identity (Entra ID) and Key Vault / runtime acquisition.
  app_settings = {
    FUNCTION_APP_URL                      = "https://${azurerm_linux_function_app.poc.default_hostname}"
    BLOB_SERVICE_ENDPOINT                 = azurerm_storage_account.poc.primary_blob_endpoint
    WORKFLOW_INPUT_CONTAINER              = "workflow-input"
    APP_SERVICE_UAMI_CLIENT_ID            = azurerm_user_assigned_identity.app_service[0].client_id
    APPLICATIONINSIGHTS_CONNECTION_STRING = azurerm_application_insights.poc.connection_string
    WORKFLOW_AUTH_MODE                    = var.app_service_workflow_auth_mode != null ? var.app_service_workflow_auth_mode : (var.workflow_auth_mode == "EntraEasyAuth" ? "EntraBearer" : "FunctionKey")
    WORKFLOW_AUTH_AUDIENCE                = var.workflow_auth_audience != null ? var.workflow_auth_audience : (var.workflow_auth_api_client_id != null ? "api://${var.workflow_auth_api_client_id}" : "")
    WORKFLOW_FUNCTION_KEY                 = var.app_service_workflow_function_key_secret_uri != null ? "@Microsoft.KeyVault(SecretUri=${var.app_service_workflow_function_key_secret_uri})" : ""
    APP_SERVICE_AUTH_MODE                 = var.app_service_auth_mode
    APP_SERVICE_AUTH_AUTHORITY            = var.app_service_auth_authority != null ? var.app_service_auth_authority : ""
    APP_SERVICE_AUTH_AUDIENCE             = var.app_service_auth_audience != null ? var.app_service_auth_audience : ""
  }
}

# Outbound regional VNet integration for App Service
resource "azurerm_app_service_virtual_network_swift_connection" "app_service" {
  count          = var.enable_app_service ? 1 : 0
  app_service_id = azurerm_linux_web_app.app_service[0].id
  subnet_id      = azurerm_subnet.app_service_outbound[0].id
}

# Inbound Private Endpoint for App Service ingress
resource "azurerm_private_endpoint" "app_service_inbound" {
  count               = var.enable_app_service ? 1 : 0
  name                = "${local.prefix}-app-pe"
  location            = local.location
  resource_group_name = local.resource_group_name
  subnet_id           = azurerm_subnet.private_endpoints.id
  tags                = local.tags

  private_service_connection {
    name                           = "app-service"
    private_connection_resource_id = azurerm_linux_web_app.app_service[0].id
    subresource_names              = ["sites"]
    is_manual_connection           = false
  }

  private_dns_zone_group {
    name                 = "app-service-dns-zone-group"
    private_dns_zone_ids = [azurerm_private_dns_zone.functions.id]
  }
}

# Dedicated input container for business workflows
resource "azurerm_storage_container" "workflow_input" {
  count                 = var.enable_app_service ? 1 : 0
  name                  = "workflow-input"
  storage_account_id    = azurerm_storage_account.poc.id
  container_access_type = "private"
}

# App Service Managed Identity granted Storage Blob Data Contributor to manage upload inputs
resource "azurerm_role_assignment" "app_service_blob_contributor" {
  count                = var.enable_app_service ? 1 : 0
  scope                = azurerm_storage_account.poc.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.app_service[0].principal_id
}

# Grant Validation VM Managed Identity read and publish permissions on App Service
resource "azurerm_role_assignment" "validation_app_service" {
  count              = var.enable_app_service ? 1 : 0
  scope              = azurerm_linux_web_app.app_service[0].id
  role_definition_id = azurerm_role_definition.validation_function.role_definition_resource_id
  principal_id       = azurerm_linux_virtual_machine.validation.identity[0].principal_id
}
