@{
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
        resource_group_name = 'existing-poc-rg'
        create_resource_group = $false
        name_suffix = 'replaceunique001'
        # Keep the validated Azure Storage backend; use a separate environment for DTS.
        durable_backend = 'azureStorage'
        durable_task_hub_name = 'PocBatchHub'
        # Storage public network access (keep $true until private connectivity is verified)
        storage_public_network_access_enabled = $true
        # Replace with your SSH PUBLIC key. No public IP/SSH ingress is enabled; use VM Run Command.
        validation_vm_ssh_public_key = 'ssh-ed25519 REPLACE_WITH_YOUR_PUBLIC_KEY poc-validation'
        validation_vm_admin_username = 'azureuser'
        validation_vm_size = 'Standard_B2s'
        # Optional operator Object ID (may equal Backend.deployer_principal_id).
        # Grants Blob Data Contributor on poc-vm-control only; replace this placeholder.
        validation_operator_principal_id = '00000000-0000-0000-0000-000000000000'
        batch_mode = 'existing'
        batch_account_id = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/existing-batch-rg/providers/Microsoft.Batch/batchAccounts/existing-account'
        batch_endpoint = 'https://your-batch-account.your-region.batch.azure.com'
        batch_pool_id = 'existing-windows-pool'
        batch_poll_seconds = 10
        workflow_timeout_minutes = 15
    }
    Backend = @{
        # Set Create=true plus location/deployer_principal_id to bootstrap a NEW dedicated backend.
        Create = $false
        resource_group_name = 'your-state-resource-group'
        storage_account_name = 'replacewithuniquestate'
        container_name = 'tfstate'
    }
    TimeoutSeconds = 1800
}
