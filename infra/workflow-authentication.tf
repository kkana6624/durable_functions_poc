variable "workflow_auth_mode" {
  type     = string
  default  = "PocFunctionKey"
  nullable = false
  validation {
    condition     = contains(["PocFunctionKey", "EntraEasyAuth"], var.workflow_auth_mode)
    error_message = "workflow_auth_mode must be PocFunctionKey or EntraEasyAuth."
  }
}

variable "workflow_auth_tenant_id" {
  type    = string
  default = null
}
variable "workflow_auth_api_client_id" {
  type    = string
  default = null
}
variable "workflow_auth_audience" {
  type    = string
  default = null
}
variable "workflow_auth_allowed_backend_app_ids" {
  type     = list(string)
  default  = []
  nullable = false
}
variable "workflow_auth_required_app_role" {
  type    = string
  default = null
}

locals {
  workflow_auth_strict = var.workflow_auth_mode == "EntraEasyAuth"
  workflow_auth_guids  = concat([var.workflow_auth_tenant_id, var.workflow_auth_api_client_id], var.workflow_auth_allowed_backend_app_ids)
  workflow_auth_valid = alltrue([
    for id in local.workflow_auth_guids : try(can(regex("(?i)^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", id)) && lower(id) != "00000000-0000-0000-0000-000000000000", false)
    ]) && length(var.workflow_auth_allowed_backend_app_ids) > 0 && (
    length(distinct([for id in var.workflow_auth_allowed_backend_app_ids : try(lower(id), "")])) == length(var.workflow_auth_allowed_backend_app_ids)
  ) && try(length(trimspace(var.workflow_auth_audience)) > 0, false) && try(length(trimspace(var.workflow_auth_required_app_role)) > 0, false)
  workflow_auth_app_settings = local.workflow_auth_strict ? {
    WORKFLOW_AUTH_MODE                    = var.workflow_auth_mode
    WORKFLOW_AUTH_TENANT_ID               = try(lower(var.workflow_auth_tenant_id), "")
    WORKFLOW_AUTH_AUDIENCE                = try(trimspace(var.workflow_auth_audience), "")
    WORKFLOW_AUTH_ALLOWED_BACKEND_APP_IDS = join(",", [for id in var.workflow_auth_allowed_backend_app_ids : try(lower(id), "")])
    WORKFLOW_AUTH_REQUIRED_APP_ROLE       = try(trimspace(var.workflow_auth_required_app_role), "")
  } : { WORKFLOW_AUTH_MODE = "PocFunctionKey" }
}
