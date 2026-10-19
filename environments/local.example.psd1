@{
    # Local/developer example configuration.
    # Copy to a unique environment file (e.g., environments/local.psd1) and replace values.
    Terraform = @{
        workflow_auth_mode = 'PocFunctionKey'
        subscription_id = '00000000-0000-0000-0000-000000000000'
        resource_group_name = 'your-local-poc-rg'
        create_resource_group = $true
        location = 'your-azure-region'
        name_suffix = 'local001'
        durable_backend = 'azureStorage'
        durable_task_hub_name = 'PocBatchHub'
        # Storage public network access (keep $true until private endpoint connectivity is verified in TASK-3.2/3.3)
        storage_public_network_access_enabled = $true
        validation_vm_ssh_public_key = 'ssh-ed25519 REPLACE_WITH_YOUR_PUBLIC_KEY local-validation'
        validation_vm_admin_username = 'azureuser'
        validation_vm_size = 'Standard_B2s'
        validation_operator_principal_id = '00000000-0000-0000-0000-000000000000'
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
    }
    Backend = @{
        Create = $true
        resource_group_name = 'your-local-state-rg'
        location = 'your-azure-region'
        storage_account_name = 'replacewithlocalstate'
        container_name = 'tfstate'
        deployer_principal_id = '00000000-0000-0000-0000-000000000000'
    }
    TimeoutSeconds = 1800
}
