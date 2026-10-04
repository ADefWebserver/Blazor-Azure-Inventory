# Derived Azure MCP Server image: HTTP transport, On-Behalf-Of outgoing auth, read-only, tool allowlist.
# Pin AZURE_MCP_TAG to a specific version (or use a digest) for production; never ship :latest.
# Keep the --tool list in sync with AzureMcp:AllowedTools in BlazorAzureInventory/appsettings.json.
ARG AZURE_MCP_TAG=latest
FROM mcr.microsoft.com/azure-sdk/azure-mcp:${AZURE_MCP_TAG}

CMD ["--transport", "http", \
     "--outgoing-auth-strategy", "UseOnBehalfOf", \
     "--read-only", \
     "--mode", "all", \
     "--tool", "azmcp_subscription_list", \
     "--tool", "azmcp_group_list", \
     "--tool", "azmcp_appservice_webapp_get", \
     "--tool", "azmcp_functionapp_get", \
     "--tool", "azmcp_sql_server_get", \
     "--tool", "azmcp_sql_db_get", \
     "--tool", "azmcp_storage_account_get", \
     "--tool", "azmcp_containerapps_list", \
     "--tool", "azmcp_acr_registry_list", \
     "--tool", "azmcp_acr_registry_repository_list", \
     "--tool", "azmcp_cosmos_list", \
     "--tool", "azmcp_redis_list", \
     "--tool", "azmcp_resourcehealth_availability-status_get", \
     "--tool", "azmcp_monitor_metrics_query"]
