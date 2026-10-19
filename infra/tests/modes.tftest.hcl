mock_provider "azapi" {
  mock_resource "azapi_resource" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/shared-rg/providers/Microsoft.DurableTask/schedulers/scheduler-test"
      output = {
        properties = { endpoint = "https://scheduler-test.region.durabletask.io" }
      }
    }
  }
}

mock_provider "azurerm" {
  mock_resource "azurerm_virtual_network" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test-rg/providers/Microsoft.Network/virtualNetworks/test-vnet" }
  }
  mock_resource "azurerm_subnet" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test-rg/providers/Microsoft.Network/virtualNetworks/test-vnet/subnets/vm" }
  }
  mock_resource "azurerm_network_security_group" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test-rg/providers/Microsoft.Network/networkSecurityGroups/vm" }
  }
  mock_resource "azurerm_nat_gateway" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test-rg/providers/Microsoft.Network/natGateways/egress" }
  }
  mock_resource "azurerm_public_ip" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test-rg/providers/Microsoft.Network/publicIPAddresses/egress" }
  }
  mock_resource "azurerm_private_dns_zone" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test-rg/providers/Microsoft.Network/privateDnsZones/privatelink.azurewebsites.net" }
  }
  mock_resource "azurerm_network_interface" {
    defaults = {
      id                 = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test-rg/providers/Microsoft.Network/networkInterfaces/vm"
      private_ip_address = "10.80.1.4"
    }
  }
  mock_resource "azurerm_linux_virtual_machine" {
    defaults = {
      id       = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test-rg/providers/Microsoft.Compute/virtualMachines/vm"
      identity = { principal_id = "33333333-3333-3333-3333-333333333333", tenant_id = "22222222-2222-2222-2222-222222222222" }
    }
  }
  mock_resource "azurerm_role_definition" {
    defaults = { role_definition_resource_id = "/subscriptions/00000000-0000-0000-0000-000000000000/providers/Microsoft.Authorization/roleDefinitions/44444444-4444-4444-4444-444444444444" }
  }
  mock_resource "azurerm_user_assigned_identity" {
    defaults = {
      id           = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-uami"
      principal_id = "77777777-7777-7777-7777-777777777777"
      client_id    = "88888888-8888-8888-8888-888888888888"
    }
  }
  mock_data "azurerm_user_assigned_identity" {
    defaults = {
      principal_id = "99999999-9999-9999-9999-999999999999"
      client_id    = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
    }
  }
  mock_resource "azurerm_storage_account" {
    defaults = {
      id                    = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/poctest001"
      primary_blob_endpoint = "https://poctest001.blob.core.windows.net/"
    }
  }
  mock_resource "azurerm_service_plan" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test-rg/providers/Microsoft.Web/serverFarms/test-plan" }
  }
  mock_data "azurerm_service_plan" {
    defaults = {
      id       = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/shared-plan-rg/providers/Microsoft.Web/serverFarms/shared-linux-plan"
      os_type  = "Linux"
      location = "test-region"
    }
  }
  mock_resource "azurerm_log_analytics_workspace" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test-rg/providers/Microsoft.OperationalInsights/workspaces/test-logs" }
  }
  mock_resource "azurerm_linux_function_app" {
    defaults = {
      id               = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test-rg/providers/Microsoft.Web/sites/test-function"
      default_hostname = "test-function.azurewebsites.net"
      identity         = { principal_id = "11111111-1111-1111-1111-111111111111", tenant_id = "22222222-2222-2222-2222-222222222222" }
    }
  }
  mock_resource "azurerm_linux_web_app" {
    defaults = {
      id               = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test-rg/providers/Microsoft.Web/sites/test-app"
      default_hostname = "test-app.azurewebsites.net"
    }
  }
  mock_resource "azurerm_app_service_virtual_network_swift_connection" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test-rg/providers/Microsoft.Web/sites/test-app/networkConfig/virtualNetwork"
    }
  }
  mock_data "azurerm_resource_group" {
    defaults = { name = "shared-rg", location = "test-region" }
  }
  mock_resource "azurerm_batch_account" {
    defaults = {
      id               = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/test-rg/providers/Microsoft.Batch/batchAccounts/testbatch"
      account_endpoint = "testbatch.test-region.batch.azure.com"
    }
  }
}

mock_provider "azurerm" {
  alias = "gallery"
  mock_data "azurerm_shared_image_version" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/image-rg/providers/Microsoft.Compute/galleries/gallery/images/windows/versions/1.0.0"
    }
  }
}

variables {
  app_service_workflow_function_key_secret_uri = "https://test-vault.vault.azure.net/secrets/workflow-key"
  app_service_auth_authority                   = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0"
  app_service_auth_audience                    = "api://test-app-service"
  validation_vm_ssh_public_key                 = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA fixture"
  subscription_id                              = "00000000-0000-0000-0000-000000000000"
  resource_group_name                          = "shared-rg"
  name_suffix                                  = "test001"
  batch_account_id                             = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/batch-rg/providers/Microsoft.Batch/batchAccounts/testbatch"
  batch_endpoint                               = "https://testbatch.test-region.batch.azure.com"
  batch_pool_id                                = "shared-windows"
}

run "workflow_auth_default_is_opt_in" {
  command = plan
  assert {
    condition     = length(azurerm_linux_function_app.poc.auth_settings_v2) == 0 && azurerm_linux_function_app.poc.app_settings.WORKFLOW_AUTH_MODE == "PocFunctionKey" && !contains(keys(azurerm_linux_function_app.poc.app_settings), "WEBSITE_AUTH_ENABLED")
    error_message = "Default must preserve Function key mode without spoofing platform authentication guards."
  }
}

run "workflow_auth_strict" {
  command = plan
  variables {
    workflow_auth_mode                    = "EntraEasyAuth"
    workflow_auth_tenant_id               = "11111111-1111-1111-1111-111111111111"
    workflow_auth_api_client_id           = "22222222-2222-2222-2222-222222222222"
    workflow_auth_audience                = "api://workflow"
    workflow_auth_allowed_backend_app_ids = ["33333333-3333-3333-3333-333333333333"]
    workflow_auth_required_app_role       = "Workflow.Invoke"
  }
  assert {
    condition = alltrue([
      azurerm_linux_function_app.poc.auth_settings_v2[0].auth_enabled,
      azurerm_linux_function_app.poc.auth_settings_v2[0].require_authentication,
      azurerm_linux_function_app.poc.auth_settings_v2[0].require_https,
      azurerm_linux_function_app.poc.auth_settings_v2[0].unauthenticated_action == "Return401",
      azurerm_linux_function_app.poc.auth_settings_v2[0].active_directory_v2[0].tenant_auth_endpoint == "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0",
      azurerm_linux_function_app.poc.auth_settings_v2[0].active_directory_v2[0].client_id == var.workflow_auth_api_client_id,
      azurerm_linux_function_app.poc.auth_settings_v2[0].active_directory_v2[0].allowed_audiences == tolist([azurerm_linux_function_app.poc.app_settings.WORKFLOW_AUTH_AUDIENCE]),
      join(",", azurerm_linux_function_app.poc.auth_settings_v2[0].active_directory_v2[0].allowed_applications) == azurerm_linux_function_app.poc.app_settings.WORKFLOW_AUTH_ALLOWED_BACKEND_APP_IDS,
      azurerm_linux_function_app.poc.app_settings.WORKFLOW_AUTH_TENANT_ID == var.workflow_auth_tenant_id,
      azurerm_linux_function_app.poc.app_settings.WORKFLOW_AUTH_REQUIRED_APP_ROLE == var.workflow_auth_required_app_role,
      !azurerm_linux_function_app.poc.auth_settings_v2[0].login[0].token_store_enabled,
      !contains(keys(azurerm_linux_function_app.poc.app_settings), "WEBSITE_AUTH_ENABLED")
    ])
    error_message = "Strict EasyAuth and application policy must use the same explicit tenant, single audience, backend clients and role."
  }
}

run "workflow_auth_missing_configuration_rejected" {
  command = plan
  variables { workflow_auth_mode = "EntraEasyAuth" }
  expect_failures = [azurerm_linux_function_app.poc]
}

run "workflow_auth_invalid_backend_rejected" {
  command = plan
  variables {
    workflow_auth_mode                    = "EntraEasyAuth"
    workflow_auth_tenant_id               = "11111111-1111-1111-1111-111111111111"
    workflow_auth_api_client_id           = "22222222-2222-2222-2222-222222222222"
    workflow_auth_audience                = "api://workflow"
    workflow_auth_allowed_backend_app_ids = ["33333333-3333-3333-3333-333333333333", "33333333-3333-3333-3333-333333333333"]
    workflow_auth_required_app_role       = "Workflow.Invoke"
  }
  expect_failures = [azurerm_linux_function_app.poc]
}

run "workflow_auth_bad_guid_rejected" {
  command = plan
  variables {
    workflow_auth_mode                    = "EntraEasyAuth"
    workflow_auth_tenant_id               = "00000000-0000-0000-0000-000000000000"
    workflow_auth_api_client_id           = "not-a-guid"
    workflow_auth_audience                = "api://workflow"
    workflow_auth_allowed_backend_app_ids = ["33333333-3333-3333-3333-333333333333"]
    workflow_auth_required_app_role       = "Workflow.Invoke"
  }
  expect_failures = [azurerm_linux_function_app.poc]
}

run "workflow_auth_empty_policy_rejected" {
  command = plan
  variables {
    workflow_auth_mode                    = "EntraEasyAuth"
    workflow_auth_tenant_id               = "11111111-1111-1111-1111-111111111111"
    workflow_auth_api_client_id           = "22222222-2222-2222-2222-222222222222"
    workflow_auth_audience                = " "
    workflow_auth_allowed_backend_app_ids = []
    workflow_auth_required_app_role       = " "
  }
  expect_failures = [azurerm_linux_function_app.poc]
}

run "workflow_auth_invalid_mode_rejected" {
  command = plan
  variables { workflow_auth_mode = "entraeasyauth" }
  expect_failures = [var.workflow_auth_mode]
}

run "existing_preserves_shared_resources" {
  command = apply
  assert {
    condition     = length(azurerm_role_assignment.durable_observer) == 0
    error_message = "Observer data access must never be inferred or created by default."
  }
  assert {
    condition     = length(azurerm_service_plan.poc) == 1 && length(data.azurerm_service_plan.existing) == 0 && azurerm_linux_function_app.poc.service_plan_id == azurerm_service_plan.poc[0].id
    error_message = "The default must retain a managed plan, using the moved address without changing its identity."
  }
  assert {
    condition     = length(azapi_resource.durable_scheduler) == 0 && length(azapi_resource.durable_task_hub) == 0 && length(azapi_resource.durable_retention) == 0 && length(azurerm_role_assignment.durable_task_hub) == 0 && length(azurerm_private_endpoint.durable_scheduler) == 0 && length(azurerm_private_dns_zone.dts) == 0 && output.dts_private_endpoint_id == null && output.deployment.durable_backend == "azureStorage" && output.deployment.task_hub_name == "PocBatchHub" && output.deployment.durable_scheduler_endpoint == null && !contains(keys(azurerm_linux_function_app.poc.app_settings), "DURABLE_TASK_SCHEDULER_CONNECTION_STRING") && !contains(keys(azurerm_linux_function_app.poc.app_settings), "AzureFunctionsJobHost__extensions__durableTask__storageProvider__type") && !contains(keys(azurerm_linux_function_app.poc.app_settings), "AzureFunctionsJobHost__extensions__durableTask__hubName")
    error_message = "Default Azure Storage must preserve the existing app settings and create no DTS resources or permissions."
  }
  assert {
    condition     = length(azurerm_batch_account.poc) == 0 && length(azurerm_batch_pool.poc) == 0 && length(azurerm_resource_group.poc) == 0
    error_message = "Existing mode must never manage shared Batch or RG resources."
  }
  assert {
    condition     = output.deployment.batch_account_name == "testbatch" && output.deployment.batch_resource_group_name == "batch-rg" && output.deployment.batch_node_count == null && output.deployment.batch_node_type == null && output.deployment.batch_dedicated_node_count == null && output.deployment.batch_spot_node_count == null
    error_message = "Existing deployment metadata must parse the account ID without lookups."
  }
  assert {
    condition     = azurerm_linux_function_app.poc.app_settings.BATCH_POOL_ID == "shared-windows" && azurerm_role_assignment.batch.scope == var.batch_account_id
    error_message = "Existing app configuration and RBAC must use the supplied account/pool."
  }
  assert {
    condition     = azurerm_linux_function_app.poc.storage_account_name == azurerm_storage_account.poc.name && azurerm_linux_function_app.poc.storage_uses_managed_identity == true && !contains(keys(azurerm_linux_function_app.poc.app_settings), "AzureWebJobsStorage__accountName")
    error_message = "Host storage account name must come only from the provider-managed identity attributes, not app_settings."
  }
  assert {
    condition     = azurerm_linux_function_app.poc.app_settings.BATCH_POOL_UAMI_RESOURCE_ID == "" && length(azurerm_role_assignment.batch_pool_blob_reader) == 0 && length(azurerm_role_assignment.batch_pool_blob_output_contributor) == 0 && azurerm_linux_function_app.poc.app_settings.WORKFLOW_INPUT_CONTAINER == "workflow-input"
    error_message = "Existing mode must not assign Batch pool blob roles and must leave BATCH_POOL_UAMI_RESOURCE_ID empty while providing WORKFLOW_INPUT_CONTAINER."
  }
}

run "existing_cross_rg_service_plan_is_read_only" {
  command = apply
  variables {
    existing_service_plan_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/shared-plan-rg/providers/Microsoft.Web/serverFarms/shared-linux-plan"
  }
  assert {
    condition     = length(azurerm_service_plan.poc) == 0 && length(data.azurerm_service_plan.existing) == 1 && data.azurerm_service_plan.existing[0].resource_group_name == "shared-plan-rg" && data.azurerm_service_plan.existing[0].name == "shared-linux-plan" && azurerm_linux_function_app.poc.service_plan_id == var.existing_service_plan_id && output.deployment.service_plan_id == var.existing_service_plan_id
    error_message = "An existing cross-RG plan must be read through a data source, never created or managed by this environment."
  }
}

run "existing_windows_service_plan_rejected" {
  command = plan
  variables {
    existing_service_plan_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/shared-plan-rg/providers/Microsoft.Web/serverFarms/shared-linux-plan"
  }
  override_data {
    target = data.azurerm_service_plan.existing[0]
    values = {
      id       = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/shared-plan-rg/providers/Microsoft.Web/serverFarms/shared-linux-plan"
      os_type  = "Windows"
      location = "test-region"
    }
  }
  expect_failures = [azurerm_linux_function_app.poc]
}

run "existing_other_region_service_plan_rejected" {
  command = plan
  variables {
    existing_service_plan_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/shared-plan-rg/providers/Microsoft.Web/serverFarms/shared-linux-plan"
  }
  override_data {
    target = data.azurerm_service_plan.existing[0]
    values = {
      id       = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/shared-plan-rg/providers/Microsoft.Web/serverFarms/shared-linux-plan"
      os_type  = "Linux"
      location = "westus2"
    }
  }
  expect_failures = [azurerm_linux_function_app.poc]
}

run "invalid_existing_service_plan_id_rejected" {
  command = plan
  variables { existing_service_plan_id = "shared-linux-plan" }
  expect_failures = [var.existing_service_plan_id]
}

run "durable_task_scheduler_consumption_and_identity" {
  command = apply
  assert {
    condition     = length(azurerm_role_assignment.durable_observer) == 0
    error_message = "Selecting DTS alone must not grant observer access."
  }
  variables {
    durable_backend       = "durableTaskScheduler"
    durable_task_hub_name = "testhub"
  }
  override_resource {
    target = azapi_resource.durable_task_hub[0]
    values = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/shared-rg/providers/Microsoft.DurableTask/schedulers/scheduler-test/taskHubs/testhub"
    }
  }
  assert {
    condition     = length(azapi_resource.durable_scheduler) == 1 && length(azapi_resource.durable_task_hub) == 1 && length(azapi_resource.durable_retention) == 1 && azapi_resource.durable_scheduler[0].type == "Microsoft.DurableTask/schedulers@2026-02-01" && azapi_resource.durable_scheduler[0].body.properties.sku.name == "Consumption" && azapi_resource.durable_scheduler[0].body.properties.publicNetworkAccess == "Enabled" && azapi_resource.durable_scheduler[0].body.properties.ipAllowlist[0] == "0.0.0.0/0"
    error_message = "DTS must create a Consumption scheduler using the stable API and explicit public outbound connectivity."
  }
  assert {
    condition     = azapi_resource.durable_task_hub[0].name == "testhub" && azapi_resource.durable_task_hub[0].parent_id == azapi_resource.durable_scheduler[0].id && azapi_resource.durable_retention[0].name == "default" && azapi_resource.durable_retention[0].body.properties.retentionPolicies[0].retentionPeriodInDays == 30
    error_message = "The selected hub and 30-day default retention must belong to the scheduler."
  }
  assert {
    condition     = azurerm_role_assignment.durable_task_hub[0].scope == azapi_resource.durable_task_hub[0].id && endswith(azurerm_role_assignment.durable_task_hub[0].scope, "/taskHubs/testhub") && azurerm_role_assignment.durable_task_hub[0].role_definition_name == "Durable Task Data Contributor" && azurerm_role_assignment.durable_task_hub[0].principal_id == azurerm_linux_function_app.poc.identity[0].principal_id && length(azurerm_role_assignment.storage) == 3
    error_message = "Function MI must receive Data Contributor only at hub scope while retaining host Storage permissions."
  }
  assert {
    condition     = azurerm_linux_function_app.poc.app_settings.AzureFunctionsJobHost__extensions__durableTask__storageProvider__type == "azureManaged" && azurerm_linux_function_app.poc.app_settings.AzureFunctionsJobHost__extensions__durableTask__storageProvider__connectionStringName == "DURABLE_TASK_SCHEDULER_CONNECTION_STRING" && azurerm_linux_function_app.poc.app_settings.AzureFunctionsJobHost__extensions__durableTask__hubName == "testhub" && azurerm_linux_function_app.poc.app_settings.DURABLE_TASK_SCHEDULER_CONNECTION_STRING == "Endpoint=https://scheduler-test.region.durabletask.io;TaskHub=testhub;Authentication=ManagedIdentity" && azurerm_linux_function_app.poc.public_network_access_enabled == false
    error_message = "DTS must use the MI connection and selected hub while Function ingress remains private."
  }
  assert {
    condition     = output.deployment.durable_backend == "durableTaskScheduler" && output.deployment.task_hub_name == "testhub" && output.deployment.durable_scheduler_endpoint == "https://scheduler-test.region.durabletask.io" && output.deployment.durable_task_hub_id == azapi_resource.durable_task_hub[0].id
    error_message = "Non-secret deployment metadata must identify the selected backend, scheduler and hub."
  }
  assert {
    condition     = length(azurerm_private_endpoint.durable_scheduler) == 1 && length(azurerm_private_dns_zone.dts) == 1 && azurerm_private_dns_zone.dts[0].name == "privatelink.durabletask.io" && contains(azurerm_private_endpoint.durable_scheduler[0].private_service_connection[0].subresource_names, "scheduler") && contains(azurerm_private_endpoint.durable_scheduler[0].private_dns_zone_group[0].private_dns_zone_ids, azurerm_private_dns_zone.dts[0].id) && output.deployment.dts_public_network_access_enabled == true && output.dts_private_endpoint_id == azurerm_private_endpoint.durable_scheduler[0].id
    error_message = "DTS must provision Private Endpoint with subresource 'scheduler' and Private DNS Zone."
  }
}

run "dts_public_network_access_can_be_disabled" {
  command = plan
  variables {
    durable_backend                   = "durableTaskScheduler"
    durable_task_hub_name             = "testhub"
    dts_public_network_access_enabled = false
  }
  override_resource {
    target = azapi_resource.durable_task_hub[0]
    values = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/shared-rg/providers/Microsoft.DurableTask/schedulers/scheduler-test/taskHubs/testhub"
    }
  }
  assert {
    condition     = azapi_resource.durable_scheduler[0].body.properties.publicNetworkAccess == "Disabled" && length(azapi_resource.durable_scheduler[0].body.properties.ipAllowlist) == 0 && output.deployment.dts_public_network_access_enabled == false
    error_message = "DTS public network access must be Disabled with empty ipAllowlist when disabled."
  }
}

run "explicit_durable_observer_is_hub_reader_only" {
  command = apply
  variables {
    durable_backend               = "durableTaskScheduler"
    durable_task_hub_name         = "testhub"
    durable_observer_principal_id = "66666666-6666-6666-6666-666666666666"
  }
  override_resource {
    target = azapi_resource.durable_task_hub[0]
    values = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/shared-rg/providers/Microsoft.DurableTask/schedulers/scheduler-test/taskHubs/testhub"
    }
  }
  assert {
    condition     = length(azurerm_role_assignment.durable_observer) == 1 && azurerm_role_assignment.durable_observer[0].principal_id == var.durable_observer_principal_id && azurerm_role_assignment.durable_observer[0].role_definition_name == "Durable Task Data Reader" && azurerm_role_assignment.durable_observer[0].scope == azapi_resource.durable_task_hub[0].id && endswith(azurerm_role_assignment.durable_observer[0].scope, "/taskHubs/testhub")
    error_message = "Explicit observer access must be read-only and limited to the selected task hub."
  }
}

run "observer_on_azure_storage_rejected" {
  command = plan
  variables { durable_observer_principal_id = "66666666-6666-6666-6666-666666666666" }
  expect_failures = [var.durable_observer_principal_id]
}

run "invalid_durable_backend_rejected" {
  command = plan
  variables { durable_backend = "invalid" }
  expect_failures = [var.durable_backend]
}

run "invalid_durable_hub_rejected" {
  command = plan
  variables { durable_task_hub_name = "invalid-hub" }
  expect_failures = [var.durable_task_hub_name]
}

run "private_function_and_validation_vm" {
  command = apply
  assert {
    condition     = length(azurerm_role_assignment.validation_operator_control) == 0
    error_message = "Operator permissions must not be inferred or granted when no principal is explicitly supplied."
  }
  assert {
    condition     = azurerm_linux_function_app.poc.public_network_access_enabled == false && output.deployment.network_mode == "private-function-vm"
    error_message = "Functions public ingress must always be disabled, without a public fallback mode."
  }
  assert {
    condition     = azurerm_private_endpoint.functions.private_service_connection[0].private_connection_resource_id == azurerm_linux_function_app.poc.id && length(azurerm_private_endpoint.functions.private_service_connection[0].subresource_names) == 1 && contains(azurerm_private_endpoint.functions.private_service_connection[0].subresource_names, "sites") && contains(azurerm_private_endpoint.functions.private_dns_zone_group[0].private_dns_zone_ids, azurerm_private_dns_zone.functions.id)
    error_message = "Function/SCM ingress must use the sites Private Endpoint and its DNS zone group."
  }
  assert {
    condition     = azurerm_private_dns_zone.functions.name == "privatelink.azurewebsites.net" && azurerm_private_dns_zone_virtual_network_link.functions.virtual_network_id == azurerm_virtual_network.validation.id && azurerm_private_dns_zone_virtual_network_link.functions.registration_enabled == false
    error_message = "Function private DNS must be linked to the VM VNet without dynamic VM registration."
  }
  assert {
    condition     = azurerm_storage_account.poc.public_network_access_enabled == true && output.deployment.storage_public_network_access_enabled == true
    error_message = "Storage public network access must default to true until private connectivity is verified."
  }
  assert {
    condition     = contains(azurerm_private_endpoint.storage_blob.private_service_connection[0].subresource_names, "blob") && contains(azurerm_private_endpoint.storage_queue.private_service_connection[0].subresource_names, "queue") && contains(azurerm_private_endpoint.storage_table.private_service_connection[0].subresource_names, "table")
    error_message = "Storage Private Endpoints must target blob, queue, and table subresources."
  }
  assert {
    condition     = azurerm_private_dns_zone.storage_blob.name == "privatelink.blob.core.windows.net" && azurerm_private_dns_zone.storage_queue.name == "privatelink.queue.core.windows.net" && azurerm_private_dns_zone.storage_table.name == "privatelink.table.core.windows.net"
    error_message = "Storage private DNS zones must be created for blob, queue, and table."
  }
  assert {
    condition     = azurerm_private_dns_zone_virtual_network_link.storage_blob.virtual_network_id == azurerm_virtual_network.validation.id && azurerm_private_dns_zone_virtual_network_link.storage_queue.virtual_network_id == azurerm_virtual_network.validation.id && azurerm_private_dns_zone_virtual_network_link.storage_table.virtual_network_id == azurerm_virtual_network.validation.id
    error_message = "Storage private DNS zones must be linked to the validation VNet."
  }
  assert {
    condition     = var.validation_vm_subnet_prefix != var.validation_private_endpoint_subnet_prefix && alltrue([for ip in azurerm_network_interface.validation_vm.ip_configuration : ip.public_ip_address_id == null]) && azurerm_linux_virtual_machine.validation.disable_password_authentication && azurerm_linux_virtual_machine.validation.identity[0].type == "SystemAssigned"
    error_message = "VM must use a separate subnet, no public IP, no password login, and a managed identity."
  }
  assert {
    condition     = length([for rule in azurerm_network_security_group.validation_vm.security_rule : rule if rule.direction == "Inbound" && rule.access == "Deny" && rule.priority == 100 && rule.source_address_prefix == "*" && rule.destination_address_prefix == "*"]) == 1 && azurerm_subnet.validation_vm.default_outbound_access_enabled == false && azurerm_subnet_nat_gateway_association.validation.subnet_id == azurerm_subnet.validation_vm.id && azurerm_nat_gateway_public_ip_association.validation.nat_gateway_id == azurerm_nat_gateway.validation.id
    error_message = "VM inbound must be denied and explicit NAT egress must be configured."
  }
  assert {
    condition     = azurerm_role_assignment.validation_function.scope == azurerm_linux_function_app.poc.id && !contains(azurerm_role_definition.validation_function.permissions[0].actions, "Microsoft.Web/sites/write") && contains(azurerm_role_definition.validation_function.permissions[0].actions, "Microsoft.Web/sites/publish/action") && azurerm_role_assignment.validation_batch.role_definition_name == "Azure Batch Account Reader" && azurerm_role_assignment.validation_batch.scope == var.batch_account_id && length(azurerm_role_assignment.validation_batch_contributor) == 0 && azurerm_role_assignment.validation_blob.role_definition_name == "Storage Blob Data Reader" && endswith(azurerm_role_assignment.validation_control.scope, "/containers/poc-vm-control") && azurerm_role_assignment.validation_control.role_definition_name == "Storage Blob Data Contributor"
    error_message = "VM identity must have scoped publishing/read rights, Reader-only Batch access in existing mode, and write only to the private control container."
  }
  assert {
    condition     = contains(azurerm_role_definition.validation_function.permissions[0].actions, "Microsoft.Web/sites/host/listkeys/action") && !contains(azurerm_role_definition.validation_function.permissions[0].actions, "Microsoft.Web/sites/restart/action") && !contains(azurerm_role_definition.validation_function.permissions[0].actions, "Microsoft.Web/sites/start/action") && !contains(azurerm_role_definition.validation_function.permissions[0].actions, "*")
    error_message = "Recovery observation may read host keys at Function scope but must not grant the VM restart/start or wildcard management rights."
  }
  assert {
    condition     = azurerm_linux_virtual_machine.validation.source_image_reference[0].publisher == "RedHat" && azurerm_linux_virtual_machine.validation.source_image_reference[0].offer == "RHEL" && azurerm_linux_virtual_machine.validation.source_image_reference[0].sku == "9-lvm-gen2" && strcontains(base64decode(azurerm_linux_virtual_machine.validation.custom_data), "packages.microsoft.com/config/rhel/9.0/packages-microsoft-prod.rpm") && strcontains(base64decode(azurerm_linux_virtual_machine.validation.custom_data), "dnf install -y azure-cli powershell") && strcontains(base64decode(azurerm_linux_virtual_machine.validation.custom_data), "poc-tools-ready") && output.deployment.vm_control_container_name == "poc-vm-control"
    error_message = "RHEL 9 Gen2 VM cloud-init must install official Microsoft RPM tools and expose the completion marker."
  }
  assert {
    condition     = azurerm_linux_function_app.poc.virtual_network_subnet_id == azurerm_subnet.functions_outbound.id && azurerm_linux_function_app.poc.site_config[0].vnet_route_all_enabled == true && azurerm_subnet.functions_outbound.delegation[0].service_delegation[0].name == "Microsoft.Web/serverFarms" && azurerm_subnet.batch_nodes.default_outbound_access_enabled == false
    error_message = "Functions must integrate with outbound subnet, route all traffic through VNet, and batch nodes subnet must have default outbound disabled."
  }
}

run "nvme_only_validation_vm" {
  command = plan
  variables {
    validation_vm_size                 = "Standard_F1as_v7"
    validation_vm_disk_controller_type = "NVMe"
  }
  assert {
    condition     = azurerm_linux_virtual_machine.validation.size == "Standard_F1as_v7" && azurerm_linux_virtual_machine.validation.disk_controller_type == "NVMe"
    error_message = "NVMe-only VM size must receive its explicitly configured disk controller."
  }
}

run "invalid_validation_vm_disk_controller_rejected" {
  command = plan
  variables { validation_vm_disk_controller_type = "invalid" }
  expect_failures = [var.validation_vm_disk_controller_type]
}

run "explicit_operator_has_control_container_access_only" {
  command = apply
  variables { validation_operator_principal_id = "55555555-5555-5555-5555-555555555555" }
  assert {
    condition     = length(azurerm_role_assignment.validation_operator_control) == 1 && azurerm_role_assignment.validation_operator_control[0].principal_id == var.validation_operator_principal_id && azurerm_role_assignment.validation_operator_control[0].scope == "${azurerm_storage_account.poc.id}/blobServices/default/containers/${azurerm_storage_container.vm_control.name}" && azurerm_role_assignment.validation_operator_control[0].role_definition_name == "Storage Blob Data Contributor" && azurerm_role_assignment.validation_blob.principal_id != var.validation_operator_principal_id && azurerm_role_assignment.validation_function.principal_id != var.validation_operator_principal_id && azurerm_role_assignment.validation_batch.principal_id != var.validation_operator_principal_id
    error_message = "The explicit operator must receive Contributor only on the private control container, without VM Function/Batch/results roles."
  }
}

run "invalid_operator_principal_rejected" {
  command = plan
  variables { validation_operator_principal_id = "not-an-object-id" }
  expect_failures = [var.validation_operator_principal_id]
}

run "private_key_rejected" {
  command = plan
  variables { validation_vm_ssh_public_key = "-----BEGIN OPENSSH PRIVATE KEY-----" }
  expect_failures = [var.validation_vm_ssh_public_key]
}

run "gallery_id_rejected_in_marketplace_mode" {
  command = plan
  variables {
    batch_mode               = "new"
    batch_account_id         = null
    batch_endpoint           = null
    batch_pool_id            = null
    batch_image_source       = "marketplace"
    batch_node_agent_sku_id  = "batch.node.windows amd64"
    marketplace_image        = { publisher = "MicrosoftWindowsServer", offer = "WindowsServer", sku = "2022-datacenter", version = "20348.2527.240607" }
    gallery_image_version_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/image-rg/providers/Microsoft.Compute/galleries/gallery/images/windows/versions/1.0.0"
  }
  expect_failures = [var.gallery_image_version_id]
}

run "gallery_id_rejected_in_existing_mode" {
  command = plan
  variables {
    gallery_image_version_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/image-rg/providers/Microsoft.Compute/galleries/gallery/images/windows/versions/1.0.0"
  }
  expect_failures = [var.gallery_image_version_id]
}

run "new_marketplace_creates_dedicated_resources" {
  command = apply
  variables {
    batch_mode                = "new"
    batch_account_id          = null
    batch_endpoint            = null
    batch_pool_id             = null
    create_resource_group     = true
    resource_group_name       = "poc-test"
    location                  = "test-region"
    batch_image_source        = "marketplace"
    batch_node_agent_sku_id   = "batch.node.windows amd64"
    marketplace_image         = { publisher = "MicrosoftWindowsServer", offer = "WindowsServer", sku = "2022-datacenter", version = "20348.2527.240607" }
    batch_diagnostics_enabled = true
    batch_node_count          = 2
    batch_max_tasks_per_node  = 3
  }
  assert {
    condition     = length(azurerm_batch_account.poc) == 1 && length(azurerm_batch_pool.poc) == 1 && length(azurerm_resource_group.poc) == 1 && length(data.azurerm_resource_group.existing) == 0
    error_message = "New mode must create isolated Batch resources and the explicitly requested RG."
  }
  assert {
    condition     = azurerm_batch_account.poc[0].pool_allocation_mode == "BatchService" && azurerm_batch_account.poc[0].allowed_authentication_modes == toset(["AAD"]) && azurerm_batch_account.poc[0].public_network_access_enabled == false
    error_message = "New Batch accounts must use BatchService, AAD only, and have public network access disabled."
  }
  assert {
    condition     = azurerm_batch_pool.poc[0].target_node_communication_mode == "Simplified" && azurerm_batch_pool.poc[0].fixed_scale[0].target_dedicated_nodes == 2 && azurerm_batch_pool.poc[0].fixed_scale[0].target_low_priority_nodes == 0 && azurerm_batch_pool.poc[0].max_tasks_per_node == 3 && length(azurerm_monitor_diagnostic_setting.batch) == 1 && length(azurerm_batch_pool.poc[0].network_configuration) == 1 && azurerm_batch_pool.poc[0].network_configuration[0].public_address_provisioning_type == "NoPublicIPAddresses" && azurerm_batch_pool.poc[0].network_configuration[0].subnet_id == azurerm_subnet.batch_nodes.id
    error_message = "Pool sizing, Simplified communication, opt-in diagnostics, and subnet integration with NoPublicIPAddresses must be wired."
  }
  assert {
    condition     = azurerm_batch_pool.poc[0].storage_image_reference[0].version == "20348.2527.240607" && length(data.azurerm_shared_image_version.existing) == 0
    error_message = "Marketplace must use the exact supplied version without gallery access."
  }
  assert {
    condition     = output.deployment.batch_endpoint == "https://testbatch.test-region.batch.azure.com" && output.deployment.batch_node_count == 2 && output.deployment.batch_node_type == "dedicated" && output.deployment.batch_dedicated_node_count == 2 && output.deployment.batch_spot_node_count == 0
    error_message = "New deployment metadata must expose the actual account endpoint and target count."
  }
  assert {
    condition     = length(azurerm_private_dns_zone.batch) == 1 && azurerm_private_dns_zone.batch[0].name == "privatelink.batch.azure.com" && length(azurerm_private_endpoint.batch_account) == 1 && length(azurerm_private_endpoint.batch_node_management) == 1 && contains(azurerm_private_endpoint.batch_account[0].private_service_connection[0].subresource_names, "batchAccount") && contains(azurerm_private_endpoint.batch_node_management[0].private_service_connection[0].subresource_names, "nodeManagement")
    error_message = "New mode must provision Batch private DNS and private endpoints for batchAccount and nodeManagement."
  }
  assert {
    condition     = azurerm_role_assignment.validation_batch.role_definition_name == "Azure Batch Account Reader" && length(azurerm_role_assignment.validation_batch_contributor) == 1 && azurerm_role_assignment.validation_batch_contributor[0].role_definition_name == "Azure Batch Account Contributor"
    error_message = "New mode must grant VM identity both Reader and Contributor on the dedicated Batch account for autoscale operations."
  }
  assert {
    condition     = azurerm_batch_pool.poc[0].identity[0].type == "UserAssigned" && contains(azurerm_batch_pool.poc[0].identity[0].identity_ids, azurerm_user_assigned_identity.batch_pool.id)
    error_message = "Batch pool must be configured with UserAssigned identity."
  }
  assert {
    condition     = azurerm_role_assignment.batch_pool_blob_reader[0].role_definition_name == "Storage Blob Data Reader" && azurerm_role_assignment.batch_pool_blob_reader[0].principal_id == azurerm_user_assigned_identity.batch_pool.principal_id && azurerm_role_assignment.batch_pool_blob_output_contributor[0].role_definition_name == "Storage Blob Data Contributor" && azurerm_role_assignment.batch_pool_blob_output_contributor[0].principal_id == azurerm_user_assigned_identity.batch_pool.principal_id
    error_message = "Batch pool UAMI must receive Reader on storage account and Contributor on workflow-output container."
  }
  assert {
    condition     = azurerm_linux_function_app.poc.app_settings.BATCH_POOL_UAMI_RESOURCE_ID == azurerm_user_assigned_identity.batch_pool.id && azurerm_linux_function_app.poc.app_settings.BATCH_OUTPUT_CONTAINER == "workflow-output" && azurerm_linux_function_app.poc.app_settings.BLOB_SERVICE_ENDPOINT == azurerm_storage_account.poc.primary_blob_endpoint
    error_message = "Function app settings must include Batch storage options."
  }
}

run "new_gallery_uses_existing_version" {
  command = apply
  variables {
    batch_mode               = "new"
    batch_account_id         = null
    batch_endpoint           = null
    batch_pool_id            = null
    gallery_image_version_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/image-rg/providers/Microsoft.Compute/galleries/gallery/images/windows/versions/1.0.0"
    batch_node_agent_sku_id  = "batch.node.windows amd64"
  }
  assert {
    condition     = azurerm_batch_pool.poc[0].storage_image_reference[0].id == data.azurerm_shared_image_version.existing[0].id && data.azurerm_shared_image_version.existing[0].name == "1.0.0"
    error_message = "Gallery mode must read and use the real existing version, not an evidence-only variable."
  }
  assert {
    condition     = length(azurerm_resource_group.poc) == 0 && length(azurerm_monitor_diagnostic_setting.batch) == 0
    error_message = "Existing RG and disabled diagnostics must remain unmanaged."
  }
}

run "missing_existing_account_rejected" {
  command = plan
  variables { batch_account_id = null }
  expect_failures = [var.batch_account_id]
}

run "new_cannot_reuse_shared_account_id" {
  command = plan
  variables {
    batch_mode               = "new"
    batch_endpoint           = null
    batch_pool_id            = null
    batch_node_agent_sku_id  = "batch.node.windows amd64"
    gallery_image_version_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/image-rg/providers/Microsoft.Compute/galleries/gallery/images/windows/versions/1.0.0"
  }
  expect_failures = [var.batch_account_id]
}

run "marketplace_latest_rejected" {
  command = plan
  variables {
    batch_mode              = "new"
    batch_account_id        = null
    batch_endpoint          = null
    batch_pool_id           = null
    batch_image_source      = "marketplace"
    batch_node_agent_sku_id = "batch.node.windows amd64"
    marketplace_image       = { publisher = "MicrosoftWindowsServer", offer = "WindowsServer", sku = "2022-datacenter", version = "latest" }
  }
  expect_failures = [var.marketplace_image]
}

run "zero_nodes_rejected" {
  command = plan
  variables { batch_node_count = 0 }
  expect_failures = [var.batch_node_count]
}

run "spot_pool_has_no_dedicated_fallback" {
  command = apply
  variables {
    batch_mode               = "new"
    batch_account_id         = null
    batch_endpoint           = null
    batch_pool_id            = null
    batch_node_type          = "spot"
    batch_node_count         = 1
    gallery_image_version_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/image-rg/providers/Microsoft.Compute/galleries/gallery/images/windows/versions/1.0.0"
    batch_node_agent_sku_id  = "batch.node.windows amd64"
  }
  assert {
    condition     = azurerm_batch_pool.poc[0].fixed_scale[0].target_dedicated_nodes == 0 && azurerm_batch_pool.poc[0].fixed_scale[0].target_low_priority_nodes == 1 && length(azurerm_batch_pool.poc[0].auto_scale) == 0
    error_message = "Spot must allocate only the requested low-priority target, without dedicated fallback or autoscale."
  }
  assert {
    condition     = output.deployment.batch_node_type == "spot" && output.deployment.batch_node_count == 1 && output.deployment.batch_dedicated_node_count == 0 && output.deployment.batch_spot_node_count == 1
    error_message = "Deployment readiness must receive separate Spot and dedicated targets with the unchanged total."
  }
}

run "existing_ignores_spot_sizing" {
  command = apply
  variables {
    batch_node_type  = "spot"
    batch_node_count = 3
  }
  assert {
    condition     = length(azurerm_batch_account.poc) == 0 && length(azurerm_batch_pool.poc) == 0 && length(data.azurerm_shared_image_version.existing) == 0 && length(azurerm_monitor_diagnostic_setting.batch) == 0 && output.deployment.batch_node_type == null && output.deployment.batch_node_count == null && output.deployment.batch_dedicated_node_count == null && output.deployment.batch_spot_node_count == null && azurerm_linux_function_app.poc.app_settings.BATCH_POOL_ID == "shared-windows"
    error_message = "Spot selection in existing mode must not create or resize shared Batch/image resources or claim a managed target."
  }
}

run "invalid_node_type_rejected" {
  command = plan
  variables { batch_node_type = "lowpriority" }
  expect_failures = [var.batch_node_type]
}

run "spot_autoscale_pool_zero_to_target" {
  command = apply
  variables {
    batch_mode                          = "new"
    batch_account_id                    = null
    batch_endpoint                      = null
    batch_pool_id                       = null
    batch_node_type                     = "spot"
    batch_scale_mode                    = "autoscale"
    batch_autoscale_evaluation_interval = "PT5M"
    batch_node_count                    = 1
    gallery_image_version_id            = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/image-rg/providers/Microsoft.Compute/galleries/gallery/images/windows/versions/1.0.0"
    batch_node_agent_sku_id             = "batch.node.windows amd64"
  }
  assert {
    condition     = length(azurerm_batch_pool.poc[0].fixed_scale) == 0 && length(azurerm_batch_pool.poc[0].auto_scale) == 1 && azurerm_batch_pool.poc[0].auto_scale[0].evaluation_interval == "PT5M" && strcontains(azurerm_batch_pool.poc[0].auto_scale[0].formula, "$TargetDedicatedNodes = 0;") && strcontains(azurerm_batch_pool.poc[0].auto_scale[0].formula, "$TargetLowPriorityNodes = $tasks > 0 ? 1 : 0;")
    error_message = "Spot autoscale pool must configure 0-to-1 low-priority autoscale without fixed_scale."
  }
  assert {
    condition     = output.deployment.batch_scale_mode == "autoscale" && output.deployment.batch_node_count == 0 && output.deployment.batch_max_node_count == 1 && output.deployment.batch_dedicated_node_count == null && output.deployment.batch_spot_node_count == null
    error_message = "Autoscale deployment metadata must expose 0 initial minimum nodes and max node count."
  }
}

run "invalid_batch_scale_mode_rejected" {
  command = plan
  variables { batch_scale_mode = "manual" }
  expect_failures = [var.batch_scale_mode]
}

run "invalid_autoscale_interval_rejected" {
  command = plan
  variables { batch_autoscale_evaluation_interval = "PT1M" }
  expect_failures = [var.batch_autoscale_evaluation_interval]
}

run "storage_public_network_access_can_be_disabled" {
  command = plan
  variables {
    storage_public_network_access_enabled = false
  }
  assert {
    condition     = azurerm_storage_account.poc.public_network_access_enabled == false && output.deployment.storage_public_network_access_enabled == false
    error_message = "Storage public network access must be configurable to false."
  }
}

run "app_service_disabled_by_default" {
  command = plan
  assert {
    condition     = length(azurerm_service_plan.app_service) == 0 && length(azurerm_linux_web_app.app_service) == 0 && length(azurerm_user_assigned_identity.app_service) == 0 && length(azurerm_private_endpoint.app_service_inbound) == 0 && length(azurerm_app_service_virtual_network_swift_connection.app_service) == 0 && length(azurerm_storage_container.workflow_input) == 0 && output.deployment.enable_app_service == false && output.deployment.app_service_name == null
    error_message = "App service resources must not be created when enable_app_service is false."
  }
}

run "app_service_enabled_provisions_isolated_resources" {
  command = plan
  variables {
    enable_app_service = true
    app_service_sku    = "B1"
  }
  assert {
    condition     = length(azurerm_service_plan.app_service) == 1 && azurerm_service_plan.app_service[0].sku_name == "B1" && azurerm_service_plan.app_service[0].os_type == "Linux"
    error_message = "App service plan must be created with specified SKU and Linux os_type."
  }
  assert {
    condition     = length(azurerm_linux_web_app.app_service) == 1 && azurerm_linux_web_app.app_service[0].https_only == true && azurerm_linux_web_app.app_service[0].public_network_access_enabled == false
    error_message = "App service must be created with https_only and public network access disabled."
  }
  assert {
    condition     = length(azurerm_user_assigned_identity.app_service) == 1 && azurerm_linux_web_app.app_service[0].identity[0].type == "UserAssigned" && length(azurerm_linux_web_app.app_service[0].identity[0].identity_ids) == 1
    error_message = "App service must be configured with UserAssigned identity."
  }
  assert {
    condition     = length(azurerm_private_endpoint.app_service_inbound) == 1 && contains(azurerm_private_endpoint.app_service_inbound[0].private_service_connection[0].subresource_names, "sites") && contains(azurerm_private_endpoint.app_service_inbound[0].private_dns_zone_group[0].private_dns_zone_ids, azurerm_private_dns_zone.functions.id)
    error_message = "App service inbound private endpoint must link to sites and privatelink.azurewebsites.net DNS zone."
  }
  assert {
    condition     = length(azurerm_app_service_virtual_network_swift_connection.app_service) == 1 && length(azurerm_subnet.app_service_outbound) == 1
    error_message = "App service must configure outbound regional VNet integration on dedicated outbound subnet."
  }
  assert {
    condition     = length(azurerm_storage_container.workflow_input) == 1 && azurerm_storage_container.workflow_input[0].name == "workflow-input" && length(azurerm_role_assignment.app_service_blob_contributor) == 1 && azurerm_role_assignment.app_service_blob_contributor[0].role_definition_name == "Storage Blob Data Contributor"
    error_message = "App service must provision workflow-input container and grant Storage Blob Data Contributor to UAMI."
  }
  assert {
    condition     = azurerm_linux_web_app.app_service[0].app_settings.BLOB_SERVICE_ENDPOINT == azurerm_storage_account.poc.primary_blob_endpoint && azurerm_linux_web_app.app_service[0].app_settings.WORKFLOW_INPUT_CONTAINER == "workflow-input" && azurerm_linux_web_app.app_service[0].app_settings.WORKFLOW_AUTH_MODE == "FunctionKey" && azurerm_linux_web_app.app_service[0].app_settings.WORKFLOW_FUNCTION_KEY == "@Microsoft.KeyVault(SecretUri=${var.app_service_workflow_function_key_secret_uri})" && azurerm_linux_web_app.app_service[0].app_settings.WORKFLOW_AUTH_AUDIENCE == "" && azurerm_linux_web_app.app_service[0].app_settings.APP_SERVICE_AUTH_MODE == "ClaimsPrincipal" && azurerm_linux_web_app.app_service[0].app_settings.APP_SERVICE_AUTH_AUTHORITY == var.app_service_auth_authority && azurerm_linux_web_app.app_service[0].app_settings.APP_SERVICE_AUTH_AUDIENCE == var.app_service_auth_audience && length(azurerm_role_assignment.validation_app_service) == 1 && output.deployment.enable_app_service == true
    error_message = "App service app_settings must reference Function URL, Storage endpoint, default FunctionKey auth mode, production upstream auth settings, and validation VM role assignment."
  }
}

run "invalid_app_service_sku_rejected" {
  command = plan
  variables {
    enable_app_service = true
    app_service_sku    = "INVALID_SKU"
  }
  expect_failures = [var.app_service_sku]
}

run "existing_with_custom_uami" {
  command = plan
  variables {
    existing_batch_pool_uami_resource_id = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/shared-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/existing-uami"
  }
  assert {
    condition     = azurerm_linux_function_app.poc.app_settings.BATCH_POOL_UAMI_RESOURCE_ID == var.existing_batch_pool_uami_resource_id && length(azurerm_role_assignment.batch_pool_blob_reader) == 1 && length(azurerm_role_assignment.batch_pool_blob_output_contributor) == 1 && azurerm_role_assignment.batch_pool_blob_reader[0].principal_id == "99999999-9999-9999-9999-999999999999" && azurerm_role_assignment.batch_pool_blob_output_contributor[0].principal_id == "99999999-9999-9999-9999-999999999999" && length(azurerm_batch_pool.poc) == 0
    error_message = "The existing pool identity must get storage roles on the new storage without changing the existing pool."
  }
}

run "app_service_with_entra_auth" {
  command = plan
  variables {
    enable_app_service                    = true
    workflow_auth_mode                    = "EntraEasyAuth"
    workflow_auth_tenant_id               = "11111111-1111-1111-1111-111111111111"
    workflow_auth_api_client_id           = "22222222-2222-2222-2222-222222222222"
    workflow_auth_audience                = "api://workflow"
    workflow_auth_allowed_backend_app_ids = ["33333333-3333-3333-3333-333333333333"]
    workflow_auth_required_app_role       = "Workflow.Invoke"
  }
  assert {
    condition     = azurerm_linux_web_app.app_service[0].app_settings.WORKFLOW_AUTH_MODE == "EntraBearer" && azurerm_linux_web_app.app_service[0].app_settings.WORKFLOW_AUTH_AUDIENCE == "api://workflow" && azurerm_linux_web_app.app_service[0].app_settings.WORKFLOW_FUNCTION_KEY == "@Microsoft.KeyVault(SecretUri=${var.app_service_workflow_function_key_secret_uri})"
    error_message = "App service should resolve EntraBearer and audience from workflow auth configuration."
  }
}

run "app_service_with_explicit_auth_settings" {
  command = apply
  variables {
    enable_app_service             = true
    app_service_workflow_auth_mode = "FunctionKey"
  }
  assert {
    condition     = azurerm_linux_web_app.app_service[0].app_settings.WORKFLOW_AUTH_MODE == "FunctionKey" && azurerm_linux_web_app.app_service[0].app_settings.WORKFLOW_FUNCTION_KEY == "@Microsoft.KeyVault(SecretUri=${var.app_service_workflow_function_key_secret_uri})" && azurerm_linux_web_app.app_service[0].key_vault_reference_identity_id == azurerm_user_assigned_identity.app_service[0].id
    error_message = "App service must use a Key Vault reference resolved with its assigned identity, never the key value."
  }
}

run "raw_function_key_rejected" {
  command = plan
  variables {
    app_service_workflow_function_key = "test-only-rejection-marker"
  }
  expect_failures = [var.app_service_workflow_function_key]
}

run "app_service_requires_secret_reference" {
  command = plan
  variables {
    enable_app_service                           = true
    app_service_workflow_function_key_secret_uri = null
  }
  expect_failures = [var.app_service_workflow_function_key_secret_uri]
}

run "function_key_secret_reference_rejects_query" {
  command = plan
  variables {
    app_service_workflow_function_key_secret_uri = "https://test-vault.vault.azure.net/secrets/workflow-key?sig=test-only"
  }
  expect_failures = [var.app_service_workflow_function_key_secret_uri]
}

run "existing_pool_identity_in_another_subscription_rejected" {
  command = plan
  variables {
    existing_batch_pool_uami_resource_id = "/subscriptions/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/resourceGroups/shared-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/existing-uami"
  }
  expect_failures = [var.existing_batch_pool_uami_resource_id]
}

run "app_service_stub_auth_rejected" {
  command = plan
  variables {
    enable_app_service    = true
    app_service_auth_mode = "Stub"
  }
  expect_failures = [var.app_service_auth_mode]
}

run "app_service_claims_principal_requires_authority" {
  command = plan
  variables {
    enable_app_service         = true
    app_service_auth_mode      = "ClaimsPrincipal"
    app_service_auth_authority = null
  }
  expect_failures = [var.app_service_auth_authority]
}

