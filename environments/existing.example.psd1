@{
    Terraform = @{
        subscription_id = '00000000-0000-0000-0000-000000000000'
        resource_group_name = 'existing-poc-rg'
        create_resource_group = $false
        name_suffix = 'replaceunique001'
        # Replace with your SSH PUBLIC key. No public IP/SSH ingress is enabled; use VM Run Command.
        validation_vm_ssh_public_key = 'ssh-ed25519 REPLACE_WITH_YOUR_PUBLIC_KEY poc-validation'
        validation_vm_admin_username = 'pocadmin'
        validation_vm_size = 'Standard_B2s'
        # Optional operator Object ID (may equal Backend.deployer_principal_id).
        # Grants Blob Data Contributor on poc-vm-control only; replace this placeholder.
        validation_operator_principal_id = '00000000-0000-0000-0000-000000000000'
        batch_mode = 'existing'
        batch_account_id = '/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/existing-batch-rg/providers/Microsoft.Batch/batchAccounts/existing-account'
        batch_endpoint = 'https://existing-account.japaneast.batch.azure.com'
        batch_pool_id = 'existing-windows-pool'
        batch_poll_seconds = 10
        workflow_timeout_minutes = 15
    }
    Backend = @{
        # Set Create=true plus location/deployer_principal_id to bootstrap a NEW dedicated backend.
        Create = $false
        resource_group_name = 'poc-terraform-state-rg'
        storage_account_name = 'pocreplaceuniquestate'
        container_name = 'tfstate'
    }
    TimeoutSeconds = 1800
}
