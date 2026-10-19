@{
    # Copy to a NEW environment file (e.g. dts-isolated.psd1) and invoke:
    #   ./scripts/deploy.ps1 -Environment dts-isolated -ConfigFile ./environments/dts-isolated.psd1 -PlanOnly
    #
    # CRITICAL GUIDANCE & NON-DESTRUCTIVE ISOLATION:
    # 1. Do NOT repoint or in-place modify the validated Azure Storage PoC environment (poc.psd1).
    #    Azure Storage orchestration history and Entity state CANNOT be migrated to DTS.
    # 2. DTS comparison MUST be deployed to a completely isolated environment:
    #    - Dedicated Resource Group (resource_group_name)
    #    - Dedicated name suffix (name_suffix)
    #    - Dedicated Task Hub (durable_task_hub_name)
    #    - Dedicated App Service plan (leave existing_service_plan_id null to create a new plan)
    #    - Dedicated Batch account & Windows compute pool (batch_mode = 'new')
    # 3. Independent Terraform state key:
    #    deploy.ps1 resolves the state blob key directly from the -Environment argument.
    #    Using a distinct environment name (e.g., -Environment dts-isolated) guarantees that
    #    dts-isolated.tfstate is used, strictly preventing destruction or overwrite of poc.tfstate.
    # 4. Prerequisites:
    #    - Microsoft.DurableTask must be registered in the deployment subscription.
    #    - The chosen Azure region must support Consumption DTS.
    Terraform = @{
        # Opt-in EasyAuth; registrations, Application App Role assignment and idtyp claim are external prerequisites.
        workflow_auth_mode = 'PocFunctionKey'
        # For EntraEasyAuth, set all five values; backend IDs must be a unique nonempty GUID array.
        # workflow_auth_tenant_id = '<tenant-guid>'
        # workflow_auth_api_client_id = '<api-application-client-guid>'
        # workflow_auth_audience = 'api://<api-application-client-guid>'
        # workflow_auth_allowed_backend_app_ids = @('<backend-application-client-guid>')
        # workflow_auth_required_app_role = 'Workflow.Invoke'
        # Function keys remain required. Use Invoke-WorkflowAuthValidation.ps1 instead of deploy -Smoke.

        subscription_id = '00000000-0000-0000-0000-000000000000'
        # Dedicated Resource Group for isolated DTS environment (must start with poc- when created)
        resource_group_name = 'poc-dts-isolated-rg'
        create_resource_group = $true
        location = 'japaneast'

        # Unique 4-16 lowercase alphanumeric suffix. Must not collide with existing environments.
        name_suffix = 'dtsiso001'

        # DTS backend with a dedicated task hub name
        durable_backend = 'durableTaskScheduler'
        durable_task_hub_name = 'DtsIsolatedHub'

        # Storage public network access (keep $true until private connectivity is verified)
        storage_public_network_access_enabled = $true

        # Validation VM: SSH public key only (no SSH ingress enabled; VM Run Command used for guest execution)
        validation_vm_ssh_public_key = 'ssh-ed25519 REPLACE_WITH_YOUR_PUBLIC_KEY dts-validation'
        validation_vm_admin_username = 'azureuser'
        validation_vm_size = 'Standard_B2s'
        # For NVMe-only sizes, explicitly set validation_vm_disk_controller_type='NVMe'.
        validation_operator_principal_id = '00000000-0000-0000-0000-000000000000'

        # Dedicated Batch account & Windows compute pool (batch_mode = 'new').
        # Creates an isolated Batch account, pool, and VNet subnet, avoiding shared pool contention.
        batch_mode = 'new'
        batch_image_source = 'gallery'
        gallery_image_version_id = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/images-rg/providers/Microsoft.Compute/galleries/gallery/images/windows/versions/1.0.0'
        batch_node_agent_sku_id = 'batch.node.windows amd64'
        batch_vm_size = 'Standard_D2s_v5'
        batch_node_type = 'spot'
        batch_node_count = 1
        batch_max_tasks_per_node = 1
        batch_diagnostics_enabled = $true
        batch_poll_seconds = 10
        workflow_timeout_minutes = 15

        # Marketplace alternative: set batch_image_source='marketplace', omit gallery ID,
        # and set marketplace_image=@{publisher='...';offer='...';sku='...';version='...'}.
        # Pick an exact supported image version using Azure Batch supported-images API.

        # Optional business App Service (TASK-4.1)
        # enable_app_service = $false
        # app_service_sku    = 'B1'
    }
    Backend = @{
        # Reuse state container with a distinct -Environment argument to isolate tfstate key.
        Create = $false
        resource_group_name = 'your-state-resource-group'
        storage_account_name = 'replacewithuniquestate'
        container_name = 'tfstate'
        # If creating a new dedicated state storage account, set Create = $true and supply deployer principal:
        # deployer_principal_id = '00000000-0000-0000-0000-000000000000'
    }
    TimeoutSeconds = 1800
}
